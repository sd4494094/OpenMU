# C2 F2 01 - ResetNpcDialogExtended (by server)

## Is sent when

The player talks to the configured reset NPC, queries requirements, or requests a confirmation on the open source client.

## Causes the following actions on the client side

The client shows an NPC menu with localized requirements and actions. It sends the existing resetinfo or reset prepare/confirm commands only when the player chooses the corresponding option. Closing sends CloseNpcRequest. Displaying this packet never performs a reset.

## Structure

| Index | Length | Data Type | Value | Description |
|-------|--------|-----------|-------|-------------|
| 0 | 1 |   Byte   | 0xC2  | [Packet type](PacketTypes.md) |
| 1 | 2 |    Short   |      | Packet header - length of the packet |
| 3 | 1 |    Byte   | 0xF2  | Packet header - packet type identifier |
| 4 | 1 |    Byte   | 0x01  | Packet header - sub packet type identifier |
| 5 | 33 | String |  | ConfirmationToken |
| 38 | 96 | String |  | Title |
| 134 | 64 | String |  | QueryLabel |
| 198 | 64 | String |  | ApplyLabel |
| 262 | 64 | String |  | CloseLabel |
| 326 |  | String |  | Text |