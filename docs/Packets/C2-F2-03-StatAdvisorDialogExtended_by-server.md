# C2 F2 03 - StatAdvisorDialogExtended (by server)

## Is sent when

After a supporting client explicitly requests the stat advisor from its current stat reset NPC conversation.

## Causes the following actions on the client side

StateJson is bounded UTF-8 JSON: SessionId and Token are 32 lowercase hex digits (Token may be empty); Page is home=0, reset preview=1, allocation=2, allocation preview=3, closed=4. Title, Message and Labels are localized. CanConfirm gates explicit confirmation. ClearInputs tells the client to discard local draft amounts. AvailablePoints is the authoritative normal point balance; Stats rows contain Id (0..4), Name, Current base value, Maximum and proposed Amount. Replies replace the current page; old sessions are ignored. Successful allocations refresh authoritative character stats separately, without incremental point acknowledgements.

## Structure

| Index | Length | Data Type | Value | Description |
|-------|--------|-----------|-------|-------------|
| 0 | 1 |   Byte   | 0xC2  | [Packet type](PacketTypes.md) |
| 1 | 2 |    Short   |      | Packet header - length of the packet |
| 3 | 1 |    Byte   | 0xF2  | Packet header - packet type identifier |
| 4 | 1 |    Byte   | 0x03  | Packet header - sub packet type identifier |
| 5 |  | String |  | StateJson |