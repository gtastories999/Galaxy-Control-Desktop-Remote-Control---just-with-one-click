# Bring your own Supabase

Each operator or GitHub downloader must use their **own** project. No shared Galaxy Control database or project credentials are included.

## Technician experience

The steps below are for developers configuring their own distribution. Technicians open the configured app, choose **I'm a technician**, and register/sign in there. The app saves public project settings automatically for future updates. No configuration copying or HTML setup page is part of the technician workflow. The legacy HTML page remains an optional developer diagnostic only.

## Install

1. Create a dedicated Supabase project. Keep `public` exposed to the Data API and keep `galaxy_private` **unexposed**.
2. Run [`schema.sql`](schema.sql) once in its SQL Editor. It creates private, RLS-enabled tables and the authenticated `public.gc_exchange` RPC. No Edge Function or separate signaling server is required. Do not run it against unrelated application tables. Reapplying the installation script is intentionally not destructive; it fails if those tables already exist.
3. Enable email/password authentication and **Anonymous Sign-Ins** under Auth providers. Keep email confirmation enabled. The customer uses anonymous authentication invisibly, without entering an account or password. This initial client does not implement a CAPTCHA challenge UI; CAPTCHA-protected anonymous sign-ins require an additional client integration. Retain Supabase's managed rate limits and assess the deployment's abuse controls.
4. Choose **I'm a technician → Create technician account** in the desktop app. Register, click the emailed confirmation link, then return to the app and select **Check confirmation & go online**. The legacy HTML setup page is optional and no longer required. The operator-configured ZIP preloads this public client configuration. With default email templates, confirmation completes before the Site URL redirect; return to Galaxy Control afterwards even if the default localhost landing page is unavailable. Configure a suitable Site URL for your own deployment. Supabase's default email provider only delivers to project team addresses; configure custom SMTP before onboarding other technician addresses. Anonymous and unconfirmed users cannot register an email in the directory. A verified technician can register only the email on their actual Auth user record, never an arbitrary address supplied by the client.
5. Copy the project's HTTPS URL and **publishable** key from its Connect/API settings. Never use a secret key, service-role key or database password in the desktop app.
6. For a downloaded build, edit its adjacent `connection.json`:

```json
{
  "SupabaseUrl": "https://YOUR-PROJECT.supabase.co",
  "SupabasePublishableKey": "sb_publishable_YOUR_OWN_KEY",
  "StunUrls": ["stun:stun.l.google.com:19302"]
}
```

For a source build, run `python scripts/configure.py https://YOUR-PROJECT.supabase.co sb_publishable_YOUR_OWN_KEY`. This writes the Git-ignored `src/GalaxyControl/connection.local.json`. Publishing uses it as `connection.json`; generic CI builds use `connection.example.json`. Public source and CI artifacts must retain placeholders.

Both PCs must use the **same operator-owned project**. Separate forks using separate projects are isolated: an email in project A cannot discover a technician registered only in project B.

## What is stored

- A verified technician's email, current app instance and online lease.
- The technician's literal IP and port, refreshed from WebRTC ICE candidates as negotiation gathers them. Public/server-reflexive endpoints take priority. An idle/new directory entry can have no endpoint yet; the app exchanges the actual session-specific addresses automatically. A stale IP or a single fixed port is not treated as a guarantee that NAT can be traversed.
- A pending/accepted/closed support request, its two authenticated participants and a maximum one-hour lifetime.
- Bounded SDP and ICE metadata needed by WebRTC. **No screenshot, audio, file, clipboard, mouse or keyboard payload is stored in Supabase.**

The app polls metadata once per second rather than requiring Realtime setup. The technician has a 45-second online lease; pending requests expire after 60 seconds; sessions stop if either participant stops renewing its 45-second lease. The direct control channel additionally uses an eight-second peer heartbeat. A metadata/API failure closes the native session rather than leaving remote control running silently.

## Security boundaries

The `galaxy_private` tables have RLS enabled and no client table grants. The public RPC is `SECURITY INVOKER`; the privileged implementation is in the unexposed schema, has a fixed empty search path, requires `auth.uid()`, and checks the live Auth account and both session participants. Only authenticated user JWTs can call it. Anonymous Auth users have the `authenticated` database role, so they are explicitly refused technician registration and cannot accept a request or inspect another user's session.

Directory lookups require an exact email and are limited to ten attempts per caller per minute; the directory cannot be listed through the API. Only one live app instance and one active session per technician are allowed. Session messages have strict sizes, roles and types; only `offer`, `answer` and direct `candidate` metadata are accepted. Client secrets and refresh tokens stay in memory and are not written to configuration or logs.

Supabase still coordinates discovery and connection metadata; it is not a media relay. STUN discovers network endpoints. P2P remains subject to NAT/firewall restrictions, with no TURN fallback. The database operator remains trusted to bind technician identities to connection fingerprints.

## Maintenance

Run ordinary Supabase security advisors on your own deployment. The automated repository tests verify RLS flags, direct table denial, RPC grants, verified-email ownership, instance fencing, cross-user isolation, field stripping, acceptance, metadata limits and rejection of input/media/relay candidates. No hosted project is changed by those tests.

The installation script does not install a scheduled cleanup job. An operator can periodically remove closed/expired metadata older than a chosen retention window, e.g. from the SQL Editor:

```sql
delete from galaxy_private.sessions
where (status = 'closed' or deadline < now())
  and created_at < now() - interval '7 days';
-- Signals are deleted by the session's cascading foreign key.
```

Anonymous Auth users also need an operator-chosen retention policy. Supabase does not currently remove them automatically. Revoke active sessions before deleting users if strict revocation is required; the directory implementation additionally checks that the Auth user still exists on every RPC call.

## Sources checked 2026-10-21

- [Supabase changelog](https://supabase.com/changelog.md): explicit API grants and current TLS changes were reviewed. This .NET 8 HTTPS client uses modern TLS defaults.
- [Anonymous Sign-Ins](https://supabase.com/docs/guides/auth/auth-anonymous): enable the provider; anonymous users assume the authenticated role and have a managed signup rate limit.
- [Securing your API](https://supabase.com/docs/guides/api/securing-your-api): explicit grants, private schemas and function execution permissions.
- [RLS](https://supabase.com/docs/guides/database/postgres/row-level-security): RLS/authorization boundaries.
- [Official Auth client](https://github.com/supabase/auth-js/blob/master/src/GoTrueClient.ts): anonymous `POST /auth/v1/signup`, password-token and refresh-token REST flows.

Account access, provider settings and production credentials remain operator-specific and are not assumed by this repository.

## Package a private operator download

Download the green Windows Actions artifact, then run:

```sh
python scripts/package-windows.py Galaxy-Control-P2P-Windows-x64.zip src/GalaxyControl/connection.local.json Galaxy-Control-Configured.zip
```

The ZIP contains the existing tested EXE, public client configuration and start instructions. The app imports its configuration automatically. Technician registration and email-confirmation checks take place in the app. No management token, secret key, service-role key or password belongs in it. Keep this operator-specific package separate from public generic releases. Customers only open the EXE and enter the technician email; account setup is for technicians once. The setup page uses standard Supabase Auth confirmation emails and stores no credentials.
