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

public class EntityMinecart : Entity, IMountable
{
    private const float MaxSpeed = 5f;
    private const float Acceleration = 1.5f;
    private const float Friction = 2f;
    private const float RiderYOffset = 0.35f;

    private static readonly Dictionary<string, float> FuelValues = new()
    {
        { "game:firewood",    30f  },
        { "game:coal-lignite", 60f  },
        { "game:coal-bituminous", 90f },
        { "game:coal-anthracite", 120f },
        { "game:charcoal",    80f  },
    };

    private MinecartSeat[] _seats = null!;

    private InventoryGeneric fuelInventory = null!;
    private float fuelSecondsRemaining = 0f;

    private BlockFacing? travelDirection = null;
    private BlockPos? lastRailPos = null;
    private bool _isReversing = false;

    public override void Initialize(EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
    {
        base.Initialize(properties, api, InChunkIndex3d);
        _seats = new[] { new MinecartSeat(this) };
        fuelInventory = new InventoryGeneric(1, "vintagecarts-fuel-" + EntityId, api);
        fuelInventory.SlotModified += _ => { };
        Pos.Motion.Set(0, 0, 0);
        ServerPos.Motion.Set(0, 0, 0);
    }

    public IMountableSeat[] Seats => _seats;
    public EntityPos Position => Pos;
    public double StepPitch => 0;
    public bool AnyMounted() => _seats[0].Passenger != null;
    public Entity Controller => _seats[0].Passenger;
    public Entity OnEntity => this;
    public EntityControls ControllingControls => _seats[0].Controls;

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

    public ItemSlot FuelSlot => fuelInventory[0];
    public float FuelSecondsRemaining => fuelSecondsRemaining;

    public void HandleFuelSlotPacket(IServerPlayer fromPlayer, FuelSlotChangedPacket packet)
    {
        if (packet.Action == 0)
        {
            if (fuelInventory[0].Itemstack != null)
            {
                if (!fromPlayer.InventoryManager.TryGiveItemstack(fuelInventory[0].Itemstack))
                    World.SpawnItemEntity(fuelInventory[0].Itemstack, Pos.XYZ);
                fuelInventory[0].Itemstack = null;
                fuelInventory[0].MarkDirty();
            }
        }
        else if (packet.Action == 1)
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

    public override void OnGameTick(float dt)
    {
        base.OnGameTick(dt);
        if (Api.Side != EnumAppSide.Server) return;

        BlockPos entityBlockPos = Pos.AsBlockPos;

        Block? railBlock = null;
        BlockPos? railPos = null;
        double bestDistSq = double.MaxValue;

        for (int yOffset = 0; yOffset >= -5; yOffset--)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                for (int dz = -2; dz <= 2; dz++)
                {
                    BlockPos checkPos = entityBlockPos.Copy();
                    checkPos.Y += yOffset;
                    checkPos.X += dx;
                    checkPos.Z += dz;

                    Block blockAtPos = World.BlockAccessor.GetBlock(checkPos);
                    if (blockAtPos is not BlockRail) continue;

                    double cx = checkPos.X + 0.5;
                    double cy = checkPos.Y + 1.0;
                    double cz = checkPos.Z + 0.5;
                    double dxp = Pos.X - cx;
                    double dyp = Pos.Y - cy;
                    double dzp = Pos.Z - cz;
                    double distSq = dxp * dxp + dyp * dyp + dzp * dzp;

                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        railBlock = blockAtPos;
                        railPos = checkPos;
                    }
                }
            }
        }

        if (railBlock == null && lastRailPos != null)
        {
            Block fallbackBlock = World.BlockAccessor.GetBlock(lastRailPos);
            double fx = lastRailPos.X + 0.5;
            double fy = lastRailPos.Y + 1.0;
            double fz = lastRailPos.Z + 0.5;
            double fdx = Pos.X - fx;
            double fdy = Pos.Y - fy;
            double fdz = Pos.Z - fz;
            double fallbackDistSq = fdx * fdx + fdy * fdy + fdz * fdz;

            if (fallbackBlock is BlockRail && fallbackDistSq < 9)
            {
                railBlock = fallbackBlock;
                railPos = lastRailPos.Copy();
            }
        }

        if (railBlock != null)
        {
            lastRailPos = railPos!.Copy();
            double railSurfaceY = railPos!.Y + 1.0;
            if (Math.Abs(Pos.Y - railSurfaceY) > 0.01)
            {
                Pos.Y = railSurfaceY;
                ServerPos.Y = railSurfaceY;
            }

            if (!railBlock.Code.Path.StartsWith("railslope"))
            {
                Pos.Motion.Y = 0;
                ServerPos.Motion.Y = 0;
            }
            HandleRailMovement(railBlock, railPos!, dt);
        }
        else
        {
            if (GetSpeed() > 0.001f)
            {
                Pos.Motion.Set(0, 0, 0);
                ServerPos.Motion.Set(0, 0, 0);
                if (lastRailPos != null)
                {
                    Pos.X = lastRailPos.X + 0.5;
                    Pos.Y = lastRailPos.Y + 1.0;
                    Pos.Z = lastRailPos.Z + 0.5;
                    ServerPos.X = Pos.X;
                    ServerPos.Y = Pos.Y;
                    ServerPos.Z = Pos.Z;
                }
            }
            travelDirection = null;
            _isReversing = false;
            Block blockAt = World.BlockAccessor.GetBlock(entityBlockPos);
            Block blockBelow = World.BlockAccessor.GetBlock(entityBlockPos.DownCopy());
            Api.Logger.Debug($"[Minecart {EntityId}] Not on rail. Block at entity: {blockAt.Code}, Block below: {blockBelow.Code}");
        }
    }

    private void HandleRailMovement(Block railBlock, BlockPos railPos, float dt)
    {
        string orientation = railBlock.Variant.ContainsKey("orientation")
            ? railBlock.Variant["orientation"] : "ns";

        bool isSlope = railBlock.Code.Path.StartsWith("railslope");

        bool hasPassenger = _seats[0].Passenger != null;
        bool movingForward  = hasPassenger && _seats[0].Controls.Forward;
        bool movingBackward = hasPassenger && (_seats[0].Controls.Backward || _seats[0].Controls.Sneak || _seats[0].Controls.Jump);

        Api.Logger.Debug($"[Minecart {EntityId}] Passenger: {hasPassenger}, Forward: {movingForward}, Backward: {movingBackward}");

        // _isReversing tracks the player's last directional intent.
        // Direction flips ONLY when the player switches between W and S —
        // re-pressing the same key after coasting continues in the same direction.
        if (movingForward)
        {
            if (_isReversing)
            {
                travelDirection = (travelDirection ?? DefaultFacingForOrientation(orientation)).Opposite;
                Pos.Motion.Set(0, 0, 0);
                ServerPos.Motion.Set(0, 0, 0);
                _isReversing = false;
            }
            else if (travelDirection == null || GetSpeed() < 0.05f)
            {
                travelDirection = DefaultFacingForOrientation(orientation);
            }
        }
        else if (movingBackward)
        {
            if (!_isReversing)
            {
                travelDirection = (travelDirection ?? DefaultFacingForOrientation(orientation)).Opposite;
                Pos.Motion.Set(0, 0, 0);
                ServerPos.Motion.Set(0, 0, 0);
                _isReversing = true;
            }
            else if (travelDirection == null)
            {
                travelDirection = DefaultFacingForOrientation(orientation).Opposite;
            }
        }
        else
        {
            if (travelDirection == null)
                travelDirection = DefaultFacingForOrientation(orientation);
        }

        int switchState = 0;
        var be = World.BlockAccessor.GetBlockEntity(railPos) as BlockEntityRailSwitch;
        if (be != null) switchState = be.SwitchState;

        BlockFacing entry = (travelDirection ?? BlockFacing.SOUTH).Opposite;
        BlockFacing exit = (railBlock as BlockRail)!.GetExitFacing(orientation, entry, switchState, World, railPos);
        travelDirection = exit;

        // Align cart visually with travel direction.
        Pos.Yaw = FacingToYaw(exit);
        ServerPos.Yaw = Pos.Yaw;

        Vec3d targetMotion = FacingToMotion(exit);

        if (isSlope)
        {
            bool ascending = orientation == "n" && exit == BlockFacing.NORTH
                          || orientation == "s" && exit == BlockFacing.SOUTH
                          || orientation == "e" && exit == BlockFacing.EAST
                          || orientation == "w" && exit == BlockFacing.WEST;
            targetMotion.Y = ascending ? 0.5 : -0.5;
            double len = Math.Sqrt(targetMotion.X * targetMotion.X + 0.25 + targetMotion.Z * targetMotion.Z);
            if (len > 0) { targetMotion.X /= len; targetMotion.Z /= len; targetMotion.Y /= len; }
        }

        float speed = (float)GetSpeed();
        float newSpeed;

        if (movingForward || movingBackward)
        {
            newSpeed = Math.Min(speed + Acceleration * dt, MaxSpeed);
        }
        else
        {
            newSpeed = Math.Max(0, speed - Friction * dt);
            if (newSpeed < 0.01f)
            {
                newSpeed = 0;
                _isReversing = false;
                if (!hasPassenger) travelDirection = null;
            }
        }

        Pos.Motion.Set(targetMotion.X * newSpeed, targetMotion.Y * newSpeed, targetMotion.Z * newSpeed);
        ServerPos.Motion.Set(targetMotion.X * newSpeed, targetMotion.Y * newSpeed, targetMotion.Z * newSpeed);

        Pos.X += targetMotion.X * newSpeed * dt;
        Pos.Z += targetMotion.Z * newSpeed * dt;

        if (orientation is "ns" or "n" or "s")
            Pos.X = railPos.X + 0.5;
        else if (orientation is "ew" or "e" or "w")
            Pos.Z = railPos.Z + 0.5;

        ServerPos.X = Pos.X;
        ServerPos.Y = Pos.Y;
        ServerPos.Z = Pos.Z;

        if (_seats[0].Passenger is EntityPlayer riderPlayer && Api is ICoreServerAPI sapi)
        {
            sapi.Network.GetChannel(VintageCartsModSystem.ChannelName)
                .SendPacket(new CartPositionPacket
                {
                    EntityId = EntityId,
                    X = Pos.X, Y = Pos.Y, Z = Pos.Z,
                    MotionX = Pos.Motion.X, MotionZ = Pos.Motion.Z,
                    Yaw = Pos.Yaw
                }, (IServerPlayer)riderPlayer.Player);
        }

        Api.Logger.Debug($"[Minecart {EntityId}] Speed: {newSpeed:F2}, Motion: ({Pos.Motion.X:F3}, {Pos.Motion.Y:F3}, {Pos.Motion.Z:F3}), Pos: ({Pos.X:F2},{Pos.Y:F2},{Pos.Z:F2})");
    }

    public void ApplyClientPositionUpdate(double x, double y, double z, double mx, double mz, float yaw = 0)
    {
        Pos.X = x; Pos.Y = y; Pos.Z = z;
        ServerPos.X = x; ServerPos.Y = y; ServerPos.Z = z;
        Pos.Motion.X = mx; Pos.Motion.Z = mz;
        ServerPos.Motion.X = mx; ServerPos.Motion.Z = mz;
        Pos.Yaw = yaw;
        ServerPos.Yaw = yaw;
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
            ServerPos.Motion.X *= scale;
            ServerPos.Motion.Z *= scale;
        }
        Pos.Motion.Y = Math.Max(Pos.Motion.Y, -20);
        ServerPos.Motion.Y = Math.Max(ServerPos.Motion.Y, -20);
    }

    private double GetSpeed()
    {
        double mx = Pos.Motion.X, mz = Pos.Motion.Z;
        return Math.Sqrt(mx * mx + mz * mz);
    }

    private static BlockFacing DefaultFacingForOrientation(string orientation) => orientation switch
    {
        "ew" or "e" => BlockFacing.EAST,
        "w"         => BlockFacing.WEST,
        "n"         => BlockFacing.NORTH,
        _           => BlockFacing.SOUTH
    };

    // VS entity Yaw: 0 = South (+Z), pi/2 = West, pi = North (-Z), 3pi/2 = East (+X)
    private static float FacingToYaw(BlockFacing facing)
    {
        if (facing == BlockFacing.NORTH) return (float)Math.PI;
        if (facing == BlockFacing.WEST)  return (float)(Math.PI * 0.5);
        if (facing == BlockFacing.EAST)  return (float)(Math.PI * 1.5);
        return 0f; // SOUTH
    }

    private BlockFacing DominantFacing()
    {
        double ax = Math.Abs(Pos.Motion.X);
        double az = Math.Abs(Pos.Motion.Z);
        if (ax < 0.001 && az < 0.001) return BlockFacing.SOUTH;
        if (ax > az)
            return Pos.Motion.X > 0 ? BlockFacing.EAST : BlockFacing.WEST;
        else
            return Pos.Motion.Z > 0 ? BlockFacing.SOUTH : BlockFacing.NORTH;
    }

    private static Vec3d FacingToMotion(BlockFacing facing)
    {
        if (facing == BlockFacing.NORTH) return new Vec3d(0, 0, -1);
        if (facing == BlockFacing.SOUTH) return new Vec3d(0, 0, 1);
        if (facing == BlockFacing.EAST)  return new Vec3d(1, 0, 0);
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
        string code = stack.Collectible.Code.ToString();
        if (FuelValues.TryGetValue(code, out float val)) return val;
        foreach (var kv in FuelValues)
            if (code.StartsWith(kv.Key.Split(':')[1])) return kv.Value;
        return 0;
    }

    public override void OnInteract(EntityAgent byEntity, ItemSlot slot, Vec3d hitPosition, EnumInteractMode mode)
    {
        if (mode != EnumInteractMode.Interact) return;
        if (Api.Side != EnumAppSide.Server) return;
        if (byEntity is not EntityPlayer entityPlayer) return;
        IServerPlayer player = (IServerPlayer)entityPlayer.Player;

        if (byEntity.Controls.Sneak)
        {
            (Api as ICoreServerAPI)!.Network
                .GetChannel(VintageCartsModSystem.ChannelName)
                .SendPacket(new OpenFuelGuiPacket { EntityId = EntityId }, player);
            return;
        }

        if (_seats[0].Passenger == null)
            byEntity.TryMount(_seats[0]);
        else if (_seats[0].Passenger == byEntity)
            byEntity.TryUnmount();
    }

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
        catch { }
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        if (Api.Side == EnumAppSide.Server && fuelInventory[0].Itemstack != null)
        {
            World.SpawnItemEntity(fuelInventory[0].Itemstack, Pos.XYZ);
            fuelInventory[0].Itemstack = null;
        }
        if (_seats?[0].Passenger != null)
            (_seats[0].Passenger as EntityAgent)?.TryUnmount();
        base.OnEntityDespawn(despawn);
    }
}
