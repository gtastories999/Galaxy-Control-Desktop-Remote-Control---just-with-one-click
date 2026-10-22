using System.Net.Http;
using System.Net.Mail;
using System.Text.Json;
namespace GalaxyControl;
internal sealed class TechnicianRegistrationWindow : Form {
    readonly TextBox email=new(),password=new(){UseSystemPasswordChar=true};
    readonly GoldButton create=new(){Text="Create technician account"};
    readonly RoundedButton check=new(){Text="Check confirmation & go online",Visible=false},resend=new(){Text="Resend confirmation email",Visible=false};
    readonly Label feedback=new(){AutoSize=false};
    readonly CancellationTokenSource lifetime=new();
    readonly TechnicianAccount account;
    bool working,disposed;
    public string TechnicianEmail=>email.Text.Trim();
    public string TechnicianPassword=>password.Text;
    public TechnicianRegistrationWindow(ConnectionSettings settings,string initialEmail,string initialPassword){
        account=new TechnicianAccount(settings);
        Text="Galaxy Control · Technician account";AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
        ClientSize=new Size(550,620);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;
        BackColor=Color.FromArgb(250,250,250);ForeColor=Color.FromArgb(35,35,35);Font=new Font("Segoe UI",10);
        Controls.Add(new GalaxyLogo{Bounds=new Rectangle(24,18,70,70)});
        Controls.Add(new Label{Text="Your technician account",Bounds=new Rectangle(108,30,420,48),Font=new Font("Segoe UI",20,FontStyle.Bold)});
        Controls.Add(new Label{Text="Register directly in Galaxy Control. Confirm your email, then return here to go online.",Bounds=new Rectangle(30,104,490,54)});
        Controls.Add(new Label{Text="Email",Bounds=new Rectangle(30,174,490,25)});email.Text=initialEmail;email.SetBounds(30,203,490,32);
        Controls.Add(new Label{Text="Password (10+ characters)",Bounds=new Rectangle(30,254,490,25)});password.Text=initialPassword;password.SetBounds(30,283,490,32);
        create.SetBounds(30,342,490,46);check.SetBounds(30,398,490,46);resend.SetBounds(30,454,490,42);
        feedback.SetBounds(30,516,490,85);feedback.Text="Your credentials go only to this app's configured Supabase project. No separate setup page is needed.";
        Controls.AddRange([email,password,create,check,resend,feedback]);AcceptButton=create;
        create.Click+=async(_,_)=>await Run(async()=>{
            if(!MailAddress.TryCreate(TechnicianEmail,out var address)||address.Address!=TechnicianEmail||password.Text.Length<10)throw new InvalidOperationException("Enter a valid email and a password of at least 10 characters.");
            await account.Register(TechnicianEmail,password.Text,lifetime.Token);
            email.ReadOnly=true;create.Visible=false;check.Visible=true;resend.Visible=true;AcceptButton=check;
            feedback.Text="Check your inbox and spam folder for the confirmation email. If this address already has an account, use its existing password. Then click Check confirmation & go online.";
        });
        check.Click+=async(_,_)=>await Run(async()=>{await account.Check(TechnicianEmail,password.Text,lifetime.Token);DialogResult=DialogResult.OK;Close();});
        resend.Click+=async(_,_)=>await Run(async()=>{await account.Resend(TechnicianEmail,lifetime.Token);feedback.Text="Confirmation requested. Check your inbox and spam folder, confirm the email, then return here.";});
        FormClosing+=(_,_)=>lifetime.Cancel();
    }
    async Task Run(Func<Task> action){
        if(working)return;working=true;create.Enabled=check.Enabled=resend.Enabled=false;email.Enabled=password.Enabled=false;
        feedback.Text="Contacting your project's account service…";
        try{await action();}
        catch(OperationCanceledException){if(!IsDisposed)feedback.Text="Account request cancelled or timed out. You can retry.";}
        catch(Exception e) when(e is HttpRequestException or JsonException or InvalidOperationException){if(!IsDisposed)feedback.Text=e is InvalidOperationException?e.Message:"Account service unavailable. Check your connection and retry.";}
        finally{working=false;if(!IsDisposed){create.Enabled=check.Enabled=resend.Enabled=true;email.Enabled=password.Enabled=true;}}
    }
    protected override void Dispose(bool disposing){if(disposing&&!disposed){disposed=true;lifetime.Cancel();account.Dispose();lifetime.Dispose();}base.Dispose(disposing);}
}
