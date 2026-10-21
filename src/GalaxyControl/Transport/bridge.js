/* Only the embedded about:blank page can call this WebView2 bridge. */
(() => {
    "use strict";
    let peer, id;
    const cancelled = new Set();
    let queue = Promise.resolve();
    chrome.webview.addEventListener("message", ({ data }) => {
        // Stop is synchronous and takes precedence over pending ICE/SDP operations.
        if (data.type === "close") { cancelled.add(data.id); if (data.id === id) { peer?.close(); peer = null; } return; }
        queue = queue.then(async () => {
            if (cancelled.has(data.id)) return;
            if (data.type === "start") {
                peer?.close(); id = data.id;
                peer = new GalaxyPeer({ role: data.role, stunUrls: data.stunUrls,
                    emit: message => chrome.webview.postMessage({ ...message, id: data.id }) });
                await peer.start();
            } else if (data.id === id && peer && !peer.closed) {
                if (data.type === "signal") await peer.signal(data.signal);
                else if (data.type === "frame") peer.sendFrame(data.data);
                else if (data.type === "input") peer.sendInput(data.input);
                else if (data.type === "tick") peer.tick();
            }
        }).catch(() => {
            peer?.fail("The direct connection could not be established.");
        });
    });
    chrome.webview.postMessage({ type: "ready" });
})();
