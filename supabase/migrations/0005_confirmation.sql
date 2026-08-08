-- A command can now wait for its author to confirm it.
--
-- /delete_devices and /modify_devices remove work from the drawing. Typed into
-- a phone, against a room name that only has to be close enough to resolve,
-- that is one autocorrect away from clearing the wrong room. The add-in cannot
-- ask — it polls a queue and never sees the person — so the queue itself has to
-- hold the command still until they answer.
--
-- 'awaiting_confirmation' is that hold. The add-in claims 'pending' rows and
-- nothing else, so a command parked here is invisible to Revit until the
-- Telegram callback promotes it. Declining sets 'cancelled', which the queue
-- already understood.

alter table commands_queue
  drop constraint if exists commands_queue_status_check;

alter table commands_queue
  add constraint commands_queue_status_check
  check (status in (
    'pending',
    'awaiting_confirmation',
    'processing',
    'completed',
    'failed',
    'cancelled'
  ));

-- /undo reads back the last placement a person made in a project, so it can
-- name the marks to remove. Without this it is a scan of every command ever
-- queued, ordered, to take one row.
create index if not exists commands_queue_recent_for_user
  on commands_queue (user_id, project_id, status, completed_at desc);

-- A confirmation nobody answers should not sit in the queue forever holding a
-- room hostage in someone's memory of what they asked for. The cleanup cron
-- already sweeps old rows; this is the one status where expiry is a decision
-- rather than tidiness, so it is written down here rather than left implicit.
comment on constraint commands_queue_status_check on commands_queue is
  'awaiting_confirmation rows are promoted to pending by the Telegram callback, '
  'or expired to cancelled by /api/cron/cleanup after 24h.';
