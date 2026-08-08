# Revit Electrical Command Center

Place electrical devices in a Revit model by sending a Telegram message.

An engineer types `/create_cable_tray CT-A1 from=PA-01 to=Zone_A hanger_spacing=1500`
into Telegram. A Vercel webhook parses it, validates it, and puts it on a
Supabase queue. A Revit add-in polls that queue, builds the tray, places the
hangers, and writes the result back. The engineer gets a reply about eight to
twelve seconds later.

```
Telegram ──▶ Vercel webhook ──▶ Supabase queue ──▶ Revit add-in (polling)
   ▲                                   │                      │
   └────────── result reply ◀──────────┴──── result written ◀──┘
```

**Why a queue and not a socket.** Revit runs on an engineer's laptop behind a
corporate firewall, and it is not always open. Polling over outbound HTTPS needs
no inbound port, no VPN, and no IT ticket, and a command sent while Revit is
closed simply runs when it next opens.

## What it does

Eight device categories, each placeable from Telegram:

| Category | Command | What it automates |
|---|---|---|
| Lighting | `/place_lighting` | Fixture count from a lux target, ceiling grid, load, circuits |
| Receptacle | `/place_receptacle` | Perimeter outlets, load, circuit split |
| Cable tray + hangers | `/create_cable_tray` | Routing, tray sizing, **smart hanger placement** |
| Fire alarm | `/place_fire_alarm` | NFPA 72 spacing, loop addressing, compliance checks |
| Telephone | `/place_telephone` | Jack placement |
| LAN | `/place_lan` | Jacks, switch-port allocation, PoE budget |
| Security | `/place_security` | Cameras/sensors, coverage from FoV and resolution |
| Communication | `/place_communication` | Speakers/antennas, coverage radius |

Plus `/equip_room` to run all eight against one room, `/export` for schedules
and reports, and `/query` to read back what is already in the model —
`/query Office_A what=lighting`, or just "ada berapa lampu di Office_A?".
`/query` opens no Revit transaction, so it cannot change the drawing.

### The hanger automation

This is the feature the system exists for. Given a tray run and a spacing, it:

1. **Auto-matches the hanger type to the tray size.** A 150×100 mm tray gets
   hanger type `"150x100"`; a 600 mm ladder gets `"600"`. If there is no exact
   match it takes the next size *up*, never down.
2. **Preserves the hangers already in the model.** Engineers hand-place hangers
   around structure, ducts and other services, and those positions encode
   knowledge the model does not. Only the genuine gaps get filled.
3. **Skips vertical drops.** Hangers apply to horizontal runs; vertical segments
   are counted and reported, not hung.
4. **Calculates load per hanger** from tray mass and cable fill, and reports it
   against the family's rated capacity.

The pure geometry is specified and tested in
[`src/hangers/gapfill.ts`](src/hangers/gapfill.ts), and mirrored in
[`HangerPositionCalculator.cs`](revit-addin/RevitCommandCenter.Electrical/SmartHangers/HangerPositionCalculator.cs).
See [docs/HANGERS.md](docs/HANGERS.md) for the algorithm in detail.

### Bilingual and themed

Every reply is rendered in the user's language (Indonesian or English) and
theme (Apple-glass light or dark), stored per user. `/lang en`, `/theme dark`.

## Repository layout

```
api/                      Vercel serverless functions
  telegram/message.ts       webhook: parse, validate, enqueue, reply
  telegram/callback.ts      result sweeper for late-finishing commands
  admin/setup.ts            one-URL Telegram wiring (webhook + command menu)
  cron/cleanup.ts           archive + timeout sweep (daily)
  cron/check-api-expiry.ts  credential expiry notices (daily)
  health.ts                 liveness + queue depth
.github/workflows/
  sweep.yml                 15-minute sweeper, stands in for a sub-daily cron
public/index.html         landing page + the static output dir Vercel's build needs
src/
  parser/                 grammar, Claude NLP fallback, validation, schema
  format/                 bilingual, theme-aware message rendering
  services/               users, queue, credentials, admin, delivery
  hangers/gapfill.ts      hanger algorithm reference implementation
  i18n/                   id.json, en.json
  theme/                  light/dark tokens
  lib/                    Supabase and Telegram clients
supabase/migrations/      schema, indexes, RLS, queue RPCs
revit-addin/              C# Revit 2025 add-in (.NET 8)
tests/                    vitest suites
docs/                     deployment, add-in install, commands, troubleshooting
```

## Getting started

```bash
npm install
cp .env.example .env.local     # then fill it in
npm run check                  # typecheck + tests
```

Then follow, in order:

1. **[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)** — Supabase, Vercel, the Telegram bot.
2. **[docs/REVIT-ADDIN.md](docs/REVIT-ADDIN.md)** — building and installing the add-in.
3. **[docs/COMMANDS.md](docs/COMMANDS.md)** — the full command reference.

## Environment variables

Set these in `.env.local` for development and in Vercel → Settings →
Environment Variables for deployment. Names and purposes only — never commit
values. Full list in [`.env.example`](.env.example).

| Variable | Required | Purpose |
|---|---|---|
| `ANTHROPIC_API_KEY` | yes | Claude API, for natural-language command parsing |
| `ANTHROPIC_MODEL` | no | Model override; defaults to `claude-opus-5` |
| `ANTHROPIC_BASE_URL` | no | Anthropic-compatible gateway to call instead of Anthropic; the key must then be the gateway's |
| `TELEGRAM_BOT_TOKEN` | yes | Bot token from @BotFather |
| `TELEGRAM_WEBHOOK_SECRET` | recommended | Shared secret that authenticates the webhook |
| `SUPABASE_URL` | yes | Supabase project URL |
| `SUPABASE_ANON_KEY` | yes | Anon key (RLS-protected) |
| `SUPABASE_SERVICE_ROLE_KEY` | yes | **Server only.** Bypasses RLS |
| `CRON_SECRET` | yes | Bearer secret required by `/api/cron/*` |
| `POLLING_INTERVAL_SECONDS` | no | Add-in poll interval; default 4 |
| `MAX_COMMAND_RETRY` | no | Attempts before permanent failure; default 3 |
| `COMMAND_TIMEOUT_SECONDS` | no | Before a stuck command is reclaimed; default 120 |

The Revit add-in reads its own settings from
`%APPDATA%\RevitCommandCenter\config.json`, not from these.

## Commands

```bash
npm run dev          # vercel dev
npm run typecheck    # tsc --noEmit
npm run test         # vitest
npm run check        # typecheck + test
npm run sync:i18n    # copy src/i18n into the add-in's resources
```

Building the add-in needs Windows with the .NET 8 SDK and Revit 2025 installed:

```powershell
cd revit-addin/RevitCommandCenter.Electrical
dotnet build -c Release
```

## Security notes

- The service role key bypasses RLS. It belongs on the Vercel server and the
  add-in machine, never in a browser.
- API credentials are stored as SHA-256 hashes with a four-character hint. The
  plaintext is shown once, at `/api connect`, and never again.
- RLS policies enforce per-project isolation for any anon/authenticated client.
- The webhook verifies Telegram's `X-Telegram-Bot-Api-Secret-Token`; the cron
  endpoints require `Authorization: Bearer $CRON_SECRET`.

## Status

The TypeScript half — webhook, parser, formatters, services, schema, cron jobs —
is complete, typechecked and covered by 134 tests, and is deployed.

The C# add-in **compiles** against the Revit 2025 API in CI (the `Revit add-in`
workflow, which publishes the installable folder as an artifact). That is a
compile-time guarantee only: nothing has yet run inside Revit against a real
model. The Revit API surface it uses (`CableTray.Create`, `NewFamilyInstance`,
`ExternalEvent`, `FilteredElementCollector`) is standard for Revit 2025, and the
pure geometry it depends on is tested via the TypeScript mirror — but treat the
first run in Revit as part of the work, not a formality.
[docs/REVIT-ADDIN.md](docs/REVIT-ADDIN.md) covers what to check.
