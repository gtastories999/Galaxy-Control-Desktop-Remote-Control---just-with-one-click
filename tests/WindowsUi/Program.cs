using System.Drawing.Imaging;
using System.Reflection;
using GalaxyControl;

internal static class Program {
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern nint SendMessage(nint handle,int message,nint wParam,nint lParam);
    [STAThread] static void Main() {
        ApplicationConfiguration.Initialize();
        try {
            Directory.CreateDirectory("artifacts/ui");
            foreach(var scale in new[]{1f,1.25f,1.5f,2f})Verify(scale);
            using(var setup=(Form)Activator.CreateInstance(Assembly.Load("GalaxyControl").GetType("GalaxyControl.SupabaseSetupWindow",true)!,new object[]{"", ""})!){
                setup.Show();Application.DoEvents();
                Require(Descendants(setup).Any(c=>c.Visible&&c.Text=="Connect Supabase"),"Automatic setup must expose the account connection action.");
                Snapshot(setup,"supabase-account-setup");setup.Close();
            }
            using(var oauth=(Form)Activator.CreateInstance(Assembly.Load("GalaxyControl").GetType("GalaxyControl.OAuthProviderWindow",true)!)!){
                oauth.Show();Application.DoEvents();
                var fields=Descendants(oauth).OfType<TextBox>().ToArray();
                Require(fields.Count(t=>t.UseSystemPasswordChar)==2,"Bootstrap access token and OAuth client secret must be masked.");
                Require(fields.Where(t=>t.UseSystemPasswordChar).All(t=>t.Text==""),"Generic OAuth setup must not contain credentials.");
                var projectField=fields.Single(t=>t.Top==167);projectField.Text="abcdefghijklmnopqrst";Application.DoEvents();
                Require(fields.Single(t=>t.ReadOnly).Text=="https://abcdefghijklmnopqrst.supabase.co/functions/v1/connect-supabase/callback","OAuth callback must derive from the developer's own project.");
                Require(Descendants(oauth).Any(c=>c.Visible&&c.Text=="Deploy my OAuth backend"),"Generic Admin setup must expose backend deployment.");
                Snapshot(oauth,"admin-oauth-bootstrap");oauth.Close();
            }
            VerifyLoginSetup();
            var assembly=Assembly.Load("GalaxyControl");
            var settings=Activator.CreateInstance(assembly.GetType("GalaxyControl.ConnectionSettings",true)!,new object[]{"https://example.supabase.co","sb_publishable_test_only_not_real",Array.Empty<string>()})!;
            foreach(var pending in new[]{false,true}){
                using var registration=(Form)Activator.CreateInstance(assembly.GetType("GalaxyControl.TechnicianRegistrationWindow",true)!,new object[]{settings,"tech@example.com","",pending})!;
                registration.Show();Application.DoEvents();
                Require(Descendants(registration).Any(c=>c.Visible&&c.Text==(pending?"Check confirmation & go online":"Sign Up")),"Registration must expose the action for its current stage.");
                Require(Descendants(registration).Any(c=>c.GetType().Name=="GalaxyLogo"),"Registration must reuse the existing login logo.");
                Snapshot(registration,pending?"technician-confirmation":"technician-registration");registration.Close();
            }
            Console.WriteLine("PASS: actual Windows UI, visible native role selector, role-specific copy, transparent button perimeters, embedded alpha logo and 100/125/150/200% layout snapshots.");
        }catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}
    }
    static void VerifyLoginSetup(){
        var assembly=Assembly.Load("GalaxyControl");
        using var window=(Form)Activator.CreateInstance(assembly.GetType("GalaxyControl.MainWindow",true)!,true)!;
        window.GetType().GetField("signingUp",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,true);
        window.Show();Application.DoEvents();
        var controls=Descendants(window).ToArray();
        ((ComboBox)controls.Single(c=>c.Name=="connectionMode")).SelectedIndex=1;
        controls.Single(c=>c.Name=="technicianEmail").Text="tech@example.com";
        controls.Single(c=>c.Name=="technicianPassword").Text="test-password";
        ((IButtonControl)controls.Single(c=>c.Name=="connectButton")).PerformClick();Application.DoEvents();
        Require(!Application.OpenForms.Cast<Form>().Any(f=>f.GetType().Name=="SupabaseSetupWindow"),"Technicians must not be routed into developer setup.");
        Require(controls.Single(c=>c.Name=="connectionStatus").Text.Contains("administrator"),"Missing installation must explain who supplies the configured app.");
        ((ComboBox)controls.Single(c=>c.Name=="connectionMode")).SelectedIndex=2;
        bool opened=false;
        using var closer=new System.Windows.Forms.Timer{Interval=100};
        closer.Tick+=(_,_)=>{
            var dialog=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.GetType().Name=="SupabaseSetupWindow");
            if(dialog is null)return;
            opened=true;closer.Stop();Snapshot(dialog,"technician-login-first-run");dialog.Close();
        };
        closer.Start();((IButtonControl)controls.Single(c=>c.Name=="supabaseSetupButton")).PerformClick();Application.DoEvents();
        Require(opened,"Only the Admin setup action must open in-app project setup.");
        Require(controls.Single(c=>c.Name=="connectButton").Enabled,"Cancelling setup must leave sign-in usable.");
        window.Close();
    }
    static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    static IEnumerable<Control> Descendants(Control parent) {
        foreach(Control child in parent.Controls){yield return child;foreach(var nested in Descendants(child))yield return nested;}
    }
    static void Verify(float scale) {
        var assembly=Assembly.Load("GalaxyControl");
        using var window=(Form)Activator.CreateInstance(assembly.GetType("GalaxyControl.MainWindow",true)!,true)!;
        // Apply a proportional layout/font stress test in addition to native Windows rendering.
        if(scale!=1f){
            foreach(var control in Descendants(window).ToArray())control.Font=new Font(control.Font.FontFamily,control.Font.Size*scale,control.Font.Style);
            window.Scale(new SizeF(scale,scale));
        }
        window.StartPosition=FormStartPosition.Manual;window.Location=new Point(0,0);window.GetType().GetField("signingUp",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window,true);
        window.Show();Application.DoEvents();
        var controls=Descendants(window).ToArray();
        T Find<T>(string name) where T:Control => (T)controls.Single(c=>c.Name==name);
        var mode=Find<ComboBox>("connectionMode");
        Require(mode.Visible&&mode.Enabled&&mode.IsHandleCreated&&mode.Parent is not null,"Connection mode must be attached, visible and selectable.");
        Require(mode.Items.Count==3,"Customer, technician and admin roles must be available.");
        Require(mode.Width>=300*scale&&mode.Height>=mode.Font.Height+4,"Role selector must have a usable size.");
        mode.DroppedDown=true;Application.DoEvents();Require(mode.DroppedDown,"Native role dropdown must open.");mode.DroppedDown=false;
        var hint=Find<Label>("modeHint");var password=Find<TextBox>("technicianPassword");var connect=Find<Control>("connectButton");
        mode.SelectedIndex=0;Application.DoEvents();
        Require(!password.Visible&&hint.Text.Contains("stop sharing"),"Customer view must explain sharing and hide the password.");
        Require(connect.Text=="Connect to technician","Customer action must connect to a technician.");
        Snapshot(window,$"customer-{scale*100:0}");
        mode.SelectedIndex=1;Application.DoEvents();
        Require(password.Visible&&password.UseSystemPasswordChar,"Technician view must expose a masked password.");
        Require(hint.Text.Contains("Create your account")&&!hint.Text.Contains("view your screen"),"Technician hint must describe account sign-in, not screen sharing.");
        Require(Find<LinkLabel>("registerButton").Visible,"Technicians must be able to switch between Sign Up and Sign In.");
        Require(!Find<Control>("supabaseSetupButton").Visible,"Supabase setup must never appear in the technician view.");
        Require(connect.Text=="Sign Up"&&Find<Control>("stopButton").Text=="Go offline","Technician actions must match the role.");
        Require(hint.Bottom<=connect.Top,"Role explanation must not overlap the primary action.");
        Require(password.Parent!.Bottom<=hint.Top,$"Password and explanation must not overlap at {scale*100}%. Password bottom={password.Parent.Bottom}, hint top={hint.Top}.");
        foreach(var button in controls.Where(c=>c is IButtonControl&&c is not LinkLabel)) {
            Require(button is not Button,"Rounded controls must not use the native Windows BUTTON renderer.");
            Require(button.BackColor==Color.Transparent,"Button corners must show the parent surface.");
            using var rendered=new Bitmap(button.Width,button.Height);
            button.DrawToBitmap(rendered,new Rectangle(Point.Empty,rendered.Size));
            for(var x=0;x<rendered.Width;x++)for(var y=0;y<rendered.Height;y++) {
                if(x>3&&x<rendered.Width-4&&y>3&&y<rendered.Height-4)continue;
                var pixel=rendered.GetPixel(x,y);
                Require(pixel.A==0||pixel.R+pixel.G+pixel.B>300,"Button perimeter must not contain dark native borders.");
            }
        }
        foreach(var label in controls.OfType<Label>().Where(c=>c.Visible&&c.Name=="modeHint")) {
            var measured=TextRenderer.MeasureText(label.Text,label.Font,new Size(label.Width,int.MaxValue),TextFormatFlags.WordBreak);
            Require(measured.Height<=label.Height,"Role explanation must not be vertically clipped.");
        }
        using var stream=assembly.GetManifestResourceStream("GalaxyControl.Assets.galaxy-logo.png");
        Require(stream is not null,"The 3D logo must be embedded in the application.");
        using var logo=new Bitmap(stream!);Require(Image.IsAlphaPixelFormat(logo.PixelFormat)&&logo.GetPixel(0,0).A==0,"3D logo must have an actual transparent background.");
        Snapshot(window,$"technician-{scale*100:0}");
        var switchLink=Find<LinkLabel>("registerButton");
        void SwitchAccountView()=>typeof(LinkLabel).GetMethod("OnLinkClicked",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(switchLink,[new LinkLabelLinkClickedEventArgs(switchLink.Links[0])]);
        SwitchAccountView();Application.DoEvents();
        Require(connect.Text=="Sign In"&&switchLink.Text=="New here? Sign Up","Text link must switch to Sign In while keeping one primary button.");
        Require(!Find<Control>("supabaseSetupButton").Visible,"Switching to Sign In must not expose developer setup.");
        Snapshot(window,$"technician-sign-in-{scale*100:0}");
        SwitchAccountView();Application.DoEvents();
        Require(connect.Text=="Sign Up","Text link must switch back to Sign Up.");
        if(scale==1f){
            var clicks=0;connect.Click+=(_,_)=>clicks++;
            var center=(nint)((connect.Width/2)|((connect.Height/2)<<16));
            SendMessage(connect.Handle,0x0201,1,center);SendMessage(connect.Handle,0x0202,0,center);
            Require(clicks==1,"The new button must respond to a real Windows mouse click.");
            ((IButtonControl)connect).PerformClick();Require(clicks==2,"Default form action must remain clickable.");
            SendMessage(connect.Handle,0x0100,(nint)Keys.Space,0);SendMessage(connect.Handle,0x0101,(nint)Keys.Space,0);
            Require(clicks==3,"Space must activate the focused button once.");
        }
        mode.SelectedIndex=2;Application.DoEvents();
        Require(Find<Control>("supabaseSetupButton").Visible,"Admin must expose Connect Supabase.");
        Require(!Find<Control>("registerButton").Visible,"Admin must not expose technician registration.");
        Require(connect.Text=="Sign In"&&Find<Control>("supabaseSetupButton").Bottom<=connect.Top,"Admin setup must appear above Sign In without overlap.");
        Snapshot(window,$"admin-{scale*100:0}");
        mode.SelectedIndex=0;Application.DoEvents();Require(!password.Visible,"Switching back to Get help must hide technician credentials.");
        window.Close();
    }
    static void Snapshot(Form window,string name){
        using var bitmap=new Bitmap(window.Width,window.Height);window.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
        bitmap.Save(Path.Combine("artifacts/ui",name+".png"),ImageFormat.Png);
    }
}
