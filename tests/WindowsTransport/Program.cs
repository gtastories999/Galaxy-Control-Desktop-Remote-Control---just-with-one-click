using System.Text.Json;
using GalaxyControl;

internal static class Program {
    [STAThread] static void Main() {
        ApplicationConfiguration.Initialize();
        Application.Run(new SmokeWindow());
    }
}
sealed class SmokeWindow : Form {
    readonly TaskCompletionSource[] connected = [new(),new()];
    readonly TaskCompletionSource<string> frame = new();
    readonly TaskCompletionSource input = new(), ended = new();
    readonly PeerBridge[] peers = [new(),new()];
    readonly string[] ids = ["host-test","technician-test"];
    readonly System.Windows.Forms.Timer heartbeat = new() {Interval=500};
    bool stopping;
    public SmokeWindow() {
        Text="Galaxy Control Windows transport test"; Width=400;Height=200;
        Shown+=async(_,_)=>await Run();
        FormClosed+=(_,_)=>{heartbeat.Dispose();foreach(var peer in peers)peer.Dispose();};
    }
    void Message(int side,JsonElement message) {
        try {
            var type=message.GetProperty("type").GetString();
            if(type=="signal")peers[1-side].Post(new {type="signal",id=ids[1-side],signal=message.GetProperty("signal")});
            else if(type=="connected") {
                var route=message.GetProperty("route");
                if(route.GetProperty("local").GetString()!="host" || route.GetProperty("remote").GetString()!="host")throw new Exception("Unexpected relayed route.");
                connected[side].TrySetResult();
            }else if(type=="frame" && side==1)frame.TrySetResult(message.GetProperty("data").GetString()!);
            else if(type=="input" && side==0) {
                var command=message.GetProperty("input");
                if(command.GetProperty("key").GetInt32()!=65 || !command.GetProperty("down").GetBoolean())throw new Exception("Invalid direct input.");
                input.TrySetResult();
            }else if(type=="ended") {
                if(stopping&&side==1)ended.TrySetResult();
                else throw new Exception(message.GetProperty("message").GetString());
            }
        }catch(Exception e){Fail(e);}
    }
    void Fail(Exception e) {
        foreach(var tcs in connected)tcs.TrySetException(e);
        frame.TrySetException(e);input.TrySetException(e);ended.TrySetException(e);
    }
    async Task Run() {
        try {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            for(var i=0;i<2;i++) {
                var side=i;peers[side].Message+=message=>Message(side,message);
                peers[side].Failed+=()=>Fail(new Exception("WebView2 process failed."));
                await peers[side].Initialize(this,timeout.Token);
            }
            peers[1].Post(new {type="start",id=ids[1],role="technician",stunUrls=Array.Empty<string>()});
            peers[0].Post(new {type="start",id=ids[0],role="host",stunUrls=Array.Empty<string>()});
            await Task.WhenAll(connected.Select(t=>t.Task)).WaitAsync(timeout.Token);
            heartbeat.Tick+=(_,_)=>{for(var i=0;i<2;i++)peers[i].Post(new {type="tick",id=ids[i]});};
            heartbeat.Start();
            var bytes=Enumerable.Range(0,70000).Select(i=>(byte)(i%251)).ToArray();
            var encoded=Convert.ToBase64String(bytes);
            peers[0].Post(new {type="frame",id=ids[0],data=encoded});
            if(await frame.Task.WaitAsync(timeout.Token)!=encoded)throw new Exception("Direct frame bytes changed.");
            peers[1].Post(new {type="input",id=ids[1],input=new {type="input",kind="key",key=65,down=true}});
            await input.Task.WaitAsync(timeout.Token);
            heartbeat.Stop();stopping=true;
            peers[0].Post(new {type="close",id=ids[0]});
            await ended.Task.WaitAsync(timeout.Token);
            Console.WriteLine("PASS: real Windows WebView2 bridge, direct encrypted route, chunked frame, keyboard input and remote stop.");
            Environment.ExitCode=0;
        }catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}
        finally {Close();}
    }
}
