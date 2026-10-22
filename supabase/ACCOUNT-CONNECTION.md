# Automatic Supabase account connection

Implementation checked against official Supabase sources on 2026-10-22. Every developer/distributor registers their own OAuth application and deploys their own exchange backend. Generic builds intentionally contain no shared provider. A live end-to-end test must use that developer’s integration. Automated HTTP and browser-flow tests use controlled fakes; they are not evidence of live authorization.

## Technician workflow

1. Create a dedicated project named **Galaxy Control** in Supabase.
2. Choose **I'm a technician**, then **Connect Supabase** in Galaxy Control.
3. Enter the technician account email/password and click **Connect Supabase**. Sign in to Supabase and approve the project's requested access in the browser. If several accessible projects share that name, select one explicitly.
4. The app waits for a starting project, installs the embedded schema if absent, verifies RLS and RPC grants, configures email and anonymous sign-in, keeps email confirmation enabled, restricts the Data API to public schemas, creates only a publishable app key and atomically saves public client settings under the user's Local AppData. It never creates a project or paid resources automatically.
5. Existing technician credentials are checked, or a project Auth account is registered. Complete email confirmation when needed, then sign in. Supabase's default email delivery restrictions still apply; additional technician addresses can require operator SMTP configuration.
6. **Export customer app** creates a customer ZIP containing the app, known WebView2 runtime components and public connection settings. Passwords, owner tokens and OAuth credentials are excluded. Customers only enter the technician email.

Passwords are handled by project Auth, not an IP-address table. The directory binds the authenticated, verified account to its email and instance. Actual IP/port data updates from WebRTC candidates during a connection; it can be empty before negotiation and is not a permanent network address.

## One-time setup for each developer

Fork or clone this repository. Do not reuse the original author's OAuth application, backend, credentials or Supabase project. Technicians using your configured distribution authorize **your** integration; they do not register another OAuth application themselves.

1. In your own Supabase organization, register a Management API OAuth application under **OAuth Apps**. Choose your own HTTPS exchange endpoint first and register its exact `<broker-root>/callback` redirect URL.
2. Request only Projects Read, Database Write, Auth Write, Rest Write and Secrets Write. These allow project discovery, schema installation, Auth/Data API configuration and publishable-key creation. Project creation, billing, Storage and Edge Function permissions are unnecessary for technician authorization.
3. Deploy the included `supabase/functions/connect-supabase` function to your own backend. The checked-in `supabase/config.toml` disables gateway JWT verification for this function only: signed state and PKCE protect the OAuth flow instead. Link the Supabase CLI to your own backend project before deployment.
4. Copy `supabase/functions/connect-supabase/.env.example` to `.env` in that directory. Set your OAuth client ID, client secret and public broker root URL. Store these as server-side function secrets and deploy the function. Real `.env` files are Git-ignored. Never put the client secret in desktop configuration, the EXE, GitHub or customer packages.

For a Supabase Edge Function backend, run from the repository root after installing and authenticating the Supabase CLI:

```bash
supabase link --project-ref YOUR_BACKEND_PROJECT_REF
supabase secrets set --env-file supabase/functions/connect-supabase/.env
supabase functions deploy connect-supabase
python scripts/configure-provider.py https://YOUR-BACKEND-PROJECT.supabase.co/functions/v1/connect-supabase/
```

The helper creates ignored `src/GalaxyControl/setup-provider.local.json` containing **only your public broker URL**. The checked-in `setup-provider.json` stays blank. Build your configured Windows distribution:

```powershell
dotnet publish src/GalaxyControl -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/windows
```

The output is named `setup-provider.json`; ship it with the executable. No OAuth credentials are copied into the build. Generic GitHub builds reject any shared provider and remain unconfigured by design. Each fork follows these same steps independently; no activation of the original author's account is needed.

Before distributing your configured build, test your deployed OAuth flow with an authorized owner and a dedicated **Galaxy Control** project. Check denied consent, cancellation, multiple projects, existing installations, email confirmation and customer export. Automated tests use controlled endpoints and do not claim a live hosted OAuth test.

## Authorization boundary

The native app generates a random state and PKCE verifier. A loopback TCP listener binds only 127.0.0.1 on an ephemeral port. The exchange service signs an expiring ticket with the application secret, sends the browser to Supabase, and redirects only the returned **authorization code** to the fixed loopback address. Neither account tokens nor project keys appear in callback URLs.

The app checks its state and exchanges the code over HTTPS with its original verifier. The service checks ticket signature, age, port range and verifier challenge, then exchanges with the official Supabase endpoint using its confidential application credential. It returns only the short-lived account access token. Refresh tokens are not returned or stored. The native client keeps account authorization only in memory during setup and sends it only to `api.supabase.com`; technician credentials go directly to the selected project Auth API.

Setup refuses unrelated project names, unready/paused projects, partial pre-existing schemas, failed access verification and non-publishable keys. Existing complete installations and matching public client keys are preserved. Partial operations do not overwrite unrelated tables. Setup does not alter SMTP, CAPTCHA, MFA, TLS or database passwords.

A publisher controls the account-exchange service and its release configuration. Technicians should authorize a publisher they trust. The service has no screen capture, input forwarding, management proxy or token database.

## Verified primary sources

- [Build a Supabase OAuth integration](https://supabase.com/docs/guides/integrations/build-a-supabase-oauth-integration): application registration, client-secret authentication, state and PKCE.
- [OAuth scopes](https://supabase.com/docs/guides/integrations/build-a-supabase-oauth-integration/oauth-scopes): category permissions are configured when registering the application.
- [Create project API key](https://supabase.com/docs/reference/api/v1-create-project-api-key): publishable key creation and explicit reveal option.
- [Run database query](https://supabase.com/docs/reference/api/v1-run-a-query): Management API query endpoint is documented as experimental/Beta and may change.
- [Update Auth configuration](https://supabase.com/docs/reference/api/v1-update-auth-service-config): email and anonymous sign-in flags.
- [Update PostgREST configuration](https://supabase.com/docs/reference/api/v1-update-postgrest-service-config): dedicated project's exposed schemas.
- [Supabase for Platforms](https://supabase.com/docs/guides/integrations/supabase-for-platforms): project readiness, public key creation and configuration endpoints. This implementation does not create customer projects or change billing.

## Verification

`dotnet run --project tests/Provisioning -c Release` checks the owned-project boundary, repeat installation, partial schema and permission refusals, publishable-only keys, loopback state rejection and original PKCE verifier. `node tests/account-broker.cjs` checks signed tickets, verifier binding, expiration, fixed redirects, token isolation and inactive publisher configuration. Existing schema, directory, WebRTC, real Windows WebView2 and native UI tests also remain required.

## In-app technician registration

Configured distributions expose **Create technician account** directly in technician mode. Signup, confirmation resend, and confirmation checks use only the distribution's configured project and publishable key. Passwords are not written to configuration files. After email confirmation, **Check confirmation & go online** validates the credentials and starts the normal attended technician session. Existing accounts continue using **Sign in & go online**. Unconfigured distributions route account creation to **Connect Supabase**; their developer must supply their own OAuth integration first. No separate HTML page is required.

Galaxy is the umbrella brand: use Gradient Yellow Gold for buttons and the existing gold 3D star-and-orbit logo from the login screen throughout onboarding. Generic builds remain free of operator configuration and credentials.

Auth signup and confirmation resend references checked on 2026-10-22: https://supabase.com/docs/reference/javascript/auth-signup and https://supabase.com/docs/reference/javascript/auth-resend. Email confirmation and SMTP delivery remain project prerequisites; no email verification is bypassed.
