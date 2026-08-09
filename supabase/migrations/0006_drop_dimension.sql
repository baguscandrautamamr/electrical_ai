-- The /dimension command is gone.
--
-- Nothing in the schema named it — command_type is free text, so no constraint
-- or enum has to change. What is left behind is rows: a `dimension` command
-- queued before this deploy now names a handler the add-in no longer registers.
--
-- Left alone, a pending one is claimed, fails "unknown handler", and burns its
-- three retries before the user is told anything useful. Cancelling them here
-- says the real reason once, at the moment the feature was removed.
--
-- Only the rows still in flight. Completed and failed history is a record of
-- what actually happened and is not rewritten.

update commands_queue
   set status        = 'cancelled',
       error_message = 'The /dimension command has been removed from this bot.',
       completed_at  = now()
 where command_type = 'dimension'
   and status in ('pending', 'awaiting_confirmation', 'processing');
