using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRateLimiter(o => o.AddPolicy("connect", context => RateLimitPartition.GetFixedWindowLimiter(
    context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions {
        PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
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
app.MapGet("/health", () => Results.Ok(new { service = "Galaxy Control Relay", status = "ready" }));
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
    Session? session = null;
    try {
        if (role == "technician") {
            if (!technicians.TryAdd(email, peer)) { await peer.Text("error", "This technician is already online.", ct); return; }
            await peer.Text("online", "Waiting for a support request.", ct);
        } else {
            if (!technicians.TryGetValue(email, out var technician)) { await peer.Text("error", "Technician is offline. Please try again shortly.", ct); return; }
            session = new Session(peer, technician);
            if (!sessions.TryAdd(email, session)) { await peer.Text("error", "Technician is busy. Please try again shortly.", ct); return; }
            await peer.Text("waiting", "Waiting for your technician to accept.", ct);
            await technician.Text("request", "A customer is requesting desktop support.", ct);
        }
        var buffer = new byte[2 * 1024 * 1024];
        while (socket.State == WebSocketState.Open) {
            using var data = new MemoryStream();
            WebSocketReceiveResult part;
            do {
                part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (part.MessageType == WebSocketMessageType.Close) return;
                if (data.Length + part.Count > buffer.Length) throw new InvalidDataException("Message too large.");
                data.Write(buffer, 0, part.Count);
            } while (!part.EndOfMessage);
            if (!sessions.TryGetValue(email, out var current)) continue;
            if (role == "technician" && !ReferenceEquals(current.Technician, peer)) continue;
            if (role == "host" && !ReferenceEquals(current.Host, peer)) continue;
            var bytes = data.ToArray();
            if (part.MessageType == WebSocketMessageType.Text) {
                using var json = JsonDocument.Parse(bytes);
                var type = json.RootElement.GetProperty("type").GetString();
                if (role == "technician" && type == "accept" && !current.Active) {
                    current.Active = true;
                    await current.Host.Text("connected", "Your technician is connected. You can stop sharing at any time.", ct);
                    await peer.Text("connected", "Connected to the customer's desktop.", ct);
                    continue;
                }
                if (type == "stop") return;
                if (role != "technician" || type != "input" || bytes.Length > 1024 || !current.Active) continue;
            } else if (role != "host" || !current.Active) continue;
            await (role == "host" ? current.Technician : current.Host).Send(bytes, part.MessageType, ct);
        }
    } catch (Exception e) when (e is WebSocketException or OperationCanceledException or JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException) {
        app.Logger.LogInformation("Support connection ended: {Reason}", e.GetType().Name);
    } finally {
        if (role == "technician") ((ICollection<KeyValuePair<string,Peer>>)technicians).Remove(new(email, peer));
        if (sessions.TryGetValue(email, out var current) &&
            (ReferenceEquals(current.Host, peer) || ReferenceEquals(current.Technician, peer)) && sessions.TryRemove(email, out _)) {
            var other = role == "host" ? current.Technician : current.Host;
            // Closing both sides ends control immediately; a new session requires a fresh connection.
            other.Socket.Abort();
        }
        socket.Abort();
    }
}).RequireRateLimiting("connect");
app.Run();

sealed class Peer(WebSocket socket) {
    public WebSocket Socket { get; } = socket;
    readonly SemaphoreSlim sending = new(1);
    public Task Text(string type, string message, CancellationToken ct) => Send(JsonSerializer.SerializeToUtf8Bytes(new { type, message }), WebSocketMessageType.Text, ct);
    public async Task Send(byte[] data, WebSocketMessageType type, CancellationToken ct) {
        await sending.WaitAsync(ct);
        try { await Socket.SendAsync(new ArraySegment<byte>(data), type, true, ct); }
        finally { sending.Release(); }
    }
}
sealed class Session(Peer host, Peer technician) {
    public Peer Host { get; } = host;
    public Peer Technician { get; } = technician;
    public bool Active { get; set; }
}
