using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using VintageCarts.BlockEntities;
using VintageCarts.Blocks;
using VintageCarts.Network;

namespace VintageCarts.Entities;

/// <summary>
/// Rideable, fuel-powered minecart entity.
/// Follows rail tracks, consumes fuel items, and exposes a fuel inventory GUI.
/// </summary>
public class EntityMinecart : Entity, IMountable
{
    // ── Constants ─────────────────────────────────────────────────────────

    private const float MaxSpeed = 5f;          // m/s along the rail
    private const float Acceleration = 1.5f;    // m/s² while fuelled
    private const float Friction = 2f;           // m/s² deceleration when off-rail or unfuelled
    private const float RiderYOffset = 0.35f;   // seat height above entity origin

    private static readonly Dictionary<string, float> FuelValues = new()
    {
        { "game:firewood",    30f  },
        { "game:coal-lignite", 60f  },
        { "game:coal-bituminous", 90f },
        { "game:coal-anthracite", 120f },
        { "game:charcoal",    80f  },
    };

    // ── IMountable seat ────────────────────────────────────────────────────

    private MinecartSeat[] _seats = null!;

    // ── State ──────────────────────────────────────────────────────────────

    private InventoryGeneric fuelInventory = null!;
    private float fuelSecondsRemaining = 0f;

    // Direction the cart is travelling (used to pick exit at junctions)
    private BlockFacing? travelDirection = null;

    // ── Initialization ─────────────────────────────────────────────────────

    public override void Initialize(EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
    {
        base.Initialize(properties, api, InChunkIndex3d);

        _seats = new[] { new MinecartSeat(this) };

        fuelInventory = new InventoryGeneric(1, "vintagecarts-fuel-" + EntityId, api);
        fuelInventory.SlotModified += _ => { };
    }

    // ── IMountable ─────────────────────────────────────────────────────────

    public IMountableSeat[] Seats => _seats;
    public EntityPos Position => Pos;
    public double StepPitch => 0;
    public bool AnyMounted() => _seats[0].Passenger != null;
    public Entity Controller => _seats[0].Passenger;
    public Entity OnEntity => this;
    public EntityControls ControllingControls => _seats[0].Controls;

    /// <summary>Factory delegate registered with api.RegisterMountable.</summary>
    public static IMountableSeat? GetMountable(IWorldAccessor world, TreeAttribute tree)
    {
        long entityId = tree.GetLong("entityId");
        string seatId = tree.GetString("seatId");
        if (world.GetEntityById(entityId) is EntityMinecart cart)
        {
            foreach (var seat in cart._seats)
                if (seat.SeatId == seatId) return seat;
        }
        return null;
    }

    // ── Fuel inventory (accessible for GUI) ───────────────────────────────

    public ItemSlot FuelSlot => fuelInventory[0];

    public float FuelSecondsRemaining => fuelSecondsRemaining;

    public void HandleFuelSlotPacket(IServerPlayer fromPlayer, FuelSlotChangedPacket packet)
    {
        if (packet.Action == 0) // take fuel out
        {
            if (fuelInventory[0].Itemstack != null)
            {
                if (!fromPlayer.InventoryManager.TryGiveItemstack(fuelInventory[0].Itemstack))
                    World.SpawnItemEntity(fuelInventory[0].Itemstack, Pos.XYZ);
                fuelInventory[0].Itemstack = null;
                fuelInventory[0].MarkDirty();
            }
        }
        else if (packet.Action == 1) // put held item into fuel slot
        {
            ItemSlot held = fromPlayer.InventoryManager.ActiveHotbarSlot;
            if (held?.Itemstack != null && GetFuelValue(held.Itemstack) > 0)
            {
                ItemStack oneItem = held.Itemstack.Clone();
                oneItem.StackSize = 1;
                if (fuelInventory[0].Itemstack == null)
                {
                    fuelInventory[0].Itemstack = oneItem;
                    held.TakeOut(1);
                    held.MarkDirty();
                    fuelInventory[0].MarkDirty();
                }
            }
        }
    }

    // ── Game Tick ──────────────────────────────────────────────────────────

    private (Block? rail, BlockPos? pos) FindRailUnder()
    {
        // Check current block, one below, and two below to handle edge-of-block Y positions.
        BlockPos p = Pos.AsBlockPos;
        for (int dy = 0; dy <= 2; dy++)
        {
            BlockPos check = dy == 0 ? p : p.DownCopy(dy);
            Block b = World.BlockAccessor.GetBlock(check);
            if (b is BlockRail) return (b, check);
        }
        return (null, null);
    }

    public override void OnGameTick(float dt)
    {
        if (Api.Side == EnumAppSide.Server)
        {
            // PRE-PHYSICS: if on a flat rail, zero any downward motion so passivephysics
            // cannot pull the cart into the rail block during its integration step.
            var (preRail, preRailPos) = FindRailUnder();
            if (preRail != null && !preRail.Code.Path.StartsWith("railslope"))
            {
                if (Pos.Motion.Y < 0) Pos.Motion.Y = 0;
                if (ServerPos.Motion.Y < 0) ServerPos.Motion.Y = 0;
                // Also pin Y before physics so passivephysics starts from the correct position.
                double railTop = preRailPos!.Y + 0.125;
                Pos.Y = railTop;
                ServerPos.Y = railTop;
            }
        }

        base.OnGameTick(dt);

        if (Api.Side != EnumAppSide.Server) return;

        // POST-PHYSICS: re-detect and snap again (belt-and-braces).
        var (railBlock, railPos) = FindRailUnder();

        if (railBlock != null)
        {
            HandleRailMovement(railBlock, railPos!, dt);
        }
        else
        {
            // Apply gravity manually (passivephysics gravity is disabled on this entity).
            Pos.Motion.Y = Math.Max(Pos.Motion.Y - 0.04f * dt * 20f, -0.5f);
            ApplyFriction(dt);
        }
    }

    private void HandleRailMovement(Block railBlock, BlockPos railPos, float dt)
    {
        string orientation = railBlock.Variant.ContainsKey("orientation")
            ? railBlock.Variant["orientation"]
            : "ns";

        bool isSlope = railBlock.Code.Path.StartsWith("railslope");

        // Pin the cart to the top of flat rails every tick (gravity is disabled, so this is authoritative).
        if (!isSlope)
        {
            double railTopY = railPos.Y + 0.125;
            Pos.Y = railTopY;
            ServerPos.Y = railTopY;
            Pos.Motion.Y = 0;
            ServerPos.Motion.Y = 0;
        }

        // Determine current travel direction from velocity
        if (travelDirection == null || GetSpeed() < 0.05f)
            travelDirection = DominantFacing();

        // Get switch state if this is a junction
        int switchState = 0;
        var be = World.BlockAccessor.GetBlockEntity(railPos) as BlockEntityRailSwitch;
        if (be != null) switchState = be.SwitchState;

        // Determine exit facing
        BlockFacing entry = travelDirection ?? BlockFacing.SOUTH;
        BlockFacing exit = (railBlock as BlockRail)!.GetExitFacing(orientation, entry, switchState, World, railPos);
        travelDirection = exit;

        // Build target velocity vector
        Vec3d targetMotion = FacingToMotion(exit);

        // Y component for slopes
        if (isSlope)
        {
            bool ascending = orientation == "n" && exit == BlockFacing.NORTH
                          || orientation == "s" && exit == BlockFacing.SOUTH
                          || orientation == "e" && exit == BlockFacing.EAST
                          || orientation == "w" && exit == BlockFacing.WEST;
            targetMotion.Y = ascending ? 0.5 : -0.5;
            // Normalize to keep total speed reasonable
            double len = Math.Sqrt(targetMotion.X * targetMotion.X + 1.0 * 0.25 + targetMotion.Z * targetMotion.Z);
            if (len > 0) { targetMotion.X /= len; targetMotion.Z /= len; targetMotion.Y /= len; }
        }

        // Burn fuel and accelerate or decelerate
        if (fuelSecondsRemaining > 0)
        {
            fuelSecondsRemaining -= dt;
            if (fuelSecondsRemaining <= 0)
            {
                fuelSecondsRemaining = 0;
                BurnNextFuel();
            }

            float speed = (float)GetSpeed();
            float newSpeed = Math.Min(speed + Acceleration * dt, MaxSpeed);
            Pos.Motion.Set(targetMotion.X * newSpeed, targetMotion.Y * newSpeed, targetMotion.Z * newSpeed);
        }
        else
        {
            // Coast to stop, still staying on rail direction
            float speed = (float)GetSpeed();
            float newSpeed = Math.Max(0, speed - Friction * dt);
            Pos.Motion.Set(targetMotion.X * newSpeed, targetMotion.Y * newSpeed, targetMotion.Z * newSpeed);
        }
    }

    private void ApplyFriction(float dt)
    {
        float speed = (float)GetSpeed();
        float newSpeed = Math.Max(0, speed - Friction * dt);
        if (speed > 0.001f)
        {
            double scale = newSpeed / speed;
            Pos.Motion.X *= scale;
            Pos.Motion.Z *= scale;
        }
        Pos.Motion.Y = Math.Max(Pos.Motion.Y, -20); // gravity already applied by physics behavior
    }

    private double GetSpeed()
    {
        double mx = Pos.Motion.X, mz = Pos.Motion.Z;
        return Math.Sqrt(mx * mx + mz * mz);
    }

    private BlockFacing DominantFacing()
    {
        double ax = Math.Abs(Pos.Motion.X);
        double az = Math.Abs(Pos.Motion.Z);

        if (ax < 0.001 && az < 0.001) return BlockFacing.SOUTH; // default

        if (ax > az)
            return Pos.Motion.X > 0 ? BlockFacing.EAST : BlockFacing.WEST;
        else
            return Pos.Motion.Z > 0 ? BlockFacing.SOUTH : BlockFacing.NORTH;
    }

    private static Vec3d FacingToMotion(BlockFacing facing)
    {
        if (facing == BlockFacing.NORTH) return new Vec3d(0, 0, -1);
        if (facing == BlockFacing.SOUTH) return new Vec3d(0, 0,  1);
        if (facing == BlockFacing.EAST)  return new Vec3d( 1, 0, 0);
        if (facing == BlockFacing.WEST)  return new Vec3d(-1, 0, 0);
        return new Vec3d(0, 0, 1);
    }

    private void BurnNextFuel()
    {
        ItemStack? stack = fuelInventory[0].Itemstack;
        if (stack == null) return;

        float value = GetFuelValue(stack);
        if (value <= 0) return;

        fuelSecondsRemaining = value;
        stack.StackSize--;
        if (stack.StackSize <= 0) fuelInventory[0].Itemstack = null;
        fuelInventory[0].MarkDirty();
    }

    private static float GetFuelValue(ItemStack stack)
    {
        string code = stack.Collectible.Code.ToShortString();
        if (FuelValues.TryGetValue(code, out float val)) return val;

        // Check by first part (e.g. any firewood variant)
        foreach (var kv in FuelValues)
            if (code.StartsWith(kv.Key.Split(':')[1])) return kv.Value;

        return 0;
    }

    // ── Interaction ────────────────────────────────────────────────────────

    public override void OnInteract(EntityAgent byEntity, ItemSlot slot, Vec3d hitPosition, EnumInteractMode mode)
    {
        if (mode != EnumInteractMode.Interact) return;
        if (Api.Side != EnumAppSide.Server) return;

        if (byEntity is not EntityPlayer entityPlayer) return;
        IServerPlayer player = (IServerPlayer)entityPlayer.Player;

        if (byEntity.Controls.Sneak)
        {
            // Open fuel GUI
            (Api as ICoreServerAPI)!.Network
                .GetChannel(VintageCartsModSystem.ChannelName)
                .SendPacket(new OpenFuelGuiPacket { EntityId = EntityId }, player);
            return;
        }

        // Mount / dismount
        if (_seats[0].Passenger == null)
        {
            byEntity.TryMount(_seats[0]);
        }
        else if (_seats[0].Passenger == byEntity)
        {
            byEntity.TryUnmount();
        }
    }

    // ── Serialization ──────────────────────────────────────────────────────

    public override void ToBytes(BinaryWriter writer, bool forClient)
    {
        base.ToBytes(writer, forClient);
        writer.Write(fuelSecondsRemaining);
        var fuelTree = new TreeAttribute();
        fuelInventory.ToTreeAttributes(fuelTree);
        fuelTree.ToBytes(writer);
    }

    public override void FromBytes(BinaryReader reader, bool isSync)
    {
        base.FromBytes(reader, isSync);
        try
        {
            fuelSecondsRemaining = reader.ReadSingle();
            TreeAttribute tree = new TreeAttribute();
            tree.FromBytes(reader);
            fuelInventory?.FromTreeAttributes(tree);
        }
        catch { /* tolerate empty/missing data on first spawn */ }
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        // Drop remaining fuel
        if (Api.Side == EnumAppSide.Server && fuelInventory[0].Itemstack != null)
        {
            World.SpawnItemEntity(fuelInventory[0].Itemstack, Pos.XYZ);
            fuelInventory[0].Itemstack = null;
        }

        // Unmount any rider
        if (_seats?[0].Passenger != null)
            (_seats[0].Passenger as EntityAgent)?.TryUnmount();

        base.OnEntityDespawn(despawn);
    }
}
