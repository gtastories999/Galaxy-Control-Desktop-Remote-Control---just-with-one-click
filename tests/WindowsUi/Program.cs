using System.Drawing.Imaging;
using System.Reflection;
using GalaxyControl;

internal static class Program {
    [STAThread] static void Main() {
        ApplicationConfiguration.Initialize();
        try {
            Directory.CreateDirectory("artifacts/ui");
            foreach(var scale in new[]{1f,1.25f,1.5f,2f})Verify(scale);
            Console.WriteLine("PASS: actual Windows UI, visible native role selector, role-specific copy, transparent button perimeters, embedded alpha logo and 100/125/150/200% layout snapshots.");
        }catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}
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
        window.StartPosition=FormStartPosition.Manual;window.Location=new Point(0,0);window.Show();Application.DoEvents();
        var controls=Descendants(window).ToArray();
        T Find<T>(string name) where T:Control => (T)controls.Single(c=>c.Name==name);
        var mode=Find<ComboBox>("connectionMode");
        Require(mode.Visible&&mode.Enabled&&mode.IsHandleCreated&&mode.Parent is not null,"Connection mode must be attached, visible and selectable.");
        Require(mode.Items.Count==2,"Both support roles must be available.");
        Require(mode.Width>=300*scale&&mode.Height>=mode.Font.Height+4,"Role selector must have a usable size.");
        mode.DroppedDown=true;Application.DoEvents();Require(mode.DroppedDown,"Native role dropdown must open.");mode.DroppedDown=false;
        var hint=Find<Label>("modeHint");var password=Find<TextBox>("technicianPassword");var connect=Find<Control>("connectButton");
        mode.SelectedIndex=0;Application.DoEvents();
        Require(!password.Visible&&hint.Text.Contains("stop sharing"),"Customer view must explain sharing and hide the password.");
        Require(connect.Text=="Connect to technician","Customer action must connect to a technician.");
        Snapshot(window,$"customer-{scale*100:0}");
        mode.SelectedIndex=1;Application.DoEvents();
        Require(password.Visible&&password.UseSystemPasswordChar,"Technician view must expose a masked password.");
        Require(hint.Text.Contains("verified technician account")&&!hint.Text.Contains("view your screen"),"Technician hint must describe account sign-in, not screen sharing.");
        Require(connect.Text=="Sign in & go online"&&Find<Control>("stopButton").Text=="Go offline","Technician actions must match the role.");
        Require(hint.Bottom<=connect.Top,"Role explanation must not overlap the primary action.");
        Require(password.Parent!.Bottom<=hint.Top,$"Password and explanation must not overlap at {scale*100}%. Password bottom={password.Parent.Bottom}, hint top={hint.Top}.");
        foreach(var button in controls.Where(c=>c is IButtonControl)) {
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
        mode.SelectedIndex=0;Application.DoEvents();Require(!password.Visible,"Switching back to Get help must hide technician credentials.");
        window.Close();
    }
    static void Snapshot(Form window,string name){
        using var bitmap=new Bitmap(window.Width,window.Height);window.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
        bitmap.Save(Path.Combine("artifacts/ui",name+".png"),ImageFormat.Png);
    }
}
