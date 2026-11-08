# C1 F2 03 - StatAdvisorRequestExtended (by client)

## Is sent when

The extended client opens the stat advisor or navigates, previews or confirms an allocation or stat reset.

## Causes the following actions on the server side

Open=0, reset preview=1, reset confirm=2, allocation form=3, allocation preview=4, allocation confirm=5, home=6. Non-open requests require the current SessionId. Confirmations require the one-use Token; the server uses its stored quote, never client amounts. Other amounts are nonnegative increases, with zero for unsupported attributes. The original F2/00 request remains unchanged.

## Structure

| Index | Length | Data Type | Value | Description |
|-------|--------|-----------|-------|-------------|
| 0 | 1 |   Byte   | 0xC1  | [Packet type](PacketTypes.md) |
| 1 | 1 |    Byte   |   91   | Packet header - length of the packet |
| 2 | 1 |    Byte   | 0xF2  | Packet header - packet type identifier |
| 3 | 1 |    Byte   | 0x03  | Packet header - sub packet type identifier |
| 4 | 1 | Byte |  | Operation |
| 5 | 33 | String |  | SessionId |
| 38 | 33 | String |  | Token |
| 71 | 4 | IntegerLittleEndian |  | Strength |
| 75 | 4 | IntegerLittleEndian |  | Agility |
| 79 | 4 | IntegerLittleEndian |  | Vitality |
| 83 | 4 | IntegerLittleEndian |  | Energy |
| 87 | 4 | IntegerLittleEndian |  | Leadership |