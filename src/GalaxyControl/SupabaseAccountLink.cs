using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace GalaxyControl;

// Public desktop client: the OAuth app secret remains exclusively in the hosted exchange service.
internal static class SupabaseAccountLink {
    static string Random()=>Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    static string Base64Url(byte[] b)=>Convert.ToBase64String(b).TrimEnd('=').Replace('+','-').Replace('/','_');
    public static async Task<string> Connect(Uri broker,Action<Uri> openBrowser,CancellationToken ct,HttpMessageHandler? handler=null){
        if(broker.Scheme!="https"||broker.UserInfo!=""||broker.Query!=""||broker.Fragment!="")throw new InvalidOperationException("A trusted HTTPS account-connection service is required.");
        var baseUrl=broker.AbsoluteUri.TrimEnd('/')+"/";
        var verifier=Base64Url(RandomNumberGenerator.GetBytes(32));var challenge=Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));var state=Random();
        using var http=handler is null?new HttpClient():new HttpClient(handler);http.Timeout=TimeSpan.FromSeconds(30);
        using(var check=await http.GetAsync(baseUrl+"status",ct)){
            if(!check.IsSuccessStatusCode)throw new InvalidOperationException("Supabase account connection has not been activated by this build's publisher yet.");
        }
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        try{
            var port=((IPEndPoint)listener.LocalEndpoint).Port;
            openBrowser(new Uri(baseUrl+$"start?port={port}&state={state}&challenge={challenge}"));
            while(true){
                using var client=await listener.AcceptTcpClientAsync(ct);using var readTimeout=CancellationTokenSource.CreateLinkedTokenSource(ct);readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await using var stream=client.GetStream();var bytes=new List<byte>();var one=new byte[1];
                while(bytes.Count<8192&&await stream.ReadAsync(one,readTimeout.Token)>0){bytes.Add(one[0]);if(bytes.Count>=4&&Encoding.ASCII.GetString(bytes.TakeLast(4).ToArray())=="\r\n\r\n")break;}
                var line=Encoding.ASCII.GetString(bytes.ToArray()).Split('\r')[0].Split(' ');
                if(line.Length!=3||line[0]!="GET"||!line[1].StartsWith("/callback?",StringComparison.Ordinal)){await Reply(stream,false,ct);continue;}
                var uri=new Uri("http://127.0.0.1"+line[1]);var query=uri.Query.TrimStart('?').Split('&').Select(v=>v.Split('=',2)).Where(v=>v.Length==2).ToDictionary(v=>v[0],v=>Uri.UnescapeDataString(v[1]));
                if(!query.TryGetValue("state",out var returned)||!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(state),Encoding.ASCII.GetBytes(returned))){await Reply(stream,false,ct);continue;}
                if(!query.TryGetValue("code",out var code)||!query.TryGetValue("ticket",out var ticket)||code.Length>2048||ticket.Length>4096){await Reply(stream,false,ct);throw new InvalidOperationException("Supabase account authorization was cancelled or incomplete.");}
                await Reply(stream,true,ct);
                using var exchange=await http.PostAsJsonAsync(baseUrl+"exchange",new {code,verifier,ticket},ct);
                if(!exchange.IsSuccessStatusCode)throw new InvalidOperationException("Supabase account authorization could not be completed. Try connecting again.");
                var data=await exchange.Content.ReadFromJsonAsync<JsonElement>(cancellationToken:ct);var token=data.GetProperty("access_token").GetString();
                if(string.IsNullOrEmpty(token)||token.Length>8192)throw new InvalidOperationException("Supabase returned an invalid account authorization.");
                return token;
            }
        }finally{listener.Stop();}
    }
    static async Task Reply(Stream stream,bool ok,CancellationToken ct){
        var body=Encoding.UTF8.GetBytes(ok?"Galaxy Control received your authorization. Return to the app to finish setup.":"This is not a valid Galaxy Control authorization request.");
        var header=Encoding.ASCII.GetBytes($"HTTP/1.1 {(ok?"200 OK":"400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");await stream.WriteAsync(header,ct);await stream.WriteAsync(body,ct);
    }
    public static void OpenBrowser(Uri uri)=>Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});
}
