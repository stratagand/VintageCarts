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

// ── Storage cart packets ────────────────────────────────────────────────────

/// <summary>Server → client: open the storage cart GUI.</summary>
[ProtoContract]
public class OpenStorageGuiPacket
{
    [ProtoMember(1)]
    public long EntityId { get; set; }
}

// ── Drill cart packets ──────────────────────────────────────────────────────

/// <summary>Client → server: set the locked drill direction (0=Level, 1=Ascend, 2=Descend).</summary>
[ProtoContract]
public class DrillDirectionPacket
{
    [ProtoMember(1)]
    public long EntityId { get; set; }

    [ProtoMember(2)]
    public int Direction { get; set; }
}
