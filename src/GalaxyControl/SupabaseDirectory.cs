using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace GalaxyControl;

// Only the operator's Supabase URL and publishable key are distributed. User tokens stay in memory.
internal sealed class SupabaseDirectory : IDisposable {
    readonly HttpClient http;
    readonly string apiKey, instance;
    readonly SemaphoreSlim refreshing = new(1);
    string access = "", refresh = "";
    DateTime expires;
    Guid? supportSession;
    long cursor;
    bool technician,negotiated,requestShown;
    public event Action<JsonElement>? Message;
    public SupabaseDirectory(string url,string publishableKey,string instanceId,HttpMessageHandler? handler=null) {
        var endpoint=new Uri(url.TrimEnd('/')+"/");
        if(endpoint.Scheme!="https" || !string.IsNullOrEmpty(endpoint.UserInfo) || endpoint.AbsolutePath!="/" || endpoint.Host.Contains("YOUR-PROJECT",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Configure your own Supabase project URL in connection.json.");
        if(!publishableKey.StartsWith("sb_publishable_",StringComparison.Ordinal) || publishableKey.Length<25)
            throw new InvalidOperationException("Use your own Supabase publishable key. Secret and service-role keys must never be distributed.");
        http=handler is null?new HttpClient():new HttpClient(handler);
        http.BaseAddress=endpoint;http.Timeout=TimeSpan.FromSeconds(15);
        apiKey=publishableKey;instance=instanceId;
    }
    async Task<JsonElement> Request(string path,object body,string? bearer,CancellationToken ct) {
        using var request=new HttpRequestMessage(HttpMethod.Post,path){Content=JsonContent.Create(body)};
        request.Headers.Add("apikey",apiKey);
        if(bearer is not null)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",bearer);
        using var response=await http.SendAsync(request,ct);
        var text=await response.Content.ReadAsStringAsync(ct);
        if(text.Length>200000)throw new IOException("Supabase response was too large.");
        if(!response.IsSuccessStatusCode) {
            // Avoid displaying raw SQL errors, tokens or endpoint metadata in the customer's interface.
            if((int)response.StatusCode==429)throw new InvalidOperationException("Too many requests. Please wait and try again.");
            throw new InvalidOperationException(path.StartsWith("auth/")?"Sign-in failed. Check technician credentials or enable anonymous customer sign-ins in your Supabase project.":"Directory request failed. Check your Supabase setup and account permissions.");
        }
        using var document=JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
    void SetSession(JsonElement session) {
        access=session.GetProperty("access_token").GetString()!;
        refresh=session.GetProperty("refresh_token").GetString()!;
        expires=DateTime.UtcNow.AddSeconds(session.GetProperty("expires_in").GetInt32());
    }
    async Task<JsonElement> Exchange(string action,object data,CancellationToken ct) {
        await refreshing.WaitAsync(ct);
        try {
            if(DateTime.UtcNow>expires-TimeSpan.FromMinutes(1))
                SetSession(await Request("auth/v1/token?grant_type=refresh_token",new {refresh_token=refresh},null,ct));
        }finally{refreshing.Release();}
        var result=await Request("rest/v1/rpc/gc_exchange",new {p_action=action,p_data=data},access,ct);
        if(result.TryGetProperty("error",out var error))throw new InvalidOperationException(error.GetString());
        return result;
    }
    void Emit(string type,string message) {
        using var document=JsonDocument.Parse(JsonSerializer.Serialize(new {type,message}));
        Message?.Invoke(document.RootElement.Clone());
    }
    public async Task Run(bool isTechnician,string email,string password,CancellationToken ct) {
        technician=isTechnician;
        SetSession(await Request(technician?"auth/v1/token?grant_type=password":"auth/v1/signup",
            technician?(object)new {email,password}:new {data=new {}},null,ct));
        if(technician) {
            await Exchange("online",new {instance_id=instance},ct);
            Emit("online","Online · customers can connect using your email.");
        }else{
            var request=await Exchange("request",new {email},ct);
            supportSession=request.GetProperty("session_id").GetGuid();
            Emit("waiting","Waiting for your technician to accept.");
        }
        while(!ct.IsCancellationRequested) {
            var state=await Exchange("poll",new {instance_id=instance,session_id=supportSession,after_id=cursor},ct);
            ct.ThrowIfCancellationRequested();
            var status=state.GetProperty("status").GetString();
            if(state.TryGetProperty("session_id",out var id))supportSession=id.GetGuid();
            if(status=="closed"){Emit("ended","Session ended. Your computer is no longer shared.");return;}
            if(status=="pending" && technician && !requestShown){requestShown=true;Emit("request","A customer is requesting desktop support.");}
            if(status=="accepted" && !negotiated){negotiated=true;Emit("negotiate","Establishing a direct, encrypted P2P connection…");}
            if(state.TryGetProperty("events",out var events))foreach(var item in events.EnumerateArray()) {
                Message?.Invoke(item.GetProperty("signal").Clone());
                cursor=Math.Max(cursor,item.GetProperty("id").GetInt64());
            }
            await Task.Delay(1000,ct);
        }
    }
    public Task Accept(CancellationToken ct)=>Exchange("accept",new {instance_id=instance,session_id=supportSession},ct);
    public Task Send(JsonElement signal,CancellationToken ct)=>Exchange("signal",new {instance_id=instance,session_id=supportSession,signal},ct);
    public async Task Stop() {
        if(string.IsNullOrEmpty(access))return;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try {await Exchange("stop",new {instance_id=instance,session_id=supportSession},timeout.Token);}
        catch(Exception e) when(e is HttpRequestException or OperationCanceledException or InvalidOperationException or ObjectDisposedException or JsonException){ /* The online/session lease expires if the network is unavailable. */ }
    }
    public void Dispose(){http.Dispose();refreshing.Dispose();access="";refresh="";}
}
