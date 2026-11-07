# C2 F2 02 - LevelingWarpDialogExtended (by server)

## Is sent when

A human player talks to the dedicated leveling teleport NPC on the open source client.

## Causes the following actions on the client side

The client displays up to 64 scrollable destinations with full map name, location description, effective level and Zen price. Selecting a row sends /levelwarp followed by Token and the stable Number using the existing chat command transport; closing sends CloseNpcRequest. EntriesJson is a UTF-8 JSON array of Number, Name, Description, Level, Costs and Enabled fields. Names and row positions never identify a destination.

## Structure

| Index | Length | Data Type | Value | Description |
|-------|--------|-----------|-------|-------------|
| 0 | 1 |   Byte   | 0xC2  | [Packet type](PacketTypes.md) |
| 1 | 2 |    Short   |      | Packet header - length of the packet |
| 3 | 1 |    Byte   | 0xF2  | Packet header - packet type identifier |
| 4 | 1 |    Byte   | 0x02  | Packet header - sub packet type identifier |
| 5 | 33 | String |  | Token |
| 38 | 96 | String |  | Title |
| 134 | 64 | String |  | CloseLabel |
| 198 | 32 | String |  | LevelLabel |
| 230 | 32 | String |  | CostLabel |
| 262 |  | String |  | EntriesJson |