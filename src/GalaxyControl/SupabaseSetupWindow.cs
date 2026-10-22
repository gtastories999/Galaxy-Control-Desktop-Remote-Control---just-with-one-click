using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
namespace GalaxyControl;
internal sealed class SupabaseSetupWindow : Form {
    readonly TextBox email=new(),password=new(){UseSystemPasswordChar=true};
    readonly ComboBox project=new(){DropDownStyle=ComboBoxStyle.DropDownList,Visible=false};
    readonly GoldButton link=new(){Text="Connect Supabase"};
    readonly RoundedButton create=new(){Text="Create Galaxy Control project"},cancel=new(){Text="Cancel"};
    readonly RoundedButton export=new(){Text="Export customer app",Visible=false};
    readonly Label feedback=new(){AutoSize=false};
    readonly ProgressBar progress=new(){Style=ProgressBarStyle.Marquee,Visible=false};
    readonly CancellationTokenSource lifetime=new(TimeSpan.FromMinutes(10));
    SupabaseProvisioner? provisioner;
    OperatorProject[] projects=[];
    bool working,disposed;
    public bool Configured {get;private set;}
    public string TechnicianEmail=>email.Text.Trim();
    public string TechnicianPassword=>password.Text;
    public SupabaseSetupWindow(string initialEmail,string initialPassword){
        Text="Galaxy Control · Connect Supabase";AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
        ClientSize=new Size(550,640);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;
        Font=new Font("Segoe UI",10);BackColor=Color.FromArgb(250,250,250);ForeColor=Color.FromArgb(35,35,35);
        Controls.Add(new GalaxyLogo{Bounds=new Rectangle(25,18,64,64)});
        Controls.Add(new Label{Text="Your account. Your project.",Bounds=new Rectangle(100,28,420,42),Font=new Font("Segoe UI",21,FontStyle.Bold)});
        Controls.Add(new Label{Text="Create a Supabase project named Galaxy Control. Then connect your account to set it up automatically.",Bounds=new Rectangle(30,82,490,52),ForeColor=Color.DimGray});
        create.SetBounds(30,142,490,44);Controls.Add(create);
        Controls.Add(new Label{Text="Technician email",Bounds=new Rectangle(30,204,490,23)});email.Text=initialEmail;email.PlaceholderText="you@example.com";email.SetBounds(30,232,490,32);
        Controls.Add(new Label{Text="Technician password",Bounds=new Rectangle(30,278,490,23)});password.Text=initialPassword;password.PlaceholderText="Choose a password (10+ characters)";password.SetBounds(30,306,490,32);
        Controls.Add(new Label{Text="Your Supabase account sign-in opens in your browser. Your technician password is used only with your own project.",Bounds=new Rectangle(30,352,490,48),ForeColor=Color.DimGray});
        project.SetBounds(30,408,490,32);link.SetBounds(30,456,490,48);progress.SetBounds(30,516,490,5);feedback.SetBounds(30,532,490,61);feedback.ForeColor=Color.DimGray;
        export.SetBounds(30,594,300,34);export.Click+=(_,_)=>ExportCustomer();
        cancel.SetBounds(350,594,170,34);Controls.AddRange([email,password,project,link,progress,feedback,cancel,export]);AcceptButton=link;
        create.Click+=(_,_)=>SupabaseAccountLink.OpenBrowser(new Uri("https://supabase.com/dashboard/new"));
        cancel.Click+=(_,_)=>Close();FormClosing+=(_,_)=>lifetime.Cancel();
        link.Click+=async(_,_)=>await SetUp();
        feedback.Text="No SQL scripts, project keys or IP addresses to copy.";
    }
    void Report(string text){if(disposed||IsDisposed)return;feedback.Text=text;feedback.Refresh();}
    async Task SetUp(){
        if(working)return;
        if(!MailAddress.TryCreate(TechnicianEmail,out var address)||address.Address!=TechnicianEmail||password.Text.Length<10){Report("Enter your technician email and a password of at least 10 characters.");return;}
        working=true;link.Enabled=false;email.Enabled=false;password.Enabled=false;create.Enabled=false;progress.Visible=true;
        try{
            if(provisioner is null){
                var providerPath=Path.Combine(AppContext.BaseDirectory,"setup-provider.json");
                if(!File.Exists(providerPath))throw new InvalidOperationException("This distribution's developer must configure their own Supabase OAuth connection first.");
                using var config=JsonDocument.Parse(File.ReadAllText(providerPath));
                if(!Uri.TryCreate(config.RootElement.GetProperty("BrokerUrl").GetString(),UriKind.Absolute,out var broker))throw new InvalidOperationException("This generic build has no shared OAuth provider. Its developer must register their own Supabase OAuth app and configure their own backend.");
                Report("Sign in to Supabase in your browser and authorize Galaxy Control…");
                var accountToken=await SupabaseAccountLink.Connect(broker,SupabaseAccountLink.OpenBrowser,lifetime.Token);
                provisioner=new SupabaseProvisioner(accountToken);accountToken="";
            }
            var selectedRef=project.SelectedIndex>=0&&project.SelectedIndex<projects.Length?projects[project.SelectedIndex].Ref:null;
            Report("Finding your Galaxy Control project…");projects=await provisioner.Projects(lifetime.Token);
            if(projects.Length==0){Report("Create a project named Galaxy Control in Supabase, wait until it is ready, then click Connect Supabase again.");return;}
            if(projects.Length>1&&selectedRef is null){project.Items.Clear();project.Items.AddRange(projects.Select(p=>(object)(p.Name+" · "+p.Ref)).ToArray());project.Visible=true;Report("More than one Galaxy Control project was found. Select the one you want to configure, then continue.");link.Text="Configure selected project";return;}
            var selected=projects.Length==1?projects[0]:projects.SingleOrDefault(p=>p.Ref==selectedRef)??throw new InvalidOperationException("Project selection changed. Reopen setup and select your project again.");
            using var stream=typeof(SupabaseSetupWindow).Assembly.GetManifestResourceStream("GalaxyControl.Setup.schema.sql")??throw new IOException("The setup schema is missing.");
            using var reader=new StreamReader(stream);var schema=await reader.ReadToEndAsync(lifetime.Token);
            ConnectionSettings? previous=null;
            try{previous=JsonSerializer.Deserialize<ConnectionSettings>(File.ReadAllText(ConnectionStore.CurrentPath));previous?.Validate();}catch(Exception e) when(e is IOException or JsonException or InvalidOperationException){previous=null;}
            var settings=await provisioner.Configure(selected.Ref,schema,Report,lifetime.Token,previous);
            lifetime.Token.ThrowIfCancellationRequested();ConnectionStore.Save(settings);Configured=true;export.Visible=true;cancel.Text="Done";link.Text="Project connected";
            Report("Preparing your technician account…");
            var ready=await TechnicianAccount.Ensure(settings,TechnicianEmail,password.Text,lifetime.Token);
            Report(ready?"Supabase is connected. Close this window and sign in to go online.":"Project configured. Confirm your technician email, then sign in to Galaxy Control. Your current network address updates when connecting.");
            link.Text="Setup complete";link.Enabled=false;cancel.Text="Done";
            provisioner.Dispose();provisioner=null;
        }catch(OperationCanceledException){Report("Setup was cancelled or timed out. You can safely retry.");}
        catch(Exception e) when(e is HttpRequestException or IOException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or System.ComponentModel.Win32Exception){Report(e is InvalidOperationException?e.Message:"Setup could not finish. Check your connection and try again.");}
        finally{if(!disposed&&!IsDisposed){working=false;progress.Visible=false;if(!Configured){link.Enabled=true;email.Enabled=true;password.Enabled=true;create.Enabled=true;}}}
    }
    void ExportCustomer(){
        using var save=new SaveFileDialog{Filter="ZIP archive|*.zip",FileName="Galaxy-Control-Customer.zip"};if(save.ShowDialog(this)!=DialogResult.OK)return;
        try{
            var settings=JsonSerializer.Deserialize<ConnectionSettings>(File.ReadAllText(ConnectionStore.CurrentPath))??throw new IOException();settings.Validate();
            var temporary=save.FileName+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{
                using(var zip=ZipFile.Open(temporary,ZipArchiveMode.Create)){
                    zip.CreateEntryFromFile(Path.Combine(AppContext.BaseDirectory,"GalaxyControl.exe"),"GalaxyControl.exe");
                    foreach(var name in new[]{"WebView2Loader.dll","Microsoft.Web.WebView2.Core.dll","Microsoft.Web.WebView2.WinForms.dll","runtimes/win-x64/native/WebView2Loader.dll"}){var file=Path.Combine(AppContext.BaseDirectory,name);if(File.Exists(file))zip.CreateEntryFromFile(file,name);}
                    using var writer=new StreamWriter(zip.CreateEntry("connection.json").Open());writer.Write(JsonSerializer.Serialize(settings,new JsonSerializerOptions{WriteIndented=true}));
                }
                File.Move(temporary,save.FileName,true);
            }finally{if(File.Exists(temporary))File.Delete(temporary);}
            Report("Customer app exported. Your customer opens the app and enters your technician email.");
        }catch(Exception e) when(e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException){Report("The customer app could not be exported. Choose a writable folder and retry.");}
    }
    protected override void Dispose(bool disposing){if(disposing&&!disposed){disposed=true;lifetime.Cancel();lifetime.Dispose();provisioner?.Dispose();}base.Dispose(disposing);}
}
