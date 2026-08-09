-- Standards mode: a two-way PUIL / SNI / IEC reference channel.
--
-- Every other command this bot has is one shot — a message in, a result out,
-- nothing remembered. A standards question is not like that: the second
-- question is usually "and if it were three-phase?", which only means anything
-- next to the first one.
--
-- Two things therefore have to be stored rather than held in memory, because
-- the webhook is serverless and remembers nothing between requests:
--
--   1. WHICH MODE the user is in. While a user is in 'standards', the webhook
--      answers questions and never reaches the parser, so nothing they type can
--      become a queued command. That is the safety property the mode exists
--      for: a question about how to install a socket outlet must not place
--      socket outlets in the model. It is enforced by the branch, not by a
--      prompt.
--   2. THE CONVERSATION so far, so a follow-up has its antecedent.
--
-- Neither touches commands_queue, and the Revit add-in never learns this
-- feature exists.

-- --------------------------------------------------------------------- mode

alter table users
  add column if not exists chat_mode text not null default 'command';

alter table users
  drop constraint if exists users_chat_mode_check;

alter table users
  add constraint users_chat_mode_check check (chat_mode in ('command', 'standards'));

-- Last activity in the current mode, not the moment it was entered: it is
-- refreshed on every turn so an idle session can be timed out. Someone who
-- asked a question this morning and types a placement command this afternoon
-- must not have it swallowed as a question.
alter table users
  add column if not exists chat_mode_at timestamptz;

comment on column users.chat_mode is
  'command = normal Revit command parsing; standards = PUIL/SNI/IEC Q&A, which '
  'never reaches the queue. Expired back to command by /api/cron/cleanup.';

-- ------------------------------------------------------------------ threads

create table if not exists standards_threads (
  user_id     uuid primary key references users(id) on delete cascade,
  -- Kept on the row so a stale thread can be closed off in the right chat
  -- without a join back through the queue.
  chat_id     bigint,
  -- [{ "role": "user" | "assistant", "text": "..." }, ...], oldest first,
  -- trimmed to the last few turns by the service that writes it.
  turns       jsonb not null default '[]'::jsonb,
  started_at  timestamptz not null default now(),
  updated_at  timestamptz not null default now()
);

-- The idle sweep reads by age across every user, not by user.
create index if not exists idx_standards_threads_idle
  on standards_threads(updated_at);

alter table standards_threads enable row level security;

-- Same shape as users_self_read: a thread is personal, and there is no project
-- to scope it by — standards mode deliberately works with no project selected.
drop policy if exists standards_threads_self on standards_threads;
create policy standards_threads_self on standards_threads
  for all using (user_id = auth.uid()) with check (user_id = auth.uid());
