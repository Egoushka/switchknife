-- The facts library's schema (docs/adr/0001-facts-library.md). The benchmark harness applies this file, so
-- the design is executed in CI, not only described. PostgreSQL 18; btree_gist for the exclusion constraint. No other
-- extension: vectors are stored as bit codes and int8 (docs/adr/0003-facts-vector-storage.md).
create extension if not exists btree_gist;
create schema if not exists facts;

-- A thing facts are about. Consumers that already own the identity (Nytka's people) pass external_ref and the
-- library never creates a second row for it. A merge points merged_into at the survivor and deletes nothing.
create table facts.entities (
    id           uuid        primary key,
    namespace    text        not null,
    kind         text        not null,
    name         text        not null,
    external_ref text        null,
    merged_into  uuid        null references facts.entities (id),
    created_at   timestamptz not null,
    unique (namespace, external_ref)
);

-- Blocking keys for entity resolution (ADR section "Entity resolution"): both are candidate generators, never decisions.
create table facts.entity_aliases (
    entity_id     uuid not null references facts.entities (id),
    alias         text not null,
    key_phonetic  text not null,
    key_skeleton  text not null,
    primary key (entity_id, alias)
);
create index entity_aliases_phonetic on facts.entity_aliases (key_phonetic);
create index entity_aliases_skeleton on facts.entity_aliases (key_skeleton);

-- What a predicate allows. one: a subject has one value at a time (lives_in), a new value ends the old. many: values
-- coexist (likes). event: a point in time that is never superseded (met, bought).
create table facts.predicates (
    namespace   text not null,
    name        text not null,
    cardinality text not null check (cardinality in ('one', 'many', 'event')),
    primary key (namespace, name)
);

-- One row per piece of source text an extraction read. The text stays with the consumer; this keeps a pointer.
create table facts.episodes (
    id            uuid        primary key,
    namespace     text        not null,
    source_kind   text        not null,
    source_ref    text        not null,
    content_hash  text        not null,
    occurred_at   timestamptz not null,
    recorded_at   timestamptz not null,
    trust         text        not null default 'owner' check (trust in ('owner', 'agent', 'third_party')),
    status        text        not null check (status in ('pending', 'done', 'failed')),
    extractor     text        null,
    model         text        null,
    input_tokens  int         null,
    output_tokens int         null,
    unique (namespace, source_kind, source_ref, content_hash)
);

-- A fact is immutable. Only retired_at, retired_reason and superseded_by are ever written after the insert.
-- valid is when the fact was true in the world (unbounded ends are unknown, not "forever"); recorded_at and retired_at
-- are when this database believed it. slot is set by the library for cardinality 'one' (subject/predicate) and left
-- null otherwise: the exclusion constraint then makes two live, overlapping values for one slot impossible. Purge is the one
-- exception to immutability: it deletes rows and rewrites a slot.
create table facts.facts (
    id             uuid        primary key,
    namespace      text        not null,
    subject_id     uuid        not null references facts.entities (id),
    predicate      text        not null,
    object_id      uuid        null references facts.entities (id),
    object_value   text        null,
    text           text        not null check (length(text) <= 300),
    valid          tstzrange   not null,
    recorded_at    timestamptz not null,
    retired_at     timestamptz null,
    retired_reason text        null check (retired_reason in ('superseded', 'retracted', 'source_changed', 'outranked', 'merged')),
    superseded_by  uuid        null references facts.facts (id),
    slot           text        null,
    fingerprint    text        not null,
    confidence     real        null,
    episode_id     uuid        not null references facts.episodes (id),
    search         tsvector    generated always as (to_tsvector('simple', text)) stored,
    foreign key (namespace, predicate) references facts.predicates (namespace, name),
    check ((object_id is null) <> (object_value is null)),
    check ((retired_at is null) = (retired_reason is null)),
    exclude using gist (slot with =, valid with &&) where (retired_at is null and slot is not null)
);

-- One live row per assertion for many and event predicates. A one predicate's assertion may live in several pieces (clipped
-- on one side, resumed after a closed range), all sharing the fingerprint, so there the start tells the pieces apart.
create unique index facts_live_fingerprint on facts.facts (namespace, fingerprint) where retired_at is null and slot is null;
create unique index facts_live_piece on facts.facts (namespace, fingerprint, lower(valid)) where retired_at is null and slot is not null;
create index facts_by_subject on facts.facts (namespace, subject_id, predicate);
create index facts_by_valid on facts.facts using gist (valid);
create index facts_by_episode on facts.facts (episode_id);
create index facts_search on facts.facts using gin (search);

-- A retracted assertion's fingerprint: every write path skips it, so a fact the owner deleted does not come back.
-- An event log: a row blocks its fingerprint until restored_at is set. Purge replays it.
create table facts.retractions (
    namespace    text        not null,
    fingerprint  text        not null,
    retracted_at timestamptz not null,
    restored_at  timestamptz null,
    primary key (namespace, fingerprint, retracted_at)
);

-- Where a fact was read: a line range or a quote inside the episode. Every extracted fact has at least one. The assertion_*
-- columns keep what the episode asserted (the assertion's fingerprint, its start, the end it asked for, value or entity, text)
-- for a single-valued slot, because the row a source lands on can belong to a neighbouring assertion of the same value: purge
-- replays these, not the rows. Empty for many and event predicates.
create table facts.fact_sources (
    fact_id          uuid        not null references facts.facts (id),
    episode_id       uuid        not null references facts.episodes (id),
    locator          jsonb       not null default '{}',
    quote            text        null,
    assertion        text        not null default '',
    assertion_from   timestamptz null,
    assertion_to     timestamptz null,
    assertion_value  text        null,
    assertion_entity uuid        null,
    assertion_text   text        null,
    primary key (fact_id, episode_id, locator, assertion)
);

create index fact_sources_by_episode on facts.fact_sources (episode_id);

-- Embeddings, two tables per ADR 0003. fact_embeddings is the narrow table a search scans: one sign bit per component
-- (varbit, no fixed dimension), compared by XOR and bit_count. fact_vectors is the wide table only candidates are read
-- from: the unit vector as int8 (cosine ignores the scale, so none is kept), ranked by cosine in the application. One row per fact and model, so a new model
-- adds rows and never forces a migration; a model keeps one dimension (a mismatch is an error at search time).
create table facts.fact_embeddings (
    fact_id   uuid    not null references facts.facts (id),
    model     text    not null,
    namespace text    not null,
    bits      varbit  not null,
    primary key (fact_id, model)
);

create table facts.fact_vectors (
    fact_id uuid  not null references facts.facts (id),
    model   text  not null,
    q8      bytea not null,
    primary key (fact_id, model)
);
