using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
namespace GalaxyControl;
internal sealed class TechnicianAccountException(string code,string message) : InvalidOperationException(message) {
    public string Code {get;}=code;
}
internal sealed class TechnicianAccount : IDisposable {
    readonly HttpClient http;
    public TechnicianAccount(ConnectionSettings settings,HttpMessageHandler? handler=null){
        settings.Validate();http=handler is null?new HttpClient():new HttpClient(handler);
        http.BaseAddress=new Uri(settings.SupabaseUrl.TrimEnd('/')+"/");http.Timeout=TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.Add("apikey",settings.SupabasePublishableKey);
    }
    async Task<JsonElement> Send(string path,object body,CancellationToken ct){
        using var response=await http.PostAsJsonAsync(path,body,ct);
        if(!response.IsSuccessStatusCode){
            string code="";try{using var error=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));if(error.RootElement.TryGetProperty("error_code",out var value))code=value.GetString()??"";}catch(JsonException){}
            throw new TechnicianAccountException(code,code switch {
                "email_not_confirmed"=>"Confirm your email first, then click Check confirmation & go online.",
                "invalid_credentials"=>"Email or password is incorrect. Choose Sign Up if you are new.",
                "signup_disabled"=>"Account registration is disabled in this distribution's Supabase project.",
                "weak_password"=>"Choose a stronger password that meets your project's password policy.",
                _=>(int)response.StatusCode switch {429=>"Too many attempts. Wait a minute before trying again.",401 or 403=>"The project rejected this request. Check its public app configuration.",_=>"Account request failed. Check your connection and the project's email settings, then retry."}});
        }
        using var document=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));return document.RootElement.Clone();
    }
    public async Task Register(string email,string password,CancellationToken ct)=>_ = await Send("auth/v1/signup",new {email=email.Trim().ToLowerInvariant(),password},ct);
    public async Task Resend(string email,CancellationToken ct)=>_ = await Send("auth/v1/resend",new {email=email.Trim().ToLowerInvariant(),type="signup"},ct);
    public async Task Check(string email,string password,CancellationToken ct){
        var result=await Send("auth/v1/token?grant_type=password",new {email=email.Trim().ToLowerInvariant(),password},ct);
        if(!result.TryGetProperty("access_token",out var token)||string.IsNullOrWhiteSpace(token.GetString()))throw new InvalidOperationException("Sign-in could not be verified. Please try again.");
    }
    public async Task CheckAdmin(string email,string password,CancellationToken ct){
        JsonElement session;
        try{session=await Send("auth/v1/token?grant_type=password",new {email=email.Trim().ToLowerInvariant(),password},ct);}
        catch(TechnicianAccountException e) when(e.Code is "invalid_credentials" or "email_not_confirmed"){
            throw new InvalidOperationException(e.Code=="email_not_confirmed"?"Confirm your admin email, then Sign In.":"Admin email or password is incorrect.");
        }
        if(!session.TryGetProperty("access_token",out var token)||string.IsNullOrWhiteSpace(token.GetString()))throw new InvalidOperationException("Admin sign-in could not be verified.");
        using var request=new HttpRequestMessage(HttpMethod.Get,"auth/v1/user");
        request.Headers.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",token.GetString());
        using var response=await http.SendAsync(request,ct);
        if(!response.IsSuccessStatusCode)throw new InvalidOperationException("Admin session could not be verified. Sign in again.");
        using var document=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var user=document.RootElement;
        // Never authorize from editable user_metadata or unverified local JWT claims.
        if(!user.TryGetProperty("app_metadata",out var metadata)||!metadata.TryGetProperty("galaxy_role",out var role)||role.GetString()!="admin")
            throw new InvalidOperationException("This account is not an administrator. Use the technician view, or ask your installation owner.");
    }
    public static async Task<bool> Ensure(ConnectionSettings settings,string email,string password,CancellationToken ct){
        using var account=new TechnicianAccount(settings);
        try{await account.Check(email,password,ct);return true;}
        catch(InvalidOperationException e) when(e.Message.StartsWith("Confirm your email",StringComparison.Ordinal)){return false;}
        catch(InvalidOperationException e) when(e.Message.StartsWith("Email or password",StringComparison.Ordinal)){}
        await account.Register(email,password,ct);return false;
    }
    public void Dispose()=>http.Dispose();
}
