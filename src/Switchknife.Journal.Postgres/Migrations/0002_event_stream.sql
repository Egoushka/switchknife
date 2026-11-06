-- Switchknife.Journal.Postgres event stream: a tag to read by and a key that makes an append safe to repeat.
-- Idempotent, like 0001_steps.sql; run it after 0001 with the same migrator.
alter table step_events add column if not exists tag text;
alter table step_events add column if not exists key text;

create index if not exists step_events_by_tag on step_events (tag, seq);

create unique index if not exists step_events_by_key on step_events (flow, key) where key is not null;
