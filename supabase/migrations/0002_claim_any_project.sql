-- Let a Revit instance serve every project instead of one.
--
-- The project a command belongs to is chosen in Telegram (/project), so pinning
-- the add-in to a single project id meant configuring the same fact twice — and
-- meant someone had to walk to the Revit machine to change it. With
-- p_project_id null, claim_next_command takes the oldest pending command from
-- any project; passing an id still scopes it, which is what you want when two
-- Revit instances each serve a different site.
--
-- Everything else is unchanged: still a single atomic claim with SKIP LOCKED,
-- so concurrent pollers cannot take the same row.

create or replace function claim_next_command(
  p_project_id uuid default null,
  p_worker_id  text default null,
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
     and (p_project_id is null or project_id = p_project_id)
     and started_at < now() - make_interval(secs => p_timeout_seconds);

  return query
  with next_cmd as (
    select id
      from commands_queue
     where (p_project_id is null or project_id = p_project_id)
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
