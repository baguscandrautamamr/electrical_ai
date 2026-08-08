# Command reference

Parameters are `key=value` pairs separated by spaces. `key: value` also works —
both forms appear in the examples below. Values containing spaces need quotes:
`zone_id="North Wing"`.

You can also write in plain language; anything the grammar cannot parse goes to
Claude, which maps it onto one of these commands. `pasang 4 stop kontak di
Office_A` resolves to `/place_receptacle Office_A count=4`.

`/help` lists everything; `/help create_cable_tray` shows one command's
parameters in full.

## Roles

| Role | Can |
|---|---|
| `viewer` | `/export`, and all read-only admin commands |
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
| `area` | number | **required** | Room area in m² |
| `height` | number | 2.8 | Ceiling height in m |
| `lux_target` | number | 300 | Target illumination |
| `fixture_type` | string | LED_15W | Revit family name; wattage is read from it |
| `mounting` | ceiling\|wall\|floor | ceiling | |
| `spacing` | string | auto | `auto` or an explicit grid like `3.5x3.2` |
| `breaker_max` | number | 16 | Max current per breaker (A) |
| `distribution` | balanced\|manual | balanced | |
| `phase_preference` | string | ABC | |

```
/place_lighting Office_A area=45 height=2.8 lux_target=300 fixture_type=LED_15W mounting=ceiling spacing=auto breaker_max=16
```

Count comes from the lumen method — `N = (E × A) / (F × UF × MF)` — with a 0.6
combined utilisation and maintenance factor.

### `/place_receptacle <room>`

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `count` | integer | **required** | Number of outlets |
| `type` | single\|double\|grounded\|double_grounded\|gfci\|20a | double_grounded | |
| `height` | number | 0.4 | Height from floor (m) |
| `placement` | walls\|perimeter\|manual | walls | |
| `load_per_outlet` | number | 1500 | Design load (W) |
| `breaker_size` | number | 20 | A |
| `circuit_type` | general\|dedicated | general | |
| `voltage` | number | 230 | V |

```
/place_receptacle Office_A count=4 type=double_grounded height=0.4 placement=walls load_per_outlet=1500 breaker_size=20
```

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
| `area` | number | — | Improves the spacing calculation |
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

Runs all eight categories against one room. A failure in one category does not
abort the rest — a missing camera family should not cost you the lighting that
placed fine.

| Parameter | Type | Default |
|---|---|---|
| `area` | number | **required** |
| `height` | number | 2.8 |
| `lux_target` | number | 300 |
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
/equip_room Office_A area=45 height=2.8 lux_target=300 outlets=4 phone_jacks=2 lan_jacks=4 security_cameras=2 fire_alarm=auto cable_tray=yes hanger_spacing=1500
```

### `/export`

| Parameter | Type | Default |
|---|---|---|
| `type` | lighting_schedule, receptacle_schedule, cable_tray, hanger_schedule, fire_alarm_schedule, telephone_schedule, lan_schedule, security_schedule, communication_schedule, panel_schedule, compliance_report, all | all |
| `format` | excel\|pdf\|dwg\|dxf\|ifc | excel |

```
/export type=hanger_schedule format=excel
```

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
• area is required
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
