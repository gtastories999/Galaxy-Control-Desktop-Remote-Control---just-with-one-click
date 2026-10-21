using System.Runtime.InteropServices;
using System.Text.Json;
namespace GalaxyControl;
internal static class NativeInput {
    [StructLayout(LayoutKind.Sequential)] struct Mouse { public int x,y; public uint data,flags,time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct Keyboard { public ushort key,scan; public uint flags,time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct Payload { [FieldOffset(0)] public Mouse mouse; [FieldOffset(0)] public Keyboard keyboard; }
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint type; public Payload payload; }
    [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] input, int size);
    static readonly HashSet<ushort> keys = [];
    static readonly HashSet<int> buttons = [];
    static void Send(Input input) => SendInput(1, [input], Marshal.SizeOf<Input>());
    public static void Apply(JsonElement j) {
        var kind = j.GetProperty("kind").GetString();
        if (kind == "move") {
            var x=j.GetProperty("x").GetDouble(); var y=j.GetProperty("y").GetDouble();
            if (!double.IsFinite(x) || !double.IsFinite(y)) return;
            Send(new Input { payload = new Payload { mouse = new Mouse { x=(int)(Math.Clamp(x,0,1)*65535), y=(int)(Math.Clamp(y,0,1)*65535),flags=0x8001 } } });
        } else if (kind == "button") {
            var button=j.GetProperty("button").GetInt32(); var down=j.GetProperty("down").GetBoolean();
            if (button is not (0 or 1)) return;
            if(down) buttons.Add(button); else buttons.Remove(button);
            Send(new Input { payload=new Payload { mouse=new Mouse { flags=(uint)(button==0?(down?2:4):(down?8:16)) } } });
        } else if(kind=="wheel") {
            Send(new Input { payload=new Payload { mouse=new Mouse { flags=0x800, data=unchecked((uint)Math.Clamp(j.GetProperty("delta").GetInt32(),-1200,1200)) } } });
        } else if(kind=="key") {
            var key=j.GetProperty("key").GetInt32(); if(key<1 || key>254) return;
            var down=j.GetProperty("down").GetBoolean();
            if(down) keys.Add((ushort)key); else keys.Remove((ushort)key);
            Send(new Input { type=1,payload=new Payload { keyboard=new Keyboard { key=(ushort)key,flags=down?0u:2u } } });
        }
    }
    public static void ReleaseAll() {
        foreach(var key in keys) Send(new Input {type=1,payload=new Payload {keyboard=new Keyboard {key=key,flags=2}}});
        foreach(var b in buttons) Send(new Input {payload=new Payload {mouse=new Mouse {flags=b==0?4u:16u}}});
        keys.Clear(); buttons.Clear();
    }
}
