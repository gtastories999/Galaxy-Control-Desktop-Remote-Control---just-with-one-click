"""Configure this developer's public OAuth broker URL for a local Windows build.

OAuth client IDs/secrets belong exclusively in the developer's server environment.
"""
import argparse
import json
from pathlib import Path
from urllib.parse import urlsplit


def configure(value: str) -> Path:
    endpoint = urlsplit(value)
    if (endpoint.scheme != 'https' or not endpoint.hostname or endpoint.username
            or endpoint.password or endpoint.query or endpoint.fragment
            or any(char.isspace() for char in value)):
        raise ValueError('Use your own HTTPS broker root URL without credentials, query parameters or fragments.')
    # Validate the port too; urlsplit otherwise defers invalid-port errors.
    _ = endpoint.port
    target = Path(__file__).resolve().parent.parent / 'src/GalaxyControl/setup-provider.local.json'
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps({'BrokerUrl': value.rstrip('/') + '/'}, indent=2) + '\n')
    return target


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('broker_url', help='The public HTTPS root of YOUR deployed account exchange service')
    args = parser.parse_args()
    try:
        configure(args.broker_url)
    except ValueError as error:
        parser.error(str(error))
    print('Your distribution is configured locally. No OAuth secret was written. Build the Windows app next.')
