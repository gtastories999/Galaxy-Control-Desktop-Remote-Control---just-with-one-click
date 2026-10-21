"""Configure a local build for YOUR Supabase project; never publish this local file."""
import json
from pathlib import Path
import sys
from urllib.parse import urlparse

if len(sys.argv) != 3:
    raise SystemExit('Usage: python scripts/configure.py https://YOUR-PROJECT.supabase.co sb_publishable_YOUR_KEY')
url, key = sys.argv[1].rstrip('/'), sys.argv[2]
parsed = urlparse(url)
if parsed.scheme != 'https' or not parsed.hostname or parsed.path or parsed.query or parsed.fragment or parsed.username:
    raise SystemExit('Enter the HTTPS root URL of your own Supabase project.')
if not key.startswith('sb_publishable_') or len(key) < 25:
    raise SystemExit('Use a publishable key. Never use a secret key or service-role key.')
config = Path(__file__).resolve().parent.parent / 'src/GalaxyControl/connection.local.json'
with config.open('x') as output:
    json.dump({'SupabaseUrl':url, 'SupabasePublishableKey':key,'StunUrls':['stun:stun.l.google.com:19302']},output,indent=2)
    output.write('\n')
print('Your local build configuration is ready. The file is ignored by Git; public builds keep using the example configuration.')
