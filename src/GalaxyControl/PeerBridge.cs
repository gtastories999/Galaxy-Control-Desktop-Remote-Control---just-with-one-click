using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Reflection;
using System.Text.Json;

namespace GalaxyControl;

// A local, non-navigable WebView2 runs the browser's maintained WebRTC stack.
// It has no desktop, filesystem, microphone or camera permissions of its own.
internal sealed class PeerBridge : IDisposable {
    readonly WebView2 view = new() { Size = new Size(1, 1), TabStop = false };
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event Action<JsonElement>? Message;
    public event Action? Failed;
    bool disposed;
    public async Task Initialize(Control parent, CancellationToken cancellationToken) {
        parent.Controls.Add(view);
        var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GalaxyControl", "WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
        await view.EnsureCoreWebView2Async(environment);
        cancellationToken.ThrowIfCancellationRequested();
        var core = view.CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = true;
        core.Settings.IsScriptEnabled = true;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += (_, e) => { if (e.Uri != "about:blank") { Console.WriteLine("Blocked transport navigation: " + e.Uri); e.Cancel = true; } };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.ProcessFailed += (_, _) => { if(!disposed) Failed?.Invoke(); };
        core.WebMessageReceived += (_, e) => {
            if (disposed) return;
            if (!ready.Task.IsCompleted) Console.WriteLine("Transport bootstrap message from: " + e.Source);
            if (e.Source != "about:blank") return;
            try {
                using var json = JsonDocument.Parse(e.WebMessageAsJson);
                var message = json.RootElement;
                if (message.GetProperty("type").GetString() == "ready") ready.TrySetResult();
                else if (message.GetProperty("type").GetString() == "bootstrap-error") ready.TrySetException(new InvalidOperationException("Local transport initialization failed: " + message.GetProperty("message").GetString()));
                else Message?.Invoke(message.Clone());
            } catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { Failed?.Invoke(); }
        };
        var navigated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.NavigationCompleted += (_, e) => {
            if (e.IsSuccess) navigated.TrySetResult();
            else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                navigated.TrySetException(new IOException("Local transport navigation failed: " + e.WebErrorStatus));
        };
        // Initialize an empty local document before injecting the bundled transport. This avoids
        // racing inline script execution against WebView2's initial navigation.
        core.Navigate("about:blank");
        await navigated.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        var script = "window.addEventListener('error', event => chrome.webview.postMessage({type:'bootstrap-error',message:event.message}));\n" + ReadResource("peer.js") + "\n" + ReadResource("bridge.js");
        await core.ExecuteScriptAsync(script);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        view.Visible = false;
    }
    static string ReadResource(string file) {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("GalaxyControl.Transport." + file)
            ?? throw new IOException("Missing embedded peer transport.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    public void Post(object message) {
        if (!disposed) view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }
    public void Dispose() {
        if (disposed) return;
        disposed = true;
        view.Dispose();
    }
}
