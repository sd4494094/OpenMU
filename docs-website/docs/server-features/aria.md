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

All aliases share the same conversation. Only you receive Aria's answer. She
replies once after the complete answer is ready; another question must wait
until the current request finishes. If the service is busy or unavailable, try
again later.

Aria remembers recent conversation and a stored summary for your character.
Memory remains separate from other players and NPCs. To clear your character's
history and summary with Aria, enter `/Aria forget` or `/Aria 忘记我`.
Server operators control raw conversation retention, which defaults to 30 days.
The server sends relevant conversation and character context to its configured
AI provider.

Aria cannot grant items, change attributes, teleport you or perform GM actions.
Her help is based on available server information; if a rule is unknown, she
should ask for clarification instead of inventing a server-specific answer.

Operators can find configuration, migration and recovery instructions in
[`deploy/aria/README.md`](https://github.com/sd4494094/OpenMU/blob/dev/deploy/aria/README.md).
This feature does not enable or reconfigure AI player bots.
