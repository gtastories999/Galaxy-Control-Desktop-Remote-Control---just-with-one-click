using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace GalaxyControl;

internal sealed record OperatorProject(string Ref,string Name,string Status);
internal sealed record ConnectionSettings(string SupabaseUrl,string SupabasePublishableKey,string[] StunUrls) {
    public void Validate(){
        if(!Uri.TryCreate(SupabaseUrl,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo!=""||uri.AbsolutePath!="/"||uri.Query!=""||uri.Fragment!=""||uri.Host.Contains("YOUR-PROJECT",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("A valid Supabase project URL is required.");
        if(string.IsNullOrWhiteSpace(SupabasePublishableKey)||!SupabasePublishableKey.StartsWith("sb_publishable_",StringComparison.Ordinal)||SupabasePublishableKey.Length<25)throw new InvalidOperationException("Only a public publishable app key can be saved.");
        if(StunUrls is null||StunUrls.Length>4||StunUrls.Any(u=>!Regex.IsMatch(u,@"^stun:[a-zA-Z0-9.-]+(?::\d+)?$")))throw new InvalidOperationException("Only direct-connection STUN servers are supported.");
    }
}
// Setup-only Management API client. The OAuth token is never persisted or sent to a project API.
internal sealed class SupabaseProvisioner : IDisposable {
    readonly HttpClient http;
    public const string InspectSql="select to_regprocedure('public.gc_exchange(text,jsonb)') is not null as installed, to_regnamespace('galaxy_private') is not null as occupied";
    public const string VerifySql="""
        select
          (select count(*)=4 and bool_and(c.relrowsecurity) from pg_class c join pg_namespace n on n.oid=c.relnamespace where n.nspname='galaxy_private' and c.relname in ('technicians','sessions','signals','lookup_limits'))
          and not has_function_privilege('anon','public.gc_exchange(text,jsonb)','execute')
          and has_function_privilege('authenticated','public.gc_exchange(text,jsonb)','execute')
          and (select not p.prosecdef from pg_proc p where p.oid='public.gc_exchange(text,jsonb)'::regprocedure)
          and not exists (select 1 from pg_class c join pg_namespace n on n.oid=c.relnamespace where n.nspname='galaxy_private' and c.relkind='r' and (has_table_privilege('anon',c.oid,'select') or has_table_privilege('authenticated',c.oid,'select'))) as secure
        """;
    public SupabaseProvisioner(string accessToken,HttpMessageHandler? handler=null){
        if(string.IsNullOrWhiteSpace(accessToken))throw new ArgumentException("Supabase account authorization is required.");
        http=handler is null?new HttpClient():new HttpClient(handler);
        http.BaseAddress=new Uri("https://api.supabase.com/v1/");http.Timeout=TimeSpan.FromSeconds(45);
        http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",accessToken);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GalaxyControl/1.0");
    }
    async Task<JsonElement> Request(HttpMethod method,string path,object? body,CancellationToken ct){
        using var request=new HttpRequestMessage(method,path);if(body is not null)request.Content=JsonContent.Create(body);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if(!response.IsSuccessStatusCode)throw new InvalidOperationException((int)response.StatusCode switch {401=>"Supabase authorization expired. Connect your account again.",403=>"Supabase did not grant the permissions required to configure this project.",429=>"Supabase is busy. Wait a moment and retry.",_=>"Supabase could not complete project setup. You can safely retry."});
        await using var stream=await response.Content.ReadAsStreamAsync(ct);using var limited=new MemoryStream();
        var buffer=new byte[8192];int read;while((read=await stream.ReadAsync(buffer,ct))>0){if(limited.Length+read>2000000)throw new IOException("Supabase setup response exceeded the size limit.");limited.Write(buffer,0,read);}
        if(limited.Length==0)return JsonSerializer.SerializeToElement(new {});
        using var doc=JsonDocument.Parse(limited.ToArray());return doc.RootElement.Clone();
    }
    public async Task<OperatorProject[]> Projects(CancellationToken ct){
        var data=await Request(HttpMethod.Get,"projects",null,ct);
        return data.EnumerateArray().Where(p=>Regex.Replace(p.GetProperty("name").GetString()??"","[^a-zA-Z0-9]","").Equals("GalaxyControl",StringComparison.OrdinalIgnoreCase))
            .Select(p=>new OperatorProject(p.TryGetProperty("ref",out var reference)?reference.GetString()!:p.GetProperty("id").GetString()!,p.GetProperty("name").GetString()!,p.GetProperty("status").GetString()!)).ToArray();
    }
    async Task<JsonElement> Query(string project,string sql,CancellationToken ct)=>await Request(HttpMethod.Post,$"projects/{project}/database/query",new {query=sql},ct);
    public async Task<ConnectionSettings> Configure(string projectRef,string schema,Action<string> progress,CancellationToken ct,ConnectionSettings? existing=null){
        if(!Regex.IsMatch(projectRef,"^[a-z0-9]{20}$"))throw new InvalidOperationException("Invalid Supabase project reference.");
        var projects=await Projects(ct);var project=projects.SingleOrDefault(p=>p.Ref==projectRef)??throw new InvalidOperationException("Select an accessible project named Galaxy Control.");
        for(var attempt=0;project.Status is "COMING_UP" or "RESTARTING"&&attempt<60;attempt++){
            progress("Waiting for your Galaxy Control project to finish starting…");await Task.Delay(3000,ct);
            project=(await Projects(ct)).SingleOrDefault(p=>p.Ref==projectRef)??throw new InvalidOperationException("The selected project is no longer accessible.");
        }
        if(project.Status!="ACTIVE_HEALTHY")throw new InvalidOperationException("Your Galaxy Control project is not ready. Check its status in Supabase and retry.");
        progress("Checking your Galaxy Control project…");
        var inspection=(await Query(projectRef,InspectSql,ct))[0];
        if(!inspection.GetProperty("installed").GetBoolean()){
            if(inspection.GetProperty("occupied").GetBoolean())throw new InvalidOperationException("This project contains an incomplete Galaxy Control installation. No existing data was overwritten.");
            progress("Creating the directory and access rules…");await Query(projectRef,schema,ct);
        }
        progress("Checking database access rules…");
        if(!(await Query(projectRef,VerifySql,ct))[0].GetProperty("secure").GetBoolean())throw new InvalidOperationException("Galaxy Control access rules could not be verified. Setup was stopped.");
        progress("Configuring customer and technician sign-in…");
        await Request(HttpMethod.Patch,$"projects/{projectRef}/config/auth",new {external_email_enabled=true,external_anonymous_users_enabled=true,disable_signup=false,mailer_autoconfirm=false},ct);
        // This project is dedicated to Galaxy Control. Private metadata is never exposed.
        await Request(HttpMethod.Patch,$"projects/{projectRef}/postgrest",new {db_schema="public, graphql_public"},ct);
        progress("Creating your public app configuration…");
        if(existing is not null&&existing.SupabaseUrl.TrimEnd('/')==$"https://{projectRef}.supabase.co"){existing.Validate();return existing;}
        var key=await Request(HttpMethod.Post,$"projects/{projectRef}/api-keys?reveal=true",new {type="publishable",name="galaxy_control",description="Public client access for Galaxy Control"},ct);
        var config=new ConnectionSettings($"https://{projectRef}.supabase.co",key.GetProperty("api_key").GetString()!,["stun:stun.l.google.com:19302"]);
        config.Validate();return config;
    }
    public void Dispose(){http.DefaultRequestHeaders.Authorization=null;http.Dispose();}
}
internal static class ConnectionStore {
    public static string LocalPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GalaxyControl","connection.json");
    public static ConnectionSettings Load()=>Load(LocalPath,Path.Combine(AppContext.BaseDirectory,"connection.json"));
    // A configured distribution imports its public settings once. Later generic updates
    // automatically reuse them, even when extracted into a different folder.
    internal static ConnectionSettings Load(string localPath,string distributionPath){
        foreach(var path in new[]{localPath,distributionPath}.Distinct()){
            try{
                var config=JsonSerializer.Deserialize<ConnectionSettings>(File.ReadAllText(path))??throw new JsonException();
                config.Validate();
                if(path!=localPath)Save(config,localPath);
                return config;
            }catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException){}
        }
        throw new InvalidOperationException("Connect your project in Galaxy Control to continue.");
    }
    public static bool TryLoad(out ConnectionSettings? config){
        try{config=Load();return true;}
        catch(InvalidOperationException){config=null;return false;}
    }
    public static void Save(ConnectionSettings config)=>Save(config,LocalPath);
    static void Save(ConnectionSettings config,string path){
        config.Validate();Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(staging,JsonSerializer.Serialize(config,new JsonSerializerOptions{WriteIndented=true}));File.Move(staging,path,true);}
        finally{if(File.Exists(staging))File.Delete(staging);}
    }
}
