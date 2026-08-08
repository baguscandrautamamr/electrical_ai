# Revit add-in: build and install

Requires **Windows** and the **.NET 8 SDK**. A **Revit 2025** install is used
when present but is not required to compile — see [Where the Revit API comes
from](#where-the-revit-api-comes-from).

## Build in CI

The `Revit add-in` workflow builds this project on every push that touches
`revit-addin/`, and on demand from the Actions tab. The run uploads an artifact
named `RevitCommandCenter.Electrical-2025` — download it, unzip it, and it is
the folder to install. Use this if you do not have a Windows machine handy.

## Build locally

```powershell
cd revit-addin\RevitCommandCenter.Electrical
dotnet build -c Release
```

If Revit is not on the default path:

```powershell
dotnet build -c Release -p:RevitApiDir="D:\Autodesk\Revit 2025"
```

### Where the Revit API comes from

The project builds against `RevitAPI.dll` and `RevitAPIUI.dll` from your Revit
install when it finds them, and against Nice3point's published reference
assemblies when it does not — which is what lets CI compile without Revit. Same
public API surface either way; the reference assemblies contain no runtime code
and are excluded from the output, because Revit loads the real ones itself and
must not find a second copy.

Force one or the other:

```powershell
dotnet build -c Release -p:UseRevitNuGet=true    # reference assemblies
dotnet build -c Release -p:UseRevitNuGet=false   # your local install
```

A CI build proves the code compiles against the Revit 2025 API. It cannot prove
behaviour inside Revit — that still needs a real install and a real model.

## Install

Copy the build output and the manifest to Revit's add-ins folder:

Revit discovers add-ins by scanning `Addins\2025` for `.addin` manifests. It
scans that folder **only** — a manifest one level down is never found. The
assembly it names, on the other hand, may live in a subfolder, and should:
the build output is around a hundred files, and that folder is shared with
every other add-in you have installed.

So the layout is two items, side by side:

```
%APPDATA%\Autodesk\Revit\Addins\2025\
  RevitCommandCenter.Electrical.addin      <- manifest, must be at this level
  RevitCommandCenter.Electrical\           <- the DLLs, Resources, runtimes
```

### From the CI artifact

The artifact already has exactly that shape. Unzip it and copy **both items
inside it** into `Addins\2025` — not the unzipped folder itself:

```powershell
$addins = "$env:APPDATA\Autodesk\Revit\Addins\2025"
New-Item -ItemType Directory -Force $addins | Out-Null
Copy-Item .\RevitCommandCenter.Electrical-2025\* $addins -Recurse -Force
```

### From a local build

`bin\Release` is flat, so the manifest's `Assembly` path already matches a
flat install. Move the payload into a subfolder and point the manifest at it:

```powershell
$addins = "$env:APPDATA\Autodesk\Revit\Addins\2025"
$lib    = "$addins\RevitCommandCenter.Electrical"
New-Item -ItemType Directory -Force $lib | Out-Null

Copy-Item .\bin\Release\* $lib -Recurse -Force -Exclude '*.addin'
(Get-Content .\bin\Release\RevitCommandCenter.Electrical.addin -Raw).Replace(
  '<Assembly>RevitCommandCenter.Electrical.dll</Assembly>',
  '<Assembly>RevitCommandCenter.Electrical\RevitCommandCenter.Electrical.dll</Assembly>'
) | Set-Content "$addins\RevitCommandCenter.Electrical.addin"
```

Restart Revit. A **Command Center** tab appears with Connect, Disconnect,
Status and Log.

If no tab appears, the manifest is the first thing to check: it must be
directly in `Addins\2025`, and its `<Assembly>` path must resolve relative to
it.

### Why there are so many files

`EnableDynamicLoading` in the `.csproj` makes the build emit the add-in's
entire dependency closure — EPPlus and iText each pull in a stack of
`Microsoft.Extensions.*` assemblies, plus a `runtimes\` folder. That is
deliberate: it is what lets Revit load the add-in in its own context instead
of fighting over shared assembly versions. Do not prune it by hand.

The Revit API assemblies are the exception and are excluded on purpose — Revit
supplies those itself, and a second copy in the load path causes type-identity
errors. CI fails the build if any slip through.

## Configure

Press **Settings** on the Command Center ribbon. Enter the Supabase project URL
and `service_role` key — both from the Supabase dashboard under Project Settings
→ API — then press **Test connection and load projects**. It reports what
Supabase actually said if something is wrong, and on success fills the project
dropdown from your `projects` table so you pick one by code and name rather than
pasting a UUID. Save, then press **Connect**.

It has to be the `service_role` key — labelled **secret** on newer projects, and
starting `sb_secret_`. The anon/publishable key is the one the dashboard shows
first and it authenticates perfectly well, which is what makes it such an
expensive mistake: row-level security applies to it, so the command queue reads
as permanently empty and the add-in sits there looking idle. Settings and Status
both name the key class now, and Settings refuses to save an anon key.

If the project list comes back empty, the database has no projects yet: run
[`supabase/seed_first_user.sql`](../supabase/seed_first_user.sql) first.

### The file behind the dialog

Settings are stored in `%APPDATA%\RevitCommandCenter\config.json`, which you can
still edit by hand. On first run the add-in writes this template:

```json
{
  "supabase_url": "https://YOUR-PROJECT.supabase.co",
  "supabase_key": "YOUR-SERVICE-ROLE-KEY",
  "project_id": "the projects.id UUID this machine serves",
  "polling_interval_seconds": 4,
  "command_timeout_seconds": 120,
  "hanger_family_name": "Hanger",
  "export_directory": "C:\\Users\\you\\AppData\\Roaming\\RevitCommandCenter\\exports",
  "export_base_url": "",
  "language": "id",
  "start_polling_on_launch": false
}
```

| Key | Notes |
|---|---|
| `supabase_key` | Service role key. This machine is trusted; the key never leaves it. |
| `project_id` | The **UUID** from `projects.id`, not the project code. One Revit instance drains one project's queue. |
| `hanger_family_name` | Must match the family name in your model exactly. |
| `export_base_url` | If exports are served over HTTP, put the base URL here and Telegram replies become clickable links. Otherwise replies carry the local path. |
| `start_polling_on_launch` | `false` by default: opening Revit to look at a model should not silently start mutating it. |

Then press **Connect**. Status shows the connection state and counters; Log
shows recent activity and opens the full file at
`%APPDATA%\RevitCommandCenter\logs\`.

## Model prerequisites

Commands act on what is in the model. Before sending any:

- **Rooms must be placed and named.** A name is matched exactly first — against
  the room's name, its number, and the two together — and only then by prefix,
  and only when the prefix picks out a single room. `meeting` in a model holding
  MEETING 1 and MEETING 2 is reported as ambiguous rather than run against
  whichever came back first. Case, spacing and underscores are ignored, so
  `meeting_1`, `Meeting 1` and `MEETING  1` all reach the same room.
- **Families must be loaded** for each category you use — lighting fixtures,
  lighting devices (switches), electrical fixtures, fire alarm devices, data
  devices, security devices, communication devices, and a cable tray type.
- **Face-based families for wall devices, ideally.** Receptacles, switches, LAN
  and telephone outlets are placed on the wall's vertical face. A family that is
  not face-based still places — it falls back to hosting on the wall, then to no
  host at all — and the log line says which happened.
- **Doors help.** `/place_lighting_device` puts each switch beside a door on the
  room's boundary wall. With no door in the model it falls back to the walls.
- **Hanger family types must be named after tray sizes**: `150x100` for a
  150×100 mm tray, `600` for a 600 mm ladder. This naming *is* the auto-match.
- **Panels should carry a `Mark`** matching what users type (`PA-01`), so
  `from=PA-01` resolves.
- **A document must be open.** Commands arriving with no open document fail as
  retryable and run once you open one.

## How execution works

The threading is the part most likely to bite if you modify it.

```
Timer thread                     Revit API thread
────────────                     ────────────────
claim_next_command  (HTTP)
worker.TrySubmit(cmd)
externalEvent.Raise()  ──────▶   CommandQueueWorker.Execute()
await completion                   ├─ CommandProcessor.Execute()
                                   ├─ handler opens Transaction
                                   └─ resolves TaskCompletionSource
        ◀────────────────────────
flush device rows   (HTTP)
complete_command    (HTTP)
```

Two rules follow from this:

1. **The Revit API may only be touched from the external event handler.** The
   poller stages work and waits; it never calls into the model itself.
2. **Handlers must not block on network I/O.** They queue rows via
   `context.Persist(...)` and the poller flushes them after the transaction has
   committed, off Revit's thread.

Commands are drained one at a time. Concurrent transactions on one `Document`
are not permitted anyway, so a second command arriving while one is in flight is
left on the queue for the next poll.

## First run in Revit

The project compiles against the Revit 2025 API in CI, so the signatures below
resolve. What CI cannot check is behaviour: whether a call does the right thing
to a real model. These are the places most likely to need attention the first
time you actually run it, in rough order of probability:

- **`CableTray.Create` signature.** Verify the overload against your Revit 2025
  API reference; the tray-creation call in `CableTrayHandler` is the most
  version-sensitive line in the project.
- **`BuiltInCategory` names.** `OST_TelephoneDevices`, `OST_SecurityDevices`,
  `OST_CommunicationDevices`, `OST_DataDevices`, `OST_FireAlarmDevices` all
  exist in recent Revit, but confirm each resolves.
- **`Element.LevelId`.** Used in `ExportHandler`; not every element exposes it,
  so it may need a `get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)` fallback.
- **`IFCExportOptions` / `DWGExportOptions`.** Export APIs move between releases.
- **EPPlus licensing.** `ExcelExporter` sets `LicenseContext.NonCommercial`.
  Replace it with a commercial licence before shipping to a paying project.
- **Package versions.** Newtonsoft.Json, EPPlus and itext7 versions in the
  `.csproj` may need bumping for .NET 8.

The parts least likely to need changes are the ones with no Revit dependency:
`HangerPositionCalculator`, `AddinConfig`, `Logger`, `SupabaseClient`,
`Localization`.

## Verifying the hanger logic

The gap-fill algorithm is specified by the test suite on the TypeScript side:

```bash
npm run test -- tests/hangers.test.ts
```

`HangerPositionCalculator.cs` mirrors `src/hangers/gapfill.ts` function for
function. **If you change one, change both** — the header comment on each file
says so, and a divergence here silently produces wrong hanger positions rather
than an error.

To sanity-check against a real model: put two hangers on a 12 m tray at 0 m and
3 m, run `/add_hangers CT-A1 spacing=1500`, and confirm the reply says 9 total,
2 preserved, 7 added — matching the worked example in
[HANGERS.md](HANGERS.md).

## Multiple machines

Two Revit instances can serve the same project. `claim_next_command` uses
`FOR UPDATE SKIP LOCKED`, so each claim goes to exactly one instance, and
`claimed_by` records `MACHINE/PID` for tracing.

If an instance crashes mid-command, the row stays `processing` until
`command_timeout_seconds` elapses; the next claim from any instance reclaims it
and either retries or fails it per `max_retries`.
