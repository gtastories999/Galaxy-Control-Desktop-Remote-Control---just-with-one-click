using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using GalaxyControl;

static class Program {
    const string Project="abcdefghijklmnopqrst";
    static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    static async Task Reject(Func<Task> action){try{await action();throw new Exception("Expected refusal");}catch(InvalidOperationException){}}
    static async Task Main(){
        var fake=new Management();using(var api=new SupabaseProvisioner("test-owner-token",fake)){
            var config=await api.Configure(Project,"TEST_INSTALL_SQL",_=>{},CancellationToken.None);
            Assert(config.SupabaseUrl==$"https://{Project}.supabase.co","Only selected project may be configured");
            Assert(fake.Installs==1&&fake.Keys==1&&fake.Patches==2,"Schema, authentication, private API scope and public key must be configured");
            fake.Installed=true;await api.Configure(Project,"TEST_INSTALL_SQL",_=>{},CancellationToken.None,config);
            Assert(fake.Installs==1&&fake.Keys==1,"Repeat setup must preserve the existing schema and public client key");
        }
        foreach(var mode in new[]{"foreign","starting","partial","insecure","secret"}){
            var bad=new Management{Mode=mode};using var api=new SupabaseProvisioner("test-owner-token",bad);
            await Reject(async()=>await api.Configure(Project,"TEST_INSTALL_SQL",_=>{},CancellationToken.None));
            if(mode is "foreign" or "starting" or "partial" or "insecure")Assert(bad.Patches==0&&bad.Keys==0,"Refused installation must not alter account settings or create keys");
        }
        var h=new Exchange();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));Task? browser=null;
        var token=await SupabaseAccountLink.Connect(new Uri("https://broker.example.test/connect/"),uri=>{
            browser=Task.Run(async()=>{
                var q=uri.Query.TrimStart('?').Split('&').Select(x=>x.Split('=',2)).ToDictionary(x=>x[0],x=>x[1]);h.Challenge=q["challenge"];
                using var client=new HttpClient();var root=$"http://127.0.0.1:{q["port"]}/callback?";
                using var wrong=await client.GetAsync(root+"state=invalid&code=test&ticket=test");Assert(wrong.StatusCode==HttpStatusCode.BadRequest,"Wrong callback state must be rejected");
                using var valid=await client.GetAsync(root+"state="+q["state"]+"&code=test&ticket=signed-test");Assert(valid.IsSuccessStatusCode,"Valid callback must return to the desktop app");
            });
        },timeout.Token,h);
        if(browser is not null)await browser;
        Assert(token=="test-account-token"&&h.Exchanged,"OAuth response must be exchanged in memory with PKCE");
        Console.WriteLine("PASS: owned named project only, safe repeat install, authorization/readiness/partial-schema/permission/key failures, loopback state checks and PKCE token exchange.");
    }
    sealed class Management:HttpMessageHandler {
        public string Mode="";public bool Installed;public int Installs,Keys,Patches;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){
            Assert(r.RequestUri!.Host=="api.supabase.com","Owner authorization must never be sent to the project host");Assert(r.Headers.Authorization?.Parameter=="test-owner-token","Management API needs explicit owner authorization");
            var path=r.RequestUri.AbsolutePath;object result=new {};
            if(r.Method==HttpMethod.Get&&path=="/v1/projects")result=new[]{new {id=Project,name=Mode=="foreign"?"Another app":"Galaxy Control",status=Mode=="starting"?"INACTIVE":"ACTIVE_HEALTHY"}};
            else if(path.EndsWith("/database/query")){
                var body=await r.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken:ct);var sql=body.GetProperty("query").GetString();
                if(sql==SupabaseProvisioner.InspectSql)result=new[]{new {installed=Installed,occupied=Mode=="partial"}};
                else if(sql==SupabaseProvisioner.VerifySql)result=new[]{new {secure=Mode!="insecure"}};
                else{Assert(sql=="TEST_INSTALL_SQL","Only embedded installation SQL may be sent");Installs++;}
            }else if(r.Method==HttpMethod.Patch){
                Patches++;var body=await r.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken:ct);
                if(path.EndsWith("/config/auth")){Assert(!body.GetProperty("mailer_autoconfirm").GetBoolean(),"Email confirmation must remain enabled");Assert(body.GetProperty("external_anonymous_users_enabled").GetBoolean(),"Customers must not need accounts");}
                else Assert(body.GetProperty("db_schema").GetString()=="public, graphql_public","Private directory schema must remain unexposed");
            }else if(r.Method==HttpMethod.Post&&path.EndsWith("/api-keys")){
                var body=await r.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken:ct);Assert(body.GetProperty("type").GetString()=="publishable","No elevated key may be requested");Keys++;result=new {api_key=Mode=="secret"?"sb_secret_must_never_be_saved":"sb_publishable_test_public_configuration_only"};
            }else throw new Exception("Unexpected setup endpoint "+path);
            return new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(result)};
        }
    }
    sealed class Exchange:HttpMessageHandler {
        public string Challenge="";public bool Exchanged;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){
            Assert(r.RequestUri!.Host=="broker.example.test","OAuth exchange must stay on its configured HTTPS broker");
            if(r.Method==HttpMethod.Get)return new(HttpStatusCode.OK){Content=JsonContent.Create(new {ready=true})};
            var data=await r.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken:ct);var verifier=data.GetProperty("verifier").GetString()!;
            var actual=Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+','-').Replace('/','_');Assert(actual==Challenge,"The original PKCE verifier must be used");Exchanged=true;
            Assert(data.GetProperty("ticket").GetString()=="signed-test","Provider state ticket is required");
            return new(HttpStatusCode.OK){Content=JsonContent.Create(new {access_token="test-account-token"})};
        }
    }
}
