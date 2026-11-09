---
title: Dedicated leveling travel NPC
---

The optional leveling travel service keeps its destinations separate from the ordinary
**M** move menu. In Season 6, its initial NPC is **Travel Guide Noah** (710), outdoors
in Lorencia at **143, 131**. Locations are shared map positions, not private rooms.

Install the optional **Add leveling travel NPC** configuration update on existing
Season 6 databases. Fresh databases already contain its disabled defaults.
Enable both **Leveling travel feature** and **Leveling travel NPC** plugins after
reviewing their custom configuration. Disabling either prevents new travel requests.
The NPC talk configuration selects the NPC; the feature configuration owns the list.

The matching MuMain client shows a fixed right-side NPC panel headed by the NPC's
localized name. Each scrollable row shows the full map/floor name and location
description. Hover over the row to read its effective required level and Zen price;
the exit button stays visible while scrolling. Clicking an enabled row immediately requests travel. **Close**
or Escape leaves without charging. No typed command or confirmation is needed.
Unsupported clients receive an unavailable message and their conversation closes.

Initial data contains 24 leveling locations and four boss locations. Each has its
own non-respawn exit gate, and is absent from the public WarpList. The two Yeti Room
destinations are on Devias (map 2), at the verified walkable tiles **195, 165** and
**37, 25**. Their level and Zen requirements follow the ordinary Devias move at the
time of installation. Other initial levels and fees are copied from their matching
public moves; subsequent changes to the two lists are independent. Ordinary class
level reductions, map requirements and gate-entry plugins still apply.

Operators may reorder or translate rows freely. Keep each destination **Number**
stable and never reuse a deleted number for another location. **Name** identifies the
full map/floor; **Description** identifies the place within it. **IsEnabled** disables
one destination. **LevelRequirement**, **Costs** and **Gate** are independently configurable.
Plugin configuration references may be loaded independently of the running maps; travel
uses the matching runtime map and its current entry requirements.
Use a single walkable landing tile on an active map hosted by the same game context.
A menu supports up to 64 destinations and interaction within **MaximumDistance**
(default three tiles, accepted range one to ten).

The server rejects bot accounts, remote interaction, stale conversations, changed
quotes, invalid landings and unmet conditions before charging. Selections are
single-use. It closes the NPC conversation before starting the map change. A confirmed
failure to place the character is refunded; a lost client acknowledgement alone
does not trigger a refund. Dispatch logs record the stable destination, map, coordinates,
fee and whether the character was placed; they do not prove the client finished loading.

Before releasing customized locations, test the actual client landing, movement,
visibility and reopening another NPC after cancellation or travel.
