-- Refused inside a transaction block.
create index concurrently widgets_id on widgets (id);
