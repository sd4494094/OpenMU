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
