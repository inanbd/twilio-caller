# Setup

Start to finish: a running backend, a Twilio account wired to it, and an app that
calls and texts from your own numbers.

---

## 1. What you need from Twilio

Sign in at [console.twilio.com](https://console.twilio.com) and collect three things.

| Value | Where | Looks like |
|---|---|---|
| **Account SID** | Console home | `ACxxxxxxxx…` (34 chars) |
| **API Key SID** | Account → API keys & tokens → Create API key | `SKxxxxxxxx…` (34 chars) |
| **API Key Secret** | Shown once, when you create the key | a long random string |

Create a **Standard** API key. Twilio shows the secret exactly once — if you lose
it, delete the key and make another.

The **Auth Token** on the console home page is optional. Supply it only if you
want the backend to verify `X-Twilio-Signature` on incoming webhooks. It is
worth doing for anything long-lived.

> **Trial accounts** can only call and text numbers you have verified in the
> console, and Twilio prepends a trial notice to outbound calls. Everything in
> this app works on a trial account, within those limits.

---

## 2. Run the backend

The app cannot work without it. Twilio Voice access tokens must be signed with
your API key secret, and inbound calls and texts are delivered by webhook to a
public URL — neither of which an app can do for itself.

### Generate the secrets

```bash
openssl rand -base64 32   # MASTER_KEY — encrypts your Twilio secret at rest
openssl rand -base64 48   # SESSION_SIGNING_KEY — signs the app's login tokens
```

Optionally also set `ADMIN_EMAIL` and `ADMIN_PASSWORD` to pre-provision the
administrator account. Without them, **the first account registered — from the
app or the portal — becomes the administrator.**

### Locally, with Docker

```bash
cp .env.example .env
# paste both secrets into .env
docker compose up --build
```

The backend listens on `http://localhost:8080`, but Twilio has to reach it from
the public internet. In a second terminal:

```bash
ngrok http 8080
```

Put the forwarding URL (`https://abc123.ngrok.app`) into `.env` as
`PUBLIC_BASE_URL` and restart. **This URL is baked into the webhooks Twilio
calls**, so if it changes — as a free ngrok URL does on every restart — re-save
number routing in the app afterwards.

### Locally, without Docker

```bash
cd backend
export PublicBaseUrl="https://abc123.ngrok.app"
export Security__MasterKey="…"
export Security__SessionSigningKey="…"
dotnet run --project TwilioCaller.Api
```

### Deployed

Any host that runs a container and gives you a stable HTTPS URL works — Fly.io,
Render, Railway, Azure App Service, a VPS behind Caddy. Set the same three
environment variables, and **mount a volume at `/data`**: that is where the
SQLite database holding your encrypted credentials lives, and without it every
restart forces a fresh sign-in.

Confirm it is up:

```bash
curl https://your-backend.example.com/health
# {"status":"ok"}
```

---

## 3. Run the app

```bash
cd app
flutter pub get

flutter run                                     # a connected device
flutter run -d chrome                           # web
flutter build apk --release                     # Android
```

To skip typing the backend URL on every install, bake it in:

```bash
flutter build apk --release --dart-define=BACKEND_URL=https://your-backend.example.com
```

---

## 4. Register, connect and route your numbers

1. Open the app. Enter the backend URL and **register an account** (email and
   password, at least 8 characters), or sign in if you already have one. The
   same account works in the web portal.
2. Connect your Twilio account: Account SID, API Key SID and secret. The secret
   goes to your backend, is verified against Twilio, and is stored there
   encrypted, tied to your account. The app keeps only a session token — the
   secret is never written to the device.
3. Go to **Settings → Phone numbers & routing**, tick the numbers you want this
   app to answer for, and press **Save routing**.

That last step is what makes receiving work. It rewrites each selected number's
voice and SMS webhooks to point at your backend, and creates the TwiML
Application the Voice SDK needs. Numbers you leave unticked are untouched.

Until at least one number is routed, the app shows a red banner and nothing
inbound will arrive.

### Fallback forwarding

Optionally set a number for Twilio to ring when no app is registered — your
actual mobile, say. Without it, a call arriving with no app online hears a short
"nobody is available" message.

---

## 5. The web portal

The backend serves a management portal at its **root URL** — the same address
you put into the app, opened in a browser:

```
https://your-backend.example.com/
```

Sign in with the same email and password as in the app (or register there
first — accounts are shared). From the portal you can do everything the app
does:

- connect, replace or disconnect your **Twilio account**;
- tick which **phone numbers** route to this backend, and set the fallback
  forward number;
- read and send **messages**, per conversation;
- see **call history** and place calls via dial-out (the browser has no Voice
  SDK, so Twilio rings your own phone first, then bridges the call);
- change your **password** — which signs you out everywhere else.

### Administration

Members of the `Administrator` role get an extra **Administration** section
listing every account on the backend, with actions to:

- create accounts (including more administrators);
- promote or demote administrators — the last administrator cannot be demoted;
- disable and re-enable accounts — a disabled account's sessions stop working
  immediately, in the app and the portal;
- reset an account's password — which signs that account out everywhere;
- delete an account, along with its Twilio connection, devices and stored
  events. Twilio itself is not touched.

The first account ever registered becomes the administrator automatically; the
`ADMIN_EMAIL` / `ADMIN_PASSWORD` environment variables seed one explicitly and
also re-grant the role on every startup, which doubles as a recovery hatch if
you demote yourself by accident.

---

## 6. Ringing while the app is closed

Everything above gives you calls that ring **while the app is open**. To be woken
by a call when the app is backgrounded or killed, Twilio needs a **Push
Credential**, and the app needs a device push token.

| Platform | What Twilio needs |
|---|---|
| Android | A **Push Credential (FCM)** built from a Firebase project's server key |
| iOS | A **Push Credential (APNs)** built from a VoIP Services certificate |

Create it under Account → Keys & Credentials → Push Credentials. You get a SID
like `CRxxxxxxxx…`. Set it on your connection row (`AndroidPushCredentialSid` or
`ApplePushCredentialSid`) and the backend will include it in every access token
it issues for that platform.

The app side then needs `firebase_messaging` (Android) or PushKit (iOS) to fetch
a device token and pass it to `setTokens(deviceToken: …)`. That is deliberately
left out here: it requires your own Firebase project and a `google-services.json`,
which cannot be checked in generically.

**Without this, inbound calls ring only while the app is in the foreground.**
Inbound *SMS* is unaffected — it arrives over the realtime channel, and is
replayed from the backend on next launch regardless.

---

## 7. Platform differences

| | Android | iOS | macOS | Web | Linux / Windows |
|---|---|---|---|---|---|
| List numbers | ✅ | ✅ | ✅ | ✅ | ✅ |
| Send / receive SMS | ✅ | ✅ | ✅ | ✅ | ✅ |
| In-app calls (real audio) | ✅ | ✅ | ✅ | ✅ | ❌ |
| Inbound call ringing | ✅ | ✅ | ✅ | ✅ (tab open) | ❌ |
| System call UI | ✅ | ✅ | — | — | — |
| Background ringing | with FCM | with APNs | — | ❌ | ❌ |

Linux and Windows have no Twilio Voice SDK implementation. Those builds fall back
to **dial-out**: Twilio rings a handset you nominate, then bridges it to the
destination. The app says so rather than silently disabling the call button.

---

## Troubleshooting

**"A Twilio API Key SID starts with SK"** — you pasted the Account SID twice. The
`AC…` value is the account; the `SK…` value is the key.

**Twilio rejected the credentials** — the key was deleted, or belongs to a
different account than the Account SID you entered.

**Calls connect but nobody can hear anything** — microphone permission. On
Android, also check the phone account is enabled in system call settings.

**Inbound calls or texts never arrive** — almost always the webhook URL. Check
that `PublicBaseUrl` matches the address Twilio can actually reach, that the
number shows as "routed here" in Settings, and that Twilio's
[error log](https://console.twilio.com/us1/monitor/logs/debugger) shows a 200
from your backend rather than a timeout.

**Everything works, then stops after a restart** — the SQLite volume is not
persisted, so the accounts, the connection row and its webhook key were lost.
Mount `/data`.

**Signed out unexpectedly on every device** — that is deliberate: it happens
when your password was changed or reset, your role changed, or an administrator
disabled the account. Sign in again (or ask your administrator).

**Locked out as the only administrator** — set `ADMIN_EMAIL` and
`ADMIN_PASSWORD` and restart the backend; the seeder re-grants the role at
startup.

**The realtime icon stays grey** — the SignalR hub is unreachable. Messages still
arrive on pull-to-refresh. Check that your host allows WebSocket upgrades.
