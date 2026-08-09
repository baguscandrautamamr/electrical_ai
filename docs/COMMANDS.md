# Command reference

Parameters are `key=value` pairs separated by spaces. `key: value` also works —
both forms appear in the examples below. Values containing spaces need quotes:
`zone_id="North Wing"`.

You can also write in plain language; anything the grammar cannot parse goes to
Claude, which maps it onto one of these commands. `pasang 4 stop kontak di
Office_A` resolves to `/place_receptacle Office_A count=4`.

`/help` lists everything; `/help create_cable_tray` shows one command's
parameters in full.

## Saying it in Indonesian

Every device command answers to a `pasang_` name as well as its `place_` one.
They are the same command — same parameters, same behaviour — so use whichever
reads better:

| English | Indonesian |
|---|---|
| `/place_lighting` | `/pasang_lampu`, `/pasang_lighting` |
| `/place_lighting_device` | `/pasang_saklar`, `/pasang_switch` |
| `/place_receptacle` | `/pasang_stopkontak`, `/pasang_stop_kontak` |
| `/create_cable_tray` | `/pasang_cable_tray`, `/pasang_kabel_tray`, `/buat_cable_tray` |
| `/add_hangers` | `/pasang_hanger`, `/tambah_hanger` |
| `/place_fire_alarm` | `/pasang_fire_alarm`, `/pasang_detektor` |
| `/place_telephone` | `/pasang_telepon` |
| `/place_lan` | `/pasang_lan`, `/pasang_jaringan`, `/pasang_data` |
| `/place_security` | `/pasang_cctv`, `/pasang_kamera` |
| `/place_communication` | `/pasang_speaker`, `/pasang_komunikasi` |
| `/equip_room` | `/lengkapi_ruangan`, `/pasang_semua` |
| `/delete_devices` | `/hapus`, `/buang`, `/delete` |
| `/modify_devices` | `/modifikasi`, `/ubah`, `/ganti` |
| `/list_sheets` | `/sheets`, `/daftar_sheet` |
| `/undo` | `/batal`, `/batalkan` |
| `/print_pdf` | `/pdf`, `/cetak_pdf`, `/cetak`, `/print` |
| `/dimension` | `/dimensi`, `/beri_dimensi`, `/ukur` |

Only the canonical names appear in Telegram's command menu — one entry per
command keeps it readable — but the parser accepts either everywhere.

## Room names

Give the room exactly as it reads on the drawing, including its number:
`/place_lighting meeting 1`, not `/place_lighting meeting`. Quotes are optional;
every word up to the first `key=value` is part of the name.

A name that matches no room is reported as such. A name that matches more than
one — `meeting`, where the model has MEETING 1 and MEETING 2 — is reported too,
with the candidates listed, rather than run against whichever the add-in found
first.

## Roles

| Role | Can |
|---|---|
| `viewer` | `/query`, `/export`, `/print_pdf`, `/list_sheets`, and all read-only admin commands |
| `editor` | everything above, plus every device command and `/api connect` |
| `admin` | everything above, plus `/user list` |

Roles are per project, from `user_project_access`.

---

## Devices

### `/place_lighting <room>`

Places fixtures on a ceiling grid sized to hit a lux target, then splits the
load across circuits.

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `space` | number | from the model | Floor area in m²; read off the Revit space when omitted |
| `count` | integer | from `lux_target` | Number of fixtures; stating it overrides the lux calculation |
| `grid` | size | — | Explicit layout, columns × rows, e.g. `3x2` |
| `height` | number | 2.8 | Ceiling height in m |
| `lux_target` | number | 300 | Target illumination |
| `fixture_type` | string | LED_15W | Revit family name; the wattage in it is only used when the family states none |
| `mounting` | ceiling\|wall\|floor | ceiling | |
| `spacing` | string | auto | `auto` or an explicit grid like `3.5x3.2` |
| `distribution` | balanced\|manual | balanced | |
| `phase_preference` | string | ABC | |

```
/place_lighting Lounge count=6 height=3 fixture_type=act_e_downlight
/place_lighting meeting 1 3x2 height=3 fixture_type=downlight
```

Only the room is required. Everything else has an answer already: the area comes
from the Revit space, and the fixture count from the lumen method —
`N = (E × A) / (F × UF × MF)`, with a 0.6 combined utilisation and maintenance
factor — unless you state `count`, in which case you get exactly that many.

**The grid.** `3x2` is three fixtures across by two deep, six in total, laid out
as written rather than re-shaped to fit. It is how a lighting layout is
described on a drawing, and it says something `count` cannot: six fixtures is a
very different ceiling as `3x2` than as `6x1`. Written bare after the room name
it is recognised as the grid, so `/place_lighting meeting 1 3x2` works — as does
`grid=3x2`, and `3 x 2` with spaces. The grid runs along the room's longer side,
so the same `3x2` reads correctly in a room of either proportion.

Lux is not reported back. The lumen-method figure is an estimate over the floor
area with an assumed efficacy — fine for sizing a count nobody stated, not
something to grade a count somebody did.

**Load.** The reported wattage is read off each placed fixture's own electrical
data in Revit — `Apparent Load`, `Wattage` or `Load`, on the instance or its
type. The number in a family name like `LED_15W` is only a fallback for a family
that states nothing, and it reads 15 W off every family whose name has no number
in it. The reply's **Load source** row says which of the two you got.

The lumen method still sizes an unstated `count` from the family name, so a
family with no wattage in its name is counted as 15 W per fixture even when its
electrical data says otherwise. State `count` or `grid` when that matters.

`space` was called `area`; both names still work, as does the Indonesian `luas`.
`breaker_max` is gone — lighting circuits are split at 16 A, which is not a
decision worth making per command.

### `/place_lighting_device <room>`

Switches and dimmers — Revit's **Lighting Devices** category, which is not the
same thing as the Lighting Fixtures `/place_lighting` places. The fixtures are
the lamps; these are what turn them on.

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `type` | single_gang\|double_gang\|three_gang\|four_gang\|two_way\|dimmer\|occupancy_sensor | single_gang | `three_gang` is the "S3" on the drawing |
| `count` | integer | 1 | Number of switch plates |
| `height` | number | 1.2 | Height from floor (m) |
| `mounting` | ceiling\|wall\|floor | wall | |
| `placement` | door\|walls\|manual | door | Beside the door, or spread along the walls |
| `controls` | string | — | What it switches: a circuit id, a fixture mark, or a group name |
| `family` | string | Switch | Revit family name |

```
/place_lighting_device Meeting_1 type=three_gang count=1 height=1.2 controls=LF-001
/pasang_saklar meeting 1
```

By default each switch goes beside a door in the room, on the wall, **300 mm
from the edge of the door leaf** — the house standard, and where you would
otherwise drag it after the command ran. So `/pasang_saklar pantry` on its own
is a complete instruction. A room with no door in the model falls back to the
walls, as does `placement=walls`.

Doors and windows are read off the room's boundary, so a switch is measured from
the real jamb and an outlet is never spaced onto an opening. Receptacles that
would have landed in one move clear of it rather than being dropped — you asked
for four outlets, you get four.

**The type comes from the project, not from the parameter name.** Offices do not
agree on what a two-gang switch is called: the same device is `2 Gang` here,
`S2` on the drawing, `Double Pole` in a catalogue family. The types actually
loaded in the model are read and scored against all of those conventions, and
the reply's **Family type** row names the Revit type that was placed. Ask for a
`three_gang` in a project that has no three-gang family loaded and you get a
failed compliance line listing what *is* loaded, rather than a silent
substitution you find out about later.

`family` still wins outright when you name one — you have looked at the project
browser and this has not.

### `/place_receptacle <room>`

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `count` | integer | **required** | Number of outlets |
| `type` | single\|double\|grounded\|double_grounded\|gfci\|20a | double_grounded | |
| `height` | number | 0.4 | Height from floor (m) |
| `placement` | walls\|perimeter\|manual | walls | |
| `load_per_outlet` | number | *(the family's)* | Overrides the family's electrical data (W) |
| `breaker_size` | number | 20 | A |
| `circuit_type` | general\|dedicated | general | |
| `voltage` | number | 230 | V |

```
/place_receptacle Office_A count=4 type=double_grounded height=0.4 placement=walls breaker_size=20
```

The reported load comes from the outlet's own **electrical data in Revit** — the
`Apparent Load` on its connector, which is what the panel schedule totals. Three
200 VA outlets are reported as 600 W, not as three times a design figure.

`load_per_outlet` is only for a load the family does not state: a dedicated
circuit for a machine, or a family whose electrical data was never filled in.
When neither the family nor you states one, 1500 W per outlet is assumed, and
the reply says so.

Every placement reply carries a **Load source** row saying which of the three it
used, so a total that disagrees with the Revit schedule can be traced without
opening the model.

### `/create_cable_tray <tray_id>`

Routes a tray and hangs it. See [HANGERS.md](HANGERS.md) for the hanger
algorithm.

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `from` | string | **required** | Origin, e.g. panel `PA-01` |
| `to` | string | **required** | Destination, e.g. `Zone_A` |
| `cable_type` | power\|data\|mixed | power | |
| `size` | string | auto | `auto` or `150x100` |
| `material` | aluminum\|steel\|stainless | aluminum | Drives the load estimate |
| `installation` | ceiling\|wall\|floor | ceiling | |
| `hanger_spacing` | number | 1500 | mm; alias `spacing` |
| `fill_target` | number | 50 | % |
| `preserve_existing` | boolean | true | **Keep hangers already in the model** |
| `hanger_family` | string | Hanger | Family name in Revit |

```
/create_cable_tray CT-A1 from=PA-01 to=Zone_A cable_type=power size=auto material=aluminum installation=ceiling hanger_spacing=1500 fill_target=50 preserve_existing=true
```

### `/add_hangers <tray_id>`

Hangs a tray that already exists. Same engine, no routing.

| Parameter | Type | Default |
|---|---|---|
| `spacing` | number | 1500 |
| `preserve_existing` | boolean | true |
| `hanger_family` | string | Hanger |

```
/add_hangers CT-A1 spacing=1500 preserve_existing=true
```

### `/place_fire_alarm <room>`

NFPA 72 spacing, addressable loop assignment, compliance reported per rule.

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `type` | smoke\|heat\|dual\|manual_call_point | dual | |
| `standard` | NFPA_72\|SNI_3985 | NFPA_72 | |
| `loop_id` | string | **required** | e.g. `FD-Loop-01` |
| `address` | string | auto | `auto` or an explicit address |
| `mounting` | ceiling\|wall\|floor | ceiling | |
| `coverage_target` | number | 100 | % |
| `space` | number | from the model | Floor area in m²; read off the Revit space when omitted |
| `roof_pitch_deg` | number | 0 | Above 14° triggers apex rules |

```
/place_fire_alarm Office_A type=dual standard=NFPA_72 loop_id=FD-Loop-01 address=auto mounting=ceiling coverage_target=100
```

Checks reported: smoke spacing ≤5.5 m, heat spacing ≤7.0 m, manual call points
≤25 m, apex coverage on pitched roofs, and loop addresses within 46–113.

### `/place_telephone <room>`

| Parameter | Type | Default |
|---|---|---|
| `type` | data\|voice\|data_voice | data_voice |
| `count` | integer | **required** |
| `height` | number | 0.4 |

```
/place_telephone Office_A type=data_voice count=2 height=0.4
```

### `/place_lan <room>`

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `count` | integer | **required** | |
| `type` | 1Gbps\|10Gbps\|PoE | 1Gbps | |
| `poe_enabled` | boolean | false | Reports against a 740 W switch budget |
| `switch_panel` | string | SW-01 | Ports allocated from the first free one |
| `height` | number | 0.4 | |

```
/place_lan Office_A count=4 type=1Gbps poe_enabled=true switch_panel=SW-01
```

### `/place_security <room>`

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `type` | camera\|motion_sensor\|door_sensor | camera | |
| `camera_type` | dome\|turret\|bullet | dome | |
| `resolution` | 2MP\|4MP\|8MP | 4MP | Drives useful range (12/18/25 m) |
| `coverage_fov` | number | 90 | Degrees |
| `count` | integer | 1 | |
| `zone_id` | string | — | Defaults to the room name |

```
/place_security Lobby type=camera camera_type=dome coverage_fov=90 resolution=4MP count=2
```

### `/place_communication <room>`

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `type` | speaker\|antenna\|microphone | speaker | |
| `system` | pa\|intercom\|emergency | pa | |
| `quantity` | integer | 1 | alias `count` |
| `coverage_radius` | number | — | m; defaults from type and mount height |
| `panel` | string | — | |

```
/place_communication Lobby type=speaker system=pa quantity=3
```

### `/equip_room <room>`

Runs every category against one room. A failure in one category does not abort
the rest — a missing camera family should not cost you the lighting that placed
fine.

The room is resolved once, up front. An ambiguous name fails here with one clear
message instead of nine copies of it.

| Parameter | Type | Default |
|---|---|---|
| `space` | number | from the model |
| `height` | number | 2.8 |
| `lux_target` | number | 300 |
| `switches` | integer | 1 |
| `outlets` | integer | 4 |
| `phone_jacks` | integer | 2 |
| `lan_jacks` | integer | 4 |
| `security_cameras` | integer | 2 |
| `speakers` | integer | 2 |
| `fire_alarm` | string | auto |
| `cable_tray` | boolean | true |
| `hanger_spacing` | number | 1500 |
| `preserve_existing` | boolean | true |

Set any count to `0`, or `fire_alarm=none`, to skip that category.

```
/equip_room Office_A height=2.8 lux_target=300 switches=1 outlets=4 phone_jacks=2 lan_jacks=4 security_cameras=2 fire_alarm=auto cable_tray=yes hanger_spacing=1500
```

### `/export`

| Parameter | Type | Default |
|---|---|---|
| `type` | lighting_schedule, lighting_device_schedule, receptacle_schedule, cable_tray, hanger_schedule, fire_alarm_schedule, telephone_schedule, lan_schedule, security_schedule, communication_schedule, panel_schedule, compliance_report, all | all |
| `format` | excel\|pdf\|dwg\|dxf\|ifc | excel |

```
/export type=hanger_schedule format=excel
```

### `/delete_devices <room>`

Removes devices of one category from one room. Aliases: `/delete`, `/hapus`,
`/buang`. Plain language works: *"hapus lampu di ruangan A"*.

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `room` | string | **required** | Room to clear |
| `what` | lighting, lighting_device, receptacle, fire_alarm, telephone, lan, security, communication, all | all | Category to remove |

```
/delete_devices Pantry what=lighting
/hapus Meeting_1 what=receptacle
```

Deliberately narrow: **both the room and a category are required.** "Delete the
lighting" with no room names the whole model, and no amount of confirming makes
that safe to accept from a chat message. Cable trays and hangers are out of
reach here — a tray is routed rather than placed in a room.

Scoped by the same point-in-room test `/query` counts with, so what goes is what
`/query` said was there. The reply lists the marks it removed, and Revit's undo
is one step. Removing nothing comes back as a warning rather than a tick — the
usual cause is the right command against the wrong room name.

**It asks first.** `/delete_devices` and `/modify_devices` reply with the room
and category they resolved to and a Yes/Cancel pair of buttons. Nothing reaches
Revit until Yes is tapped: the command sits in the queue in a state the add-in
does not poll for. The button works once, only for the person who typed the
command, and expires after 24 hours — a Yes tapped on yesterday's question would
run against a drawing that has moved on.

### `/undo`

Removes what your last placement added. Aliases: `/batal`, `/batalkan`.

```
/undo
```

Aimed at **marks, not at a room**: it takes back the exact fixtures that command
reported placing, so a colleague who added two more outlets to the same room in
between keeps theirs. That also means it still works if someone has since
dragged one of them into the corridor.

It reverses one placement — `/place_*` or the placing half of a `/modify`. It
will not reverse a `/delete`: this system never held the geometry that was
removed, so there is nothing to put back. Revit's own undo does that, on the
machine running Revit.

Asks for confirmation first, and names the marks it is about to remove.

### `/modify_devices <room>`

Re-lays out a room: same category, new count or grid. Aliases: `/modify`,
`/modifikasi`, `/ubah`, `/ganti`.

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `room` | string | **required** | Room to change |
| `what` | lighting, lighting_device, receptacle, fire_alarm, telephone, lan, security, communication | lighting | Category to re-lay out |
| `count` | integer | — | New number of devices |
| `grid` | size | — | New layout, columns × rows |
| `height`, `fixture_type`, `type` | | unchanged | Forwarded to the placement |

```
/modify_devices Meeting_1 what=lighting grid=2x3
/modifikasi Office_B what=lighting count=9
```

*"modifikasi lampu di ruangan B menjadi 2x3"* and *"…menjadi 9 lampu"* both work.

**It replaces rather than edits.** Revit cannot turn a 2×2 grid into a 2×3 one
by moving fixtures — you would get four on the old spacing with two squeezed
between them — so the old set comes out and a new one goes in, which is what you
would do by hand. The reply says how many it removed, so a wrong room shows up in
the chat.

One of `count` or `grid` is required. Without either, "modify" would mean
"delete what is there and put back a default layout", which is nobody's
intention. Anything you do state — height, family, type — is carried into the new
layout; anything you leave out keeps the placement command's own defaults.

### `/list_sheets`

Lists the sheets in the model with their numbers. Aliases: `/sheets`,
`/daftar_sheet`, `/sheet`. A viewer may run it.

```
/list_sheets
```

The numbers it returns are exactly what `/print_pdf` takes, so this is the step
before printing when you cannot remember how the set is numbered. Sheets are
always listed rather than counted — a count of sheets answers nothing.
Placeholder sheets are left out; they carry a number but no drawing.

Same data as `/query what=sheet`, under the name people ask it by.

### `/print_pdf <sheets>`

Prints sheets to PDF, chosen by the number in their title block. Aliases:
`/pdf`, `/cetak_pdf`, `/print`, `/cetak`.

Not the same thing as `/export format=pdf`, which writes the compliance report
this system generates. This prints the drawing.

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `sheets` | string | **required** | Sheet number, a comma-separated list, or a pattern |
| `combine` | boolean | true | One PDF holding every sheet, rather than a file per sheet |

```
/print_pdf E-101
/print_pdf E-101,E-102,E-103
/print_pdf E-1* combine=false
/print_pdf all
```

`*` and `?` work as they do in a file dialog, so `E-1*` takes every sheet
numbered `E-1…`. `all` takes the set. A pattern is matched against the sheet
number and the sheet name, so `/pdf "Lighting Plan"` finds it too. A pattern
that matches nothing is named back to you rather than silently skipped — that is
how a drawing set goes out one sheet short.

Placeholder sheets are left out: they have no drawing on them, and Revit rejects
a whole batch that contains one.

**The PDF arrives in the chat as a file.** The add-in uploads it to Telegram
directly, so there is nothing to configure beyond the bot token in the add-in's
settings, and the drawing goes only to the chat that asked for it. Telegram
accepts up to 50 MB per document; a set larger than that needs `combine=false`
or fewer sheets per run.

The file also stays in the add-in's export directory, and the reply names that
path. Set `export_base_url` if that folder is served over the web and you would
rather have a link.

A viewer may run this — it reads the model and writes a file, and changes
neither.

### `/dimension [room]`

Dimensions the devices in a room, or a whole plan view's grids and walls.
Aliases: `/dimensi`, `/beri_dimensi`, `/auto_dimension`, `/ukur`.

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `room` | string | the open view | Room to dimension; a plan view name also works |
| `what` | lighting, lighting_device, receptacle, fire_alarm, telephone, lan, security, communication, grids, walls, all | all | What to measure to |
| `view` | string | the room's floor plan | Plan view to draw in |
| `offset` | number | 1000 | Distance from the outermost element to the dimension line (mm) |

```
/dimension Pantry
/dimension Pantry what=lighting
/dimension "Level 1" what=grids
```

Plain language works: *"kasih dimensi lampu di pantry"*.

**With a room**, the devices are what gets measured — centre to centre, the way
a ceiling layout is dimensioned on a real drawing. `all` means the lighting
there; dimensioning eight categories at once buries the layout under seven
strings nobody asked for.

**Without one**, the whole view's grids and walls are measured instead. Grids
are measured to the grid line, walls to the vertical faces the view actually
shows, so a wall hidden by the crop or a filter is not measured.

Two strings per run either way: one below the drawing picking up everything
running north-south, one to its left picking up everything running east-west.

The view is worked out for you: the room's own floor plan, or whatever is open
in Revit when it is on the right storey. Only plan views — a string laid out in
plan coordinates means nothing in a section or a 3D view.

Dimensions attach to the reference planes a family publishes. A family authored
without them cannot be dimensioned to, and comes back as "no dimensionable
devices" rather than with a string measured to something arbitrary.

**It adds and never removes.** Running it twice draws the strings twice rather
than replacing them — deciding that an existing dimension was this command's
rather than yours is a guess, and the wrong guess deletes your work. Undo in
Revit is one step.

A view with nothing dimensionable in it comes back as a success with zero
strings and a note saying so, not as an error.

### `/query [room]`

Reads the model and reports what is already there. The only command that opens
no Revit transaction, so it cannot change the drawing — which is why a `viewer`
may run it.

Omit the room to search the whole model.

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `what` | all, lighting, lighting_device, receptacle, cable_tray, hanger, fire_alarm, telephone, lan, security, communication, panel, room, sheet | all | `all` covers every device category; rooms and sheets are reported only when asked for by name |
| `level` | string | | Restrict to one level, e.g. `"Level 1"` |
| `detail` | summary\|list | summary | `list` also names each element |
| `limit` | integer | 30 | Most items to name when `detail=list`; the cap is per query, not per category |

```
/query Office_A what=lighting detail=list
/query what=hanger level="Level 1"
```

The reply leads with what was searched, because a count means nothing without
it. A room name that matches nothing is reported as such rather than answered
with `0` — "no such room" and "that room is empty" are different answers.

Plain language works too: *"ada berapa lampu di Office_A?"*, *"list hanger di
lantai 1"*, *"cek panel"*.

Lighting groups carry total wattage, cable tray total length, rooms total area.
`what=lighting` reads each fixture's `Wattage`, `Apparent Load` or `Load`
parameter and falls back to the wattage in the family name — the same reading
`/place_lighting` used when it placed them.

---

## Admin

| Command | Role | What it does |
|---|---|---|
| `/start`, `/help [command]` | viewer | Command reference |
| `/project list` | viewer | Projects you can access; ★ marks the active one |
| `/project use <code>` | viewer | Switch active project |
| `/api connect <key> <YYYY-MM-DD>` | editor | Store a credential with a hard expiry |
| `/api connect <YYYY-MM-DD>` | editor | Same, but generate the key (shown once) |
| `/api status` | viewer | Key hint, active/expired, expiry date |
| `/api disconnect` | viewer | Deactivate the active credential |
| `/user list` | admin | Users on the active project |
| `/health`, `/status` | viewer | Database, AI parser, queue depth, failures in the last hour |
| `/theme light\|dark` | viewer | Switch theme; confirmation renders in the new one |
| `/lang id\|en` | viewer | Switch language; confirmation renders in the new one |

Credentials are stored as a SHA-256 hash plus a four-character hint. The
plaintext appears once and is never recoverable — `/api connect` again to
rotate. The daily cron warns three days before expiry and again on the day.

---

## Errors

**Validation** reports every bad field at once, then repeats the command's
example:

```
❌ Invalid parameters
• from is required
• hanger_spacing must be between 100 and 6000

Example
/create_cable_tray CT-A1 from=PA-01 to=Zone_A …
```

**Unknown parameters are ignored, not rejected.** A natural-language parse
routinely produces one stray key, and losing the whole command over it would be
worse than dropping it.

**Execution failures** report the retry count. Retryable failures (Revit closed,
transient network) are re-queued with exponential backoff up to `max_retries`.
Non-retryable ones (room not found, no matching family) fail immediately —
retrying would fail identically and only delay the answer.
