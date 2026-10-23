"""Package a tested public Windows build with an operator's PUBLIC client configuration."""
import json
from pathlib import Path
import sys
from urllib.parse import urlparse
import zipfile

if len(sys.argv) != 4:
    raise SystemExit('Usage: python scripts/package-windows.py PUBLIC_BUILD.zip CONNECTION.json PRIVATE_OUTPUT.zip')
build, config_path, output = map(Path, sys.argv[1:])
config = json.loads(config_path.read_text())
url = urlparse(config.get('SupabaseUrl', ''))
key = config.get('SupabasePublishableKey', '')
if url.scheme != 'https' or not url.hostname or url.username or url.path not in ('','/') or url.query or url.fragment or 'YOUR-PROJECT' in url.hostname:
    raise SystemExit('A real HTTPS Supabase root URL is required.')
if not key.startswith('sb_publishable_') or len(key) < 25:
    raise SystemExit('Only a publishable client key may be packaged. Never use a secret or service-role key.')
if output.exists():
    raise SystemExit('Refusing to overwrite an existing package.')
notes = '''Galaxy Control - Windows x64\n\nExtract this entire ZIP before opening GalaxyControl.exe. The app saves its project connection automatically.\n\nTECHNICIAN: Open GalaxyControl.exe and select "I'm a technician".\nChoose Sign Up for a new account, or use the small Sign In link for an existing account.\nRegistration and email-confirmation checks happen in the app. No files to copy\nand no separate setup page. Keep the app open and accept the customer's request.\n\nADMIN: Select Admin. Connect Supabase appears above Sign In.\nProject setup requires the Supabase owner authorization. Technicians do not use this setup.\n\nCUSTOMER: Open GalaxyControl.exe, enter the technician's email and connect.\nNo account, password, IP address or port is needed for customers.\nUse Stop sharing to end access.\n\nMicrosoft Edge WebView2 Runtime must be installed:\nhttps://developer.microsoft.com/en-us/microsoft-edge/webview2/\n\nScreen and controls use a direct encrypted WebRTC connection. Supabase handles\nonly directory/session metadata. Some router/firewall combinations cannot form\na direct route; there is no relay fallback. Primary screen and normal-privilege\nWindows apps only. No UAC control or unattended access.\n'''
with zipfile.ZipFile(build) as source:
    if 'GalaxyControl.exe' not in source.namelist():
        raise SystemExit('Expected the tested Windows build artifact.')
    with zipfile.ZipFile(output, 'x', compression=zipfile.ZIP_DEFLATED) as target:
        for item in source.infolist():
            if item.filename in ('connection.json','GalaxyControl.pdb','Technician-Setup.html'):
                continue
            target.writestr(item, source.read(item))
        target.writestr('connection.json', json.dumps(config, indent=2)+'\n')
        target.writestr('START-HERE.txt', notes)
print('Configured Windows package created. Keep it separate from public generic releases.')
