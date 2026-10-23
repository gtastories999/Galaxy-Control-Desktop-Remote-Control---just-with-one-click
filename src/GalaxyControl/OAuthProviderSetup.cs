using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace GalaxyControl;

// Bootstrap credentials live only in memory and are sent only to the official Management API.
internal sealed class OAuthProviderSetup : IDisposable {
    readonly HttpClient management;
    readonly HttpClient broker;
    public OAuthProviderSetup(string token,HttpMessageHandler? managementHandler=null,HttpMessageHandler? brokerHandler=null){
        if(string.IsNullOrWhiteSpace(token)||token.Any(char.IsWhiteSpace))throw new InvalidOperationException("Enter your Supabase personal access token.");
        management=new HttpClient(managementHandler??new HttpClientHandler{AllowAutoRedirect=false});
        management.BaseAddress=new Uri("https://api.supabase.com/v1/");management.Timeout=TimeSpan.FromMinutes(2);
        management.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
        broker=new HttpClient(brokerHandler??new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(20)};
    }
    public static Uri BrokerUrl(string projectRef){
        if(!Regex.IsMatch(projectRef,"^[a-z]{20}$"))throw new InvalidOperationException("Enter the 20-letter reference of your own Galaxy Control project.");
        return new Uri($"https://{projectRef}.supabase.co/functions/v1/connect-supabase/");
    }
    static Task Check(HttpResponseMessage response){
        if(!response.IsSuccessStatusCode)throw new InvalidOperationException((int)response.StatusCode switch {
            401=>"Your Supabase access token is invalid or expired.",403=>"Your access token needs project read, Edge Functions write and Secrets write permissions for this project.",
            429=>"Supabase is busy. Wait a moment and retry.",_=>"Supabase could not finish the OAuth backend setup. No local connection was saved. Check your project and retry."});
        return Task.CompletedTask;
    }
    public async Task<Uri> Deploy(string projectRef,string clientId,string clientSecret,string entrypoint,string handler,Action<string> progress,CancellationToken ct){
        var root=BrokerUrl(projectRef);
        if(string.IsNullOrWhiteSpace(clientId)||string.IsNullOrWhiteSpace(clientSecret))throw new InvalidOperationException("Register your OAuth application first, then enter its client ID and client secret.");
        progress("Checking your Galaxy Control project…");
        using(var response=await management.GetAsync($"projects/{projectRef}",ct)){
            await Check(response);var project=await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken:ct);
            if(project.GetProperty("name").GetString()!="Galaxy Control"||project.GetProperty("status").GetString()!="ACTIVE_HEALTHY")throw new InvalidOperationException("Use your own active project named Galaxy Control. No changes were made.");
        }
        // Never overwrite an existing publisher's broker or unrelated application secrets.
        using(var response=await management.GetAsync($"projects/{projectRef}/functions",ct)){
            await Check(response);var functions=await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken:ct);
            if(functions.EnumerateArray().Any(f=>f.GetProperty("slug").GetString()=="connect-supabase"))throw new InvalidOperationException("An OAuth backend already exists in this project. Use Connect existing backend instead; this assistant will not overwrite it.");
        }
        using(var response=await management.GetAsync($"projects/{projectRef}/secrets",ct)){
            await Check(response);var secrets=await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken:ct);
            if(secrets.EnumerateArray().Any(s=>s.GetProperty("name").GetString()?.StartsWith("GALAXY_SUPABASE_",StringComparison.Ordinal)==true))throw new InvalidOperationException("OAuth server settings already exist. Check the previous setup in Supabase before retrying; no credentials were overwritten.");
        }
        progress("Saving OAuth credentials in your server environment…");
        using(var response=await management.PostAsJsonAsync($"projects/{projectRef}/secrets",new[]{
            new {name="GALAXY_SUPABASE_CLIENT_ID",value=clientId.Trim()},
            new {name="GALAXY_SUPABASE_CLIENT_SECRET",value=clientSecret.Trim()},
            new {name="GALAXY_SUPABASE_BROKER_URL",value=root.AbsoluteUri}},ct))await Check(response);
        progress("Deploying your OAuth backend…");
        using var multipart=new MultipartFormDataContent();
        multipart.Add(new StringContent(JsonSerializer.Serialize(new{entrypoint_path="index.ts",verify_jwt=false,name="connect-supabase"})),"metadata");
        multipart.Add(new StringContent(entrypoint),"file","index.ts");multipart.Add(new StringContent(handler),"file","handler.mjs");
        using(var response=await management.PostAsync($"projects/{projectRef}/functions/deploy?slug=connect-supabase",multipart,ct))await Check(response);
        progress("Checking your deployed OAuth backend…");
        await Verify(root,ct);return root;
    }
    public async Task Verify(Uri root,CancellationToken ct){
        ProviderSettings.Validate(root);
        using var response=await broker.GetAsync(root.AbsoluteUri.TrimEnd('/')+"/status",ct);
        if(!response.IsSuccessStatusCode)throw new InvalidOperationException("The OAuth backend is not ready. Check its server secrets and retry Connect existing backend.");
        var status=await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken:ct);
        if(!status.TryGetProperty("ready",out var ready)||ready.ValueKind!=JsonValueKind.True)throw new InvalidOperationException("The OAuth backend did not confirm readiness.");
    }
    public void Dispose(){management.DefaultRequestHeaders.Authorization=null;management.Dispose();broker.Dispose();}
}

internal static class ProviderSettings {
    public static string LocalPath=>Path.Combine(Path.GetDirectoryName(ConnectionStore.LocalPath)!,"setup-provider.json");
    internal static void Validate(Uri uri){if(uri.Scheme!="https"||uri.UserInfo!=""||uri.Query!=""||uri.Fragment!="")throw new InvalidOperationException("Enter your own trusted HTTPS OAuth backend URL.");}
    public static Uri? Load()=>Load(LocalPath,Path.Combine(AppContext.BaseDirectory,"setup-provider.json"));
    internal static Uri? Load(params string[] paths){
        foreach(var path in paths)try{
            using var json=JsonDocument.Parse(File.ReadAllText(path));
            if(Uri.TryCreate(json.RootElement.GetProperty("BrokerUrl").GetString(),UriKind.Absolute,out var uri)){Validate(uri);return uri;}
        }catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException){}
        return null;
    }
    public static void Save(Uri uri)=>Save(uri,LocalPath);
    internal static void Save(Uri uri,string path){
        Validate(uri);Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temporary,JsonSerializer.Serialize(new{BrokerUrl=uri.AbsoluteUri}));File.Move(temporary,path,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
