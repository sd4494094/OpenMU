---
sidebar_position: 31
---

# Aria AI conversation

When enabled by the server operator, the creator goddess Aria offers private
conversation and help through these equivalent commands:

```text
/Aria your question
/aria your question
/艾莉雅 你的问题
/小助理 你的问题
```

Use normal chat mode and a space after the command. Clicking Aria also explains
how to start a conversation. You may ask from other maps.

All aliases share the same conversation. Your submitted question is echoed only
to you, and only you receive Aria's answer. She waits until the complete answer
is ready, then sends it together as short lines that fit the game chat panel; another question must wait
until the current request finishes. If the service is busy or unavailable, try
again later.

Aria remembers recent conversation and a stored summary for your character.
Memory remains separate from other players and NPCs. To clear your character's
history and summary with Aria, enter `/Aria forget` or `/Aria 忘记我`.
Server operators control raw conversation retention, which defaults to 30 days.
The server sends relevant conversation and character context to its configured
AI provider.

Aria cannot grant items, change attributes, teleport you or perform GM actions.
She can answer general MU Season 6 Episode 3 questions from model knowledge.
Verified server information takes precedence over general rules; she must not
present an unverified rule as a confirmed setting of this server. Her answers
remain short, with clause-aware line breaks for the narrow chat panel.

Transient connection failures and HTTP 500/502/503/504 responses are retried
once after a short randomized delay, within the original request deadline
(default 30 seconds). Authentication, billing/rate-limit errors and explicit
failed/incomplete model output are not retried. A retry never publishes partial
text or repeats the player echo, conversation storage or final reply.

Operators can find configuration, migration and recovery instructions in
[`deploy/aria/README.md`](https://github.com/sd4494094/OpenMU/blob/dev/deploy/aria/README.md).
This feature does not enable or reconfigure AI player bots.


## Optional server gameplay queries

When the operator enables Aria's dedicated MCP connection, she can query current
experience multipliers, spawn configuration, content activation and event schedules,
and explain candidate drop groups for a specified monster and map. She uses only
five read-only gameplay tools. Account data, other players, generic configuration,
logs and administration operations are not available to her.

The drop query is not an item-to-all-monsters search. A group's configured chance
is not a final item probability, dynamic quest drops may be missing, and a page
or truncated result does not prove an item cannot drop. If a query fails, Aria
must distinguish missing server information from general Season 6 knowledge.

Queries share the existing conversation deadline, with at most four calls over
three tool rounds, followed by an answer-only turn. Each page is at most 20 rows
(default 10), and tool output totals at most 16 KiB. Tool results are treated as
data and are not added to long-term player summaries. Only the final short answer
is sent and stored as part of the normal conversation; intermediate tool outputs
and model preludes are not displayed. Casual conversation makes no MCP calls.
