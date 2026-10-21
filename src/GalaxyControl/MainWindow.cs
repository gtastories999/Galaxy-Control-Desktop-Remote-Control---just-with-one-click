using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.WebSockets;
using System.Text.Json;
namespace GalaxyControl;
internal sealed class GoldButton : Button {
    protected override void OnPaint(PaintEventArgs e) {
        using var brush=new LinearGradientBrush(ClientRectangle,Color.FromArgb(255,225,83),Color.FromArgb(222,165,42),25);
        e.Graphics.FillRectangle(brush,ClientRectangle);
        TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,Color.FromArgb(39,34,20),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
    }
}
internal sealed class GalaxyLogo : Control {
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using var gold=new LinearGradientBrush(ClientRectangle,Color.FromArgb(255,230,80),Color.FromArgb(204,143,20),40);
        PointF[] star=[new(24,2),new(30,18),new(46,24),new(30,30),new(24,46),new(18,30),new(2,24),new(18,18)];
        e.Graphics.FillPolygon(gold,star);
        using var pen=new Pen(gold,2);e.Graphics.DrawEllipse(pen,5,12,38,23);
    }
}
internal sealed class MainWindow : Form {
    readonly TextBox email=new(), token=new(){UseSystemPasswordChar=true};
    readonly ComboBox mode=new(){DropDownStyle=ComboBoxStyle.DropDownList};
    readonly Label status=new(){AutoSize=false};
    readonly GoldButton connect=new(){Text="Connect to technician"};
    readonly Button stop=new(){Text="Stop sharing",Enabled=false}, accept=new(){Text="Accept support request",Visible=false};
    readonly PictureBox screen=new(){Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.FromArgb(24,26,32),TabStop=true};
    readonly SemaphoreSlim sendLock=new(1);
    ClientWebSocket? socket;
    CancellationTokenSource? lifetime;
    bool active,technician,busy;
    Form? viewer;
    long lastMove;
    public MainWindow() {
        Text="Galaxy Control"; ClientSize=new Size(520,650); MinimumSize=Size; MaximumSize=Size;
        BackColor=Color.FromArgb(250,250,247); Font=new Font("Segoe UI",11); StartPosition=FormStartPosition.CenterScreen;
        var brand=new Label {Text="Galaxy Control",Font=new Font("Segoe UI",25,FontStyle.Bold),ForeColor=Color.FromArgb(182,132,21),Bounds=new Rectangle(95,32,390,55)};
        var title=new Label {Text="A little help. One connection.",Bounds=new Rectangle(38,102,450,35),Font=new Font("Segoe UI",17,FontStyle.Bold)};
        var subtitle=new Label {Text="Connect with someone you trust.",Bounds=new Rectangle(38,145,450,30),ForeColor=Color.DimGray};
        mode.Items.AddRange(["Get help","I'm a technician"]); mode.SelectedIndex=0; mode.SetBounds(38,198,444,34);
        var emailLabel=new Label {Text="Technician email",Bounds=new Rectangle(38,250,440,26)};
        email.PlaceholderText="technician@example.com"; email.SetBounds(38,282,444,36);
        token.PlaceholderText="Technician access key"; token.SetBounds(38,330,444,36); token.Visible=false;
        var consent=new Label {Text="By connecting, you allow this technician to view your primary screen and control your mouse and keyboard until you stop sharing.",Bounds=new Rectangle(38,378,444,65),ForeColor=Color.DimGray,Font=new Font("Segoe UI",10)};
        connect.SetBounds(38,458,444,48); stop.SetBounds(38,516,216,40); accept.SetBounds(266,516,216,40);
        status.SetBounds(38,578,444,55); status.Text="Ready when you are.";
        Controls.Add(new GalaxyLogo {Bounds=new Rectangle(36,35,48,48)});
        Controls.AddRange([brand,title,subtitle,mode,emailLabel,email,token,consent,connect,stop,accept,status]);
        mode.SelectedIndexChanged+=(_,_)=>{token.Visible=mode.SelectedIndex==1;connect.Text=mode.SelectedIndex==1?"Go online":"Connect to technician";};
        connect.Click+=async(_,_)=>await Connect(); stop.Click+=(_,_)=>Disconnect();
        accept.Click+=async(_,_)=>{accept.Enabled=false;await SendSafe(new {type="accept"});};
        FormClosing+=(_,_)=>Disconnect();
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
    async Task Input(object message) {if(active&&technician) await SendSafe(message);}
    async Task SendSafe(object message) {try{await Send(JsonSerializer.SerializeToUtf8Bytes(message),WebSocketMessageType.Text,lifetime?.Token??default);}catch(Exception e) when(e is WebSocketException or OperationCanceledException or ObjectDisposedException){Disconnect();}}
    async Task Send(byte[] data,WebSocketMessageType type,CancellationToken ct) {
        await sendLock.WaitAsync(ct); try { if(socket?.State==WebSocketState.Open) await socket.SendAsync(new ArraySegment<byte>(data),type,true,ct); } finally {sendLock.Release();}
    }
    async Task Connect() {
        if(busy)return;
        if(!System.Net.Mail.MailAddress.TryCreate(email.Text.Trim(),out var address)||address.Address!=email.Text.Trim()){status.Text="Please enter a valid technician email.";return;}
        technician=mode.SelectedIndex==1;
        if(technician&&string.IsNullOrWhiteSpace(token.Text)){status.Text="Enter your technician access key.";return;}
        try {
            using var config=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"connection.json")));
            var endpoint=new Uri(config.RootElement.GetProperty("RelayUrl").GetString()!);
            if(endpoint.Scheme!="wss" || endpoint.Host=="YOUR-RELAY-DOMAIN") throw new InvalidOperationException("Configure a secure relay URL in connection.json first.");
            busy=true;socket=new ClientWebSocket(); lifetime=new CancellationTokenSource();
            if(technician) socket.Options.SetRequestHeader("Authorization","Bearer "+token.Text.Trim());
            var uri=new UriBuilder(endpoint){Query=$"role={(technician?"technician":"host")}&email={Uri.EscapeDataString(address.Address.ToLowerInvariant())}"};
            connect.Enabled=false;mode.Enabled=false;email.Enabled=false;token.Enabled=false;stop.Enabled=true;status.Text="Connecting…";
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(uri.Uri,deadline.Token);
            await Receive(lifetime.Token);
        } catch(Exception e) when(e is WebSocketException or OperationCanceledException or IOException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException or System.Runtime.InteropServices.ExternalException or ArgumentException) {status.Text=e is InvalidOperationException?e.Message:"Connection ended or unavailable. Check the relay and technician details.";}
        finally {if(active)status.Text="Session ended. Your computer is no longer shared.";busy=false;Disconnect(false);}
    }
    async Task Receive(CancellationToken ct) {
        var buffer=new byte[2*1024*1024];
        while(socket?.State==WebSocketState.Open) {
            using var data=new MemoryStream(); WebSocketReceiveResult part;
            do {part=await socket.ReceiveAsync(new ArraySegment<byte>(buffer),ct);if(part.MessageType==WebSocketMessageType.Close)return;
                if(data.Length+part.Count>buffer.Length)throw new IOException("Message too large.");data.Write(buffer,0,part.Count);
            } while(!part.EndOfMessage);
            if(part.MessageType==WebSocketMessageType.Binary && technician && active) {
                data.Position=0;using var frame=Image.FromStream(data);var previous=screen.Image;screen.Image=new Bitmap(frame);previous?.Dispose();continue;
            }
            if(part.MessageType!=WebSocketMessageType.Text)continue;
            using var json=JsonDocument.Parse(data.ToArray());var root=json.RootElement;var type=root.GetProperty("type").GetString();
            if(type=="input" && active && !technician){NativeInput.Apply(root);continue;}
            if(root.TryGetProperty("message",out var message))status.Text=message.GetString();
            if(type=="error")return;
            if(type=="request"){accept.Visible=true;accept.Enabled=true;}
            if(type=="connected") {
                active=true;TopMost=!technician;accept.Visible=false;BackColor=Color.FromArgb(255,247,218);
                if(technician) OpenViewer(); else _=CaptureScreen(ct);
            }
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
    async Task CaptureScreen(CancellationToken ct) {
        try {
            var bounds=Screen.PrimaryScreen!.Bounds;
            using var bitmap=new Bitmap(bounds.Width,bounds.Height);using var graphics=Graphics.FromImage(bitmap);
            var encoder=ImageCodecInfo.GetImageEncoders().Single(c=>c.MimeType=="image/jpeg");using var quality=new EncoderParameters(1);quality.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,45L);
            while(active&&!ct.IsCancellationRequested) {
                graphics.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size);
                using var data=new MemoryStream();bitmap.Save(data,encoder,quality);
                if(data.Length<=2*1024*1024)await Send(data.ToArray(),WebSocketMessageType.Binary,ct);
                await Task.Delay(160,ct);
            }
        }catch(Exception e) when(e is OperationCanceledException or WebSocketException or System.Runtime.InteropServices.ExternalException or IOException){Disconnect();}
    }
    void Disconnect(bool updateStatus=true) {
        active=false;TopMost=false;lifetime?.Cancel();socket?.Abort();socket?.Dispose();socket=null;
        NativeInput.ReleaseAll();
        var old=viewer;viewer=null;if(old is not null){old.Controls.Remove(screen);old.Close();old.Dispose();}
        screen.Image?.Dispose();screen.Image=null;
        connect.Enabled=!busy;mode.Enabled=true;email.Enabled=true;token.Enabled=true;stop.Enabled=false;accept.Visible=false;
        BackColor=Color.FromArgb(250,250,247);if(updateStatus)status.Text="Session ended. Your computer is no longer shared.";
    }
}
