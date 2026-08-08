# Revit add-in: build and install

Requires **Windows**, **.NET 8 SDK**, and **Revit 2025** installed (the build
references `RevitAPI.dll` from the install directory).

> **This add-in has not been compiled.** It was written in an environment
> without Windows, the .NET SDK, or the Revit API assemblies. Expect to fix
> compile errors on the first build — see [First build](#first-build) below for
> the specific places to look. The pure geometry it depends on *is* tested, via
> the TypeScript mirror in `src/hangers/gapfill.ts`.

## Build

```powershell
cd revit-addin\RevitCommandCenter.Electrical
dotnet build -c Release
```

If Revit is not on the default path:

```powershell
dotnet build -c Release -p:RevitApiDir="D:\Autodesk\Revit 2025"
```

## Install

Copy the build output and the manifest to Revit's add-ins folder:

```powershell
$addins = "$env:APPDATA\Autodesk\Revit\Addins\2025"
New-Item -ItemType Directory -Force $addins | Out-Null

Copy-Item .\bin\Release\*.dll $addins
Copy-Item .\bin\Release\RevitCommandCenter.Electrical.addin $addins
Copy-Item .\bin\Release\Resources $addins -Recurse -Force
```

Restart Revit. A **Command Center** tab appears with Connect, Disconnect,
Status and Log.

## Configure

On first run the add-in writes a template to
`%APPDATA%\RevitCommandCenter\config.json`. Fill it in:

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

- **Rooms must be placed and named.** `/place_lighting Office_A` matches on room
  name, then room number, then a name prefix.
- **Families must be loaded** for each category you use — lighting fixtures,
  electrical fixtures, fire alarm devices, data devices, security devices,
  communication devices, and a cable tray type.
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

## First build

Likely places to need a fix, in rough order of probability:

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
