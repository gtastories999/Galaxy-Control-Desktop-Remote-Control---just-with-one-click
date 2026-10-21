/* Direct-only WebRTC transport. This file is also exercised by the browser integration tests. */
(() => {
    "use strict";
    const MAX_FRAME = 2 * 1024 * 1024;
    const CHUNK = 12 * 1024;
    class GalaxyPeer {
        constructor({ role, stunUrls = [], emit }) {
            if (!["host", "technician"].includes(role)) throw new Error("Invalid peer role.");
            if (!Array.isArray(stunUrls) || stunUrls.some(url => typeof url !== "string" || !/^stun:[a-z0-9.-]+(?::\d+)?$/i.test(url))) {
                throw new Error("Only STUN discovery servers are allowed. TURN relays are disabled.");
            }
            this.role = role;
            this.emit = emit;
            this.closed = false;
            this.active = false;
            this.pendingCandidates = [];
            this.frameId = 0;
            this.inflight = null;
            this.receiving = null;
            this.lastHeartbeat = performance.now();
            this.pc = new RTCPeerConnection({ iceServers: stunUrls.length ? [{ urls: stunUrls }] : [], iceTransportPolicy: "all" });
            this.pc.onicecandidate = ({ candidate }) => {
                if (candidate && candidate.type !== "relay") this.emit({ type: "signal", signal: { type: "candidate", candidate: candidate.toJSON() } });
            };
            this.pc.onconnectionstatechange = () => {
                if (["failed", "disconnected", "closed"].includes(this.pc.connectionState)) this.fail("The direct connection ended.");
                else void this.maybeReady();
            };
            this.pc.ondatachannel = ({ channel }) => {
                if (this.role !== "technician") return this.fail("Unexpected peer channel.");
                this.attach(channel);
            };
        }
        async start() {
            if (this.role !== "host" || this.closed) return;
            this.attach(this.pc.createDataChannel("control", { ordered: true }));
            this.attach(this.pc.createDataChannel("screen", { ordered: true }));
            await this.pc.setLocalDescription(await this.pc.createOffer());
            if (!this.closed) this.emit({ type: "signal", signal: { type: "offer", sdp: this.pc.localDescription.sdp } });
        }
        attach(channel) {
            if (!["control", "screen"].includes(channel.label) || this[channel.label]) {
                channel.close(); return this.fail("Unexpected or duplicate peer channel.");
            }
            this[channel.label] = channel;
            channel.binaryType = "arraybuffer";
            channel.onopen = () => void this.maybeReady();
            channel.onclose = () => this.fail("The direct connection ended.");
            channel.onerror = () => this.fail("The direct connection failed.");
            channel.onmessage = event => {
                if (this.closed || !this.active) return;
                try {
                    if (channel.label === "control") this.receiveControl(event.data);
                    else this.receiveFrame(event.data);
                } catch { this.fail("Invalid peer data. The session was stopped."); }
            };
        }
        async signal(signal) {
            if (this.closed) return;
            if (signal.type === "offer" || signal.type === "answer") {
                const expected = this.role === "host" ? "answer" : "offer";
                if (signal.type !== expected || this.pc.remoteDescription || typeof signal.sdp !== "string" || signal.sdp.length > 32768 || / typ relay(?: |\r|\n|$)/i.test(signal.sdp)) throw new Error("Invalid direct session description.");
                await this.pc.setRemoteDescription({ type: signal.type, sdp: signal.sdp });
                for (const candidate of this.pendingCandidates) await this.pc.addIceCandidate(candidate);
                this.pendingCandidates.length = 0;
                if (this.role === "technician") {
                    await this.pc.setLocalDescription(await this.pc.createAnswer());
                    this.emit({ type: "signal", signal: { type: "answer", sdp: this.pc.localDescription.sdp } });
                }
            } else if (signal.type === "candidate") {
                const c = signal.candidate;
                if (!c || typeof c.candidate !== "string" || c.candidate.length > 2048 || / typ relay(?: |$)/i.test(c.candidate)) throw new Error("Invalid direct candidate.");
                if (this.pc.remoteDescription) await this.pc.addIceCandidate(c);
                else {
                    if (this.pendingCandidates.length >= 128) throw new Error("Too many connection candidates.");
                    this.pendingCandidates.push(c);
                }
            } else throw new Error("Unsupported signaling message.");
        }
        async maybeReady() {
            if (this.closed || this.active || this.checking || this.pc.connectionState !== "connected" ||
                this.control?.readyState !== "open" || this.screen?.readyState !== "open") return;
            this.checking = true;
            try {
                // Verify the selected route before enabling capture or input. No relayed route is accepted.
                const reports = await this.pc.getStats();
                let pair;
                reports.forEach(report => {
                    if (report.type === "transport" && report.selectedCandidatePairId) pair = reports.get(report.selectedCandidatePairId);
                });
                if (!pair) reports.forEach(report => {
                    if (report.type === "candidate-pair" && report.state === "succeeded" && report.nominated) pair = report;
                });
                const local = pair && reports.get(pair.localCandidateId);
                const remote = pair && reports.get(pair.remoteCandidateId);
                if (!local || !remote || [local, remote].some(candidate => !["host", "srflx", "prflx"].includes(candidate.candidateType))) {
                    return this.fail("A direct route could not be verified. No relay will be used.");
                }
                if (this.closed) return;
                this.active = true;
                this.lastHeartbeat = performance.now();
                this.emit({ type: "connected", route: { local: local.candidateType, remote: remote.candidateType, protocol: local.protocol }, encrypted: true });
                this.control.send(JSON.stringify({ type: "ping" }));
            } catch { this.fail("The direct route could not be verified."); }
            finally { this.checking = false; }
        }
        sendInput(input) {
            if (!this.active || this.role !== "technician") return;
            if (this.control.bufferedAmount >= 65536) return this.fail("The control connection is congested. Sharing has ended.");
            this.control.send(JSON.stringify(input));
        }
        receiveControl(text) {
            if (typeof text !== "string" || text.length > 2048) throw new Error("Invalid control packet.");
            const message = JSON.parse(text);
            if (message.type === "ping" || message.type === "pong") {
                this.lastHeartbeat = performance.now();
                if (message.type === "ping") this.control.send(JSON.stringify({ type: "pong" }));
            } else if (message.type === "stop") this.fail("The other person ended the session.");
            else if (message.type === "frame-ack" && this.role === "host") {
                if (this.inflight?.id === message.id) { this.inflight = null; this.emit({ type: "frame-ready" }); }
            } else if (message.type === "input" && this.role === "host") this.emit({ type: "input", input: message });
            else throw new Error("Unsupported control packet.");
        }
        sendFrame(base64) {
            if (!this.active || this.role !== "host" || this.inflight) return;
            if (typeof base64 !== "string" || base64.length > Math.ceil(MAX_FRAME / 3) * 4) return this.fail("Screen frame is too large.");
            const raw = atob(base64);
            if (raw.length === 0 || raw.length > MAX_FRAME) return this.fail("Invalid screen frame.");
            if (this.screen.bufferedAmount > 512 * 1024) { this.emit({ type: "frame-ready" }); return; }
            const id = this.frameId = (this.frameId + 1) >>> 0;
            this.inflight = { id, at: performance.now() };
            for (let offset = 0; offset < raw.length; offset += CHUNK) {
                const size = Math.min(CHUNK, raw.length - offset);
                const packet = new Uint8Array(12 + size);
                const header = new DataView(packet.buffer);
                header.setUint32(0, id); header.setUint32(4, raw.length); header.setUint32(8, offset);
                for (let i = 0; i < size; i++) packet[12 + i] = raw.charCodeAt(offset + i);
                this.screen.send(packet);
            }
        }
        receiveFrame(buffer) {
            if (this.role !== "technician" || !(buffer instanceof ArrayBuffer) || buffer.byteLength < 13 || buffer.byteLength > CHUNK + 12) throw new Error("Invalid screen packet.");
            const view = new DataView(buffer);
            const id = view.getUint32(0), total = view.getUint32(4), offset = view.getUint32(8);
            const chunk = new Uint8Array(buffer, 12);
            if (total < 1 || total > MAX_FRAME || offset + chunk.length > total) throw new Error("Invalid screen size.");
            if (offset === 0) this.receiving = { id, total, offset: 0, bytes: new Uint8Array(total), at: performance.now() };
            const frame = this.receiving;
            if (!frame || frame.id !== id || frame.total !== total || frame.offset !== offset) throw new Error("Invalid screen ordering.");
            frame.bytes.set(chunk, offset); frame.offset += chunk.length;
            if (frame.offset === total) {
                let raw = "";
                for (let start = 0; start < total; start += CHUNK) raw += String.fromCharCode(...frame.bytes.subarray(start, start + CHUNK));
                this.emit({ type: "frame", data: btoa(raw) });
                this.receiving = null;
                this.control.send(JSON.stringify({ type: "frame-ack", id }));
            }
        }
        tick() {
            if (!this.active || this.closed) return;
            const now = performance.now();
            if (now - this.lastHeartbeat > 8000 || (this.inflight && now - this.inflight.at > 8000) || (this.receiving && now - this.receiving.at > 8000)) return this.fail("The peer stopped responding. Sharing has ended.");
            if (this.control.bufferedAmount > 65536) return this.fail("The control connection is congested. Sharing has ended.");
            this.control.send(JSON.stringify({ type: "ping" }));
        }
        fail(message) {
            if (this.closed) return;
            this.close(false);
            this.emit({ type: "ended", message });
        }
        close(notify = true) {
            if (this.closed) return;
            if (notify && this.control?.readyState === "open") {
                try { this.control.send(JSON.stringify({ type: "stop" })); } catch { /* Closing locally always wins. */ }
            }
            this.closed = true; this.active = false; this.inflight = null; this.receiving = null;
            this.pendingCandidates.length = 0;
            this.pc.close();
        }
    }
    globalThis.GalaxyPeer = GalaxyPeer;
})();
