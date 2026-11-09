# Aria private AI conversation

Aria (NPC 711) can answer private slash-command messages through a server-side
OpenAI-compatible Responses endpoint. This optional feature is separate from
AI player bots. It does not grant the model game administration or action tools.

## Deployment

1. Back up the database and the current application image.
2. Apply `001-memory.sql` with a deployment database owner and `ON_ERROR_STOP`.
   Apply it to both new and existing databases; repeated application is safe.
   The schema has its own version table and is deliberately independent of the
   generated game entity model and its EF migrations.
3. Grant the runtime account role access (substitute the actual role name):

   ```sql
   GRANT USAGE ON SCHEMA aria TO account;
   GRANT SELECT, INSERT, UPDATE, DELETE ON aria.turn, aria.memory TO account;
   GRANT USAGE ON ALL SEQUENCES IN SCHEMA aria TO account;
   ```

4. Supply private host configuration. Do not commit real keys:

   ```text
   Aria__Enabled=true
   Aria__Endpoint=http://cli-proxy-api:8317/v1/responses
   Aria__ApiKey=<dedicated CPA client key>
   Aria__Model=GPT-6.1 Terra
   Aria__ReasoningEffort=low
   Aria__MaxOutputTokens=1024
   Aria__Concurrency=2
   Aria__QueueCapacity=8
   Aria__TimeoutSeconds=30
   Aria__CooldownSeconds=3
   Aria__RetentionDays=30
   ```

   The host uses the initialized game account connection settings, but opens an
   independent PostgreSQL pool with at most two connections. Model waits do not
   keep a database connection or player persistence lock open.

   `Aria__PromptFile` defaults to the included
   `Aria/aria-system-prompt-v01.txt`. An optional UTF-8 `Aria__KnowledgeFile`
   may contain up to 8,000 characters of verified server-specific facts.
   Prompt and knowledge files are loaded on application startup.

5. Enable the four command plugins in the admin panel:

   | Command | Plugin id |
   | --- | --- |
   | /Aria | f6102b8b-b9e6-431a-a250-cb62d08f3811 |
   | /aria | df439c86-d936-477e-a966-7ce3fa2c2b35 |
   | /艾莉雅 | 57fcd12d-7b02-496a-963c-0d13ae28e7af |
   | /小助理 | a1cb5d3d-a45d-4744-a985-235c078a1621 |

Alternatively, during an authorized deployment, run `enable-commands.sql` with
`psql -v ON_ERROR_STOP=1 -v game_configuration_id=<UUID>` and restart the host.
It explicitly opts the selected configuration into the four plugins, creates
missing registrations and is idempotent. It does not require enabling write
access on a read-only MCP endpoint.

The plugins are disabled by default and are safe when the host has no Aria
service. Hosts other than Startup require their own integration. Do not expose
CPA publicly or supply its management password as the client key.

## Memory and privacy

Each completed question/answer pair is stored under the persistent character
GUID and the `aria` persona. Recent input is limited to six turns. After enough
unsummarized turns accumulate, older turns are summarized; raw messages are
pruned hourly after 30 days by default. Summaries persist until cleared.

`/Aria 忘记我` or `/Aria forget` deletes both conversation history and the
summary for the requesting character and Aria only. Other characters and
personas are unaffected. Deletion does not remove records already included in
database backups or logs retained by an upstream provider.

The model receives the player's name, class, level, map, a small active quest
snapshot, recent messages and the summary. It receives no account password,
management credential or complete character inventory. Logs contain timing
and error types, not question bodies or keys.

Questions are echoed only to the requesting connection. Completed replies are
sent together as bounded normal-chat lines, each with the NPC sender. Splitting
preserves the complete normalized answer and includes the sender in a conservative
40-column display budget (wide characters count twice), with at most 80 UTF-8
bytes per packet for the legacy client's 90-byte chat buffer.
This avoids a known wide-character indexing risk in the candidate MuMain
client's whisper receiver. NPC sender names and reply byte limits are bounded
for the original protocol. Verify both supported clients before claiming visual
compatibility.

## Recovery

Set `Aria__Enabled=false` and recreate the application container, or restore
the previous image/configuration. Keep the additive `aria` schema during an
application rollback; old versions do not use it. Do not drop it unless the
operator deliberately wants to erase all NPC memory.

## Verification

Build and run the affected tests on dev. Set `OPENMU_ARIA_TEST_DB` only to an
isolated, migrated development database to enable the real PostgreSQL test.
Never point tests at production. Transport tests check the low reasoning
parameter and reject partial/incomplete SSE responses. Player tests check the
non-blocking path, private reply and logout cancellation. Manual checks should
also cover all aliases, a remembered name after relogin and memory deletion.
