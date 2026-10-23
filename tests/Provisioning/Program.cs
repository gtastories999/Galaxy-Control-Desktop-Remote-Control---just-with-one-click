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
        VerifyAutomaticConfiguration();
        await VerifyTechnicianAccount();
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
        await VerifyAdminAccount();
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
    static void VerifyAutomaticConfiguration(){
        var root=Path.Combine(Path.GetTempPath(),"galaxy-config-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{
            var installed=Path.Combine(root,"profile","connection.json");var bundled=Path.Combine(root,"download","connection.json");Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
            var expected=new ConnectionSettings("https://example.supabase.co","sb_publishable_test_only_not_real",[]);
            File.WriteAllText(bundled,JsonSerializer.Serialize(expected));
            var imported=ConnectionStore.Load(installed,bundled);
            Assert(File.Exists(installed)&&imported.SupabaseUrl==expected.SupabaseUrl,"First launch must save the developer's public connection automatically");
            File.Delete(bundled);
            Assert(ConnectionStore.Load(installed,bundled).SupabasePublishableKey==expected.SupabasePublishableKey,"An update in a different folder must work without copying configuration");
            File.WriteAllText(bundled,JsonSerializer.Serialize(new ConnectionSettings("https://other.supabase.co","sb_publishable_other_distribution",[])));
            Assert(ConnectionStore.Load(installed,bundled).SupabaseUrl==expected.SupabaseUrl,"An update must preserve the technician's selected project");
            File.WriteAllText(installed,"broken JSON");
            Assert(ConnectionStore.Load(installed,bundled).SupabaseUrl=="https://other.supabase.co","A corrupt cache may recover from the configured distribution");
            File.WriteAllText(installed,"{}");File.WriteAllText(bundled,JsonSerializer.Serialize(expected with {SupabasePublishableKey="sb_secret_never_import"}));
            try{ConnectionStore.Load(installed,bundled);throw new Exception("Secret config imported");}catch(InvalidOperationException){}
            Assert(!File.ReadAllText(installed).Contains("sb_secret_"),"Only validated public settings may be persisted");
            Console.WriteLine("PASS: automatic first launch, update without copied files, existing project preservation, corrupt-cache recovery and secret-key refusal.");
        }finally{Directory.Delete(root,true);}
    }
    static async Task VerifyTechnicianAccount(){
        var transport=new AccountTransport();
        var settings=new ConnectionSettings("https://example.supabase.co","sb_publishable_test_only_not_real",[]);
        using var account=new TechnicianAccount(settings,transport);
        await account.Register("Tech@Example.com","test-password",CancellationToken.None);
        Assert(transport.Path=="/auth/v1/signup"&&transport.Email=="tech@example.com","Registration must target only the selected project and normalize email");
        await account.Resend("Tech@Example.com",CancellationToken.None);
        Assert(transport.Path=="/auth/v1/resend"&&transport.SignupType,"Resend must request signup confirmation without a password");
        transport.Error="email_not_confirmed";await Reject(()=>account.Check("tech@example.com","test-password",CancellationToken.None));
        transport.Error="invalid_credentials";await Reject(()=>account.Check("tech@example.com","wrong-password",CancellationToken.None));
        transport.Error="rate_limit";await Reject(()=>account.Resend("tech@example.com",CancellationToken.None));
        transport.Error="";await account.Check("tech@example.com","test-password",CancellationToken.None);
        transport.EmptySession=true;await Reject(()=>account.Check("tech@example.com","test-password",CancellationToken.None));
        Console.WriteLine("PASS: in-app signup, confirmation resend, project isolation, unconfirmed/wrong-password/rate-limit refusals and verified session checks.");
    }
    static async Task VerifyAdminAccount(){
        var transport=new AdminTransport();
        using var account=new TechnicianAccount(new ConnectionSettings("https://example.supabase.co","sb_publishable_test_only_not_real",[]),transport);
        await Reject(()=>account.CheckAdmin("admin@example.com","password",CancellationToken.None));
        transport.Admin=true;await account.CheckAdmin("admin@example.com","password",CancellationToken.None);
        transport.Revoked=true;await Reject(()=>account.CheckAdmin("admin@example.com","password",CancellationToken.None));
        Assert(transport.UserChecks==3,"Every admin login must fetch current server-controlled authorization.");
        var management=new Management();using var provisioner=new SupabaseProvisioner("test-owner-token",management);
        await provisioner.AssignAdministrator(Project,"Admin@Example.com",CancellationToken.None);
        Assert(management.AdminAssignments==1,"Admin assignment requires owner Management API authorization.");
        Console.WriteLine("PASS: admin sign-in rejects editable metadata, checks current server roles, rejects revoked sessions and assigns roles only through owner authorization.");
    }
    sealed class AdminTransport:HttpMessageHandler {
        public bool Admin,Revoked;public int UserChecks;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
            Assert(request.RequestUri!.Host=="example.supabase.co","Admin credentials stay on the selected project.");
            if(request.Method==HttpMethod.Post)return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(new {access_token="test-admin-token"})});
            Assert(request.RequestUri.AbsolutePath=="/auth/v1/user"&&request.Headers.Authorization?.Parameter=="test-admin-token","Admin role must be fetched with the verified session token.");
            UserChecks++;
            return Task.FromResult(new HttpResponseMessage(Revoked?HttpStatusCode.Unauthorized:HttpStatusCode.OK){Content=JsonContent.Create(new {app_metadata=new {galaxy_role=Admin?"admin":"technician"},user_metadata=new {galaxy_role="admin"}})});
        }
    }
    sealed class AccountTransport:HttpMessageHandler {
        public string Path="",Email="",Error="";public bool SignupType,EmptySession;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
            Assert(request.RequestUri!.Host=="example.supabase.co","Credentials must only reach the configured project");
            Assert(request.Headers.Authorization is null,"No administrator bearer token may accompany signup");
            Assert(request.Headers.GetValues("apikey").Single()=="sb_publishable_test_only_not_real","Only publishable app keys may be used");
            Path=request.RequestUri.AbsolutePath;
            var body=await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken:ct);Email=body.GetProperty("email").GetString()!;
            SignupType=body.TryGetProperty("type",out var type)&&type.GetString()=="signup";
            if(Path.EndsWith("resend"))Assert(!body.TryGetProperty("password",out _),"Resend must not transmit the password");
            return new HttpResponseMessage(Error==""?HttpStatusCode.OK:Error=="rate_limit"?HttpStatusCode.TooManyRequests:HttpStatusCode.BadRequest){Content=JsonContent.Create(Error!=""?new {error_code=Error}:Path.EndsWith("token")&&!EmptySession?(object)new {access_token="test-session"}:new {})};
        }
    }
    sealed class Management:HttpMessageHandler {
        public string Mode="";public bool Installed;public int Installs,Keys,Patches,AdminAssignments;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){
            Assert(r.RequestUri!.Host=="api.supabase.com","Owner authorization must never be sent to the project host");Assert(r.Headers.Authorization?.Parameter=="test-owner-token","Management API needs explicit owner authorization");
            var path=r.RequestUri.AbsolutePath;object result=new {};
            if(r.Method==HttpMethod.Get&&path=="/v1/projects")result=new[]{new {id=Project,name=Mode=="foreign"?"Another app":"Galaxy Control",status=Mode=="starting"?"INACTIVE":"ACTIVE_HEALTHY"}};
            else if(path.EndsWith("/database/query")){
                var body=await r.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken:ct);var sql=body.GetProperty("query").GetString();
                if(sql==SupabaseProvisioner.InspectSql)result=new[]{new {installed=Installed,occupied=Mode=="partial"}};
                else if(sql==SupabaseProvisioner.VerifySql)result=new[]{new {secure=Mode!="insecure"}};
                else if(sql!.StartsWith("update auth.users")){Assert(sql.Contains("raw_app_meta_data")&&sql.Contains("admin@example.com"),"Admin role must use server metadata and normalized email.");AdminAssignments++;result=new[]{new {id="test-admin-user"}};}
                else{Assert(sql=="TEST_INSTALL_SQL","Only embedded installation SQL may be sent");Installs++;}
            }else if(r.Method==HttpMethod.Patch){
                Patches++;var body=await r.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken:ct);
                if(path.EndsWith("/config/auth")){Assert(!body.GetProperty("mailer_autoconfirm").GetBoolean(),"Email confirmation must remain enabled");Assert(body.GetProperty("external_anonymous_users_enabled").GetBoolean(),"Customers must not need accounts");}
                else Assert(body.GetProperty("db_schema").GetString()=="public, graphql_public","Private directory schema must remain unexposed");
            }else if(r.Method==HttpMethod.Post&&path.EndsWith("/api-keys")){
                var body=await r.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken:ct);Assert(body.GetProperty("type").GetString()=="publishable","No elevated key may be requested");Assert(System.Text.RegularExpressions.Regex.IsMatch(body.GetProperty("name").GetString()!,"^[a-z_][a-z0-9_]*$"),"Management API key name must use lowercase identifiers");Keys++;result=new {api_key=Mode=="secret"?"sb_secret_must_never_be_saved":"sb_publishable_test_public_configuration_only"};
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
