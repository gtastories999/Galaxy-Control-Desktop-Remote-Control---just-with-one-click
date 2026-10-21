"""Prepare a single-technician relay. Never commit deployment/.env."""
import hashlib
import json
from pathlib import Path
import re
import secrets
import sys

if len(sys.argv) != 3:
    raise SystemExit('Usage: python scripts/configure.py support.example.com technician@example.com')
domain, email = sys.argv[1].lower(), sys.argv[2].strip().lower()
if not re.fullmatch(r'[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?', domain) or '.' not in domain:
    raise SystemExit('Enter a DNS hostname without a scheme or path.')
if not re.fullmatch(r'[^\s@]+@[^\s@]+\.[^\s@]+', email):
    raise SystemExit('Enter a valid technician email.')
root = Path(__file__).resolve().parent.parent
config = root / 'deployment/.env'
if config.exists():
    raise SystemExit('deployment/.env already exists. Back it up and remove it before generating new credentials.')
key = secrets.token_urlsafe(32)
hashed = hashlib.sha256(key.encode()).hexdigest()
with config.open('x') as output:
    output.write('GALAXY_DOMAIN=' + domain + '\nGALAXY_TECHNICIANS=' + json.dumps({email: hashed}) + '\n')
config.chmod(0o600)
(root / 'src/GalaxyControl/connection.json').write_text(json.dumps({'RelayUrl': 'wss://' + domain + '/connect'}) + '\n')
print('Configuration ready. Store this technician access key privately; it is shown only now:\n' + key)
print('Start the relay with: docker compose --env-file deployment/.env -f deployment/compose.yaml up -d --build')
