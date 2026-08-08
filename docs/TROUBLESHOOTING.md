# Troubleshooting

## The Vercel deployment fails

A deployment that errors after a few seconds failed to *build* — env vars are
read per request, so a missing one cannot break a build. Read the build log:

- *No Output Directory named "public" found* → `public/` is missing. The `build`
  script makes Vercel expect a static output directory; see
  [DEPLOYMENT.md](DEPLOYMENT.md) §3.
- *Cron expressions must be...* / a cron limit error → a schedule is more
  frequent than the Hobby plan's once-a-day limit. See DEPLOYMENT.md §3.
- A `tsc` error → the typecheck gate did its job. `npm run check` locally.

`npx vercel build` reproduces the deployment build on your machine.

## `FUNCTION_INVOCATION_FAILED` on every endpoint

The build succeeded and the function died on import. If it happens on *every*
endpoint at once it is not an env var — a missing variable produces a 503 with a
`MissingEnvError` body, not a crash.

The usual cause is a module specifier that does not resolve at runtime. Vercel
transpiles each file and leaves import paths untouched, so `from './x.ts'`
survives into the emitted `.js` and Node cannot find it. Relative imports must
carry the emitted `.js` extension; `tests/imports.test.ts` enforces this,
because tsc and vitest both resolve `.ts` and would otherwise stay silent.

Reproduce it without deploying — build, then load each function:

```bash
npx vercel build
cd .vercel/output/functions/api/health.func && node -e "import('./api/health.js')"
```

## The bot does not reply at all

```bash
curl "https://api.telegram.org/bot$TELEGRAM_BOT_TOKEN/getWebhookInfo"
```

- `last_error_message` set → read it; usually a 403 from a mismatched
  `TELEGRAM_WEBHOOK_SECRET`, or a 500 from a missing env var.
- `pending_update_count` climbing → the function is failing. Check the Vercel
  function logs.
- `url` empty → the webhook was never registered. See
  [DEPLOYMENT.md](DEPLOYMENT.md) §4.

If the webhook is healthy, check `/api/health` directly. A `MissingEnvError` in
the logs names the variable — and remember Vercel needs a **redeploy** after any
variable change.

## "You are not registered"

Your Telegram id has no `users` row. Registration is an admin action by design.
Get your id from [@userinfobot](https://t.me/userinfobot) and insert the rows
from [DEPLOYMENT.md](DEPLOYMENT.md) §5.

## "No active project"

Either you have no `user_project_access` grant, or you have several projects and
none selected. `/project list` then `/project use <code>`. With exactly one
project the system selects it for you automatically.

## Commands queue but never run

The add-in is not draining the queue. In order:

1. **Is it connected?** Command Center → **Status**. "Connected: no" → press
   Connect.
2. **Does the add-in reach Supabase?** Connect reports it. If not, check
   `supabase_url` and `supabase_key` in
   `%APPDATA%\RevitCommandCenter\config.json`.
3. **Is it draining the right project?** `project_id` must be the **UUID** from
   `projects.id`, not the project code. A wrong-but-valid UUID polls an empty
   queue forever and looks exactly like being idle.
4. **Check the log** — Command Center → Log, or
   `%APPDATA%\RevitCommandCenter\logs\`.

```sql
-- What is actually queued
select id, command_type, status, retry_count, claimed_by, queued_at
from commands_queue
where status in ('pending', 'processing')
order by queued_at;
```

## "Revit did not execute the command within 120s"

Revit received the external event but never ran it. Almost always **a modal
dialog is open** — Revit's event pump is blocked until it is dismissed. Check
for a dialog behind the main window (warnings, "unresolved references", a save
prompt), dismiss it, and the command retries on its own.

## Commands run but no Telegram reply

The result is in the database but was not delivered. Flush the sweeper:

```bash
curl "https://<your-deployment>.vercel.app/api/telegram/callback"
```

```sql
select id, command_type, status, webhook_sent, completed_at
from commands_queue
where webhook_sent = false and status in ('completed', 'failed');
```

If `webhook_sent` is already `true` but nothing arrived, the send itself failed —
check the Vercel logs for a Telegram API error. Delivery marks the row *before*
sending, deliberately: a rare lost message is a better failure than a duplicate.

## Hangers

### None were placed

Check the reply text — it names the cause.

- *"No hanger type matches a 150x100 tray"* — the family has no type named
  `150x100`, no `150`, and nothing larger. Type names **are** the auto-match;
  see [HANGERS.md](HANGERS.md).
- *"Every segment is vertical"* — the run rises more than 10 mm end to end.
  Hangers only apply to horizontal tray.
- *"Hanger family 'Hanger' not found"* — the family is not loaded, or
  `hanger_family_name` in the add-in config does not match it exactly.

### Existing hangers were duplicated

The existing hangers are not *hosted on* the tray element. The query matches on
`instance.Host.Id == tray.Id`; a free-standing hanger placed at the same
coordinates is invisible to it and reads as a gap. Re-host them on the tray.

### Existing hangers were ignored and new ones added alongside

They are further than 50 mm from the ideal stations. That tolerance is
deliberate — see [HANGERS.md](HANGERS.md#2-gap-fill-preserving-existing-hangers).
Either accept the extra supports or use a spacing that matches the existing
layout.

### The positions look wrong

The C# and TypeScript implementations may have diverged. Run
`npm run test -- tests/hangers.test.ts` and diff
`HangerPositionCalculator.cs` against `src/hangers/gapfill.ts`. They mirror each
other function for function, and a divergence produces wrong positions rather
than an error.

## "Room 'X' not found"

Room matching tries name, then number, then a name prefix — all
case-insensitive. Common causes:

- The room is not **placed** (an unplaced room has zero area and is skipped).
- The name has trailing whitespace or a different separator (`Office A` vs
  `Office_A`).
- You are in the wrong view or the wrong model.

Quote names with spaces: `/place_lighting "Meeting Room 2" area=30`.

## Natural language does not work

Send `/health` first. The **AI parser** row says whether the configured key can
reach the configured model, and prints the API's own error when it cannot —
that answers the question in one message instead of a log dive.

The reply tells you which of these you have:

| Reply | Meaning | Fix |
| --- | --- | --- |
| "Bahasa bebas sedang mati / Plain-language messages are off" | `ANTHROPIC_API_KEY` is not set on the deployment | Add it in Vercel → Settings → Environment Variables, then **redeploy** — env changes do not reach the running deployment on their own |
| "Panggilan ke Claude API gagal / The Claude API call failed" + a code block | The key exists but the call was rejected. The code block is the API's own message | `401 authentication_error` → wrong or revoked key (check for a pasted newline). `400 invalid_request_error … credit balance` → top up at console.anthropic.com. `404 not_found_error` → the account cannot use `ANTHROPIC_MODEL`; unset it to fall back to `claude-opus-5`, or set one it can use. `429` → rate limited, retry |
| "Kalimatnya saya mengerti, tapi bukan perintah Revit / not a Revit command" | The key works. The sentence asked for something outside what the bot does — placing or modifying elements, or reading them back with `/query` | See `/help` |
| "belum cukup yakin / not sure enough to run it" | Parsed below the 0.55 confidence floor and rejected rather than guessed at | Rephrase, or use the exact form from `/help` |

Other notes:

- Slash commands never call Claude. They keep working while any of the above is
  broken.
- `max_tokens` for the parse call is 4096. Current models think by default and
  the cap covers thinking *and* the JSON, so a smaller value truncates the
  answer; a truncated response is reported as such rather than as bad phrasing.

## Cost is higher than expected

Slash commands never call Claude. High spend means people are writing prose.
Share [COMMANDS.md](COMMANDS.md), or register the command list with
`/setcommands` so Telegram autocompletes them.

## Supabase API quota

One Revit instance polling every 4 seconds is ~7,200 calls/day, which exceeds
the free tier's ~50k/month on its own. Either raise
`polling_interval_seconds`, connect only while actually working, or move to Pro.
`/api/health` reports queue depth if you want to tune the interval against real
load.

## Useful queries

```sql
-- Recent failures
select command_type, error_message, retry_count, completed_at
from commands_queue
where status = 'failed'
order by completed_at desc limit 20;

-- Throughput and latency
select command_type,
       count(*) as runs,
       round(avg(execution_time_ms)) as avg_ms,
       max(execution_time_ms) as max_ms
from commands_queue
where status = 'completed'
group by command_type;

-- Stuck in processing (reclaimed automatically after the timeout)
select id, command_type, claimed_by, started_at, now() - started_at as age
from commands_queue
where status = 'processing';

-- Credentials expiring within a week
select u.full_name, c.key_hint, c.expires_at
from api_credentials c join users u on u.id = c.user_id
where c.is_active and c.expires_at < now() + interval '7 days';
```

## Reset the queue

Non-destructive to the model — it only clears pending work:

```sql
update commands_queue
set status = 'cancelled', completed_at = now(), webhook_sent = true
where status in ('pending', 'processing');
```
