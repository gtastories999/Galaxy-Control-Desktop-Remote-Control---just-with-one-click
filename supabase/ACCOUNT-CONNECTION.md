# Automatic Supabase account connection

Implementation checked against official Supabase sources on 2026-10-22. Publisher activation and a live end-to-end OAuth/hosted-project test are still pending. Automated HTTP and browser-flow tests use controlled fakes; they are not evidence of live authorization.

## Technician workflow

1. Create a dedicated project named **Galaxy Control** in Supabase.
2. Choose **I'm a technician**, then **Connect Supabase** in Galaxy Control.
3. Enter the technician account email/password and click **Connect Supabase**. Sign in to Supabase and approve the project's requested access in the browser. If several accessible projects share that name, select one explicitly.
4. The app waits for a starting project, installs the embedded schema if absent, verifies RLS and RPC grants, configures email and anonymous sign-in, keeps email confirmation enabled, restricts the Data API to public schemas, creates only a publishable app key and atomically saves public client settings under the user's Local AppData. It never creates a project or paid resources automatically.
5. Existing technician credentials are checked, or a project Auth account is registered. Complete email confirmation when needed, then sign in. Supabase's default email delivery restrictions still apply; additional technician addresses can require operator SMTP configuration.
6. **Export customer app** creates a customer ZIP containing the app, known WebView2 runtime components and public connection settings. Passwords, owner tokens and OAuth credentials are excluded. Customers only enter the technician email.

Passwords are handled by project Auth, not an IP-address table. The directory binds the authenticated, verified account to its email and instance. Actual IP/port data updates from WebRTC candidates during a connection; it can be empty before negotiation and is not a permanent network address.

## One-time publisher activation

The Supabase developer MCP connector does not register organization-level Management API OAuth applications. Use the Supabase Dashboard's organization **OAuth Apps** screen to register **Galaxy Control**. This is a publisher action, not something each technician must repeat.

- Choose the fixed HTTPS URL for the `connect-supabase` exchange function before registration.
- Register its exact `<broker-root>/callback` URL as the OAuth redirect.
- Request only Projects Read, Database Write, Auth Write, Rest Write and Secrets Write. These permit discovering the named project, installing the schema, configuring Auth/Data API and creating a publishable key. Do not request project creation, billing, Storage or Edge Function permissions for technician authorization.
- Deploy `functions/connect-supabase/index.ts` and `handler.mjs` as a setup-only Edge Function. The OAuth callback must be reachable without a project Auth JWT; the function enforces signed state and PKCE instead. Configure gateway JWT verification accordingly for this function only.
- Set `GALAXY_SUPABASE_CLIENT_ID`, `GALAXY_SUPABASE_CLIENT_SECRET` and `GALAXY_SUPABASE_BROKER_URL` in that function's server-side environment. The app secret must never go into the EXE, Git repository or customer package.
- Populate the release's `setup-provider.json` with the broker's **public HTTPS root URL**. The checked-in generic file remains empty until a real integration is activated; a missing provider is reported accurately in the UI.
- Test using a real authorized owner, a dedicated project and the native app. Check wrong-account, denied-consent, cancellation, multiple-project, existing-project, email-confirmation and export behavior before merging/publishing the feature.

This branch intentionally does not replace the stable public release while publisher activation is pending.

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
