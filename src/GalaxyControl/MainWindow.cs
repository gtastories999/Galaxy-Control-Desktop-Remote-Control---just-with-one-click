using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
namespace GalaxyControl;
internal class RoundedButton : Button {
    protected bool Hovered;
    public bool Gold { get; set; }
    public RoundedButton() {
        FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=0; Cursor=Cursors.Hand;
        Font=new Font("Segoe UI",10,FontStyle.Bold);
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);
    }
    internal static GraphicsPath Shape(RectangleF r,float radius) {
        var p=new GraphicsPath();float d=radius*2;
        p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);
        p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
    }
    protected override void OnMouseEnter(EventArgs e){Hovered=true;Invalidate();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e){Hovered=false;Invalidate();base.OnMouseLeave(e);}
    protected override void OnPaint(PaintEventArgs e) {
        var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
        using var path=Shape(new RectangleF(1,1,Width-3,Height-3),14);
        using var fill=new LinearGradientBrush(ClientRectangle,
            !Enabled?Color.FromArgb(239,239,235):Gold?Color.FromArgb(255,231,119):Hovered?Color.FromArgb(247,245,236):Color.White,
            !Enabled?Color.FromArgb(232,232,227):Gold?Color.FromArgb(232,178,47):Color.FromArgb(250,250,247),35);
        g.FillPath(fill,path);using var border=new Pen(Gold?Color.FromArgb(223,177,57):Color.FromArgb(224,226,221));g.DrawPath(border,path);
        TextRenderer.DrawText(g,Text,Font,ClientRectangle,Enabled?Color.FromArgb(44,40,28):Color.FromArgb(143,144,136),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
        if(Focused){using var focus=new Pen(Color.FromArgb(135,101,24));focus.DashStyle=DashStyle.Dot;using var inner=Shape(new RectangleF(5,5,Width-11,Height-11),10);g.DrawPath(focus,inner);}
    }
}
internal sealed class GoldButton : RoundedButton { public GoldButton(){Gold=true;} }
internal sealed class Surface : Panel {
    public Surface(){DoubleBuffered=true;BackColor=Color.FromArgb(247,248,245);}
    protected override void OnPaint(PaintEventArgs e){
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using var shadow=RoundedButton.Shape(new RectangleF(2,5,Width-5,Height-7),24);
        using var shade=new SolidBrush(Color.FromArgb(232,234,227));e.Graphics.FillPath(shade,shadow);
        using var shape=RoundedButton.Shape(new RectangleF(1,1,Width-5,Height-8),24);
        e.Graphics.FillPath(Brushes.White,shape);using var line=new Pen(Color.FromArgb(232,234,227));e.Graphics.DrawPath(line,shape);
    }
}
internal sealed class GalaxyLogo : Control {
    protected override void OnPaint(PaintEventArgs e) {
        var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
        g.ScaleTransform(Width/64f,Height/64f);
        using var tile=RoundedButton.Shape(new RectangleF(3,2,57,57),17);
        using var tileFill=new LinearGradientBrush(new Rectangle(0,0,64,64),Color.FromArgb(255,245,185),Color.FromArgb(218,163,31),55);
        using var shadow=RoundedButton.Shape(new RectangleF(4,7,57,55),17);
        using var dark=new SolidBrush(Color.FromArgb(187,132,21));g.FillPath(dark,shadow);g.FillPath(tileFill,tile);
        PointF[] star=[new(32,10),new(38,25),new(52,31),new(38,37),new(32,52),new(26,37),new(12,31),new(26,25)];
        g.TranslateTransform(0,3);using var depth=new SolidBrush(Color.FromArgb(155,103,12));g.FillPolygon(depth,star);g.TranslateTransform(0,-3);
        using var gold=new LinearGradientBrush(new Rectangle(12,10,40,42),Color.FromArgb(255,255,231),Color.FromArgb(239,187,52),50);g.FillPolygon(gold,star);
        using var facet=new SolidBrush(Color.FromArgb(217,155,25));g.FillPolygon(facet,[new PointF(32,31),new(32,52),new(38,37),new(52,31)]);
        using var orbit=new Pen(Color.FromArgb(255,255,232),1.6f);g.DrawArc(orbit,9,21,46,23,-28,245);
    }
}
internal sealed class MainWindow : Form {
    readonly TextBox email=new(), token=new(){UseSystemPasswordChar=true};
    readonly ComboBox mode=new(){DropDownStyle=ComboBoxStyle.DropDownList};
    readonly Label status=new(){AutoSize=false};
    readonly GoldButton connect=new(){Text="Connect to technician"};
    readonly RoundedButton stop=new(){Text="Stop sharing",Enabled=false}, accept=new(){Text="Accept support request",Visible=false};
    readonly PictureBox screen=new(){Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.FromArgb(24,26,32),TabStop=true};
    SupabaseDirectory? directory;
    CancellationTokenSource? lifetime;
    bool active,technician,busy,negotiating,framePending;
    PeerBridge? peer;
    string sessionId = "";
    string[] stunUrls = [];
    DateTime negotiationStarted;
    readonly System.Windows.Forms.Timer heartbeat = new() { Interval = 1000 };
    Form? viewer;
    long lastMove;
    public MainWindow() {
        Text="Galaxy Control"; ClientSize=new Size(600,760); FormBorderStyle=FormBorderStyle.FixedSingle; MaximizeBox=false;
        AutoScaleMode=AutoScaleMode.Dpi; BackColor=Color.FromArgb(247,248,245); ForeColor=Color.FromArgb(31,36,32);
        Font=new Font("Segoe UI",11); StartPosition=FormStartPosition.CenterScreen;
        var brand=new Label {Text="Galaxy Control",Font=new Font("Segoe UI",21,FontStyle.Bold),Bounds=new Rectangle(112,37,390,40)};
        var brandNote=new Label {Text="A little help. A world closer.",Bounds=new Rectangle(114,80,390,25),ForeColor=Color.FromArgb(112,118,108),Font=new Font("Segoe UI",10)};
        Controls.Add(new GalaxyLogo {Bounds=new Rectangle(36,35,64,64)}); Controls.AddRange([brand,brandNote]);
        var card=new Surface {Bounds=new Rectangle(28,132,544,562)};Controls.Add(card);
        Label Copy(string text,int y,int height,int size=10,bool bold=false) {
            var label=new Label {Text=text,Bounds=new Rectangle(30,y,480,height),BackColor=Color.White,Font=new Font("Segoe UI",size,bold?FontStyle.Bold:FontStyle.Regular),ForeColor=bold?Color.FromArgb(31,36,32):Color.FromArgb(106,113,104)};
            card.Controls.Add(label);return label;
        }
        var title=Copy("Good help starts here.",27,42,24,true);
        var subtitle=Copy("Your technician is just one connection away.",75,30,11);
        Copy("HOW WOULD YOU LIKE TO CONNECT?",122,24,9,true);
        mode.Items.AddRange(["Get help","I'm a technician"]);mode.SelectedIndex=0;mode.SetBounds(30,153,480,34);mode.Font=new Font("Segoe UI",11);
        var emailLabel=Copy("Technician email",207,24,10,true);
        void Field(TextBox field,int y) {
            var border=new Panel {Bounds=new Rectangle(30,y,480,46),BackColor=Color.FromArgb(247,248,245),Padding=new Padding(13,10,13,10)};
            field.BorderStyle=BorderStyle.None;field.BackColor=border.BackColor;field.Dock=DockStyle.Fill;border.Controls.Add(field);card.Controls.Add(border);
        }
        email.PlaceholderText="technician@example.com";Field(email,236);
        token.PlaceholderText="Your password";Field(token,292);token.Parent!.Visible=false;
        var consent=Copy("Your technician can view your screen and control your mouse and keyboard. You can stop sharing at any time.",304,72,10);
        connect.SetBounds(30,390,480,54);stop.SetBounds(30,456,232,42);accept.SetBounds(274,456,236,42);
        card.Controls.AddRange([connect,stop,accept]);
        status.SetBounds(58,704,484,44);status.ForeColor=Color.FromArgb(106,113,104);status.Font=new Font("Segoe UI",9);status.Text="Ready when you are. You stay in control.";Controls.Add(status);
        void UpdateMode() {
            bool tech=mode.SelectedIndex==1;token.Parent!.Visible=tech;
            title.Text=tech?"Your support desk, ready.":"Good help starts here.";
            subtitle.Text=tech?"Sign in to receive and accept support requests.":"Your technician is just one connection away.";
            emailLabel.Text=tech?"Your technician email":"Technician email";
            email.PlaceholderText=tech?"you@example.com":"technician@example.com";
            consent.Top=tech?348:304;consent.Height=tech?40:72;
            consent.Text=tech?"Sign in with your verified technician account. You choose which support requests to accept.":"Your technician can view your screen and control your mouse and keyboard. You can stop sharing at any time.";
            connect.Text=tech?"Sign in & go online":"Connect to technician";
            stop.Text=tech?"Go offline":"Stop sharing";
            if(!busy)status.Text=tech?"Use the account created during technician setup.":"Ready when you are. You stay in control.";
        }
        mode.SelectedIndexChanged+=(_,_)=>UpdateMode();UpdateMode();
        connect.Click+=async(_,_)=>await Connect(); stop.Click+=(_,_)=>Disconnect();
        accept.Click+=async(_,_)=>{
            accept.Enabled=false;
            try {if(directory is not null)await directory.Accept(lifetime!.Token);}
            catch(Exception e) when(e is HttpRequestException or OperationCanceledException or InvalidOperationException){EndWithMessage(e.Message);}
        };
        heartbeat.Tick+=(_,_)=>{
            if (negotiating && !active && DateTime.UtcNow-negotiationStarted>TimeSpan.FromSeconds(30)) {
                EndWithMessage("No direct route was found. Your network may block P2P. No relay was used."); return;
            }
            if(active) PostPeer(new {type="tick", id=sessionId});
        };
        FormClosing+=(_,_)=>{Disconnect();heartbeat.Dispose();};
        screen.MouseMove+=async(_,e)=>{
            if(Environment.TickCount64-lastMove<40) return; lastMove=Environment.TickCount64;
            var point=Coordinates(e.Location); if(point is {} p) await Input(new {type="input",kind="move",x=p.X,y=p.Y});
        };
        screen.MouseDown+=async(_,e)=>{screen.Focus();if(Coordinates(e.Location) is {} p){await Input(new {type="input",kind="move",x=p.X,y=p.Y});if(e.Button is MouseButtons.Left or MouseButtons.Right) await Input(new {type="input",kind="button",button=e.Button==MouseButtons.Left?0:1,down=true});}};
        screen.MouseUp+=async(_,e)=>{if(e.Button is MouseButtons.Left or MouseButtons.Right) await Input(new {type="input",kind="button",button=e.Button==MouseButtons.Left?0:1,down=false});};
        screen.MouseWheel+=async(_,e)=>await Input(new {type="input",kind="wheel",delta=e.Delta});
    }
    PointF? Coordinates(Point p) {
        if(screen.Image is not {} image) return null;
        var scale=Math.Min((double)screen.Width/image.Width,(double)screen.Height/image.Height);
        var w=image.Width*scale; var h=image.Height*scale; var x=(p.X-(screen.Width-w)/2)/w; var y=(p.Y-(screen.Height-h)/2)/h;
        return x<0||x>1||y<0||y>1?null:new PointF((float)x,(float)y);
    }
    Task Input(object message) {
        if(active&&technician) PostPeer(new {type="input", id=sessionId, input=message});
        return Task.CompletedTask;
    }
    void PostPeer(object message) {
        try { peer?.Post(message); }
        catch(Exception e) when(e is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException) {
            EndWithMessage("The peer transport stopped. Sharing has ended.");
        }
    }
    void EndWithMessage(string message) { Disconnect(false); status.Text=message; }
    async Task SendSafe(JsonElement message) {
        try {if(directory is not null)await directory.Send(message,lifetime!.Token);}
        catch(Exception e) when(e is HttpRequestException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) {
            if(busy && lifetime?.IsCancellationRequested!=true)EndWithMessage("Directory disconnected. Sharing has ended.");
        }
    }
    async Task Connect() {
        if(busy)return;
        if(!System.Net.Mail.MailAddress.TryCreate(email.Text.Trim(),out var address)||address.Address!=email.Text.Trim()){status.Text=mode.SelectedIndex==1?"Please enter your valid technician email.":"Please enter a valid technician email.";return;}
        technician=mode.SelectedIndex==1;
        if(technician&&string.IsNullOrWhiteSpace(token.Text)){status.Text="Enter your technician password.";return;}
        try {
            using var config=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"connection.json")));
            stunUrls=config.RootElement.GetProperty("StunUrls").EnumerateArray().Select(value=>value.GetString()??"").ToArray();
            if(stunUrls.Length>4 || stunUrls.Any(url=>!System.Text.RegularExpressions.Regex.IsMatch(url,@"^stun:[a-zA-Z0-9.-]+(?::\d+)?$")))
                throw new InvalidOperationException("Only STUN discovery servers are allowed. TURN relays are disabled.");
            busy=true;lifetime=new CancellationTokenSource(TimeSpan.FromHours(1));
            sessionId=Guid.NewGuid().ToString("N");
            connect.Enabled=false;mode.Enabled=false;email.Enabled=false;token.Enabled=false;stop.Enabled=true;status.Text="Preparing secure P2P transport…";
            peer=new PeerBridge();
            peer.Message+=async message=>await HandlePeerMessage(message);
            peer.Failed+=()=>EndWithMessage("The peer transport stopped. Sharing has ended.");
            await peer.Initialize(this,lifetime.Token);
            directory=new SupabaseDirectory(config.RootElement.GetProperty("SupabaseUrl").GetString()!,config.RootElement.GetProperty("SupabasePublishableKey").GetString()!,sessionId);
            directory.Message+=ProcessDirectoryMessage;
            status.Text=technician?"Signing in to your support desk…":"Connecting to your technician…";
            await directory.Run(technician,address.Address.ToLowerInvariant(),token.Text,lifetime.Token);
        } catch(WebView2RuntimeNotFoundException) {
            status.Text="Microsoft Edge WebView2 Runtime is required. Install it from Microsoft's official WebView2 download page.";
        } catch(Exception e) when(e is HttpRequestException or OperationCanceledException or IOException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException or TimeoutException or System.Runtime.InteropServices.ExternalException or ArgumentException) {
            if(lifetime?.IsCancellationRequested!=true)status.Text=e is InvalidOperationException?e.Message:"Connection ended or unavailable. Check the Supabase directory and technician details.";
        } finally {busy=false;Disconnect(false);lifetime?.Dispose();lifetime=null;}
    }
    void ProcessDirectoryMessage(JsonElement root) {
        var type=root.GetProperty("type").GetString();
        if(root.TryGetProperty("message",out var message))status.Text=message.GetString();
        if(type=="ended"){EndWithMessage(message.GetString()??"Session ended.");return;}
        if(type=="request"){accept.Visible=true;accept.Enabled=true;}
        else if(type=="negotiate") {
            if(negotiating)throw new IOException("Duplicate connection request.");
            negotiating=true;negotiationStarted=DateTime.UtcNow;accept.Visible=false;
            status.Text="Establishing a direct, encrypted P2P connection…";
            heartbeat.Start();
            PostPeer(new {type="start",id=sessionId,role=technician?"technician":"host",stunUrls});
        }else if(type is "offer" or "answer" or "candidate") {
            if(!negotiating)throw new IOException("Unexpected connection data.");
            PostPeer(new {type="signal",id=sessionId,signal=root.Clone()});
        }
    }
    async Task HandlePeerMessage(JsonElement message) {
        if(!busy || !message.TryGetProperty("id",out var messageId) || messageId.GetString()!=sessionId)return;
        try {
            var type=message.GetProperty("type").GetString();
            if(type=="signal")await SendSafe(message.GetProperty("signal"));
            else if(type=="connected" && negotiating && !active) {
                active=true;TopMost=!technician;BackColor=Color.FromArgb(255,247,218);
                status.Text="Connected directly · encrypted P2P · no relay";
                if(technician)OpenViewer();else _=CaptureScreen(lifetime!.Token,sessionId);
            }else if(type=="frame-ready")framePending=false;
            else if(type=="frame" && active && technician) {
                var encoded=message.GetProperty("data").GetString()!;
                if(encoded.Length>2800000)throw new IOException("Frame is too large.");
                using var data=new MemoryStream(Convert.FromBase64String(encoded));
                using var frame=Image.FromStream(data);
                if((long)frame.Width*frame.Height>8000000)throw new IOException("Frame dimensions are too large.");
                var previous=screen.Image;screen.Image=new Bitmap(frame);previous?.Dispose();
            }else if(type=="input" && active && !technician)NativeInput.Apply(message.GetProperty("input"));
            else if(type=="ended")EndWithMessage(message.GetProperty("message").GetString()??"Session ended.");
        }catch(Exception e) when(e is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or IOException or ArgumentException or System.Runtime.InteropServices.ExternalException) {
            EndWithMessage("Invalid peer data. Sharing has ended.");
        }
    }
    void OpenViewer() {
        viewer=new Form {Text="Galaxy Control · Remote desktop",Width=1200,Height=800,KeyPreview=true,BackColor=Color.White};
        var toolbar=new Button {Text="End session",Dock=DockStyle.Top,Height=42};toolbar.Click+=(_,_)=>Disconnect();
        viewer.Controls.Add(screen);viewer.Controls.Add(toolbar);
        viewer.KeyDown+=async(_,e)=>{if(e.Alt&&e.KeyCode==Keys.F4)return;e.SuppressKeyPress=true;await Input(new {type="input",kind="key",key=(int)e.KeyCode,down=true});};
        viewer.KeyUp+=async(_,e)=>{e.SuppressKeyPress=true;await Input(new {type="input",kind="key",key=(int)e.KeyCode,down=false});};
        viewer.Deactivate+=async(_,_)=>{for(var key=1;key<255;key++)await Input(new {type="input",kind="key",key,down=false});for(var button=0;button<2;button++)await Input(new {type="input",kind="button",button,down=false});};
        viewer.FormClosing+=(_,_)=>{if(active)Disconnect();};viewer.Show();
    }
    async Task CaptureScreen(CancellationToken ct,string id) {
        try {
            var bounds=Screen.PrimaryScreen!.Bounds;
            using var bitmap=new Bitmap(bounds.Width,bounds.Height);using var graphics=Graphics.FromImage(bitmap);
            var scale=Math.Min(1d,1600d/Math.Max(bounds.Width,bounds.Height));
            using var resized=new Bitmap((int)(bounds.Width*scale),(int)(bounds.Height*scale));
            using var resizeGraphics=Graphics.FromImage(resized);
            resizeGraphics.InterpolationMode=InterpolationMode.HighQualityBilinear;
            var encoder=ImageCodecInfo.GetImageEncoders().Single(c=>c.MimeType=="image/jpeg");using var quality=new EncoderParameters(1);quality.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,50L);
            while(active&&!ct.IsCancellationRequested&&sessionId==id) {
                if(!framePending) {
                    graphics.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size);
                    resizeGraphics.DrawImage(bitmap,0,0,resized.Width,resized.Height);
                    using var data=new MemoryStream();resized.Save(data,encoder,quality);
                    if(data.Length<=2*1024*1024) {
                        framePending=true;
                        PostPeer(new {type="frame",id,data=Convert.ToBase64String(data.ToArray())});
                    }
                }
                await Task.Delay(160,ct);
            }
        }catch(Exception e) when(e is OperationCanceledException or System.Runtime.InteropServices.ExternalException or IOException or ArgumentException) {
            if(sessionId==id && !ct.IsCancellationRequested)EndWithMessage("Screen capture stopped. Sharing has ended.");
        }
    }

    static async Task CloseDirectory(SupabaseDirectory oldDirectory) {
        try {await oldDirectory.Stop();}finally{oldDirectory.Dispose();}
    }
    void Disconnect(bool updateStatus=true) {
        active=false;negotiating=false;framePending=false;TopMost=false;heartbeat.Stop();
        var bridge=peer;peer=null;
        try {bridge?.Post(new {type="close",id=sessionId});}catch(Exception e) when(e is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException) { /* Continue closing native capture and the directory session. */ }
        sessionId="";
        lifetime?.Cancel();
        var oldDirectory=directory;directory=null;
        if(oldDirectory is not null){oldDirectory.Message-=ProcessDirectoryMessage;_=CloseDirectory(oldDirectory);}
        bridge?.Dispose();
        NativeInput.ReleaseAll();
        var old=viewer;viewer=null;if(old is not null){old.Controls.Remove(screen);old.Close();old.Dispose();}
        screen.Image?.Dispose();screen.Image=null;
        connect.Enabled=!busy;mode.Enabled=true;email.Enabled=true;token.Enabled=true;stop.Enabled=false;accept.Visible=false;
        BackColor=Color.FromArgb(250,250,247);if(updateStatus)status.Text="Session ended. Your computer is no longer shared.";
    }
}
