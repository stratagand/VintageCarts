using ProtoBuf;

namespace VintageCarts.Network;

[ProtoContract]
public class OpenFuelGuiPacket
{
    [ProtoMember(1)]
    public long EntityId { get; set; }
}

[ProtoContract]
public class FuelSlotChangedPacket
{
    [ProtoMember(1)]
    public long EntityId { get; set; }

    // Action: 0 = take all fuel out, 1 = put itemstack in hand into the slot
    [ProtoMember(2)]
    public int Action { get; set; }
}
