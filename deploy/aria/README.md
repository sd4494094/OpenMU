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
management credential or complete character inventory. Logs contain timing, HTTP status, model-call correlation identifiers, attempt
counts and error types, not question bodies or keys. Transient connection errors
and HTTP 500/502/503/504 are retried once with 300–700 ms jitter, respecting a
server Retry-After header. Both attempts share the original end-to-end deadline
(default 30 seconds), including queue time. Cancellation never starts a new
attempt. Explicit failed/incomplete model events, invalid requests, authentication
and ambiguous 429/quota responses are not retried. Keep upstream/CPA automatic
retries disabled to avoid multiplying attempts.

Questions are echoed only to the requesting connection. Completed replies are
sent together as bounded normal-chat lines, each with the NPC sender. Splitting
preserves the complete normalized answer and includes the sender in a conservative
40-column display budget (wide characters count twice), preferring clause and
space boundaries and keeping closing punctuation off the next line. ASCII
words and coordinates are kept together when they fit. There are at most 80 UTF-8
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


## Optional read-only MCP gameplay connection

Enable only for Aria, using a **separate Viewer API key** whose allowed MCP tools
are exactly:

```text
rates_get,drops_explain,drops_find_sources,spawns_list,content_list,events_schedule
```

Supply private host configuration (never put the key in Git, the prompt, CPA,
or the game client):

```text
Aria__Mcp__Enabled=true
Aria__Mcp__Endpoint=https://<wireguard-address>/mcp
Aria__Mcp__ApiKey=<dedicated-viewer-key>
Aria__Mcp__RootCertificateFile=/app/aria-private/openmu-mcp-root-ca.pem
Aria__Mcp__TimeoutSeconds=6
```

Mount only the public root CA as a read-only file readable by the container user.
The CA private key is never mounted. Omit RootCertificateFile for an endpoint
trusted by the operating system. HTTPS and a credential-free fixed URL are
required; custom CA trust retains host-name verification. Redirects, HTTP
fallback and proxy environment discovery are disabled on this MCP client.

The application independently permits only the six names and bounded arguments,
even if the remote key is accidentally widened. Pages default to 10 rows and are
capped at 20, text filters at 64 characters, offsets at 1000, and identifiers at
0..32767. Unknown and duplicate arguments are rejected. MCP uses the project's
existing SDK version and stateless Streamable HTTP endpoint; each query opens
and disposes its own protocol client while sharing HTTP connection pooling.
Response buffering is capped at 128 KiB. Queries have a local 1..10 second
timeout (default 6) inside the shared conversation deadline, no automatic tool
retries and no cross-player result cache. The response loop permits at most
three tool rounds and four queries, then disables tools for a final answer.
Model transport retries apply to each model HTTP hop and do not rerun completed
queries. The total request deadline still bounds every hop.

Only sanitized errors reach the model. Structured successful results are limited
to 8 KiB each and 16 KiB combined; rows are removed with explicit truncation
metadata instead of cutting JSON. Logs contain approved tool name, duration,
output size and exception type, never arguments, result bodies or credentials.
The trusted prompt treats returned names as untrusted data, explains pagination
and drop limitations, and prevents historic server settings from being treated
as live facts. Memory-summary requests do not receive tools.

To disable just this integration, set Aria__Mcp__Enabled=false and recreate the
application container. Chat, model reasoning settings and PostgreSQL memory remain
available. No database migration or game configuration update is required.


### Item drop source queries

Add only `drops_find_sources` to Aria's existing Viewer key tool allowlist when
upgrading; a new application schema alone does not widen an existing key. The
NPC's independent allowlist now contains six read-only tools; it still has no
generic configuration, account, shop or write access.

The new tool takes `text` (item name or group/number key), optional `map`
(name or number), optional `itemLevel`, and normal paging arguments. It
resolves names and searches sources in a single call, so the existing three
tool rounds, four-call cap, low reasoning and 80-character final reply remain
unchanged. `content_list` now also returns translated aliases and
`localizedName` independently of the MCP caller's culture.

No database migration or data rewrite is needed. English-only built-in names
gain aliases by exact neutral-resource matches, not by guessing from item ids.
Custom translations take precedence. Ambiguous items return candidates for
clarification instead of choosing one silently. Multi-name quest items are
filtered by the named or explicit item level.

Source results use the default generator's shared eligibility predicates,
including minimum/maximum monster levels, the twelve-level random pool window,
socket classification, explicit monster exceptions and quest conditions.
Only active maps with configured positive-quantity active monster spawns are
enumerated. Event spawn triggers are conditions, not proof that the event is
currently running. Detached global groups are configuration catalogs, not
unconditional drops. Player-specific groups, custom generators and current
party/quest/runtime eligibility are explicitly outside this static query.
Never present `groupChance` as a final per-item rate.
