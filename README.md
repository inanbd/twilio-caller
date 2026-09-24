# Twilio Caller

A Flutter app for calling and texting from your own Twilio numbers, with an
ASP.NET Core backend that holds the credentials, takes Twilio's webhooks, and
serves a web portal for managing everything from a browser.

Register an account (in the app or the portal — ASP.NET Core Identity backs
both), connect your Twilio account once, pick which of its numbers to answer
for, and use them: place and receive calls with real in-app audio, and send and
receive SMS. Everything is manageable from the app **and** from the backend's
portal, and an administrator account can manage every account on the backend.

---

## Why there is a backend

The obvious design — an app that talks straight to Twilio with an API key — gets
you about halfway, and then stops:

| | App alone | With this backend |
|---|---|---|
| List the account's numbers | ✅ | ✅ |
| Send SMS | ✅ | ✅ |
| Read SMS history | ✅ | ✅ |
| Receive SMS as it arrives | ❌ webhook only | ✅ pushed in realtime |
| Place a call with in-app audio | ❌ needs a signed token | ✅ |
| Receive an incoming call | ❌ needs a webhook | ✅ |
| Keep the API key secret safe | ❌ it ships in the binary | ✅ it stays on the server |

Two hard constraints drive this:

- **Twilio Voice access tokens are JWTs signed with your API key secret.** Signing
  them in the app means shipping that secret to every device that installs it.
- **Inbound calls and SMS are delivered by webhook** to a public URL, which an app
  does not have. Something reachable from the internet has to answer Twilio and
  reply with TwiML.

So the backend owns the credentials and the webhooks; the app holds only a
session token. Setup is still one screen — the backend provisions Twilio itself,
so you never touch the console after creating an API key.

---

## Layout

```
backend/                 ASP.NET Core 8 + SQLite + Identity
  TwilioCaller.Api/
    Endpoints/           auth (register/login), app-facing API, admin, webhooks
    Services/            credential encryption, tokens, provisioning, Twilio REST
    Realtime/            SignalR hub relaying inbound events to the app
    wwwroot/             the management portal, served at the backend's root URL
  TwilioCaller.Tests/    44 tests, incl. an in-process server over auth + TwiML
app/                     Flutter — Android, iOS, macOS, web, Linux, Windows
  lib/core/              config, models, HTTP client, formatting
  lib/services/          realtime, voice
  lib/state/             the single ChangeNotifier the UI watches
  lib/ui/                screens and widgets
docs/SETUP.md            full setup, deployment and troubleshooting
```

## Quick start

```bash
openssl rand -base64 32   # MASTER_KEY
openssl rand -base64 48   # SESSION_SIGNING_KEY

cp .env.example .env      # paste both in, plus your public URL
docker compose up --build

cd app && flutter run
```

Then in the app: enter the backend URL, **register an account** (email +
password), and connect your Twilio Account SID, API Key SID and secret. Go to
**Settings → Phone numbers & routing** to choose which numbers route here.

The same backend serves a **web portal at its root URL** — sign in there with
the same account to manage the Twilio connection, numbers, messages and calls
from a browser. The first account registered becomes the **administrator** and
gets an Administration section for managing every account (or seed one with
`ADMIN_EMAIL` / `ADMIN_PASSWORD` in `.env`).

Full instructions, including deployment and getting calls to ring while the app
is closed, are in **[docs/SETUP.md](docs/SETUP.md)**.

---

## How it fits together

```
                 ┌──────────────┐
   inbound  ───► │    Twilio    │ ◄─── REST: numbers, SMS, call history
   call/SMS      └──────┬───────┘
                        │ webhook (voice / sms)
                        ▼
                 ┌──────────────┐
                 │   backend    │  Identity accounts · signs Voice tokens
                 │   + SQLite   │  answers with TwiML · serves the portal
                 └──┬────────┬──┘  stores the API key secret, AES-GCM encrypted
          SignalR   │        │   HTTPS
             HTTPS  ▼        ▼
        ┌──────────────┐  ┌──────────────┐
        │  Flutter app │  │  web portal  │  same accounts, same API;
        │ session token│  │ (/ on the    │  admins manage every account
        └──────────────┘  │   backend)   │
                          └──────────────┘
```

One webhook serves both call directions. Twilio marks a call placed from the
Voice SDK with a `client:` prefixed `From`, which is how the backend tells an
app-originated call from someone dialling your number, and answers with either
`<Dial><Number>` or `<Dial><Client>`.

## Security

- Accounts are held by ASP.NET Core Identity: hashed passwords, login lockout
  after repeated failures, and an `Administrator` role for the portal's account
  management. The first registered account becomes the administrator.
- The app and portal authenticate with a session JWT carrying the user, one
  device identity and the user's roles. A leaked token reaches this backend, not
  Twilio — and every request re-checks the account, so disabling a user,
  resetting their password or changing their role revokes their existing
  sessions immediately.
- The Twilio API key secret is sent to the backend once, verified, and stored
  encrypted with AES-GCM under a key from the environment. It is never written to
  the device and never returned by any endpoint. Each Twilio connection belongs
  to exactly one account.
- Webhook URLs carry a per-connection random key, and `X-Twilio-Signature` is
  verified whenever an auth token is on file.
- The app refuses a plain `http://` backend URL for anything but localhost, since
  that is the one connection the API key secret crosses.

## Known limits

- **Background ringing needs push credentials.** Out of the box, inbound calls
  ring while the app is open. Waking a closed app needs an FCM (Android) or APNs
  (iOS) push credential plus a device token; the backend supports the credential,
  the app-side token fetch is left to you since it needs your own Firebase
  project. See [docs/SETUP.md](docs/SETUP.md#6-ringing-while-the-app-is-closed).
- **Linux and Windows have no Voice SDK**, so those builds fall back to dial-out:
  Twilio rings a handset you nominate and bridges the call.
- **MMS attachments are counted, not displayed.** Inbound media shows as
  "1 attachment" rather than the image.
- SQLite and in-process SignalR mean one backend instance. Fine for personal use;
  multi-instance would want Postgres and a Redis backplane.

## Development

```bash
cd backend && dotnet test        # 44 tests
cd app && flutter test           # 18 tests
cd app && flutter analyze        # clean
```
