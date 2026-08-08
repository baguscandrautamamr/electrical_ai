# Deployment

Order matters: Supabase first (the webhook needs it), then Vercel, then the
Telegram webhook registration, then seed a project and user.

## 1. Supabase

1. Create a project at [supabase.com](https://supabase.com). The free tier is
   enough — the queue is small and short-lived.
2. Apply the schema:

   ```bash
   # via the Supabase CLI
   supabase link --project-ref <your-ref>
   supabase db push

   # or directly
   psql "$DATABASE_URL" -f supabase/migrations/0001_init.sql
   ```

3. From **Settings → API**, copy:
   - Project URL → `SUPABASE_URL`
   - `anon` key → `SUPABASE_ANON_KEY`
   - `service_role` key → `SUPABASE_SERVICE_ROLE_KEY`

The migration creates the tables, the polling indexes, RLS policies, and three
queue functions: `claim_next_command`, `complete_command`, `fail_command`.

> **`claim_next_command` is load-bearing.** It claims a row with
> `FOR UPDATE SKIP LOCKED` in a single statement. Without it, two Revit
> instances polling the same project would both read the same pending row and
> execute the command twice. Do not replace it with a SELECT then UPDATE.

## 2. Telegram bot

1. Message [@BotFather](https://t.me/BotFather) → `/newbot`.
2. Copy the token → `TELEGRAM_BOT_TOKEN`.
3. Invent a webhook secret (any long random string) → `TELEGRAM_WEBHOOK_SECRET`.
4. Optionally register the command list with `/setcommands`:

   ```
   place_lighting - Place light fixtures in a room
   place_receptacle - Place outlets in a room
   create_cable_tray - Route a cable tray and place hangers
   add_hangers - Add hangers to an existing tray
   place_fire_alarm - Place NFPA 72 detectors
   place_telephone - Place telephone jacks
   place_lan - Place network jacks
   place_security - Place cameras and sensors
   place_communication - Place speakers and antennas
   equip_room - Place all eight categories in one room
   export - Generate schedules and reports
   project - List or switch project
   api - Manage API credentials
   health - System status
   theme - Switch light/dark
   lang - Switch id/en
   help - Command reference
   ```

## 3. Vercel

```bash
npm i -g vercel
vercel link
```

Add every variable from `.env.example` under **Settings → Environment
Variables**. Set them per scope — Production, Preview and Development are
separate, and if staging points at a different Supabase project it must get
different values.

```bash
vercel --prod
```

`package.json` defines a `build` script (a typecheck), so Vercel runs it and
then expects a static output directory. This project is API-only, so `public/`
exists purely to be that directory — `vercel.json` points `outputDirectory` at
it, and it serves a small landing page at `/`. Deleting it fails the build with
*No Output Directory named "public" found after the Build completed*, before any
function is served.

Reproduce a deployment build locally before pushing:

```bash
npx vercel build
```

`vercel.json` already registers the cron jobs: cleanup daily at 02:00 UTC,
credential expiry daily at 01:00 UTC. Both require
`Authorization: Bearer $CRON_SECRET`, which Vercel Cron supplies automatically.

### Cron on the Hobby plan

Hobby allows **two cron jobs per project, each firing once a day** at an
approximate time within the scheduled hour. Anything more frequent —
`0 */6 * * *`, for example — is rejected at deploy time with a cron-limit error.
The two schedules above sit inside that limit, so the project deploys on Hobby
as-is. Nothing needs to be removed or commented out.

What the daily limit costs is *timeliness*, not correctness: a command stranded
in `processing` is only failed and reported on the next sweep. To keep that
prompt without upgrading, `.github/workflows/sweep.yml` calls the sweeper
endpoint every 15 minutes from GitHub Actions — set the repository Actions
variable `DEPLOYMENT_URL` to your deployment origin and it starts working; leave
it unset and the workflow no-ops. Any external scheduler works just as well
(cron-job.org, UptimeRobot, a machine you already leave on):

```bash
curl "https://<your-deployment>.vercel.app/api/telegram/callback?limit=50"
```

On a Pro plan, tighten `vercel.json` back to `0 */6 * * *` and delete the
workflow.

> Environment variables are read at request time, but a running deployment does
> not pick up new values. **Redeploy after changing any variable.**

## 4. Register the webhook

**A deployed bot stays silent until this step.** Telegram only pushes updates to
a URL you have registered, so `/api/health` can be perfectly green while the bot
ignores every message.

`npm run` only works **inside a clone of this repository** — it reads the
scripts out of `package.json`, so running it from your home directory fails with
`Could not read package.json`. If you have no clone, skip to
[By hand](#by-hand) below; it needs nothing but PowerShell.

With `TELEGRAM_BOT_TOKEN` and `TELEGRAM_WEBHOOK_SECRET` in `.env.local`:

```bash
git clone https://github.com/<you>/electrical_ai
cd electrical_ai
npm install

npm run webhook:set -- https://<your-deployment>.vercel.app
npm run webhook:info
```

Environment variables set in the shell win over `.env.local`, so you can also
export the two secrets instead of writing a file.

Use the **production** URL. Preview URLs change on every deploy, so a webhook
pointed at one breaks as soon as you push again.

`webhook:info` prints what Telegram thinks the state is, including
`last_error_message` — where a mismatched secret (403) or a crashing function
(500) actually shows up. `npm run webhook:delete` unregisters it.

### By hand

Two calls, no clone required. The first points Telegram at the deployment; the
second publishes the command menu — the button beside the chat input, without
which a new user sees no hint that the bot understands anything.

```bash
curl -X POST "https://api.telegram.org/bot$TELEGRAM_BOT_TOKEN/setWebhook" \
  -H 'content-type: application/json' \
  -d '{
    "url": "https://<your-deployment>.vercel.app/api/telegram/message",
    "secret_token": "<TELEGRAM_WEBHOOK_SECRET>",
    "allowed_updates": ["message", "edited_message", "callback_query"]
  }'

curl -X POST "https://api.telegram.org/bot$TELEGRAM_BOT_TOKEN/setMyCommands" \
  -H 'content-type: application/json' \
  -d "{\"commands\": $(cat src/services/bot-menu.json)}"
```

Without a clone, paste the contents of
[`src/services/bot-menu.json`](../src/services/bot-menu.json) in place of the
`cat`. That file is the source of truth for the menu; `npm run webhook:menu`
sends exactly it.

Then check the deployment itself:

```bash
curl "https://<your-deployment>.vercel.app/api/health"
```

`/api/health` returns 200 when Supabase is reachable and the queue is not
backlogged, and 503 otherwise — point an uptime monitor at it.

## 5. Seed a project and a user

Registration is deliberately an admin action: an unknown Telegram sender gets a
"not registered" reply rather than self-service access to a live model.

Send the bot any message. It replies "not registered" **and tells you your
numeric Telegram id** — that is the value the insert is keyed on.

Then paste [`supabase/seed_first_user.sql`](../supabase/seed_first_user.sql)
into the Supabase SQL editor, edit the five values at the top, and run it. It
creates the project, makes you an admin on it, and prints the project id the
Revit add-in needs. Re-running it is safe.

The same thing spelled out, if you would rather do it by hand:

```sql
-- 1. A project
insert into projects (code, name, client, location)
values ('SITE-A', 'Site A Tower', 'Client Name', 'Jakarta')
returning id;

-- 2. A user
insert into users (telegram_user_id, telegram_username, full_name, role, language, theme)
values (123456789, 'yourhandle', 'Your Name', 'admin', 'id', 'light')
returning id;

-- 3. Grant access and make it the active project
insert into user_project_access (user_id, project_id, role)
select u.id, p.id, 'admin'
from users u, projects p
where u.telegram_user_id = 123456789 and p.code = 'SITE-A';

update users
set active_project = (select id from projects where code = 'SITE-A')
where telegram_user_id = 123456789;
```

Send `/health` to the bot. A status card back means the webhook, Supabase and
your user record are all wired up.

## 6. Connect Revit

See [REVIT-ADDIN.md](REVIT-ADDIN.md). Until the add-in is running and connected,
commands queue up and report a timeout after `COMMAND_TIMEOUT_SECONDS` — which
is the intended behaviour, not a fault.

## Operating notes

**Latency.** Roughly 8–12 seconds end to end: up to 4s for the add-in's next
poll, 2–4s to execute, ~2s to deliver the result. The webhook waits inline for
up to 40s; anything slower is delivered by the sweeper.

**The sweeper.** `/api/telegram/callback` reclaims commands whose Revit instance
never reported back, then flushes results the webhook did not wait for. The
daily cleanup cron does the same work, which is enough for correctness but slow
for a user watching their phone — so the GitHub Actions workflow above drives it
every 15 minutes. Hit it by hand whenever you want:

```bash
curl "https://<your-deployment>.vercel.app/api/telegram/callback"
```

It is idempotent — the `webhook_sent` flag prevents double-posting, and the
timeout filter only matches commands already past `COMMAND_TIMEOUT_SECONDS`.

**Scaling past the free tier.** Supabase Free allows ~50k API calls/month. One
Revit instance polling every 4 seconds during an 8-hour day is about 7,200
calls/day, so a single always-connected instance will exceed it. Either raise
`polling_interval_seconds` in the add-in config, connect only while working, or
move to Supabase Pro.

## Cost

| Service | Monthly |
|---|---|
| Anthropic Claude API | ~$2–5 (only for natural-language parses) |
| Supabase | $0 free tier, $25 Pro |
| Vercel | $0 hobby |
| Telegram | $0 |

Slash commands never call Claude — the deterministic grammar handles them — so
API spend tracks how often people write prose instead of commands.
