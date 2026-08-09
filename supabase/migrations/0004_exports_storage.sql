-- Storage for the files the add-in generates.
--
-- A printed sheet is written to the disk of whichever machine is running Revit.
-- Telegram cannot fetch a path on someone's C: drive, so a print reached the
-- chat as a line of text nobody could open. This bucket is the hop in between:
-- the add-in uploads, and the bot hands Telegram a URL to fetch.
--
-- Public on purpose. Telegram fetches documents anonymously — it presents no
-- credential and follows no auth flow — so a private bucket cannot be delivered
-- from at all. What that means in practice is worth being clear about:
--
--   Anyone holding the full URL can read that file.
--
-- The URL contains the project id and the generated filename, including a
-- second-resolution timestamp, so it is not guessable in any practical sense;
-- but it is not secret either, and a forwarded Telegram message forwards the
-- link with it. Do not point this at a bucket holding anything the project
-- would not put in a contractor's inbox.
--
-- Turn the whole thing off with `upload_exports: false` in the add-in config,
-- and exports fall back to a local path or your own `export_base_url`.

insert into storage.buckets (id, name, public, file_size_limit)
values (
  'exports',
  'exports',
  true,
  -- Telegram refuses a document over 20 MB fetched by URL, so a larger object
  -- could only ever produce a link that fails at delivery. Rejecting it at the
  -- upload puts the error next to its cause, in the add-in log.
  20971520
)
on conflict (id) do update
  set public = excluded.public,
      file_size_limit = excluded.file_size_limit;

-- Reading is what "public" already grants. Writing is not: the add-in
-- authenticates with the service role, which bypasses RLS, so no insert policy
-- is needed and none is granted — an anon key cannot put files here.
drop policy if exists "exports are publicly readable" on storage.objects;

create policy "exports are publicly readable"
  on storage.objects
  for select
  using (bucket_id = 'exports');
