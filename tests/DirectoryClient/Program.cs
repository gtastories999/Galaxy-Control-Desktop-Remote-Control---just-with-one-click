using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GalaxyControl;

const string key="sb_publishable_test_only_not_a_real_project";
const string instance="00000000-0000-4000-8000-000000000011";
using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
static void Check(bool condition,string reason){if(!condition)throw new Exception(reason);}
foreach(var bad in new[]{"sb_secret_do_not_ship", "service_role", "YOUR_SUPABASE_PUBLISHABLE_KEY"}) {
    try{using var rejected=new SupabaseDirectory("https://example.supabase.co",bad,instance);throw new Exception("Secret configuration accepted");}
    catch(InvalidOperationException){ }
}
var api=new FakeSupabase();
using(var client=new SupabaseDirectory("https://example.supabase.co",key,instance,api)) {
    var request=new TaskCompletionSource();var negotiation=new TaskCompletionSource();var candidate=new TaskCompletionSource();
    client.Message+=message=>{
        var type=message.GetProperty("type").GetString();
        if(type=="request")request.TrySetResult();
        if(type=="negotiate")negotiation.TrySetResult();
        if(type=="candidate")candidate.TrySetResult();
    };
    var run=client.Run(true,"tech@example.com","private-password",timeout.Token);
    await request.Task.WaitAsync(timeout.Token);
    await client.Accept(timeout.Token);
    await negotiation.Task.WaitAsync(timeout.Token);
    await candidate.Task.WaitAsync(timeout.Token);
    using var json=JsonDocument.Parse("{\"type\":\"answer\",\"sdp\":\"test\"}");
    await client.Send(json.RootElement,timeout.Token);
    await client.Stop();
    await run.WaitAsync(timeout.Token);
    Check(api.Refreshes==1,"Access token should refresh exactly once.");
    Check(api.Sent && api.Accepted && api.Stopped,"Technician lifecycle was not sent to Supabase.");
}
var guestApi=new FakeSupabase {Guest=true};
using(var guestClient=new SupabaseDirectory("https://example.supabase.co",key,instance,guestApi)) {
    var negotiation=new TaskCompletionSource();
    guestClient.Message+=message=>{if(message.GetProperty("type").GetString()=="negotiate")negotiation.TrySetResult();};
    var run=guestClient.Run(false,"tech@example.com","",timeout.Token);
    await negotiation.Task.WaitAsync(timeout.Token);
    await guestClient.Stop();await run.WaitAsync(timeout.Token);
    Check(guestApi.Anonymous && guestApi.Requested,"Customer should use invisible anonymous auth and exact email lookup.");
}
Console.WriteLine("PASS: Supabase REST auth, publishable-only configuration, token refresh, email lookup, poll/accept, SDP send and stop.");

sealed class FakeSupabase : HttpMessageHandler {
    public bool Guest,Anonymous,Requested,Accepted,Sent,Stopped;
    public int Refreshes;
    readonly Guid session=Guid.Parse("00000000-0000-4000-8000-000000000099");
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
        if(request.Headers.GetValues("apikey").Single()!="sb_publishable_test_only_not_a_real_project")throw new Exception("Wrong publishable key header.");
        using var document=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var body=document.RootElement;
        object result;
        if(request.RequestUri!.AbsolutePath.StartsWith("/auth/v1/")) {
            if(request.Headers.Authorization is not null)throw new Exception("Auth requests must not send a secret or publishable key as a bearer JWT.");
            if(request.RequestUri.Query.Contains("refresh_token"))Refreshes++;
            if(request.RequestUri.AbsolutePath.EndsWith("signup"))Anonymous=true;
            result=new {access_token="user-jwt",refresh_token="refresh-test",expires_in=Refreshes==0?0:3600};
        }else{
            if(request.Headers.Authorization?.Parameter!="user-jwt")throw new Exception("Data API requires the user JWT.");
            var data=body.GetProperty("p_data");
            switch(body.GetProperty("p_action").GetString()) {
                case "online": result=new {status="online"};break;
                case "request":
                    if(data.GetProperty("email").GetString()!="tech@example.com")throw new Exception("Wrong email lookup.");
                    Requested=true;Accepted=true;result=new {status="pending",session_id=session};break;
                case "accept":Accepted=true;result=new {status="accepted"};break;
                case "signal":Sent=true;result=new {status="sent"};break;
                case "stop":Stopped=true;result=new {status="closed"};break;
                case "poll":result=new {status=Stopped?"closed":Accepted?"accepted":"pending",session_id=session,
                    events=Accepted?new[]{new {id=1,signal=new {type="candidate",candidate=new {candidate="test",sdpMid="0",sdpMLineIndex=0}}}}:[]};break;
                default:throw new Exception("Unexpected RPC action.");
            }
        }
        return new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(result)};
    }
}
