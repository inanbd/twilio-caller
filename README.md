# Twilio Caller

A Flutter app for calling and texting from your own Twilio or Plivo numbers, with
a small ASP.NET Core backend that holds the credentials and takes the provider's
webhooks.

Connect a Twilio or Plivo account once, pick which of its numbers the app should
answer for, and use them: place and receive calls with real in-app audio, and
send and receive SMS. The app is signed in to one account at a time; the connect
screen has a Twilio / Plivo switch. [What differs with Plivo](#plivo) is below.

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
backend/                 ASP.NET Core 8 + SQLite
  TwilioCaller.Api/
    Endpoints/           app-facing API, and the webhooks Twilio and Plivo call
    Services/            credential encryption, tokens, provisioning, Twilio REST
    Services/Plivo/      Plivo REST client, provisioning, webhook signature check
    Realtime/            SignalR hub relaying inbound events to the app
  TwilioCaller.Tests/    68 tests, incl. in-process servers over the call routing
app/                     Flutter — Android, iOS, macOS, web, Linux, Windows
  lib/core/              config, models, HTTP client, formatting
  lib/services/          realtime, voice (one engine per provider SDK)
  lib/state/             the single ChangeNotifier the UI watches
  lib/ui/                screens and widgets
  web/plivo_bridge.js    drives Plivo's Browser SDK for the web build
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

Then in the app: enter the backend URL and your Twilio Account SID, API Key SID
and secret (or choose Plivo and enter your Auth ID and Auth Token), and go to **Settings → Phone numbers & routing** to choose which
numbers route here.

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
                 │   backend    │  signs Voice tokens · answers with TwiML
                 │   + SQLite   │  stores the API key secret, AES-GCM encrypted
                 └──────┬───────┘
              SignalR   │   HTTPS
                        ▼
                 ┌──────────────┐
                 │  Flutter app │  holds only a session token
                 └──────────────┘
```

One webhook serves both call directions. Twilio marks a call placed from the
Voice SDK with a `client:` prefixed `From`, which is how the backend tells an
app-originated call from someone dialling your number, and answers with either
`<Dial><Number>` or `<Dial><Client>`.

## Plivo

Plivo works through the same screens and the same backend, with these differences:

- **Credentials** are the account's **Auth ID** and **Auth Token**. Plivo has no
  API keys, so the token itself is what the backend stores (encrypted, like the
  Twilio secret).
- **In-app audio is web only.** Plivo publishes a Browser SDK but no Flutter
  plugin, so the web build gets real in-app calls and every other platform uses
  dial-out. The backend creates one SIP endpoint per browser for this.
- **Message history starts when you connect.** Plivo's API returns neither the
  text of a message nor a way to list messages by number, so the backend keeps
  its own log of what it sends and receives. Messages from before, or sent
  outside this app, do not appear.
- **Routing** attaches the chosen numbers to a Plivo Application the backend
  creates, rather than rewriting per-number webhook URLs.
- **Webhook signatures are always verified** (`X-Plivo-Signature-V3`), since the
  auth token is always on file.

## Security

- The Twilio API key secret is sent to the backend once, verified, and stored
  encrypted with AES-GCM under a key from the environment. It is never written to
  the device and never returned by any endpoint.
- The app authenticates with a session JWT scoped to one connection and one
  device identity. A leaked app token reaches this backend, not Twilio.
- Webhook URLs carry a per-connection random key, and `X-Twilio-Signature` is
  verified whenever an auth token is on file. Plivo webhooks must always carry a
  valid `X-Plivo-Signature-V3`.
- The app refuses a plain `http://` backend URL for anything but localhost, since
  that is the one connection the API key secret crosses.

## Known limits

- **Background ringing needs push credentials.** Out of the box, inbound calls
  ring while the app is open. Waking a closed app needs an FCM (Android) or APNs
  (iOS) push credential plus a device token; the backend supports the credential,
  the app-side token fetch is left to you since it needs your own Firebase
  project. See [docs/SETUP.md](docs/SETUP.md#5-ringing-while-the-app-is-closed).
- **Linux and Windows have no Voice SDK**, so those builds fall back to dial-out:
  the provider rings a handset you nominate and bridges the call. With Plivo
  that applies to every platform except web.
- **Plivo message history is only what passed through this backend**, and its
  calls ring in the browser only while the tab is open.
- **MMS attachments are counted, not displayed.** Inbound media shows as
  "1 attachment" rather than the image.
- SQLite and in-process SignalR mean one backend instance. Fine for personal use;
  multi-instance would want Postgres and a Redis backplane.

## Development

```bash
cd backend && dotnet test        # 68 tests
cd app && flutter test           # 38 tests
cd app && flutter analyze        # clean
```
