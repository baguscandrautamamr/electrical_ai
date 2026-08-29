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
| `/standar` | `/standard`, `/puil`, `/sni`, `/iec`, `/referensi`, `/ref` |
| `/keluar` | `/selesai`, `/exit`, `/quit` |

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
| `viewer` | `/query`, `/inspect`, `/export`, `/print_pdf`, `/list_sheets`, and all read-only admin commands |
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
| `family` | string | — | Revit family name; obeyed exactly or refused |
| `door_offset` | number | 300 | Distance from the door leaf's edge (mm) |

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
browser and this has not. It is now obeyed **or refused**: a name that matches
nothing in the model fails with the list of families that are loaded, rather than
placing the first one in the category. That substitution is silent by nature —
the count is right, the command succeeds, and only the drawing shows it.

`door_offset` moves the switch off the 300 mm standard for the rooms that cannot
take it: a double leaf, a jamb wider than usual, a wall too short for the plate
to land clear of the frame. Leave it out and the standard applies, which is why
`/pasang_saklar pantry` needs nothing else.

### Naming a family, on any device command

Every `place_*` command accepts `family`, and all of them treat it the same way:
the named family is used, or the command fails saying which families the model
does have. Nothing is substituted.

That is deliberately stricter than the per-category hints (`type`, `camera_type`,
`fixture_type`), which stay lenient because they ARE guesses at what an office
calls its families. `family` is not a guess: it is picked from the list this
add-in itself reported through `/model_info`.

Every device reply now also carries **`family_used`** — the `Family: Type`
actually placed, not the name that was asked for. The two differ exactly when it
matters most, and until now nothing said so.

Names arrive in the form `/model_info` reports them, `Family: Type`. That whole
string is matched first, then an exact type name, then an exact family name, and
only then a partial match. Before that order existed, `Family: Type` matched
nothing at all — no symbol contains the whole of it — and the fallback placed the
first family in the category.

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

Two ways to say where it goes: between two named places (`from=`/`to=`), or
along lines already drawn (`follow=`). One of them is required.

**Following drawn lines.** `follow="Thin Lines"` traces every straight drawn in
that line style, one tray per straight and an **elbow at each corner** so the run
is connected rather than a row of separate trays that only looks right in plan.

```
/create_cable_tray CT-A1 follow="Thin Lines" size=300x300
```

Plain language works: *"pasang cable tray 300x300 mengikuti thin lines"*.

The style name is matched without brackets or spaces, so `Thin Lines`,
`<Thin Lines>` and `thinlines` all find the same style. Arcs are skipped — a
bent tray is a different job from following a drafted route, and chording one
would put the tray where nobody drew it. Where three lines meet, the run ends:
a tee is not an elbow, and picking two of the three would be inventing a
decision you did not draw. If the style holds several separate runs, the longest
is used and the reply says so.


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
| `hanger_family` | string | *the add-in setting* | Family name in Revit. Omit it: the add-in's **Hanger family name** setting is where that name lives |

```
/create_cable_tray CT-A1 from=PA-01 to=Zone_A cable_type=power size=auto material=aluminum installation=ceiling hanger_spacing=1500 fill_target=50 preserve_existing=true
```

### `/add_hangers [tray_id]`

Hangs a tray that already exists. Same engine, no routing.

| Parameter | Type | Default |
|---|---|---|
| `spacing` | number | 1500 |
| `preserve_existing` | boolean | true |
| `mode` | fill\|replace | fill |
| `hanger_family` | string | *the add-in setting* |

```
/add_hangers
/add_hangers ladder
/add_hangers CT-A1 spacing=1500
/add_hangers mode=replace
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
| `count` | integer | from NFPA coverage | Number of detectors; stating it overrides the calculation |
| `space` | number | from the model | Floor area in m²; read off the Revit space when omitted |
| `roof_pitch_deg` | number | 0 | Above 14° triggers apex rules |

```
/place_fire_alarm Office_A type=dual standard=NFPA_72 loop_id=FD-Loop-01 address=auto mounting=ceiling coverage_target=100
/place_fire_alarm Service type=smoke loop_id=FD-Loop-01 count=1 height=3
```

**A stated count is obeyed.** *"pasang fire alarm smoke detector 1 unit di
service ketinggian 3 meter"* places one, not the two the room's area works out
to. Whether one is enough is a compliance question, and the reply answers it:
the detector-count check names how many NFPA 72 coverage needs beside how many
were placed. Placing two and reporting it against a request for one is the
answer that helps nobody.

Checks reported: smoke spacing ≤5.5 m, heat spacing ≤7.0 m, manual call points
≤25 m, apex coverage on pitched roofs, detector count against coverage when a
count was stated, and loop addresses within 46–113.

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

**It asks first.** `/delete_devices` replies with the room and category it
resolved to and a Yes/Cancel pair of buttons. Nothing reaches Revit until Yes is
tapped: the command sits in the queue in a state the add-in
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

It does **not** ask for confirmation. It puts a layout back where it took one
from, its reply says how many it removed, and `/undo` reverses it — three
reasons a second tap on a command you run every few minutes is not worth its
cost. `/delete_devices`, which only removes, still asks.

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
| `setup` | string | — | Name of a Print Setup saved in the model |

`setup` takes the settings an office has already agreed on rather than a
near-copy of them. Only what means the same thing in both dialogs is carried
across: orientation, colour depth, raster quality, the hide flags, coincident-line
masking, blue view links, and zoom. **Paper size is deliberately not**, because
PDF export takes one paper format while a drawing set is normally a mix of sizes
that each title block already states — forcing them all onto the setup's one size
would rescale drawings that were correct. Margins are left alone for the same
reason. A name the model does not have is refused, and the reply names the setups
it does have; `/model_info` lists them too.

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

### `/export_cad <sheets>`

Exports the chosen sheets to DWG, using a DWG Export Setup saved in the model.

Not the same as `/export format=dwg`, which exports whichever view happens to be
active. That answers "give me a DWG of what I am looking at"; this answers what a
drawing set actually asks — "give me these sheets, exported the way this office
exports them".

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `sheets` | string | **required** | Same selection as `/print_pdf` — number, list, pattern, or `all` |
| `setup` | string | — | Name of a DWG Export Setup saved in the model |

```
/export_cad E-101,E-102 setup="DWG 2018"
/export_cad E-1* setup="Client issue"
```

The setup is what decides layer mapping, line weights, and text handling — the
things a client settles once and rejects a drawing over. A name the model does
not have is refused rather than quietly replaced by Revit's defaults: a DWG with
the wrong layers looks finished, and the person who finds out is the one who
receives it. Running without `setup` at all is allowed, and says so in the notes.

One file per sheet, each named after its sheet number. Exporting them in one call
leaves Revit to name the files from a rule most projects have never set.

A viewer may run this — it reads the model and writes files, and changes neither.

### `/model_info`

Reports the model that is open right now.

| Parameter | Type | Default | Notes |
|---|---|---|---|
| — | | | Takes nothing |

Answers three questions in one round trip, because they are always asked
together and each one otherwise costs its own trip through the queue:

- `title` and `path` — the `.rvt` actually open in Revit. The website shows this
  beside the project selector: a project here is a row in a database, and the
  expensive moment is the one where the two are not the same model.
- `printable_sheets` — how many sheets could be printed or exported.
- `print_setups` and `cad_setups` — the names of the Print Setups and DWG Export
  Setups saved in that model, which is what fills the dropdowns for `/print_pdf`
  and `/export_cad`.

A model with no saved setups returns empty lists. That is normal — Revit only
creates them once somebody saves one — and means Revit's own defaults apply.

Opens no transaction, so a viewer may run it.

### `/import_table`

Draws a spreadsheet into the model as a table, keeping its shape.

A different question from `/import_excel`, which writes cell values onto elements
that already exist. This one brings in a table with no elements behind it at all:
a schedule kept in Excel, a supplier's cable list, a legend of symbols.

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `file_url` | string | **required** | Where the workbook can be downloaded from |
| `target` | string | `schedule` | `schedule` for a drafting view, `legend` for a legend, `schedule_view` for a real Schedules/Quantities view |
| `sheet` | string | — | Which worksheet; the first one with anything on it by default |
| `name` | string | — | View name; the worksheet's own name by default |

Column widths, row heights, and merged cells come across. Interior grid lines are
drawn once rather than once per neighbouring cell, and lines that would cross a
merged region are skipped — which is what makes a merge look merged.

**`schedule` and `legend` draw the table; they do not make a Revit schedule.** A
Revit schedule reports the model: its columns are model parameters, and there is
no way to write a spreadsheet's cell into one. Asking for "the same table as in
Excel" and receiving a schedule means receiving a different table. The cost of
drawing it is worth stating plainly: the result is a picture of the data, not the
data. Changing it means changing the spreadsheet and importing again.

#### `target=schedule_view` — a real schedule

A Schedules/Quantities view, the kind that can be filtered, sorted, grouped, and
placed on a sheet as a live schedule. It is a different thing from the two above,
and it is built the only way a schedule can be built:

1. **First row is the header.** A schedule column has to be named, and the only
   name a spreadsheet offers is the one at the top of the column.
2. **One shared parameter per column**, prefixed `RCC ` and bound to Generic
   Models. Shared rather than project parameters because only shared parameters
   can be schedule fields. Their GUIDs are derived from the parameter name, so
   re-importing reuses them instead of adding a second set Revit considers
   unrelated. They live in `%APPDATA%\RevitCommandCenter\shared-parameters.txt`;
   the machine's own shared parameter file is pointed at ours only while the
   definitions are read, then put back.
3. **One geometry-less `DirectShape` per row**, in Generic Models, carrying that
   row's values plus `RCC Tabel` (which table it belongs to) and `RCC Baris` (its
   spreadsheet row number). DirectShape rather than a family instance because it
   needs no `.rft` template and no family file to install first.
4. **The schedule** is created over Generic Models, filtered to `RCC Tabel` =
   this table, and sorted by `RCC Baris` so the rows keep the spreadsheet's
   order. Column headings are set back to the spreadsheet's own wording, so the
   `RCC ` prefix never reaches a reader.

What it costs, stated because it cannot be undone by looking at the result:

- **The rows become elements in the model.** They appear in a Generic Models
  schedule, in quantity takeoffs, and in IFC exports. The reply says how many.
- **Excel's formatting is gone** — merged cells, column widths, colours. A
  schedule has formatting of its own. Anyone who wants the table to look like the
  spreadsheet wants `target=schedule`.
- **Every cell is text**, including numbers: one cell says `12` and the next says
  `12 (tentative)`, and a Number parameter would refuse the second and lose it.
  Sorting a numeric column therefore sorts it as words — which is why the row
  order is kept in its own integer parameter and used as the schedule's sort.
- **Re-importing the same table replaces it.** Rows carrying the same `RCC Tabel`
  are deleted first and the schedule is rebuilt, because the columns can change
  between imports. The reply says how many rows were replaced.

Limits: 5,000 rows (each is an element) and 60 columns. A sheet past either is
refused with its size in the message.

Not multi-category. A multi-category schedule restricts its fields to shared
parameters — which these already are — and buys nothing while every row is a
Generic Model. Where multi-category earns its keep is a schedule over real
devices from several categories at once, which reads the model rather than a
spreadsheet.

`target=legend` needs the model to hold at least one legend already. The Revit
API cannot create the first one — duplicating an existing legend is the only way
in — so a model with none is told to make one rather than handed a drafting view
under a name suggesting otherwise.

The view is drawn at 1:1, so a millimetre in the spreadsheet is a millimetre on
paper. Excel measures column width in characters of its default font, which has
no exact length; the conversion keeps the columns in proportion, which is what
"the same table" means to the eye.

A sheet larger than 5,000 cells is refused with its size in the message. Each
cell costs a text note and up to four detail lines, and Revit slows to a stop
long before it refuses.

Needs the editor role: it adds a view to the model.

### `/query [room]`

Reads the model and reports what is already there. The only command that opens
no Revit transaction, so it cannot change the drawing — which is why a `viewer`
may run it.

Omit the room to search the whole model.

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `what` | all, lighting, lighting_device, receptacle, cable_tray, hanger, fire_alarm, telephone, lan, security, communication, panel, room, sheet | all | `all` covers every device category; rooms and sheets are reported only when asked for by name |
| `level` | string | | Restrict to one level, e.g. `"Level 1"` |
| `family` | string | | Restrict to one family — `family="ACT_E_DOWNLIGHT 22WATT"`. Matches the family name, the type name, or `Family: Type`; exact first, then contains, so `family=downlight` finds the downlight |
| `detail` | summary\|list | summary | `list` also names each element |
| `limit` | integer | 30 | Most items to name when `detail=list`; the cap is per query, not per category |

```
/query Office_A what=lighting detail=list
/query what=hanger level="Level 1"
/query "LOUNGE 5" what=lighting family="ACT_E_DOWNLIGHT 22WATT"
```

`family` is what makes "how many 22 W downlights on level 1" a question this can
answer. Without it the reply was the count of every lighting fixture on level 1 —
a number that is correct about a question nobody asked, and that reads exactly
like the answer. The family that was filtered on is named in the reply, so a
narrowed count cannot be mistaken for a whole-category one. When it matches
nothing, the reply lists the families that ARE in scope: `0` on its own cannot
distinguish "this model has none" from "that is not how this model spells it".

`level` reads Reference Level, Schedule Level, and Base Constraint when `LevelId`
is empty — the usual case for cable tray, conduit, and hosted families. It used to
compare `LevelId` alone, which dropped most of the tray from a tray count with
nothing to mark the number as wrong.

The reply leads with what was searched, because a count means nothing without
it. A room name that matches nothing is reported as such rather than answered
with `0` — "no such room" and "that room is empty" are different answers.

Plain language works too: *"ada berapa lampu di Office_A?"*, *"list hanger di
lantai 1"*, *"cek panel"*.

Lighting groups carry total wattage, cable tray total length, rooms total area.
`what=lighting` reads each fixture's `Wattage`, `Apparent Load` or `Load`
parameter and falls back to the wattage in the family name — the same reading
`/place_lighting` used when it placed them.

### `/inspect`

Reads **anything** in the model, where `/query` reads the thirteen things it was
built around. Same guarantee: no transaction is opened, so a `viewer` may run it
and no phrasing of it can change the drawing.

Three modes, and the order is the point:

| `what` | Answers |
|---|---|
| `categories` | Which categories exist in this model, and how many elements each has |
| `parameters` | What a category's elements can be asked about, with a real value beside each name |
| `elements` | The rows themselves, with the columns you name *(default)* |

The first two exist because the third cannot be used without them. A parameter
has to be named exactly to be read, and nobody — engineer or assistant — can name
one they have never seen. Guessing `Length` and getting an empty column is
indistinguishable from a model that has no lengths, which is the failure this
system keeps having to design away. The sample value in `parameters` settles both
questions at once: whether this is the parameter you meant, and what unit it
comes back in.

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `what` | categories\|parameters\|elements | elements | |
| `category` | string | | Revit's name (`Doors`), its OST name (`OST_Doors`), or the short key the other commands use (`lighting`). **Several, comma separated** — `category="lighting, receptacle"`. Required except for `what=categories` |
| `params` | string | Id, Mark, Type, Level | Columns, comma separated |
| `where` | string | | Conditions: `Width>800`, `Mark~LF-`, `Comments!=`. `~` is "contains". **Several, comma separated, all of which must hold** — `where="Family=ACT_E_DOWNLIGHT 22WATT, Width>800"` |
| `total` | string | | Numeric parameters to sum, comma separated |
| `group_by` | string | | One parameter; the answer is how many elements per value |
| `room` | string | | Same room resolution as `/query` |
| `level` | string | | |
| `limit` | integer | 30 | Rows returned, at most 200 |

```
/inspect what=categories
/inspect what=parameters category=Doors
/inspect category=Doors params=Mark,Width where=Width>800 total=Width
/inspect category=cable_tray total=Length level="Level 1"
/inspect category=lighting room="LOUNGE 5" level="LANTAI 1" where="Family=ACT_E_DOWNLIGHT 22WATT"
/inspect category="lighting, lighting_device, receptacle" room="LOUNGE 5" group_by=Family
```

**Units.** Revit stores lengths in internal feet. Everything summed here is
converted into the unit the project displays first, so a total of lengths is a
total of metres — not `12.3` where the answer is `3.75 m`. Both are the same
tray, and only one of them is an answer.

**Totals cover everything that matched**, not the rows shown: a sum computed
from the first thirty of two hundred is a number that looks like an answer and is
not one. How many elements had no value to add is reported beside it, because a
total that quietly covers half the set answers a question nobody asked.

**Columns that are not parameters.** `Id`, `Category`, `Family`, `Type`, `Level`,
`Room` and `Name` are always available, and every one of them can be filtered on
and grouped by as well as printed. `Length` falls back to the element's own
geometry when no parameter answers to that name — which is how a cable tray says
how long it is, and what makes this work on a model whose Revit speaks another
language.

`Family` answers for system families too — cable tray, conduit, pipe, duct, walls
— reading the family name Revit prints in the project browser rather than only
the loadable-family instances. `Room` falls back to the element's midpoint when
the element itself does not name a room, so tray can be grouped by room and not
only filtered by it. `Level` reads Reference Level, Schedule Level, and Base
Constraint when `LevelId` is empty, which is the usual case for tray and hosted
families: filtering by level used to drop them silently, and a smaller number
looks exactly like a correct one.

**When nothing matches**, the reply names the values that ARE in scope for the
parameter you filtered on. An empty table answers "this model has no downlights"
and "that is not how this model spells downlight" in exactly the same way, and the
second is far more common.

Limits, reported when they bite: 20,000 elements scanned, 200 rows returned, 50
groups named. A federated model has millions of elements, and reading a parameter
off each one happens on Revit's UI thread.

### `/get_electrical_loads [panel]`

Every circuit in the model with what it carries and where it lands: connected
load, voltage, current, breaker rating, panel. Opens no transaction, so a
`viewer` may run it.

Omit the panel to read the whole model; it matches part of the name, ignoring
case, so `pp-1` finds `PP-1 LANTAI 2`.

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `system_type` | all, power, lighting, data, telephone, security, fire_alarm, nurse_call, communication, controls | all | Mapped to Revit's `ElectricalSystemType`. `lighting` is the exception — see below |
| `detail` | summary\|list | summary | `summary` gives totals per panel and per system type; `list` gives one row per circuit |
| `limit` | integer | 200 | Rows returned when `detail=list`. Caps the ROWS, never the totals |
| `include_element_ids` | boolean | false | The element ids wired to each circuit. Useful with `/show_element` |

```
/get_electrical_loads
/get_electrical_loads PP-1 system_type=power detail=list
```

Loads come back in VA **and** W. They differ by the power factor, and calling
either one "the load" on its own is the easiest way to size a breaker wrong.

`lighting` has no Revit equivalent — Revit does not separate lighting circuits
from power circuits. It is filtered by load and circuit NAME instead, and the
reply says so. A lighting circuit whose name says nothing about lamps will not be
counted, and a filter that quietly narrows by name is a count that is wrong for a
reason nobody can see.

A value the circuit could not answer for comes back as null, not `0`. Null means
Revit refused the read; `0` means a circuit carrying nothing. Only the first is
worth chasing.

Circuits with no panel are counted separately as `unassigned_circuits`. That
number is what explains a per-panel total that does not add up to the model's.

Zero circuits is a real answer. A model whose fixtures exist but are not
circuited has none, and that is a fact about the model rather than a failure.

### `/get_panel_schedule [panel]`

What is inside each panel: which slots are used and which are free, poles per
breaker, connected load, and the panel's own metadata. Opens no transaction.

This **reads** the panel data. It does not create a Revit Panel Schedule view;
the two are easy to confuse by name and are not the same thing.

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `detail` | summary\|list | summary | `summary` is one row per panel; `list` is the circuit directory, ordered by slot |
| `include_empty` | boolean | true | Panels with no circuits yet |
| `limit` | integer | 50 | Panels returned |

```
/get_panel_schedule
/get_panel_schedule PP-1 detail=list
```

Every Electrical Equipment instance counts as a candidate panel, empty ones
included. An empty panel that does not appear reads as a panel that does not
exist — and somebody sizing a new circuit needs to know it is there.

`max_slots` is read from the panel family, and comes back null when the family
does not publish it. `free_slots` is then null too. Guessing 42 because panels
are usually 42 produces a free-slot count that is wrong on exactly the panels
worth checking.

Slots are counted, not circuits: a three-pole breaker occupies three of them, and
counting circuits reports a full panel as half empty.

In `detail=list` the empty slots between breakers are rows of their own. A
directory that lists only what is wired cannot answer the question it is usually
opened for — where is there room for the circuit I am about to add.

### `/check_circuit_balance [panel]`

How evenly load sits across R-S-T in each panel. Opens no transaction.

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `tolerance` | number | 10 | Percent the heaviest phase may sit above the average before the panel is flagged |
| `limit` | integer | 50 | Panels returned |

```
/check_circuit_balance
/check_circuit_balance PP-1 tolerance=5
```

**Per-phase load is not read from Revit. Revit does not store it.** It is
derived, two assumptions deep:

1. Each circuit's apparent load is split **evenly** across the phases its breaker
   occupies. Real imbalance inside one three-pole breaker is invisible here.
2. The starting phase is inferred from the slot number, assuming the standard
   **A-A-B-B-C-C** panelboard arrangement — slots 1–2 on A, 3–4 on B, 5–6 on C,
   7–8 back on A.

A panel not wired that way produces wrong numbers with no error anywhere. That is
why every reply carries an `assumption` field spelling this out: it is the only
thing separating these figures from a measurement.

Single-phase panels are skipped and **counted** (`single_phase_skipped`). A panel
that vanishes from the list without explanation reads as a balanced one.

### `/show_element <ids>`

Opens a 3D view in Revit, selects the elements whose ids are given, and scrolls
until they are on screen. Opens no transaction — the active view moves and the
selection changes; the model does not.

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `ids` | string | *required* | One id, or several separated by commas: `384210` or `384210,384215` |
| `view` | 3d\|current | 3d | `current` leaves the active view alone; elements outside it will not become visible |

```
/show_element 384210
/show_element 384210,384215 view=current
```

This is what a bare element id typed into the website's chat box turns into. The
reading commands answer with numbers and names, and the next question is always
"which one is that on the drawing?" — until this existed the only answer was to
walk to the Revit PC and retype the id into Revit's own search box.

Ids that are not in this model are reported alongside the ones that were found
rather than failing the command. Two out of three found is two elements the person
can now see; failing all three means retyping all three. Only when nothing at all
is found does the command fail — and it names what it looked for, because an id
from another model looks exactly like a typo.

A model with no 3D view at all gets one isometric view created, and the reply says
so (`view_created`). This is the one place these four commands touch the document,
and refusing instead would fail precisely where the feature is needed most: a
model with no 3D view is one whose elements nobody can point at yet.

---

## Standards mode

A two-way PUIL / SNI / IEC reference channel. Not a command that answers once —
a **mode** the bot stays in until you leave it.

| Command | Role | What it does |
|---|---|---|
| `/standar [question]` | viewer | Open the channel; aliases `/standard`, `/puil`, `/sni`, `/iec`, `/referensi`, `/ref` |
| `/keluar` | viewer | Close it; aliases `/selesai`, `/exit`, `/quit` |

```
/standar
📘 STANDARDS MODE ON
   Ask anything about PUIL / SNI / IEC…

standar instalasi socket outlet
📘 Standards Reference
   …

kalau di kamar mandi?          ← follows on; no need to repeat the context
📘 Standards Reference
   …

/keluar
✅ COMMAND MODE ON
```

A question sent with the command opens the channel and answers in one go:
`/puil berapa tinggi kotak kontak`.

**Nothing reaches Revit while the mode is on.** The webhook branches to this
channel *before* the parser runs, so a question about how a device should be
installed cannot be read as a request to install one. A Revit command typed by
mistake is refused by name rather than answered:

```
/place_lighting Office_A count=6
⚠️ COMMAND NOT RUN
   └ Command: /place_lighting
   You are in standards mode… type /keluar first, then send it again.
```

`/help`, `/lang`, `/theme`, `/status` and `/start` still work inside the mode —
none of them touch the model. Everything else is either a question or a refusal.

The channel needs no active project and no add-in: it works with Revit closed
and nothing installed. It runs on the same `ANTHROPIC_API_KEY` as the parser,
grounded on the curated notes in `src/standards/references.ts`, and the model is
instructed never to cite a clause number it is not certain of — a standard named
without a clause is a good answer, a wrong pasal is not.

### What the notes cover

| | |
|---|---|
| **Indonesia** | PUIL 2011 (SNI 0225) — identity, socket outlets, RCD, conductor colours, capacity, earthing, minimum cross-section · SNI 03-6575 (lux) · SNI 6197 (energy) · SNI 03-3985 (fire alarm) · SNI 03-6574 (emergency lighting) · SNI 03-7015 (lightning) · SLO / UU 30/2009 |
| **Fire & life safety** | NFPA 72 · EN 54 · NFPA 110 & NFPA 20 · IEC 60849 / EN 54-16 & -24 |
| **Containment & cabling** | IEC 61537 (tray & ladder) · ISO/IEC 11801 + TIA-568 · IEEE 802.3af/at/bt (PoE) · IEC 62676 (CCTV) |
| **Design & safety** | IEC 60364 (incl. -5-52, -6, -7-701) · IEC 60287 · IEC 60529 (IP) · IEC 61439 · IEC 60947 · IEC 60269 · IEC 60598 · IEC 61140 · IEC 62305 · IEC 60034 · NFPA 70 (NEC) |

Every device the bot places has a standard behind it, and a test keeps it that
way: `/place_fire_alarm` → NFPA 72, `/create_cable_tray` → IEC 61537,
`/place_lan` → ISO/IEC 11801 and IEEE 802.3, `/place_security` → IEC 62676,
`/place_communication` → IEC 60849, `/place_lighting` → SNI 03-6575 and
SNI 03-6574, `/place_receptacle` → PUIL and IEC 60364-7-701.

Only the four best-matching notes go into any one request, so the table can grow
without the cost of a question growing with it. Nothing here changes how a
command behaves — the notes are read in this channel and nowhere else.

A session with no activity for **30 minutes** closes itself and says so, so a
command typed hours later is never swallowed as a question.

Every answer carries the same footer: reference material, not the standard
itself and not a substitute for a competent engineer's check.

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
