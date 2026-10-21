"""Integration test of authorization, pairing, directional relay and teardown."""
import asyncio, hashlib, json, os, subprocess, urllib.request
import websockets

async def main():
    env = dict(os.environ, GALAXY_TECHNICIANS=json.dumps({'tech@example.com': hashlib.sha256(b'test-key').hexdigest()}))
    process = subprocess.Popen((['dotnet', os.environ['GALAXY_RELAY_DLL']] if 'GALAXY_RELAY_DLL' in os.environ else ['dotnet', 'run', '--no-build', '-c', 'Release', '--project', 'src/GalaxyControl.Relay']) + ['--urls', 'http://127.0.0.1:18765'], env=env, stdout=subprocess.DEVNULL)
    base = 'ws://127.0.0.1:18765/connect?email=tech@example.com&role='
    try:
        for _ in range(100):
            try:
                with urllib.request.urlopen('http://127.0.0.1:18765/health', timeout=.2) as response:
                    assert response.status == 200
                break
            except OSError:
                await asyncio.sleep(.1)
        else:
            raise AssertionError('Relay did not start')
        try:
            async with websockets.connect(base+'technician'):
                raise AssertionError('Unauthenticated technician admitted')
        except websockets.exceptions.InvalidStatus:
            pass
        async with websockets.connect(base+'host') as offline:
            assert json.loads(await offline.recv())['type'] == 'error'
        async with websockets.connect(base+'technician', additional_headers={'Authorization': 'Bearer test-key'}) as tech:
            assert json.loads(await tech.recv())['type'] == 'online'
            async with websockets.connect(base+'host') as host:
                assert json.loads(await host.recv())['type'] == 'waiting'
                assert json.loads(await tech.recv())['type'] == 'request'
                async with websockets.connect(base+'host') as busy:
                    assert json.loads(await busy.recv())['type'] == 'error'
                # Capture must be ignored before technician acceptance.
                await host.send(b'not-yet-shared')
                try:
                    await asyncio.wait_for(tech.recv(), .15)
                    raise AssertionError('Frame leaked before acceptance')
                except asyncio.TimeoutError:
                    pass
                await tech.send(json.dumps({'type': 'accept'}))
                assert json.loads(await host.recv())['type'] == 'connected'
                assert json.loads(await tech.recv())['type'] == 'connected'
                await host.send(b'jpeg-frame')
                assert await tech.recv() == b'jpeg-frame'
                command = json.dumps({'type': 'input', 'kind': 'key', 'key': 65, 'down': True})
                await tech.send(command)
                assert await host.recv() == command
                await host.send(json.dumps({'type': 'stop'}))
                try:
                    await asyncio.wait_for(tech.recv(), 2)
                    raise AssertionError('Technician remained connected')
                except websockets.exceptions.ConnectionClosed:
                    pass
        print('PASS: authentication, offline/busy, consent gate, frames, input, session teardown')
    finally:
        process.terminate()
        process.wait(timeout=10)

asyncio.run(main())
