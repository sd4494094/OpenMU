-- Explicit opt-in only. Pass -v game_configuration_id=<configuration UUID>.
-- Enable Aria in private host configuration before using these commands.
BEGIN;
SELECT pg_advisory_xact_lock(71120261110);
CREATE TEMP TABLE aria_target ON COMMIT DROP AS
SELECT "Id" FROM config."GameConfiguration" WHERE "Id" = :'game_configuration_id'::uuid;
-- Refuse an absent target instead of silently updating nothing.
SELECT 1 / count(*)::integer FROM aria_target;
WITH requested(type_id) AS (VALUES
 ('f6102b8b-b9e6-431a-a250-cb62d08f3811'::uuid),
 ('df439c86-d936-477e-a966-7ce3fa2c2b35'::uuid),
 ('57fcd12d-7b02-496a-963c-0d13ae28e7af'::uuid),
 ('a1cb5d3d-a45d-4744-a985-235c078a1621'::uuid)
)
INSERT INTO config."PlugInConfiguration"("Id","GameConfigurationId","TypeId","IsActive")
SELECT gen_random_uuid(),target."Id",requested.type_id,true
FROM aria_target target CROSS JOIN requested
WHERE NOT EXISTS (
 SELECT 1 FROM config."PlugInConfiguration" existing
 WHERE existing."GameConfigurationId"=target."Id" AND existing."TypeId"=requested.type_id
);
UPDATE config."PlugInConfiguration" SET "IsActive"=true
WHERE "GameConfigurationId" IN (SELECT "Id" FROM aria_target)
AND "TypeId" IN (
 'f6102b8b-b9e6-431a-a250-cb62d08f3811'::uuid,
 'df439c86-d936-477e-a966-7ce3fa2c2b35'::uuid,
 '57fcd12d-7b02-496a-963c-0d13ae28e7af'::uuid,
 'a1cb5d3d-a45d-4744-a985-235c078a1621'::uuid
);
COMMIT;
