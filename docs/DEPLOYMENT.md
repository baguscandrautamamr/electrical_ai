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

`vercel.json` already registers the cron jobs: cleanup every six hours, credential
expiry daily at 01:00 UTC. Both require `Authorization: Bearer $CRON_SECRET`,
which Vercel Cron supplies automatically.

> Environment variables are read at request time, but a running deployment does
> not pick up new values. **Redeploy after changing any variable.**

## 4. Register the webhook

Point Telegram at the deployed function:

```bash
curl -X POST "https://api.telegram.org/bot$TELEGRAM_BOT_TOKEN/setWebhook" \
  -H 'content-type: application/json' \
  -d '{
    "url": "https://<your-deployment>.vercel.app/api/telegram/message",
    "secret_token": "<TELEGRAM_WEBHOOK_SECRET>",
    "allowed_updates": ["message", "edited_message"]
  }'
```

Verify:

```bash
curl "https://api.telegram.org/bot$TELEGRAM_BOT_TOKEN/getWebhookInfo"
curl "https://<your-deployment>.vercel.app/api/health"
```

`/api/health` returns 200 when Supabase is reachable and the queue is not
backlogged, and 503 otherwise — point an uptime monitor at it.

## 5. Seed a project and a user

Registration is deliberately an admin action: an unknown Telegram sender gets a
"not registered" reply rather than self-service access to a live model.

Get your numeric Telegram id from [@userinfobot](https://t.me/userinfobot), then
in the Supabase SQL editor:

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

**The sweeper.** `/api/telegram/callback` flushes results the webhook did not
wait for. The cleanup cron calls it every six hours, which is fine for
correctness but slow for a user watching their phone. If Revit is routinely
slow, hit it more often:

```bash
curl "https://<your-deployment>.vercel.app/api/telegram/callback"
```

It is idempotent — the `webhook_sent` flag prevents double-posting.

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
