---
title: Character and stat resets
---

Character resets and stat resets are separate, optional features. Enable the corresponding
**Reset Feature** or **Stat Reset Feature** plugin in the admin panel and review its
custom configuration before allowing players to use it. Neither feature is enabled by default.

A character reset raises the reset count and changes the level, attributes and available
points according to the Reset Feature configuration. Leo (NPC 371) uses this feature.
With the updated MuMain client, talking to Leo opens a menu showing the live required
and current level, price and available zen, required items, reset limit, and the resulting
level and points. The menu also explains attribute resets, relocation and returning to
character selection, as configured by the server.

Choose **Query reset conditions** to refresh that information, or **Apply for reset** to
review it before choosing **Confirm reset**. Opening or querying the menu never performs
a reset. Confirmation is valid once for two minutes; closing or reopening the dialog,
querying again, or a change in the displayed conditions requires a new confirmation.
**Leave** or Escape closes the conversation. No typed command is needed for this flow.

The menu requires matching server and MuMain updates. Original clients receive the
requirements as messages when talking to Leo and do not immediately reset. The existing
`/resetinfo` and `/reset` chat commands remain available; inside the new NPC conversation,
`/reset` opens the confirmation step instead of performing a reset.

A stat reset refunds invested attribute points and restores the character class's base
attributes. It preserves the level and character reset count. Season 6 NPC 464 opens the
native stat reset dialog. Remove equipped items before confirming the reset. Closing the
menu or pressing Escape ends the conversation without resetting or charging, and lets
you speak to another NPC. A rejected confirmation also releases the conversation.
The server checks the opened NPC dialog, equipment, safe zone, level, money and any required item
before processing the request. Repeating a confirmation after the dialog closes does
not process another NPC reset.

Fresh Season 6 configurations place NPC 464 outside the Lorencia tavern at (145,128).
Leo remains at (130,126). Existing configurations can install **Add stat reset NPC** on the
configuration updates page. The update does not enable either reset feature.
Version 2 of that optional update moves only the original single indoor placement
at (128,126) to the outdoor location. Custom NPC windows, coordinates, quantities,
directions, triggers and additional spawns are preserved. Review customized placements
manually rather than expecting the update to override them.

Fees, required levels, point formulas, required items, relocation and returning to character
selection depend on the respective plugin configuration. The client does not determine
these rules. The optional `/resetstats` chat command has its own configuration switch.

The Chinese display names used by this server are 洗点师·艾琳 and 转生导师·里奥;
clients require matching name data to display them.
