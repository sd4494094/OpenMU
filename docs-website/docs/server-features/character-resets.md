---
title: Character and stat resets
---

Character resets and stat resets are separate, optional features. Enable the corresponding
**Reset Feature** or **Stat Reset Feature** plugin in the admin panel and review its
custom configuration before allowing players to use it. Neither feature is enabled by default.

A character reset raises the reset count and changes the level, attributes and available
points according to the Reset Feature configuration. Leo (NPC 371) uses this feature.
Talking to Leo can perform the reset immediately when its requirements are met; use
`/resetinfo` to review requirements first.

A stat reset refunds invested attribute points and restores the character class's base
attributes. It preserves the level and character reset count. Season 6 NPC 464 opens the
native stat reset dialog. Remove equipped items before confirming the reset. The server
checks the opened NPC dialog, equipment, safe zone, level, money and any required item
before processing the request. Repeating a confirmation after the dialog closes does
not process another NPC reset.

Fresh Season 6 configurations place NPC 464 in Lorencia at (128,126), next to Leo
at (130,126). Existing configurations can install **Add stat reset NPC** on the
configuration updates page. The update preserves existing NPC windows and placements,
and does not enable either reset feature.

Fees, required levels, point formulas, required items, relocation and returning to character
selection depend on the respective plugin configuration. The client does not determine
these rules. The optional `/resetstats` chat command has its own configuration switch.
The Chinese display names used by this server are 洗点师·艾琳 and 转生导师·里奥;
clients require matching name data to display them.
