"""Authentication and signaling-only contract; desktop traffic must never be forwarded."""
import asyncio
import hashlib
import json
import os
import subprocess
import urllib.request
import websockets

BASE = 'ws://127.0.0.1:18765/connect?email=tech@example.com&role='
AUTH = {'Authorization': 'Bearer test-key'}
OFFER = 'v=0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\na=fingerprint:sha-256 00:11\r\n'
CANDIDATE = {'type': 'candidate', 'candidate': {'candidate': 'candidate:1 1 udp 2130706431 127.0.0.1 12345 typ host', 'sdpMid': '0', 'sdpMLineIndex': 0}}

async def closed(socket):
    try:
        await asyncio.wait_for(socket.recv(), 3)
        raise AssertionError('Connection stayed open or forbidden payload was forwarded')
    except websockets.exceptions.ConnectionClosed:
        pass

async def pair():
    tech = await websockets.connect(BASE+'technician', additional_headers=AUTH)
    assert json.loads(await tech.recv())['type'] == 'online'
    host = await websockets.connect(BASE+'host')
    assert json.loads(await host.recv())['type'] == 'waiting'
    assert json.loads(await tech.recv())['type'] == 'request'
    return tech, host

async def accept(tech, host):
    await tech.send(json.dumps({'type': 'accept'}))
    assert json.loads(await tech.recv())['type'] == 'negotiate'
    assert json.loads(await host.recv())['type'] == 'negotiate'

async def main():
    env = dict(os.environ, GALAXY_TECHNICIANS=json.dumps({'tech@example.com': hashlib.sha256(b'test-key').hexdigest()}))
    command = ['dotnet', os.environ['GALAXY_SIGNALING_DLL']] if 'GALAXY_SIGNALING_DLL' in os.environ else ['dotnet', 'run', '--no-build', '-c', 'Release', '--project', 'src/GalaxyControl.Signaling']
    process = subprocess.Popen(command + ['--urls', 'http://127.0.0.1:18765'], env=env, stdout=subprocess.DEVNULL)
    try:
        for _ in range(100):
            try:
                with urllib.request.urlopen('http://127.0.0.1:18765/health', timeout=.2) as response:
                    assert json.load(response)['mediaRelay'] is False
                break
            except OSError:
                await asyncio.sleep(.1)
        else:
            raise AssertionError('Signaling did not start')
        for header in ({}, {'Authorization':'Bearer wrong-key'}):
            try:
                async with websockets.connect(BASE+'technician', additional_headers=header):
                    raise AssertionError('Unauthenticated technician admitted')
            except websockets.exceptions.InvalidStatus:
                pass
        async with websockets.connect(BASE+'host') as offline:
            assert json.loads(await offline.recv())['type'] == 'error'
        tech,host = await pair()
        async with tech,host:
            async with websockets.connect(BASE+'host') as busy:
                assert json.loads(await busy.recv())['type'] == 'error'
            await accept(tech,host)
            # Unknown extra fields are stripped rather than passed through the signaling service.
            await host.send(json.dumps({'type':'offer','sdp':OFFER,'frame':'must not pass'}))
            assert json.loads(await tech.recv()) == {'type':'offer','sdp':OFFER}
            await tech.send(json.dumps({'type':'answer','sdp':OFFER}))
            assert json.loads(await host.recv()) == {'type':'answer','sdp':OFFER}
            await host.send(json.dumps(CANDIDATE))
            assert json.loads(await tech.recv()) == CANDIDATE
            await tech.send(json.dumps({'type':'stop'}))
            await closed(host)
        # Every forbidden payload closes both ends; there is no legacy relay path.
        for payload in [b'jpeg-frame', {'type':'input','kind':'key','key':65,'down':True}, {'type':'frame','data':'abc'}, {'type':'candidate','candidate':{'candidate':'candidate:1 1 udp 1 127.0.0.1 9000 typ relay','sdpMid':'0','sdpMLineIndex':0}}]:
            tech,host=await pair()
            async with tech,host:
                await accept(tech,host)
                await host.send(payload if isinstance(payload,bytes) else json.dumps(payload))
                await closed(tech)
        # Connection data must not be processed until the technician accepts.
        tech,host=await pair()
        async with tech,host:
            await host.send(json.dumps({'type':'offer','sdp':OFFER}))
            await closed(tech)
        print('PASS: technician authentication, offline/busy, SDP/ICE forwarding, field stripping, consent, stop; binary/frame/input/TURN relay rejected')
    finally:
        process.terminate()
        process.wait(timeout=10)

asyncio.run(main())
