/* Real browser-to-browser WebRTC test; no media server or TURN service is involved. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require(process.env.GALAXY_PLAYWRIGHT || 'playwright');
const script = fs.readFileSync(path.join(__dirname, '../src/GalaxyControl/Transport/peer.js'), 'utf8');
const bridge = fs.readFileSync(path.join(__dirname, '../src/GalaxyControl/Transport/bridge.js'), 'utf8');
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function waitFor(condition, label, ms = 15000) {
    const until = Date.now() + ms;
    while (Date.now() < until) { if (condition()) return; await delay(25); }
    throw new Error('Timed out: ' + label);
}
(async () => {
    const server = http.createServer((req, res) => {
        res.setHeader('Content-Type', 'text/html');
        res.end('<!doctype html><title>Galaxy Control transport test</title><script>' + script + '</script>');
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const url = `http://127.0.0.1:${server.address().port}`;
    const browsers = [], errors = [];
    try {
        // Separate Chromium processes avoid sharing a peer connection implementation instance.
        for (let i = 0; i < 2; i++) browsers.push(await chromium.launch({ headless: true, args: ['--no-sandbox'], ...(process.env.GALAXY_CHROMIUM ? { executablePath: process.env.GALAXY_CHROMIUM } : {}) }));
        const pages = await Promise.all(browsers.map(browser => browser.newPage()));
        const events = [[], []], signals = [];
        const queues = [Promise.resolve(), Promise.resolve()];
        for (const [i, page] of pages.entries()) {
            page.on('pageerror', error => errors.push(error.message));
            await page.exposeFunction('report', event => {
                events[i].push(event);
                if (event.type === 'signal') {
                    assert.ok(['offer', 'answer', 'candidate'].includes(event.signal.type));
                    signals.push(event.signal);
                    queues[1 - i] = queues[1 - i].then(() => pages[1 - i].evaluate(signal => peer.signal(signal), event.signal)).catch(error => errors.push(error.message));
                }
            });
            await page.goto(url);
            await page.evaluate(role => { window.peer = new GalaxyPeer({ role, stunUrls: [], emit: event => void report(event) }); }, i ? 'technician' : 'host');
        }
        assert.equal(events.flat().length, 0, 'No capture or input is allowed before negotiation');
        await pages[0].evaluate(() => peer.start());
        await waitFor(() => events.every(list => list.some(event => event.type === 'connected')), 'direct connection');
        assert.deepEqual(errors, []);
        for (const list of events) {
            const route = list.find(event => event.type === 'connected');
            assert.equal(route.encrypted, true);
            assert.equal(route.route.local, 'host');
            assert.equal(route.route.remote, 'host');
        }
        const frame = Buffer.alloc(200000);
        for (let i = 0; i < frame.length; i++) frame[i] = i % 251;
        await pages[0].evaluate(frame => peer.sendFrame(frame), frame.toString('base64'));
        await waitFor(() => events[1].some(event => event.type === 'frame'), 'chunked frame');
        assert.deepEqual(Buffer.from(events[1].find(event => event.type === 'frame').data, 'base64'), frame);
        const inputs = [{ type: 'input', kind: 'key', key: 65, down: true }, { type: 'input', kind: 'key', key: 65, down: false }, { type: 'input', kind: 'move', x: .2, y: .8 }];
        await pages[1].evaluate(inputs => inputs.forEach(input => peer.sendInput(input)), inputs);
        await waitFor(() => events[0].filter(event => event.type === 'input').length === inputs.length, 'mouse and keyboard');
        assert.deepEqual(events[0].filter(event => event.type === 'input').map(event => event.input), inputs);
        // All application payload is on the direct channels; the signaling bridge only saw SDP/ICE.
        assert.equal(signals.filter(signal => signal.type === 'offer').length, 1);
        assert.equal(signals.filter(signal => signal.type === 'answer').length, 1);
        assert.ok(signals.every(signal => !['frame', 'input'].includes(signal.type)));
        assert.equal(await pages[0].evaluate(() => { try { new GalaxyPeer({role:'host',stunUrls:['turn:relay.example.com'],emit(){}}); return false; } catch { return true; } }), true);
        assert.equal(await pages[1].evaluate(async () => {
            try { await peer.signal({ type: 'candidate', candidate: { candidate: 'candidate:1 1 udp 1 127.0.0.1 9000 typ relay', sdpMid:'0',sdpMLineIndex:0 } }); return false; }
            catch { return true; }
        }), true);
        // A stop terminates both peer connections and prevents further input/capture.
        await pages[0].evaluate(() => peer.close());
        await waitFor(() => events[1].some(event => event.type === 'ended'), 'remote stop');
        const before = events[0].filter(event => event.type === 'input').length;
        await pages[1].evaluate(() => peer.sendInput({ type:'input',kind:'key',key:66,down:true }));
        await delay(100);
        assert.equal(events[0].filter(event => event.type === 'input').length, before);
        // Real native bridge protocol: a queued start must never revive a cancelled session.
        const bridgePage = await browsers[0].newPage();
        await bridgePage.goto(url);
        await bridgePage.evaluate(() => {
            window.bridgeEvents = [];
            window.chrome = window.chrome || {};
            window.chrome.webview = { addEventListener(_type, callback) { window.command = callback; }, postMessage(message) { bridgeEvents.push(message); } };
        });
        await bridgePage.addScriptTag({ content: bridge });
        await bridgePage.evaluate(() => {
            command({ data:{type:'start',id:'cancelled',role:'host',stunUrls:[]} });
            command({ data:{type:'close',id:'cancelled'} });
        });
        await delay(100);
        assert.deepEqual(await bridgePage.evaluate(() => bridgeEvents.map(event => event.type)), ['ready']);
        // Heartbeat failure closes the actual connection, even before ICE's own timeout expires.
        const heartbeatEvents = [[], []];
        const hbQueues = [Promise.resolve(), Promise.resolve()];
        for (const [i,page] of pages.entries()) {
            await page.exposeFunction('heartbeatReport', event => {
                heartbeatEvents[i].push(event);
                if(event.type==='signal') hbQueues[1-i]=hbQueues[1-i].then(()=>pages[1-i].evaluate(signal=>heartbeatPeer.signal(signal),event.signal)).catch(error=>errors.push(error.message));
            });
            await page.evaluate(role=>{window.heartbeatPeer=new GalaxyPeer({role,stunUrls:[],emit:event=>void heartbeatReport(event)});},i?'technician':'host');
        }
        await pages[0].evaluate(()=>heartbeatPeer.start());
        await waitFor(()=>heartbeatEvents.every(list=>list.some(event=>event.type==='connected')),'heartbeat test connection');
        await pages[0].evaluate(()=>{heartbeatPeer.lastHeartbeat=performance.now()-9000;heartbeatPeer.tick();});
        await waitFor(()=>heartbeatEvents.every(list=>list.some(event=>event.type==='ended')),'heartbeat teardown');
        assert.deepEqual(errors, []);
        console.log('PASS: two Chromium processes, direct selected route, encrypted channels, frame chunks, mouse/keyboard, TURN rejection, stop, cancelled bridge start and heartbeat teardown');
    } finally {
        await Promise.all(browsers.map(browser => browser.close()));
        await new Promise(resolve => server.close(resolve));
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
