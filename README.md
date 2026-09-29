# Stamp

A paid inbox for busy people. A receiver creates a public page (e.g. `/kalai`) and sets a stamp price.
A stranger writes a message there and authorizes the stamp on their card.

- **The receiver replies within 6 days:** the payment is captured, and the receiver earns it minus a 10% platform fee.
- **The receiver declines or ignores it:** the authorization is canceled and the sender is never charged.

Built with ASP.NET Core (.NET 10) Razor Pages, EF Core (SQLite by default, PostgreSQL-ready), Stripe
(manual-capture PaymentIntents with Connect Express destination charges), and Resend for email.

---

## Contents

1. [Quick start (no Stripe account needed)](#quick-start-no-stripe-account-needed)
2. [Running with Stripe test mode](#running-with-stripe-test-mode)
3. [The expiry job](#the-expiry-job)
4. [Configuration](#configuration)
5. [Database](#database)
6. [Tests](#tests)
7. [How it works](#how-it-works)
8. [Before going to production](#before-going-to-production)

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- For real payments: a [Stripe](https://dashboard.stripe.com/register) account (test mode is enough) and the [Stripe CLI](https://docs.stripe.com/stripe-cli)
- Optional: PostgreSQL, and a [Resend](https://resend.com) account for real email

The app runs on `https://localhost:5001`. If your browser warns about the certificate, trust the ASP.NET Core development certificate once:

```bash
dotnet dev-certs https --trust
```

## Quick start (no Stripe account needed)

A development-only fake payment provider lets you click through the whole flow without Stripe keys.
It replaces the card form with a "simulated authorization" and completes payout onboarding instantly.

```bash
Payments__Provider=Fake dotnet run --project src/Stamp.Web
```

On Windows PowerShell: `$env:Payments__Provider="Fake"; dotnet run --project src/Stamp.Web`.

1. Open https://localhost:5001 and choose **Create your page**. Sign in with any email address.
2. Emails aren't really sent in development. The sign-in link is printed in the console and saved as an HTML file in `src/Stamp.Web/.emails/`.
3. Pick a handle and price, then go to **Payouts** and choose **Set up payouts**.
4. Open `https://localhost:5001/<your-handle>` in a private window and send yourself a message.
5. Back in your inbox, reply to it (you earn the stamp), or decline it (the sender isn't charged).

The database is a SQLite file at `src/Stamp.Web/App_Data/stamp.db`. It's created and migrated automatically.

## Running with Stripe test mode

### 1. Set up the Stripe account

- In the [Stripe dashboard](https://dashboard.stripe.com/test/dashboard), stay in **Test mode**.
- Enable Connect (**Settings → Connect → Get started**) and choose a platform that uses **Express** accounts.
- Copy your test keys from **Developers → API keys**: the publishable key (`pk_test_…`) and the secret key (`sk_test_…`).

### 2. Forward webhooks to your machine

Log in once with `stripe login`, then keep this running in a separate terminal:

```bash
stripe listen \
  --forward-to https://localhost:5001/webhooks/stripe \
  --forward-connect-to https://localhost:5001/webhooks/stripe \
  --events payment_intent.amount_capturable_updated,payment_intent.succeeded,payment_intent.canceled,account.updated \
  --skip-verify
```

- The command prints a webhook signing secret (`whsec_…`). You'll need it in the next step.
- `--forward-connect-to` delivers `account.updated` events from receivers' Express accounts.
- `--skip-verify` lets the CLI post to the local development certificate.

### 3. Store the secrets

Secrets are never committed. Use [user-secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets); the project already has a `UserSecretsId`:

```bash
dotnet user-secrets set "Stripe:SecretKey" "sk_test_..." --project src/Stamp.Web
dotnet user-secrets set "Stripe:PublishableKey" "pk_test_..." --project src/Stamp.Web
dotnet user-secrets set "Stripe:WebhookSecret" "whsec_..." --project src/Stamp.Web
```

Environment variables work too: `Stripe__SecretKey`, `Stripe__PublishableKey`, `Stripe__WebhookSecret`.
If a key is missing or malformed, the app refuses to start and says which one.

### 4. Run and try it

```bash
dotnet run --project src/Stamp.Web
```

- **Sign in.** Sign in and set up your page as in the quick start.
- **Onboard payouts.** Under **Payouts**, choose **Set up payouts with Stripe**. Stripe's test onboarding accepts [its documented test values](https://docs.stripe.com/connect/testing), for example:
  - phone `000 000 0000` and SMS code `000000`
  - SSN `000-00-0000`
  - address line `address_full_match`
  - routing number `110000000` and account number `000123456789`
- **Pay as a sender.** On your public page, pay with one of these cards (any future expiry date and any CVC):

  | Card | Result |
  |---|---|
  | `4242 4242 4242 4242` | Succeeds |
  | `4000 0027 6000 3184` | Requires 3-D Secure |
  | `4000 0000 0000 0002` | Declined |

- **Watch the money in the dashboard.** The payment shows as **Uncaptured** while the stamp is pending.
  - Replying captures it, with the 10% application fee going to the platform and the rest transferred to the Express account.
  - Declining or letting it expire cancels it.

## The expiry job

Card authorizations only last about 7 days, so Stamp must act before then. An hourly job runs inside the web app. On each run it:

1. Expires pending stamps whose 6-day window has passed. It releases the hold and emails the sender that they weren't charged.
2. Handles drafts whose card was never authorized, after 24 hours:
   - If Stripe says the card *was* authorized but we never heard (a missed webhook, or the sender closed the tab), the stamp is delivered.
   - Otherwise the draft is abandoned and its payment voided.
3. Retries any capture or release that failed earlier, for example during a Stripe outage.

It's safe to run concurrently on several instances, or repeatedly.

**Run it once by hand**, for cron, a Kubernetes CronJob or debugging:

```bash
dotnet run --project src/Stamp.Web -- expire-stamps
# Expired 1, recovered 0, abandoned 0, settled 1 payments (0 failed, will retry); sent 1 emails.
```

- The command also sends the emails it queued.
- It exits with `1` if some payments couldn't be settled; they're retried on the next run.
- If you schedule it externally, you can turn off the in-app timer with `Jobs__Expiry__Enabled=false`. Running both is also safe.

**Test expiry locally** by shrinking the windows:

```bash
Stamps__ReplyWindow=00:02:00 Jobs__Expiry__Interval=00:00:30 dotnet run --project src/Stamp.Web
```

Things to know:

- The reply window must be shorter than the card hold, so startup rejects anything of 7 days or more.
- Keep the reply window plus the job interval under 7 days. The default is 6 days + 1 hour.
- With `Payments:Provider=Fake`, use the in-app job rather than the CLI command. The fake keeps payments in memory, so a separate process can't see them.

## Configuration

Settings live in `src/Stamp.Web/appsettings*.json`, overridable by environment variables (`Section__Key`).
Secrets marked 🔒 belong in user-secrets or environment variables only.

| Setting | Default | Notes |
|---|---|---|
| `App:BaseUrl` | `https://localhost:5001` (dev) | Public origin used in emails and Stripe redirects. **Required** in production. |
| `Payments:Provider` | `Stripe` | `Fake` = in-memory stand-in for development. Refused in Production. |
| `Stripe:SecretKey` 🔒 | | `sk_test_…` / `sk_live_…` |
| `Stripe:PublishableKey` | | `pk_…`, used by Stripe.js |
| `Stripe:WebhookSecret` 🔒 | | `whsec_…` of the webhook endpoint (or from `stripe listen`) |
| `Stripe:ConnectWebhookSecret` 🔒 | | Production only: secret of a separate Connect-events endpoint |
| `Stripe:ConnectAccountCountry` | platform's country | Country for new Express accounts, e.g. `US` |
| `Email:Provider` | `Resend` (`File` in dev) | `File` writes emails to disk and logs them. Refused in Production. |
| `Email:From` | | e.g. `Stamp <no-reply@yourdomain.com>` (a verified domain for Resend) |
| `Email:ResendApiKey` 🔒 | | `re_…` |
| `Database:Provider` | `Sqlite` | `Sqlite` or `Postgres` |
| `ConnectionStrings:Stamp` 🔒 | `Data Source=App_Data/stamp.db` | Relative SQLite paths resolve under the app's content root |
| `Database:AutoMigrate` | `true` | Apply migrations on startup |
| `Stamps:ReplyWindow` | `6.00:00:00` | Must be under 7 days |
| `Stamps:PlatformFeePercent` | `10` | Application fee on captured stamps |
| `Stamps:AbandonUnpaidAfter` | `1.00:00:00` | When unpaid drafts are abandoned |
| `Stamps:SenderRateLimit` / `SenderRateLimitWindow` | `5` / `01:00:00` | Stamps per sender email, across all receivers |
| `RateLimits:SendStampPerIp` / `LoginPerIp` / `Window` | `10` / `10` / `00:10:00` | Per client IP |
| `Auth:MagicLinkLifetime` | `00:15:00` | Sign-in links are single-use |
| `Auth:MagicLinksPerEmailPerHour` | `5` | Stops the form being used to flood an inbox |
| `Jobs:Expiry:Enabled` / `Interval` | `true` / `01:00:00` | The in-app expiry job |

## Database

**SQLite** is the default. **PostgreSQL** needs only two settings:

```bash
Database__Provider=Postgres
ConnectionStrings__Stamp="Host=localhost;Database=stamp;Username=stamp;Password=..."
```

Each provider has its own migration set, under `src/Stamp.Infrastructure/Persistence/Migrations/{Sqlite,Postgres}`.
Migrations apply on startup. To apply them explicitly, for example in a deploy step with `Database__AutoMigrate=false`:

```bash
dotnet run --project src/Stamp.Web -- migrate
```

After changing the model, add a migration for **both** providers:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/Stamp.Infrastructure --startup-project src/Stamp.Web \
  --context SqliteStampDbContext --output-dir Persistence/Migrations/Sqlite
dotnet ef migrations add <Name> --project src/Stamp.Infrastructure --startup-project src/Stamp.Web \
  --context PostgresStampDbContext --output-dir Persistence/Migrations/Postgres
```

A test (`MigrationTests`) fails if either migration set falls out of sync with the model.

## Tests

```bash
dotnet test
```

| Project | What it covers |
|---|---|
| `Stamp.Domain.Tests` | The stamp lifecycle state machine (Pending → Replied / Declined / Expired, plus drafts), fee math, handle, price and text rules, login tokens |
| `Stamp.Application.Tests` | Every use case against a real SQLite database with a controllable fake payment provider and a fake clock that jumps 6 days: reply/decline/expiry, webhook idempotency, capture and cancel retries, recovered drafts, rate limits, the block list, magic links, the email outbox and the hosted job |
| `Stamp.IntegrationTests` | The web app end to end (pages, antiforgery, the stamp endpoint, signed Stripe webhooks, rate limits, access control), persistence on SQLite and PostgreSQL, the Stripe webhook parser, email senders, and a **Stripe test-mode suite** |

Some suites only run when their environment variable is set; otherwise they're skipped:

| Variable | Enables |
|---|---|
| `STAMP_TEST_POSTGRES` | Persistence tests against a PostgreSQL server where the tests may create databases, e.g. `Host=localhost;Username=postgres;Password=postgres`. Throwaway databases are created and dropped. |
| `STRIPE_TEST_SECRET_KEY` | The Stripe suite, which calls the real Stripe API in test mode. It refuses live keys. |
| `STRIPE_TEST_CONNECTED_ACCOUNT` | The suite's payment tests. Set it to an onboarded test Express account (`acct_…`), for example one you connected through the app, because destination charges need an active connected account. |

`global.json` opts `dotnet test` into Microsoft.Testing.Platform, which xUnit v3 requires on the .NET 10 SDK.

## How it works

### Project layout

```
src/
  Stamp.Domain/          Entities and rules. No dependencies.
  Stamp.Application/     Use cases and ports: IPaymentProvider, IPaymentWebhookParser, IEmailSender, IAppUrls
  Stamp.Infrastructure/  EF Core (SQLite + PostgreSQL), Stripe adapter, Resend/file email, outbox dispatcher, expiry job
  Stamp.Web/             Razor Pages, endpoints, cookie auth, rate limiting, composition root, CLI commands
tests/
  Stamp.Domain.Tests/  Stamp.Application.Tests/  Stamp.IntegrationTests/
```

Dependencies point inward, and architecture tests check it. The payment provider is behind
`IPaymentProvider` and a provider-neutral webhook event, so another provider (e.g. Razorpay) can be added
as a new adapter.

### The stamp lifecycle

```
submit ─► AwaitingPayment ──card authorized──► Pending ──reply (≥20 chars, before expiry)──► Replied  → capture
               │                                  ├──decline──────────────────────────────► Declined → release hold
               │ not paid within 24h              └──window passed (job) / hold released──► Expired  → release hold
               ▼
           Abandoned → void payment
```

- **Two statuses per message.** The message has a workflow status and, separately, a payment status (Created → Authorized → Captured | Canceled). The payment status only moves forward.
- **Drafts come first.** A message is saved as `AwaitingPayment` before the card step, because its text doesn't fit in Stripe metadata. Receivers never see those drafts.

### Money safety

- **Transitions commit before money moves.** A transition and its emails are saved together (an email outbox). Only then does Stamp capture or cancel at Stripe, with an idempotency key (`stamp-capture-{id}` / `stamp-cancel-{id}`), and record the outcome.
  - If Stripe is unavailable, the expiry job retries, and webhooks confirm the result.
  - So a sender is only charged once a reply has been saved, and an outage never loses a reply.
- **The reply window follows Stripe's clock.** The window starts from the time Stripe reports the authorization (the webhook event's timestamp), not from when Stamp receives the webhook. It always closes before the card hold lapses.
- **Webhooks apply once.** Stripe signatures are verified with a 5-minute replay window. Each event id is recorded in the same transaction as its effects, and every transition checks the current state first, so redelivered or out-of-order events apply once.
- **Racing updates can't both win.** Every row has a concurrency stamp. A reply and the expiry job can't both succeed, and one sign-in link can't be redeemed twice.
- **Emails survive outages.** Emails go through an outbox with retries and backoff. Each is leased before sending, and Resend gets the outbox id as its idempotency key.

### Abuse protection

- **Rate limits:**
  - per client IP, on the stamp endpoint and the sign-in form
  - per sender email, across all receivers
  - per address, on sign-in link requests
- **Block list.** Receivers can block a sender's email from the message page, or manage the list under Settings.
- **Sign-in links:**
  - single-use and short-lived
  - stored only as a SHA-256 hash
  - completed with a POST, so email link scanners can't use them up
- **Forms.** Every form, and the JSON endpoint, is protected against cross-site request forgery.

## Before going to production

- **Configuration.**
  - Set `App:BaseUrl` to your https origin.
  - Use live Stripe keys, `Email:Provider=Resend` with a verified `Email:From` domain, and PostgreSQL.
- **Stripe webhooks.**
  - In the dashboard, add an endpoint at `https://<your-domain>/webhooks/stripe` for `payment_intent.amount_capturable_updated`, `payment_intent.succeeded` and `payment_intent.canceled`.
  - Add a second endpoint (**Connected accounts**) for `account.updated`.
  - Put their signing secrets in `Stripe:WebhookSecret` and `Stripe:ConnectWebhookSecret`.
- **Reverse proxy.** Behind a proxy or load balancer, set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`. Otherwise every request appears to come from the proxy, and the per-IP rate limits apply to everyone at once.
- **Sign-in cookie keys.** The keys that protect sign-in cookies are stored in the database so sessions survive restarts. Consider encrypting them at rest (`ProtectKeysWithCertificate`, or a key vault).
- **Fees.** With destination charges, the platform pays Stripe's processing fee (about 2.9% + 30¢ on US cards). A $2 stamp earns 20¢ in fees but costs about 36¢. Consider a higher minimum price or a fixed-fee floor.

Out of scope for this MVP: Gmail/Outlook integration, mobile apps, teams, charity payouts (the setting is only stored), and analytics.
