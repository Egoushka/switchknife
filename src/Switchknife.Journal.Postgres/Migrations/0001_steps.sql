-- Switchknife.Journal.Postgres schema. Embed this file (or copy it) into your own migration assembly, run it with
-- Switchknife.Migrations or any other PostgreSQL migrator, before a PgStepStore reads or writes these tables.
create table if not exists steps (
    flow        text        not null,
    key         text        not null,
    state       smallint    not null,
    attempt     integer     not null,
    result      json,
    error       text,
    started     timestamptz not null,
    finished    timestamptz,
    lease_until timestamptz not null,
    primary key (flow, key)
);

create table if not exists step_events (
    seq     bigint generated always as identity primary key,
    flow    text        not null,
    kind    text        not null,
    at      timestamptz not null,
    payload json        not null
);

create index if not exists step_events_by_flow on step_events (flow, seq);
