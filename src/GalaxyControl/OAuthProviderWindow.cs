namespace GalaxyControl;
internal sealed class OAuthProviderWindow : Form {
    readonly TextBox project=new(),token=new(){UseSystemPasswordChar=true},clientId=new(),secret=new(){UseSystemPasswordChar=true},callback=new(){ReadOnly=true},existing=new();
    readonly Label feedback=new(){ForeColor=Color.DimGray};
    readonly GoldButton deploy=new(){Text="Deploy my OAuth backend"};
    readonly RoundedButton connect=new(){Text="Connect existing backend"};
    readonly CancellationTokenSource lifetime=new();
    bool working;
    public OAuthProviderWindow(){
        Text="Galaxy Control · Admin OAuth setup";AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
        ClientSize=new Size(640,790);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;AutoScroll=true;
        Font=new Font("Segoe UI",10);BackColor=Color.FromArgb(250,250,250);ForeColor=Color.FromArgb(35,35,35);
        Controls.Add(new GalaxyLogo{Bounds=new Rectangle(24,16,58,58)});
        Controls.Add(new Label{Text="Set up your own OAuth connection",Bounds=new Rectangle(94,24,525,48),Font=new Font("Segoe UI",18,FontStyle.Bold)});
        Controls.Add(new Label{Text="One-time setup for your distribution. Your access token and client secret are never saved on this computer or included in exports.",Bounds=new Rectangle(25,83,590,45)});
        Field("1. Your Galaxy Control project reference",project,140);
        Field("Redirect URL — register this exact URL in Supabase",callback,212);
        project.TextChanged+=(_,_)=>{try{callback.Text=new Uri(OAuthProviderSetup.BrokerUrl(project.Text.Trim()),"callback").AbsoluteUri;}catch(InvalidOperationException){callback.Text="Enter your project reference above.";}};
        var register=new RoundedButton{Text="2. Register OAuth app in Supabase",Bounds=new Rectangle(25,287,590,38)};
        register.Click+=(_,_)=>SupabaseAccountLink.OpenBrowser(new Uri("https://supabase.com/dashboard/org/_/apps"));Controls.Add(register);
        Controls.Add(new Label{Text="Request Projects Read, Database Write, Auth Write, Rest Write and Secrets Write. Return here with the client ID and client secret.",Bounds=new Rectangle(25,333,590,42),ForeColor=Color.DimGray});
        Field("OAuth client ID",clientId,384);Field("OAuth client secret (sent only to your server)",secret,456);
        Field("3. Personal access token (project read, functions write, secrets write)",token,528);
        deploy.SetBounds(25,603,590,43);deploy.Click+=async(_,_)=>await Configure(false);Controls.Add(deploy);
        existing.PlaceholderText="https://your-backend.example/connect-supabase/";existing.SetBounds(25,660,345,30);Controls.Add(existing);
        connect.SetBounds(380,657,235,37);connect.Click+=async(_,_)=>await Configure(true);Controls.Add(connect);
        feedback.SetBounds(25,708,590,60);Controls.Add(feedback);feedback.Text="Registering the OAuth app requires your Supabase browser session. No project or paid resource is created.";
        FormClosing+=(_,_)=>lifetime.Cancel();
    }
    void Field(string title,TextBox input,int y){Controls.Add(new Label{Text=title,Bounds=new Rectangle(25,y,590,24)});input.SetBounds(25,y+27,590,30);Controls.Add(input);}
    async Task Configure(bool reuse){
        if(working)return;working=true;deploy.Enabled=connect.Enabled=false;
        foreach(var input in new[]{project,token,clientId,secret,existing})input.Enabled=false;
        try{
            // Verification of an existing provider never uses Management API credentials.
            using var setup=new OAuthProviderSetup(reuse?"unused-local-placeholder":token.Text.Trim());
            Uri root;
            if(reuse){if(!Uri.TryCreate(existing.Text.Trim(),UriKind.Absolute,out root!))throw new InvalidOperationException("Enter your existing HTTPS OAuth backend URL.");await setup.Verify(root,lifetime.Token);}
            else{
                string Read(string name){using var stream=typeof(OAuthProviderWindow).Assembly.GetManifestResourceStream(name)??throw new IOException("The OAuth backend source is missing.");using var reader=new StreamReader(stream);return reader.ReadToEnd();}
                root=await setup.Deploy(project.Text.Trim(),clientId.Text.Trim(),secret.Text,Read("GalaxyControl.Setup.broker.index.ts"),Read("GalaxyControl.Setup.broker.handler.mjs"),text=>feedback.Text=text,lifetime.Token);
            }
            ProviderSettings.Save(root);DialogResult=DialogResult.OK;Close();
        }catch(OperationCanceledException){feedback.Text="Setup cancelled. Your local connection was not changed.";}
        catch(Exception e) when(e is InvalidOperationException or IOException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException or UnauthorizedAccessException){feedback.Text=e is InvalidOperationException?e.Message:"Setup could not finish. Check your project and connection, then retry. Server changes may already exist; use Connect existing backend after deployment.";}
        finally{token.Clear();secret.Clear();working=false;if(!IsDisposed){deploy.Enabled=connect.Enabled=true;foreach(var input in new[]{project,token,clientId,secret,existing})input.Enabled=true;}}
    }
    protected override void Dispose(bool disposing){if(disposing){lifetime.Cancel();token.Clear();secret.Clear();lifetime.Dispose();}base.Dispose(disposing);}
}
