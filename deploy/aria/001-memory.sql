-- Independent NPC memory migration. Apply with ON_ERROR_STOP before enabling.
BEGIN;
SELECT pg_advisory_xact_lock(71120261110);
CREATE SCHEMA IF NOT EXISTS aria;
CREATE TABLE IF NOT EXISTS aria.schema_version (
 version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS aria.memory (
 character_id uuid NOT NULL, persona varchar(64) NOT NULL,
 summary varchar(2000) NOT NULL, through_id bigint NOT NULL DEFAULT 0,
 updated_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(character_id,persona)
);
CREATE TABLE IF NOT EXISTS aria.turn (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 character_id uuid NOT NULL, persona varchar(64) NOT NULL,
 question varchar(512) NOT NULL, answer varchar(512) NOT NULL,
 model varchar(128) NOT NULL, created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_aria_turn_character ON aria.turn(character_id,persona,id DESC);
CREATE INDEX IF NOT EXISTS ix_aria_turn_created ON aria.turn(created_at);
INSERT INTO aria.schema_version(version) VALUES(1) ON CONFLICT DO NOTHING;
COMMIT;
-- Grant the runtime account role USAGE on schema, DML on turn/memory and
-- USAGE on sequences. schema_version remains owned by the deployment role.
