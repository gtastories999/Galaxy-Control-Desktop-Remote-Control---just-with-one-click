using GalaxyControl;
using System.Net;
using System.Net.Http;
using System.Text.Json;

internal static class OAuthBootstrapTests {
    const string Project="abcdefghijklmnopqrst";
    static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    public static async Task Run(){
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"),"setup-provider.json");
        try{
            var root=OAuthProviderSetup.BrokerUrl(Project);ProviderSettings.Save(root,path);
            Assert(ProviderSettings.Load(path)==root,"Public provider URL must survive restart");
            using var config=JsonDocument.Parse(File.ReadAllText(path));Assert(config.RootElement.EnumerateObject().Count()==1,"Saved provider must contain only its public URL");
            foreach(var mode in new[]{"ok","foreign","existing","secrets","failed-deploy","not-ready"}){
                var management=new Management(mode);var readiness=new Readiness(mode);
                using var setup=new OAuthProviderSetup("test-bootstrap-token",management,readiness);
                bool accepted=false;try{await setup.Deploy(Project,"test-client","test-secret","TEST_ENTRYPOINT","TEST_HANDLER",_=>{},CancellationToken.None);accepted=true;}catch(InvalidOperationException){}
                Assert(accepted==(mode=="ok"),"Only successful deployment and verified readiness may be accepted: "+mode);
                if(mode is "foreign" or "existing" or "secrets")Assert(management.Writes==0,"Existing or unrelated resources must never be overwritten");
                if(mode=="ok")Assert(management.Writes==2&&readiness.Checked,"Credentials and deployment must precede a separate readiness check");
            }
            Console.WriteLine("PASS: public-only provider persistence, scoped deployment, refusal to overwrite, deployment/readiness failures and credential isolation.");
        }finally{Directory.Delete(Path.GetDirectoryName(path)!,true);}
    }
    sealed class Management(string mode):HttpMessageHandler {
        public int Writes;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
            Assert(request.RequestUri!.Host=="api.supabase.com","Bootstrap token must only go to the official Management API");
            Assert(request.Headers.Authorization?.Parameter=="test-bootstrap-token","Bootstrap request must use its ephemeral token");
            var path=request.RequestUri.AbsolutePath;Assert(path.StartsWith("/v1/projects/"+Project),"Only the explicit project may be accessed");
            object data=new{};
            if(request.Method==HttpMethod.Get){
                if(path.EndsWith("/functions"))data=mode=="existing"?new[]{new{slug="connect-supabase"}}:Array.Empty<object>();
                else if(path.EndsWith("/secrets"))data=mode=="secrets"?new[]{new{name="GALAXY_SUPABASE_CLIENT_ID"}}:Array.Empty<object>();
                else data=new{name=mode=="foreign"?"Other project":"Galaxy Control",status="ACTIVE_HEALTHY"};
            }else{
                Writes++;var body=await request.Content!.ReadAsStringAsync(ct);
                if(path.EndsWith("/secrets")){Assert(body.Contains("test-secret")&&body.Contains("GALAXY_SUPABASE_CLIENT_SECRET"),"Secrets belong in the server secrets API");}
                else{
                    Assert(!body.Contains("test-secret")&&!body.Contains("test-bootstrap-token"),"Deployment source must not embed credentials");
                    Assert(body.Contains("verify_jwt")&&body.Contains("false")&&body.Contains("handler.mjs")&&body.Contains("TEST_ENTRYPOINT"),"Deployment must include the embedded sources and OAuth gateway setting");
                    if(mode=="failed-deploy")return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(data))};
        }
    }
    sealed class Readiness(string mode):HttpMessageHandler {
        public bool Checked;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
            Checked=true;Assert(request.Headers.Authorization is null,"Management token must never reach the broker");
            Assert(request.RequestUri!.Host==Project+".supabase.co"&&request.RequestUri.AbsolutePath.EndsWith("/status"),"Readiness must target the selected broker");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(mode=="not-ready"?"{\"ready\":false}":"{\"ready\":true}")});
        }
    }
}
