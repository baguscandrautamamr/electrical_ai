-- First project and first admin — the one setup step that cannot be done from
-- Telegram, because registration is deliberately an admin action rather than
-- self-service access to a live model.
--
-- Paste this into the Supabase SQL editor and edit the five values at the top.
-- Written as a DO block rather than with psql \set variables, because the web
-- SQL editor is not psql and silently does nothing with those.
--
-- Your Telegram id is in the bot's own "not registered" reply — send it any
-- message and it tells you. Re-running this is safe.

do $$
declare
  -- ------------------------------------------------------------- edit these
  v_telegram_id   bigint := 123456789;
  v_username      text   := 'yourhandle';
  v_full_name     text   := 'Your Name';
  v_project_code  text   := 'SITE-A';
  v_project_name  text   := 'Site A Tower';
  -- --------------------------------------------------------------------------
  v_user_id    uuid;
  v_project_id uuid;
begin
  insert into projects (code, name, client, location)
  values (v_project_code, v_project_name, 'Client Name', 'Jakarta')
  on conflict (code) do update set name = excluded.name
  returning id into v_project_id;

  -- Promote rather than fail, so a second run can fix a user created wrongly.
  insert into users (telegram_user_id, telegram_username, full_name, role, language, theme)
  values (v_telegram_id, v_username, v_full_name, 'admin', 'id', 'light')
  on conflict (telegram_user_id) do update
    set role = 'admin', is_active = true, full_name = excluded.full_name
  returning id into v_user_id;

  insert into user_project_access (user_id, project_id, role)
  values (v_user_id, v_project_id, 'admin')
  on conflict (user_id, project_id) do update set role = 'admin';

  update users set active_project = v_project_id where id = v_user_id;

  raise notice 'user % now admin on project % (%)', v_telegram_id, v_project_code, v_project_id;
end $$;

-- The project id the Revit add-in needs. The Settings dialog finds this for you
-- too — it is here for when you would rather copy it by hand.
select id as project_id, code, name from projects order by code;
