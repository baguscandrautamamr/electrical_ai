-- =============================================================================
-- Revit Electrical Command Center - initial schema
-- Target: Supabase (PostgreSQL 15+)
--
-- Apply with:  supabase db push
--         or:  psql "$DATABASE_URL" -f supabase/migrations/0001_init.sql
-- =============================================================================

create extension if not exists "pgcrypto";

-- =============================================================================
-- CORE TABLES
-- =============================================================================

create table if not exists projects (
  id               uuid primary key default gen_random_uuid(),
  code             text unique not null,
  name             text not null,
  client           text,
  location         text,
  revit_file_path  text,
  is_active        boolean not null default true,
  created_at       timestamptz not null default now(),
  updated_at       timestamptz not null default now()
);

create table if not exists users (
  id                uuid primary key default gen_random_uuid(),
  telegram_user_id  bigint unique not null,
  telegram_username text,
  full_name         text not null,
  role              text not null default 'viewer' check (role in ('viewer', 'editor', 'admin')),
  active_project    uuid references projects(id) on delete set null,
  theme             text not null default 'light' check (theme in ('light', 'dark')),
  language          text not null default 'id' check (language in ('id', 'en')),
  is_active         boolean not null default true,
  last_command_at   timestamptz,
  created_at        timestamptz not null default now(),
  updated_at        timestamptz not null default now()
);

-- Multi-project access: a user may hold a different role per project.
create table if not exists user_project_access (
  id          uuid primary key default gen_random_uuid(),
  user_id     uuid not null references users(id) on delete cascade,
  project_id  uuid not null references projects(id) on delete cascade,
  role        text not null default 'viewer' check (role in ('viewer', 'editor', 'admin')),
  granted_at  timestamptz not null default now(),
  unique (user_id, project_id)
);

create index if not exists idx_upa_user on user_project_access(user_id);
create index if not exists idx_upa_project on user_project_access(project_id);

-- API credentials with hard expiry. api_key is stored as a SHA-256 hash;
-- the plaintext is shown to the user once at /api connect time and never again.
create table if not exists api_credentials (
  id            uuid primary key default gen_random_uuid(),
  user_id       uuid not null references users(id) on delete cascade,
  project_id    uuid not null references projects(id) on delete cascade,
  provider      text not null default 'revit_api',
  api_key_hash  text not null,
  key_hint      text,
  expires_at    timestamptz not null,
  is_active     boolean not null default true,
  last_used_at  timestamptz,
  created_at    timestamptz not null default now(),
  updated_at    timestamptz not null default now()
);

create index if not exists idx_api_credentials_active
  on api_credentials(user_id, project_id)
  where is_active;

create index if not exists idx_api_credentials_expiry
  on api_credentials(expires_at)
  where is_active;

-- =============================================================================
-- QUEUE-BASED ARCHITECTURE
-- =============================================================================

create table if not exists commands_queue (
  id                 uuid primary key default gen_random_uuid(),
  user_id            uuid not null references users(id) on delete cascade,
  project_id         uuid not null references projects(id) on delete cascade,

  -- Telegram routing: kept on the row so the callback can reply without a join.
  chat_id            bigint,
  reply_to_message_id bigint,
  ack_message_id     bigint,

  command_type       text not null,
  command_text       text,
  command_json       jsonb not null default '{}'::jsonb,

  status             text not null default 'pending'
                       check (status in ('pending', 'processing', 'completed', 'failed', 'cancelled')),
  result_json        jsonb,
  error_message      text,
  error_stack        text,
  execution_time_ms  integer,

  queued_at          timestamptz not null default now(),
  started_at         timestamptz,
  completed_at       timestamptz,

  retry_count        integer not null default 0,
  max_retries        integer not null default 3,
  next_retry_at      timestamptz,

  -- Set once the result has been delivered back to Telegram, so the callback
  -- poller never double-sends.
  webhook_sent       boolean not null default false,

  -- Identifies which Revit add-in instance claimed the row.
  claimed_by         text,
  claimed_at         timestamptz
);

-- Polling index: the add-in's hot path.
create index if not exists idx_commands_queue_pending
  on commands_queue(project_id, queued_at)
  where status = 'pending';

-- Callback index: the webhook's hot path.
create index if not exists idx_commands_queue_undelivered
  on commands_queue(user_id, completed_at)
  where status in ('completed', 'failed') and not webhook_sent;

-- Timeout sweep index.
create index if not exists idx_commands_queue_processing
  on commands_queue(started_at)
  where status = 'processing';

create index if not exists idx_commands_queue_archive
  on commands_queue(completed_at)
  where status in ('completed', 'failed');

-- =============================================================================
-- DEVICE TABLES (8 categories)
-- =============================================================================

-- 1. Lighting
create table if not exists lighting_devices (
  id                uuid primary key default gen_random_uuid(),
  project_id        uuid not null references projects(id) on delete cascade,
  device_id         text not null,
  room_id           text,
  fixture_type      text,
  wattage           numeric,
  voltage           numeric,
  mounting_type     text,
  coordinates       jsonb,
  assigned_circuit  uuid,
  assigned_breaker  text,
  lux_contribution  numeric,
  revit_element_id  text,
  created_by        uuid references users(id) on delete set null,
  created_at        timestamptz not null default now(),
  unique (project_id, device_id)
);

-- 2. Receptacle
create table if not exists receptacle_devices (
  id                 uuid primary key default gen_random_uuid(),
  project_id         uuid not null references projects(id) on delete cascade,
  device_id          text not null,
  room_id            text,
  outlet_type        text,
  voltage            numeric,
  height_from_floor  numeric,
  wall_location      text,
  coordinates        jsonb,
  assigned_circuit   uuid,
  assigned_breaker   text,
  revit_element_id   text,
  created_by         uuid references users(id) on delete set null,
  created_at         timestamptz not null default now(),
  unique (project_id, device_id)
);

-- 3. Cable trays
create table if not exists cable_trays (
  id                        uuid primary key default gen_random_uuid(),
  project_id                uuid not null references projects(id) on delete cascade,
  tray_id                   text not null,
  from_location             text,
  to_location               text,
  size_width                numeric,
  size_height               numeric,
  material                  text,
  installation_type         text,
  total_length              numeric,
  cable_cross_section       numeric,
  fill_percentage           numeric,
  hanger_spacing_mm         numeric,
  preserve_existing_hangers boolean not null default true,
  route_json                jsonb,
  revit_element_ids         jsonb,
  created_by                uuid references users(id) on delete set null,
  created_at                timestamptz not null default now(),
  unique (project_id, tray_id)
);

-- 3b. Cable tray hangers (smart placement with gap-fill)
create table if not exists cable_tray_hangers (
  id                    uuid primary key default gen_random_uuid(),
  project_id            uuid not null references projects(id) on delete cascade,
  hanger_id             text not null,
  cable_tray_id         uuid not null references cable_trays(id) on delete cascade,
  hanger_family_name    text not null default 'Hanger',
  hanger_type           text,
  position_from_start   numeric,
  coordinates           jsonb,
  spacing_mm            numeric,
  load_capacity_kg      numeric,
  calculated_load_kg    numeric,
  load_utilization_pct  numeric,
  host_tray_id          text,
  is_horizontal_tray    boolean,
  is_existing_preserved boolean not null default false,
  is_new_placed         boolean not null default false,
  revit_element_id      text,
  created_at            timestamptz not null default now(),
  unique (project_id, hanger_id)
);

create index if not exists idx_hangers_tray on cable_tray_hangers(cable_tray_id);

-- 4. Fire alarm (NFPA 72)
create table if not exists fire_alarm_devices (
  id                  uuid primary key default gen_random_uuid(),
  project_id          uuid not null references projects(id) on delete cascade,
  device_id           text not null,
  room_id             text,
  device_type         text,
  detector_type       text,
  loop_id             text,
  loop_address        integer,
  mounting_type       text,
  coordinates         jsonb,
  coverage_radius_mm  numeric,
  is_apex_detector    boolean not null default false,
  standard            text not null default 'NFPA_72',
  revit_element_id    text,
  created_by          uuid references users(id) on delete set null,
  created_at          timestamptz not null default now(),
  unique (project_id, device_id)
);

-- Loop addresses must be unique within a loop on a project.
create unique index if not exists idx_fire_alarm_loop_address
  on fire_alarm_devices(project_id, loop_id, loop_address)
  where loop_address is not null;

-- 5. Telephone
create table if not exists telephone_devices (
  id                 uuid primary key default gen_random_uuid(),
  project_id         uuid not null references projects(id) on delete cascade,
  device_id          text not null,
  room_id            text,
  jack_type          text,
  height_from_floor  numeric,
  coordinates        jsonb,
  assigned_circuit   uuid,
  assigned_breaker   text,
  revit_element_id   text,
  created_by         uuid references users(id) on delete set null,
  created_at         timestamptz not null default now(),
  unique (project_id, device_id)
);

-- 6. LAN
create table if not exists lan_devices (
  id                    uuid primary key default gen_random_uuid(),
  project_id            uuid not null references projects(id) on delete cascade,
  device_id             text not null,
  room_id               text,
  port_type             text,
  height_from_floor     numeric,
  coordinates           jsonb,
  assigned_switch_panel text,
  assigned_switch_port  integer,
  poe_enabled           boolean not null default false,
  revit_element_id      text,
  created_by            uuid references users(id) on delete set null,
  created_at            timestamptz not null default now(),
  unique (project_id, device_id)
);

-- A physical switch port can only be assigned once per project.
create unique index if not exists idx_lan_switch_port
  on lan_devices(project_id, assigned_switch_panel, assigned_switch_port)
  where assigned_switch_port is not null;

-- 7. Security
create table if not exists security_devices (
  id                uuid primary key default gen_random_uuid(),
  project_id        uuid not null references projects(id) on delete cascade,
  device_id         text not null,
  room_id           text,
  device_type       text,
  camera_type       text,
  resolution        text,
  coordinates       jsonb,
  fov_degrees       numeric,
  coverage_radius_m numeric,
  assigned_zone_id  text,
  revit_element_id  text,
  created_by        uuid references users(id) on delete set null,
  created_at        timestamptz not null default now(),
  unique (project_id, device_id)
);

-- 8. Communication
create table if not exists communication_devices (
  id                    uuid primary key default gen_random_uuid(),
  project_id            uuid not null references projects(id) on delete cascade,
  device_id             text not null,
  room_id               text,
  device_type           text,
  system_type           text,
  coordinates           jsonb,
  coverage_radius_m     numeric,
  assigned_system_panel text,
  revit_element_id      text,
  created_by            uuid references users(id) on delete set null,
  created_at            timestamptz not null default now(),
  unique (project_id, device_id)
);

-- =============================================================================
-- SUPPORT TABLES
-- =============================================================================

create table if not exists electrical_circuits (
  id                   uuid primary key default gen_random_uuid(),
  project_id           uuid not null references projects(id) on delete cascade,
  circuit_id           text not null,
  panel_id             text,
  breaker_size         numeric,
  voltage              numeric,
  phase                text,
  total_load_w         numeric,
  load_utilization_pct numeric,
  assigned_devices     jsonb not null default '[]'::jsonb,
  created_at           timestamptz not null default now(),
  unique (project_id, circuit_id)
);

-- Foreign keys from device tables to circuits, added after both sides exist.
alter table lighting_devices
  drop constraint if exists lighting_devices_assigned_circuit_fkey;
alter table lighting_devices
  add constraint lighting_devices_assigned_circuit_fkey
  foreign key (assigned_circuit) references electrical_circuits(id) on delete set null;

alter table receptacle_devices
  drop constraint if exists receptacle_devices_assigned_circuit_fkey;
alter table receptacle_devices
  add constraint receptacle_devices_assigned_circuit_fkey
  foreign key (assigned_circuit) references electrical_circuits(id) on delete set null;

alter table telephone_devices
  drop constraint if exists telephone_devices_assigned_circuit_fkey;
alter table telephone_devices
  add constraint telephone_devices_assigned_circuit_fkey
  foreign key (assigned_circuit) references electrical_circuits(id) on delete set null;

-- Audit log; commands_queue rows are archived here by the cleanup cron.
create table if not exists commands_history (
  id                uuid primary key default gen_random_uuid(),
  command_id        uuid,
  user_id           uuid references users(id) on delete set null,
  project_id        uuid references projects(id) on delete set null,
  command_type      text,
  command_text      text,
  parsed_intent     jsonb,
  status            text check (status in ('success', 'failed')),
  result_summary    text,
  execution_time_ms integer,
  executed_at       timestamptz not null default now()
);

create index if not exists idx_commands_history_user on commands_history(user_id, executed_at desc);

create table if not exists export_jobs (
  id           uuid primary key default gen_random_uuid(),
  user_id      uuid not null references users(id) on delete cascade,
  project_id   uuid not null references projects(id) on delete cascade,
  export_type  text,
  status       text not null default 'queued'
                 check (status in ('queued', 'processing', 'completed', 'failed')),
  file_url     text,
  file_size    integer,
  error_message text,
  created_at   timestamptz not null default now(),
  completed_at timestamptz
);

create table if not exists model_cache_mep (
  id          uuid primary key default gen_random_uuid(),
  project_id  uuid not null references projects(id) on delete cascade,
  cache_key   text not null,
  cache_value jsonb not null,
  cached_at   timestamptz not null default now(),
  expires_at  timestamptz,
  unique (project_id, cache_key)
);

create index if not exists idx_model_cache_expiry on model_cache_mep(expires_at);

-- =============================================================================
-- TRIGGERS: keep updated_at honest
-- =============================================================================

create or replace function set_updated_at()
returns trigger
language plpgsql
as $$
begin
  new.updated_at = now();
  return new;
end;
$$;

do $$
declare
  t text;
begin
  foreach t in array array['projects', 'users', 'api_credentials'] loop
    execute format('drop trigger if exists trg_%s_updated_at on %I', t, t);
    execute format(
      'create trigger trg_%s_updated_at before update on %I
       for each row execute function set_updated_at()', t, t);
  end loop;
end;
$$;

-- =============================================================================
-- QUEUE RPCs
--
-- The add-in must never SELECT-then-UPDATE to claim work: two Revit instances
-- polling the same project would both see the same pending row and execute it
-- twice. claim_next_command() does the claim in a single atomic statement with
-- SKIP LOCKED so concurrent pollers get different rows (or none).
-- =============================================================================

create or replace function claim_next_command(
  p_project_id uuid,
  p_worker_id  text,
  p_timeout_seconds integer default 120
)
returns setof commands_queue
language plpgsql
as $$
begin
  -- Reclaim commands abandoned by a crashed/closed Revit instance.
  update commands_queue
     set status = case
                    when retry_count + 1 > max_retries then 'failed'
                    else 'pending'
                  end,
         retry_count = retry_count + 1,
         error_message = case
                           when retry_count + 1 > max_retries
                             then 'Timed out in processing; retries exhausted'
                           else error_message
                         end,
         completed_at = case
                          when retry_count + 1 > max_retries then now()
                          else completed_at
                        end,
         claimed_by = null,
         claimed_at = null,
         started_at = null
   where status = 'processing'
     and project_id = p_project_id
     and started_at < now() - make_interval(secs => p_timeout_seconds);

  return query
  with next_cmd as (
    select id
      from commands_queue
     where project_id = p_project_id
       and status = 'pending'
       and (next_retry_at is null or next_retry_at <= now())
     order by queued_at
     for update skip locked
     limit 1
  )
  update commands_queue q
     set status     = 'processing',
         started_at = now(),
         claimed_by = p_worker_id,
         claimed_at = now()
    from next_cmd
   where q.id = next_cmd.id
  returning q.*;
end;
$$;

create or replace function complete_command(
  p_command_id       uuid,
  p_result           jsonb,
  p_execution_time_ms integer
)
returns void
language sql
as $$
  update commands_queue
     set status = 'completed',
         result_json = p_result,
         execution_time_ms = p_execution_time_ms,
         completed_at = now(),
         error_message = null,
         error_stack = null
   where id = p_command_id;
$$;

-- Marks a command failed, or re-queues it with exponential backoff when
-- retries remain.
create or replace function fail_command(
  p_command_id  uuid,
  p_error       text,
  p_stack       text default null,
  p_retryable   boolean default true
)
returns void
language plpgsql
as $$
declare
  v_retry_count integer;
  v_max_retries integer;
begin
  select retry_count, max_retries
    into v_retry_count, v_max_retries
    from commands_queue
   where id = p_command_id;

  if not found then
    return;
  end if;

  if p_retryable and v_retry_count + 1 <= v_max_retries then
    update commands_queue
       set status = 'pending',
           retry_count = v_retry_count + 1,
           -- 4s, 8s, 16s ...
           next_retry_at = now() + make_interval(secs => power(2, v_retry_count + 1) * 2),
           error_message = p_error,
           error_stack = p_stack,
           claimed_by = null,
           claimed_at = null,
           started_at = null
     where id = p_command_id;
  else
    update commands_queue
       set status = 'failed',
           retry_count = v_retry_count + 1,
           error_message = p_error,
           error_stack = p_stack,
           completed_at = now()
     where id = p_command_id;
  end if;
end;
$$;

-- =============================================================================
-- ROW LEVEL SECURITY
--
-- The Telegram webhook and the Revit add-in both authenticate with the service
-- role key, which bypasses RLS. These policies exist so that anon/authenticated
-- keys (a future dashboard) can never read across project boundaries.
-- =============================================================================

alter table projects              enable row level security;
alter table users                 enable row level security;
alter table user_project_access   enable row level security;
alter table api_credentials       enable row level security;
alter table commands_queue        enable row level security;
alter table commands_history      enable row level security;
alter table export_jobs           enable row level security;
alter table model_cache_mep       enable row level security;
alter table electrical_circuits   enable row level security;
alter table lighting_devices      enable row level security;
alter table receptacle_devices    enable row level security;
alter table cable_trays           enable row level security;
alter table cable_tray_hangers    enable row level security;
alter table fire_alarm_devices    enable row level security;
alter table telephone_devices     enable row level security;
alter table lan_devices           enable row level security;
alter table security_devices      enable row level security;
alter table communication_devices enable row level security;

-- Projects the current JWT's user may see.
create or replace function current_user_project_ids()
returns setof uuid
language sql
stable
security definer
set search_path = public
as $$
  select upa.project_id
    from user_project_access upa
    join users u on u.id = upa.user_id
   where u.id = auth.uid();
$$;

-- Per-project isolation for every project-scoped table.
do $$
declare
  t text;
begin
  foreach t in array array[
    'commands_queue', 'commands_history', 'export_jobs', 'model_cache_mep',
    'electrical_circuits', 'lighting_devices', 'receptacle_devices',
    'cable_trays', 'cable_tray_hangers', 'fire_alarm_devices',
    'telephone_devices', 'lan_devices', 'security_devices',
    'communication_devices', 'api_credentials'
  ] loop
    execute format('drop policy if exists %I_project_isolation on %I', t, t);
    execute format(
      'create policy %I_project_isolation on %I
         for all
         using (project_id in (select current_user_project_ids()))
         with check (project_id in (select current_user_project_ids()))', t, t);
  end loop;
end;
$$;

drop policy if exists projects_member_read on projects;
create policy projects_member_read on projects
  for select using (id in (select current_user_project_ids()));

drop policy if exists users_self_read on users;
create policy users_self_read on users
  for select using (id = auth.uid());

drop policy if exists users_self_update on users;
create policy users_self_update on users
  for update using (id = auth.uid()) with check (id = auth.uid());

drop policy if exists upa_self_read on user_project_access;
create policy upa_self_read on user_project_access
  for select using (user_id = auth.uid());
