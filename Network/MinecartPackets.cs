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

[ProtoContract]
public class CartPositionPacket
{
    [ProtoMember(1)]
    public long EntityId { get; set; }
    [ProtoMember(2)]
    public double X { get; set; }
    [ProtoMember(3)]
    public double Y { get; set; }
    [ProtoMember(4)]
    public double Z { get; set; }
    [ProtoMember(5)]
    public double MotionX { get; set; }
    [ProtoMember(6)]
    public double MotionZ { get; set; }
    [ProtoMember(7)]
    public float Yaw { get; set; }
}
