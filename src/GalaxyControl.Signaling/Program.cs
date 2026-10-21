using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRateLimiter(o => {
    o.RejectionStatusCode = 429;
    o.AddPolicy("connect", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions {
            PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
var technicians = new ConcurrentDictionary<string, Peer>(StringComparer.OrdinalIgnoreCase);
var sessions = new ConcurrentDictionary<string, Session>(StringComparer.OrdinalIgnoreCase);
var credentials = JsonSerializer.Deserialize<Dictionary<string,string>>(Environment.GetEnvironmentVariable("GALAXY_TECHNICIANS") ?? "{}")!
    .ToDictionary(p => p.Key.Trim().ToLowerInvariant(), p => p.Value);
if (credentials.Count == 0) throw new InvalidOperationException("Set GALAXY_TECHNICIANS to an email → SHA-256 token hash JSON object.");
foreach(var pair in credentials) {
    if(pair.Value.Length!=64 || !pair.Value.All(Uri.IsHexDigit)) throw new InvalidOperationException("Technician hashes must be SHA-256 hex values.");
}
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.MapGet("/health", () => Results.Ok(new { service = "Galaxy Control Signaling", status = "ready", mediaRelay = false }));
app.Map("/connect", async context => {
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    var email = context.Request.Query["email"].ToString().Trim().ToLowerInvariant();
    var role = context.Request.Query["role"].ToString();
    if (!credentials.TryGetValue(email, out var expected) || (role != "host" && role != "technician")) {
        context.Response.StatusCode = 403; return;
    }
    if (role == "technician") {
        var token = context.Request.Headers.Authorization.ToString();
        if (!token.StartsWith("Bearer ") || !CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(token[7..])), Convert.FromHexString(expected))) {
            context.Response.StatusCode = 401; return;
        }
    }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(role == "host" ? 60 : 120));
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, context.RequestAborted);
    var ct = linked.Token;
    var peer = new Peer(socket);
    try {
        if (role == "technician") {
            if (!technicians.TryAdd(email, peer)) { await peer.Text("error", "This technician is already online.", ct); return; }
            await peer.Text("online", "Waiting for a support request.", ct);
        } else {
            if (!technicians.TryGetValue(email, out var technician)) { await peer.Text("error", "Technician is offline. Please try again shortly.", ct); return; }
            var session = new Session(peer, technician);
            if (!sessions.TryAdd(email, session)) { await peer.Text("error", "Technician is busy. Please try again shortly.", ct); return; }
            await peer.Text("waiting", "Waiting for your technician to accept.", ct);
            await technician.Text("request", "A customer is requesting desktop support.", ct);
        }
        var buffer = new byte[32768];
        var candidates = 0;
        while (socket.State == WebSocketState.Open) {
            using var data = new MemoryStream();
            WebSocketReceiveResult part;
            do {
                part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (part.MessageType == WebSocketMessageType.Close) return;
                if (part.MessageType != WebSocketMessageType.Text || data.Length + part.Count > buffer.Length)
                    throw new InvalidDataException("Only bounded text signaling is accepted. Media relay is disabled.");
                data.Write(buffer, 0, part.Count);
            } while (!part.EndOfMessage);
            using var json = JsonDocument.Parse(data.ToArray());
            var root = json.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "stop") return;
            if (!sessions.TryGetValue(email, out var current)) continue;
            if (role == "technician" && !ReferenceEquals(current.Technician, peer)) continue;
            if (role == "host" && !ReferenceEquals(current.Host, peer)) continue;
            if (role == "technician" && type == "accept" && current.TryAccept()) {
                // Prepare the answering peer before asking the customer to generate an offer.
                await peer.Text("negotiate", "Establishing a direct P2P connection…", ct);
                await current.Host.Text("negotiate", "Establishing a direct P2P connection…", ct);
                continue;
            }
            if (!current.Accepted) throw new InvalidDataException("The technician has not accepted this session.");
            object signal;
            if (type is "offer" or "answer") {
                if ((type == "offer") != (role == "host") || !current.TryDescription(role)) throw new InvalidDataException("Unexpected session description.");
                var sdp = root.GetProperty("sdp").GetString();
                if (sdp is null || sdp.Length > 30000 || !sdp.StartsWith("v=0") ||
                    !sdp.Contains("m=application ") || !sdp.Contains("a=fingerprint:sha-256 ", StringComparison.OrdinalIgnoreCase) ||
                    sdp.Contains(" typ relay", StringComparison.OrdinalIgnoreCase) ||
                    sdp.Split('\n').Any(line => line.StartsWith("m=") && !line.StartsWith("m=application ")))
                    throw new InvalidDataException("Invalid data-channel session description.");
                signal = new { type, sdp };
            } else if (type == "candidate") {
                if (++candidates > 128) throw new InvalidDataException("Too many ICE candidates.");
                var candidate = root.GetProperty("candidate");
                var text = candidate.GetProperty("candidate").GetString();
                if (text is null || text.Length > 2048 || !text.StartsWith("candidate:") ||
                    !System.Text.RegularExpressions.Regex.IsMatch(text, @" typ (host|srflx|prflx)( |$)"))
                    throw new InvalidDataException("Only direct ICE candidates are accepted.");
                var mid = candidate.TryGetProperty("sdpMid", out var m) && m.ValueKind != JsonValueKind.Null ? m.GetString() : null;
                int? index = candidate.TryGetProperty("sdpMLineIndex", out var i) && i.ValueKind != JsonValueKind.Null ? i.GetInt32() : null;
                if (mid?.Length > 32 || index is < 0 or > 8) throw new InvalidDataException("Invalid candidate index.");
                signal = new { type, candidate = new { candidate = text, sdpMid = mid, sdpMLineIndex = index } };
            } else throw new InvalidDataException("Only SDP and ICE signaling are allowed; input and screen data must use P2P.");
            await (role == "host" ? current.Technician : current.Host).Send(JsonSerializer.SerializeToUtf8Bytes(signal), ct);
        }
    } catch (Exception e) when (e is WebSocketException or OperationCanceledException or JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) {
        app.Logger.LogInformation("Signaling connection ended: {Reason}", e.GetType().Name);
    } finally {
        if (role == "technician") ((ICollection<KeyValuePair<string,Peer>>)technicians).Remove(new(email, peer));
        if (sessions.TryGetValue(email, out var current) &&
            (ReferenceEquals(current.Host, peer) || ReferenceEquals(current.Technician, peer)) &&
            ((ICollection<KeyValuePair<string, Session>>)sessions).Remove(new(email, current))) {
            var other = role == "host" ? current.Technician : current.Host;
            // Release the technician slot before the other socket observes the session ending.
            ((ICollection<KeyValuePair<string,Peer>>)technicians).Remove(new(email, current.Technician));
            // Clients close their direct peer connection if this session's signaling socket ends.
            other.Socket.Abort();
        }
        // Flush offline/busy errors and the upgrade response before ending a rejected connection.
        // An immediate TCP abort here can discard the WebSocket handshake under load.
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived) {
            using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", closing.Token); }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or InvalidOperationException) { }
        }
        socket.Abort();
    }
}).RequireRateLimiting("connect");
app.Run();

sealed class Peer(WebSocket socket) {
    public WebSocket Socket { get; } = socket;
    readonly SemaphoreSlim sending = new(1);
    public Task Text(string type, string message, CancellationToken ct) => Send(JsonSerializer.SerializeToUtf8Bytes(new { type, message }), ct);
    public async Task Send(byte[] data, CancellationToken ct) {
        await sending.WaitAsync(ct);
        try { await Socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, ct); }
        finally { sending.Release(); }
    }
}
sealed class Session(Peer host, Peer technician) {
    public Peer Host { get; } = host;
    public Peer Technician { get; } = technician;
    int accepted, offer, answer;
    public bool Accepted => Volatile.Read(ref accepted) == 1;
    public bool TryAccept() => Interlocked.CompareExchange(ref accepted, 1, 0) == 0;
    public bool TryDescription(string role) => role == "host"
        ? Interlocked.CompareExchange(ref offer, 1, 0) == 0
        : Interlocked.CompareExchange(ref answer, 1, 0) == 0;
}
