-- Switches and dimmers get their own table.
--
-- `lighting_devices` was named after Revit's Lighting *Fixtures* category and
-- holds the lamps, with a wattage and a lux contribution. Switches are the
-- other half of a lighting layout — Revit's Lighting Devices category, which is
-- a different category with different parameters — and squeezing them into the
-- fixtures table would have meant a wattage column that is always null and a
-- fixture_type column holding "three_gang".
--
-- Named for what it holds rather than for the Revit category, because
-- `lighting_devices` is already taken by the fixtures and two tables one letter
-- apart is a trap for whoever writes the next query.

create table if not exists lighting_switch_devices (
  id                 uuid primary key default gen_random_uuid(),
  project_id         uuid not null references projects(id) on delete cascade,
  device_id          text not null,
  room_id            text,
  switch_type        text,
  height_from_floor  numeric,
  -- What it switches: a circuit id, a fixture mark, or a group name. Free text
  -- rather than a foreign key — at placement time the circuit often does not
  -- exist yet, and an engineer naming "lampu meeting" is being clear enough.
  controls           text,
  mounting_type      text,
  coordinates        jsonb,
  assigned_circuit   uuid,
  assigned_breaker   text,
  revit_element_id   text,
  created_by         uuid references users(id) on delete set null,
  created_at         timestamptz not null default now(),
  unique (project_id, device_id)
);

alter table lighting_switch_devices
  drop constraint if exists lighting_switch_devices_assigned_circuit_fkey;
alter table lighting_switch_devices
  add constraint lighting_switch_devices_assigned_circuit_fkey
  foreign key (assigned_circuit) references electrical_circuits(id) on delete set null;

create index if not exists idx_lighting_switch_devices_project
  on lighting_switch_devices(project_id);

-- Same per-project isolation every other device table has: an anon or
-- authenticated client sees only the projects it has access to.
alter table lighting_switch_devices enable row level security;

drop policy if exists lighting_switch_devices_project_isolation on lighting_switch_devices;
create policy lighting_switch_devices_project_isolation on lighting_switch_devices
  for all
  using (project_id in (select current_user_project_ids()))
  with check (project_id in (select current_user_project_ids()));
