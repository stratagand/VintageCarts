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
    private static float MaxSpeed     => VintageCartsModSystem.Config.MaxSpeed;
    private static float Friction     => VintageCartsModSystem.Config.Friction;
    private static float Acceleration => VintageCartsModSystem.Config.Acceleration;
    private const double MovingAnimationSpeedThreshold = 0.01;

    private const float RiderYOffset = 0.35f;
    private const double RailVisualTopOffset = 0.125;
    private const double RampHeight = 1.0;

    private static readonly Dictionary<string, float> FuelValues = new()
    {
        { "game:firewood",    30f  },
        { "game:coal-lignite", 60f  },
        { "game:coal-bituminous", 90f },
        { "game:coal-anthracite", 120f },
        { "game:charcoal",    80f  },
    };

    private MinecartSeat[] _seats = null!;
    private ILoadedSound? _rollingSound;

    private InventoryGeneric fuelInventory = null!;
    private float fuelSecondsRemaining = 0f;
    private bool movingAnimationActive = false;

    private BlockFacing? travelDirection = null;
    private BlockPos? lastRailPos = null;
    private bool _isBeingDestroyed = false;

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
        if (Api.Side == EnumAppSide.Client)
        {
            UpdateRollingSound();
            UpdateMovingAnimation();
            return;
        }

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
                    double cy = GetRailSurfaceY(blockAtPos, checkPos, Pos.X, Pos.Z);
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
            double fy = GetRailSurfaceY(fallbackBlock, lastRailPos, Pos.X, Pos.Z);
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
            double railSurfaceY = GetRailSurfaceY(railBlock, railPos!, Pos.X, Pos.Z);
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
                    Pos.Y = GetRailSurfaceY(World.BlockAccessor.GetBlock(lastRailPos), lastRailPos, Pos.X, Pos.Z);
                    Pos.Z = lastRailPos.Z + 0.5;
                    ServerPos.X = Pos.X;
                    ServerPos.Y = Pos.Y;
                    ServerPos.Z = Pos.Z;
                }
            }
            travelDirection = null;
            Block blockAt = World.BlockAccessor.GetBlock(entityBlockPos);
            Block blockBelow = World.BlockAccessor.GetBlock(entityBlockPos.DownCopy());
            Api.Logger.Debug($"[Minecart {EntityId}] Not on rail. Block at entity: {blockAt.Code}, Block below: {blockBelow.Code}");
        }
    }

    private void HandleRailMovement(Block railBlock, BlockPos railPos, float dt)
    {
        // Extract orientation based on block type and variant group
        string orientation = ExtractRailOrientation(railBlock);
        bool isSlope = railBlock.Code.Path.StartsWith("railslope");

        bool hasPassenger = _seats[0].Passenger != null;
        bool movingForward  = hasPassenger && _seats[0].Controls.Forward;
        bool movingBackward = hasPassenger && (_seats[0].Controls.Backward || _seats[0].Controls.Sneak || _seats[0].Controls.Jump);
        bool movingLeft = hasPassenger && _seats[0].Controls.Left;
        bool movingRight = hasPassenger && _seats[0].Controls.Right;
        bool hasDirectionalInput = movingForward || movingBackward || movingLeft || movingRight;
        bool stoppedAtJunctionForInput = travelDirection == null
            && GetSpeed() < 0.05
            && (orientation.StartsWith("t-") || orientation == "cross");
        bool forwardHasNoEffect = false;
        bool backwardHasNoEffect = false;
        bool leftHasNoEffect = false;
        bool rightHasNoEffect = false;

        Api.Logger.Debug($"[Minecart {EntityId}] Passenger: {hasPassenger}, Forward: {movingForward}, Backward: {movingBackward}, Left: {movingLeft}, Right: {movingRight}");

        // Forward uses rider facing: accelerate in the rail direction most aligned with view.
        // If exactly perpendicular to available rail directions, forward has no velocity effect.
        if (movingForward)
        {
            if (stoppedAtJunctionForInput)
            {
                // Let junction branch selection resolve from rider input without
                // fabricating an entry direction when starting from rest.
            }
            else if (TryGetForwardFacingDirection(orientation, out BlockFacing facingDirection))
            {
                travelDirection = facingDirection;
            }
            else
            {
                forwardHasNoEffect = true;
            }
        }
        else if (movingBackward)
        {
            if (stoppedAtJunctionForInput)
            {
                // Let junction branch selection resolve from rider input without
                // fabricating an entry direction when starting from rest.
            }
            else if (TryGetBackwardFacingDirection(orientation, out BlockFacing facingDirection))
            {
                travelDirection = facingDirection;
            }
            else
            {
                backwardHasNoEffect = true;
            }
        }
        else if (movingLeft)
        {
            if (stoppedAtJunctionForInput)
            {
                // Let junction branch selection resolve from rider input without
                // fabricating an entry direction when starting from rest.
            }
            else if (TryGetLeftFacingDirection(orientation, out BlockFacing facingDirection))
            {
                travelDirection = facingDirection;
            }
            else
            {
                leftHasNoEffect = true;
            }
        }
        else if (movingRight)
        {
            if (stoppedAtJunctionForInput)
            {
                // Let junction branch selection resolve from rider input without
                // fabricating an entry direction when starting from rest.
            }
            else if (TryGetRightFacingDirection(orientation, out BlockFacing facingDirection))
            {
                travelDirection = facingDirection;
            }
            else
            {
                rightHasNoEffect = true;
            }
        }
        else
        {
            double horizontalSpeed = Math.Abs(Pos.Motion.X) + Math.Abs(Pos.Motion.Z);

            // With no input, direction should follow actual momentum.
            // This avoids stale-direction routing at T/cross junctions.
            if (!hasDirectionalInput && horizontalSpeed > 0.0005)
            {
                travelDirection = DominantFacing();
            }
            else if (!hasDirectionalInput && (orientation.StartsWith("t-") || orientation == "cross"))
            {
                travelDirection = InferJunctionTravelDirectionFromMotion(railPos, orientation, travelDirection);
            }
            else if (horizontalSpeed > 0.0005)
            {
                travelDirection = DominantFacing();
            }
            else if (travelDirection == null)
            {
                // If nearly stationary and no direction state exists, use rail default.
                travelDirection = horizontalSpeed > 0.001
                    ? DominantFacing()
                    : DefaultFacingForOrientation(orientation);
            }

        }

        int switchState = 0;
        var be = World.BlockAccessor.GetBlockEntity(railPos) as BlockEntityRailSwitch;
        if (be != null) switchState = be.SwitchState;

        BlockFacing entry = (travelDirection ?? BlockFacing.SOUTH).Opposite;
        BlockFacing exit = (railBlock as BlockRail)!.GetExitFacing(orientation, entry, switchState, World, railPos);

        // At junctions (multiple possible continuations), let input choose the branch
        // that best matches rider facing.
        if (hasDirectionalInput
            && TryGetJunctionPreferredExitForInput(railPos, orientation, entry, movingForward, movingBackward, movingLeft, movingRight, out BlockFacing preferredExit))
        {
            exit = preferredExit;
        }

        // T-junction special case:
        // - If straight-through continuation exists, preserve momentum by default.
        // - If straight-through does not exist and there is no user input, stop (rail-end behavior).
        bool stopAtBlockedTJunctionNoInput = orientation.StartsWith("t-")
            && !hasDirectionalInput
            && !HasRailAhead(railPos, entry.Opposite);

        if (stopAtBlockedTJunctionNoInput)
        {
            travelDirection = null;
        }
        else
        {
            travelDirection = exit;
        }

        // Avoid snapping the rider's camera: keep yaw fixed while someone is mounted.
        // We still align yaw when unmounted so parked carts face track direction.
        if (!hasPassenger)
        {
            Pos.Yaw = FacingToYaw(exit);
            ServerPos.Yaw = Pos.Yaw;
        }

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
        bool inputHasNoEffect = (movingForward && forwardHasNoEffect)
            || (movingBackward && backwardHasNoEffect)
            || (movingLeft && leftHasNoEffect)
            || (movingRight && rightHasNoEffect);

        // If the desired rail direction is opposite to current momentum,
        // brake smoothly to a full stop before reversing. Reversal begins only after
        // the cart has zeroed out (desiredDot becomes 0, so reversingInput becomes false).
        double desiredDot = Pos.Motion.X * targetMotion.X + Pos.Motion.Z * targetMotion.Z;
        bool oppositeInput = hasDirectionalInput && !inputHasNoEffect && desiredDot < -0.0001;
        bool reversingInput = oppositeInput;

        Vec3d appliedMotion = targetMotion;
        BlockFacing movementFacing = exit;

        if (stopAtBlockedTJunctionNoInput)
        {
            // Entering a blocked side of a T with no input behaves like track end.
            newSpeed = 0;
            appliedMotion = new Vec3d(0, 0, 0);
        }
        else if (reversingInput)
        {
            newSpeed = Math.Max(0, speed - Acceleration * dt);

            if (speed > 0.0001f)
            {
                Vec3d currentMotion = new Vec3d(Pos.Motion.X / speed, 0, Pos.Motion.Z / speed);
                double currentToExitDot = currentMotion.X * targetMotion.X + currentMotion.Z * targetMotion.Z;

                // Straight/opposite reversal: brake in current direction first.
                // Corner transition: reorient onto new track direction while braking.
                bool cornerLikeTransition = Math.Abs(currentToExitDot) < 0.5;

                if (cornerLikeTransition)
                {
                    appliedMotion = targetMotion;
                    movementFacing = exit;
                }
                else
                {
                    appliedMotion = currentMotion;
                    movementFacing = DominantFacing();
                }
            }
        }
        else if (movingForward && forwardHasNoEffect)
        {
            // Preserve ability to brake even when the requested direction cannot
            // be resolved on the current rail piece.
            newSpeed = Math.Max(0, speed - Acceleration * dt);
        }
        else if (movingBackward && backwardHasNoEffect)
        {
            newSpeed = Math.Max(0, speed - Acceleration * dt);
        }
        else if (movingLeft && leftHasNoEffect)
        {
            newSpeed = Math.Max(0, speed - Acceleration * dt);
        }
        else if (movingRight && rightHasNoEffect)
        {
            newSpeed = Math.Max(0, speed - Acceleration * dt);
        }
        else if (hasDirectionalInput)
        {
            newSpeed = Math.Min(speed + Acceleration * dt, MaxSpeed);
        }
        else
        {
            // No passive drag: preserve momentum until explicit user input
            // (or rail-end clamping) changes the speed.
            newSpeed = speed;
        }

        // If track ends ahead, clamp movement to this rail's boundary and stop immediately.
        // Clamp checks use the active movement-facing direction, which is either
        // current momentum (straight reverse braking) or rail exit (corner reorient).
        BlockFacing clampFacing = movementFacing;
        bool hasRailAhead = HasRailAhead(railPos, clampFacing);
        bool reachedRailEnd = false;

        double nextX = Pos.X + appliedMotion.X * newSpeed * dt;
        double nextZ = Pos.Z + appliedMotion.Z * newSpeed * dt;

        if (!hasRailAhead && newSpeed > 0)
        {
            const double edgePad = 0.001;

            if (clampFacing == BlockFacing.SOUTH)
            {
                double maxZ = railPos.Z + 1.0 - edgePad;
                if (nextZ >= maxZ)
                {
                    nextZ = maxZ;
                    reachedRailEnd = true;
                }
            }
            else if (clampFacing == BlockFacing.NORTH)
            {
                double minZ = railPos.Z + edgePad;
                if (nextZ <= minZ)
                {
                    nextZ = minZ;
                    reachedRailEnd = true;
                }
            }
            else if (clampFacing == BlockFacing.EAST)
            {
                double maxX = railPos.X + 1.0 - edgePad;
                if (nextX >= maxX)
                {
                    nextX = maxX;
                    reachedRailEnd = true;
                }
            }
            else if (clampFacing == BlockFacing.WEST)
            {
                double minX = railPos.X + edgePad;
                if (nextX <= minX)
                {
                    nextX = minX;
                    reachedRailEnd = true;
                }
            }
        }

        Pos.X = nextX;
        Pos.Z = nextZ;

        double railSurfaceY = GetRailSurfaceY(railBlock, railPos, Pos.X, Pos.Z);
        if (Math.Abs(Pos.Y - railSurfaceY) > 0.0001)
        {
            Pos.Y = railSurfaceY;
        }

        if (reachedRailEnd)
        {
            newSpeed = 0;
            travelDirection = null;
            Pos.Motion.Set(0, 0, 0);
            ServerPos.Motion.Set(0, 0, 0);
        }
        else
        {
            Pos.Motion.Set(appliedMotion.X * newSpeed, appliedMotion.Y * newSpeed, appliedMotion.Z * newSpeed);
            ServerPos.Motion.Set(appliedMotion.X * newSpeed, appliedMotion.Y * newSpeed, appliedMotion.Z * newSpeed);
        }

        // Snap to the lane implied by actual exit direction so dynamic branch exits
        // (e.g. leaving an NS run to East/West) can happen within the current tick.
        if (movementFacing == BlockFacing.NORTH || movementFacing == BlockFacing.SOUTH)
            Pos.X = railPos.X + 0.5;
        else if (movementFacing == BlockFacing.EAST || movementFacing == BlockFacing.WEST)
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

    private BlockFacing InferJunctionTravelDirectionFromMotion(BlockPos railPos, string orientation, BlockFacing? fallback)
    {
        List<BlockFacing> options = GetConnectedFacings(orientation);
        AddPerpendicularNeighborBranches(railPos, orientation, options);
        options.RemoveAll(f => !HasRailAhead(railPos, f));

        if (options.Count == 0)
            return fallback ?? BlockFacing.SOUTH;

        Vec3d motion = new Vec3d(Pos.Motion.X, 0, Pos.Motion.Z);
        double magnitude = Math.Abs(motion.X) + Math.Abs(motion.Z);
        if (magnitude <= 0.0005)
            return fallback ?? options[0];

        double bestDot = double.NegativeInfinity;
        BlockFacing best = options[0];

        foreach (BlockFacing option in options)
        {
            double dot = DotHorizontal(motion, FacingToMotion(option));
            if (dot > bestDot)
            {
                bestDot = dot;
                best = option;
            }
        }

        return best;
    }

    private void UpdateRollingSound()
    {
        if (Api is not ICoreClientAPI capi) return;

        if (_rollingSound == null)
        {
            _rollingSound = capi.World.LoadSound(new SoundParams
            {
                Location = new AssetLocation("vintagecarts:sounds/rolling-cart"),
                ShouldLoop = true,
                Position = new Vec3f((float)Pos.X, (float)Pos.Y, (float)Pos.Z),
                DisposeOnFinish = false,
                Volume = 0.8f
            });
        }

        double speed = GetSpeed();
        if (speed > 0.05)
        {
            _rollingSound.Params.Position = new Vec3f((float)Pos.X, (float)Pos.Y, (float)Pos.Z);
            float normalizedSpeed = (float)Math.Min(speed / MaxSpeed, 1.0);
            _rollingSound.SetPitch(0.8f + normalizedSpeed * 0.4f);
            if (!_rollingSound.IsPlaying)
                _rollingSound.Start();
        }
        else
        {
            if (_rollingSound.IsPlaying)
                _rollingSound.Stop();
        }
    }

    private void UpdateMovingAnimation()
    {
        if (AnimManager == null) return;

        bool shouldPlay = GetSpeed() > MovingAnimationSpeedThreshold;
        if (shouldPlay == movingAnimationActive) return;

        if (shouldPlay)
        {
            AnimManager.StartAnimation(new AnimationMetaData
            {
                Animation = "Moving",
                Code = "Moving"
            });
        }
        else
        {
            AnimManager.StopAnimation("Moving");
        }

        movingAnimationActive = shouldPlay;
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
        "se" or "sw" => BlockFacing.SOUTH,
        "ne" or "nw" => BlockFacing.NORTH,
        _ => BlockFacing.SOUTH
    };

    private static string ExtractRailOrientation(Block railBlock)
        {
            // For slope rails (railslope-*), use "orientation" variant key
            if (railBlock.Code.Path.StartsWith("railslope"))
                return railBlock.Variant.ContainsKey("orientation")
                    ? railBlock.Variant["orientation"] : "ns";

            // For switch rails (railswitch-*), use "orientation" variant key
            if (railBlock.Code.Path.StartsWith("railswitch"))
                return railBlock.Variant.ContainsKey("orientation")
                    ? railBlock.Variant["orientation"] : "ns";

            // For main rails (rail-*), use "type" variant key and extract direction component
            if (railBlock.Variant.ContainsKey("type"))
            {
                string typeValue = railBlock.Variant["type"];
                if (typeValue == "cross")
                    return "cross";
                // T-junction types: "t_n" → "t-n", "t_s" → "t-s", etc.
                if (typeValue.StartsWith("t_"))
                    return "t-" + typeValue[2..];
                // Extract direction from patterns like "curved_es", "flat_ns", "raised_we"
                int underscore = typeValue.LastIndexOf('_');
                if (underscore >= 0 && underscore < typeValue.Length - 1)
                {
                    string direction = typeValue[(underscore + 1)..];
                    // Normalize curve orientations to match GetConnections format
                    // "es" (East-South) → "se", "wn" (West-North) → "nw"
                    direction = direction switch
                    {
                        "es" => "se",
                        "wn" => "nw",
                        "we" => "ew",
                        _ => direction
                    };
                    return direction;
                }
            }

            return "ns"; // fallback
        }

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

    private bool TryGetForwardFacingDirection(string orientation, out BlockFacing direction)
    {
        return TryGetFacingDirectionByLook(orientation, GetForwardInputLookVector(), out direction);
    }

    private bool TryGetBackwardFacingDirection(string orientation, out BlockFacing direction)
    {
        return TryGetFacingDirectionByLook(orientation, GetBackwardInputLookVector(), out direction);
    }

    private bool TryGetLeftFacingDirection(string orientation, out BlockFacing direction)
    {
        return TryGetFacingDirectionByLook(orientation, GetLeftInputLookVector(), out direction);
    }

    private bool TryGetRightFacingDirection(string orientation, out BlockFacing direction)
    {
        return TryGetFacingDirectionByLook(orientation, GetRightInputLookVector(), out direction);
    }

    private bool TryGetFacingDirectionByLook(string orientation, Vec3d? lookOpt, out BlockFacing direction)
    {
        direction = BlockFacing.SOUTH;

        if (lookOpt == null)
            return false;

        Vec3d look = lookOpt;

        var (d1, d2) = BlockRail.GetConnections(orientation);

        double dot1 = DotHorizontal(look, FacingToMotion(d1));
        double dot2 = DotHorizontal(look, FacingToMotion(d2));

        // Exactly perpendicular to both candidate rail directions: no influence.
        const double epsilon = 1e-6;
        double bestDot = Math.Max(dot1, dot2);
        if (bestDot <= epsilon)
            return false;

        direction = dot1 >= dot2 ? d1 : d2;
        return true;
    }

    private bool TryGetJunctionPreferredExitForInput(BlockPos railPos, string orientation, BlockFacing entry,
        bool movingForward, bool movingBackward, bool movingLeft, bool movingRight, out BlockFacing direction)
    {
        direction = BlockFacing.SOUTH;

        Vec3d? look = movingForward ? GetForwardInputLookVector()
            : movingBackward ? GetBackwardInputLookVector()
            : movingLeft ? GetLeftInputLookVector()
            : movingRight ? GetRightInputLookVector()
            : null;

        if (look == null) return false;
        return TryGetJunctionPreferredExit(railPos, orientation, entry, look, out direction);
    }

    private bool TryGetJunctionPreferredExit(BlockPos railPos, string orientation, BlockFacing entry, Vec3d look, out BlockFacing direction)
    {
        direction = BlockFacing.SOUTH;

        List<BlockFacing> options = GetConnectedFacings(orientation);
        AddPerpendicularNeighborBranches(railPos, orientation, options);

        // Exclude the entry side we came from; we want continuation options.
        options.Remove(entry);

        // Keep only exits that actually continue to rail blocks.
        options.RemoveAll(f => !HasRailAhead(railPos, f));

        // Junction behavior only applies when more than one continuation exists.
        if (options.Count <= 1)
            return false;

        const double epsilon = 1e-6;
        double bestDot = double.NegativeInfinity;
        BlockFacing bestFacing = options[0];

        foreach (BlockFacing option in options)
        {
            double dot = DotHorizontal(look, FacingToMotion(option));
            if (dot > bestDot)
            {
                bestDot = dot;
                bestFacing = option;
            }
        }

        // If the player faces perpendicular/backward to all options, keep default routing.
        if (bestDot <= epsilon)
            return false;

        direction = bestFacing;
        return true;
    }

    private Vec3d? GetForwardInputLookVector()
    {
        if (_seats[0].Passenger is not EntityAgent rider) return null;
        return YawToHorizontalForward(rider.Pos.Yaw);
    }

    private Vec3d? GetBackwardInputLookVector()
    {
        Vec3d? forward = GetForwardInputLookVector();
        if (forward == null) return null;
        return new Vec3d(-forward.X, 0, -forward.Z);
    }

    private Vec3d? GetLeftInputLookVector()
    {
        Vec3d? forward = GetForwardInputLookVector();
        if (forward == null) return null;
        // Left vector in XZ plane
        return new Vec3d(forward.Z, 0, -forward.X);
    }

    private Vec3d? GetRightInputLookVector()
    {
        Vec3d? forward = GetForwardInputLookVector();
        if (forward == null) return null;
        // Right vector in XZ plane
        return new Vec3d(-forward.Z, 0, forward.X);
    }

    private void AddPerpendicularNeighborBranches(BlockPos railPos, string orientation, List<BlockFacing> options)
    {
        // Straight tracks can act as ad-hoc junctions if perpendicular neighbors exist.
        // NS rails may branch to E/W; EW rails may branch to N/S.
        if (orientation is "ns" or "n" or "s")
        {
            if (HasRailAhead(railPos, BlockFacing.EAST) && !options.Contains(BlockFacing.EAST))
                options.Add(BlockFacing.EAST);
            if (HasRailAhead(railPos, BlockFacing.WEST) && !options.Contains(BlockFacing.WEST))
                options.Add(BlockFacing.WEST);
        }
        else if (orientation is "ew" or "e" or "w")
        {
            if (HasRailAhead(railPos, BlockFacing.NORTH) && !options.Contains(BlockFacing.NORTH))
                options.Add(BlockFacing.NORTH);
            if (HasRailAhead(railPos, BlockFacing.SOUTH) && !options.Contains(BlockFacing.SOUTH))
                options.Add(BlockFacing.SOUTH);
        }
    }

    private static List<BlockFacing> GetConnectedFacings(string orientation)
    {
        return orientation switch
        {
            "ns" or "n" or "s" => new List<BlockFacing> { BlockFacing.NORTH, BlockFacing.SOUTH },
            "ew" or "e" or "w" => new List<BlockFacing> { BlockFacing.EAST, BlockFacing.WEST },
            "ne" => new List<BlockFacing> { BlockFacing.NORTH, BlockFacing.EAST },
            "nw" => new List<BlockFacing> { BlockFacing.NORTH, BlockFacing.WEST },
            "se" => new List<BlockFacing> { BlockFacing.SOUTH, BlockFacing.EAST },
            "sw" => new List<BlockFacing> { BlockFacing.SOUTH, BlockFacing.WEST },
            "t-n" => new List<BlockFacing> { BlockFacing.SOUTH, BlockFacing.EAST, BlockFacing.WEST },
            "t-s" => new List<BlockFacing> { BlockFacing.NORTH, BlockFacing.EAST, BlockFacing.WEST },
            "t-e" => new List<BlockFacing> { BlockFacing.NORTH, BlockFacing.SOUTH, BlockFacing.WEST },
            "t-w" => new List<BlockFacing> { BlockFacing.NORTH, BlockFacing.SOUTH, BlockFacing.EAST },
            "cross" => new List<BlockFacing> { BlockFacing.NORTH, BlockFacing.SOUTH, BlockFacing.EAST, BlockFacing.WEST },
            _ => new List<BlockFacing> { BlockFacing.NORTH, BlockFacing.SOUTH }
        };
    }

    private static Vec3d YawToHorizontalForward(float yaw)
    {
        // VS yaw convention: 0=south(+Z), pi/2=west(-X), pi=north(-Z), 3pi/2=east(+X)
        return new Vec3d(Math.Sin(yaw), 0, Math.Cos(yaw));
    }

    private static double DotHorizontal(Vec3d a, Vec3d b)
    {
        return a.X * b.X + a.Z * b.Z;
    }

    private double GetRailSurfaceY(Block railBlock, BlockPos railPos, double worldX, double worldZ)
    {
        if (TryGetRampAscentFacing(railBlock, out BlockFacing ascentFacing))
        {
            double progress = GetRampProgress(railPos, worldX, worldZ, ascentFacing);
            return railPos.Y + RailVisualTopOffset + RampHeight * progress;
        }

        return railPos.Y + RailVisualTopOffset;
    }

    private static double GetRampProgress(BlockPos railPos, double worldX, double worldZ, BlockFacing ascentFacing)
    {
        double localX = GameMath.Clamp(worldX - railPos.X, 0, 1);
        double localZ = GameMath.Clamp(worldZ - railPos.Z, 0, 1);

        if (ascentFacing == BlockFacing.NORTH) return 1.0 - localZ;
        if (ascentFacing == BlockFacing.SOUTH) return localZ;
        if (ascentFacing == BlockFacing.EAST) return localX;
        if (ascentFacing == BlockFacing.WEST) return 1.0 - localX;
        return 0.0;
    }

    private static bool TryGetRampAscentFacing(Block railBlock, out BlockFacing ascentFacing)
    {
        ascentFacing = BlockFacing.NORTH;

        if (railBlock.Code.Path.StartsWith("railslope"))
        {
            string orientation = ExtractRailOrientation(railBlock);
            ascentFacing = orientation switch
            {
                "n" => BlockFacing.NORTH,
                "s" => BlockFacing.SOUTH,
                "e" => BlockFacing.EAST,
                "w" => BlockFacing.WEST,
                _ => BlockFacing.NORTH
            };

            return orientation is "n" or "s" or "e" or "w";
        }

        if (!railBlock.Variant.ContainsKey("type"))
            return false;

        ascentFacing = railBlock.Variant["type"] switch
        {
            "raised_ns" => BlockFacing.NORTH,
            "raised_sn" => BlockFacing.SOUTH,
            "raised_we" => BlockFacing.WEST,
            "raised_ew" => BlockFacing.EAST,
            _ => BlockFacing.NORTH
        };

        return railBlock.Variant["type"].StartsWith("raised_");
    }

    private bool HasRailAhead(BlockPos railPos, BlockFacing exit)
    {
        BlockPos ahead = OffsetPos(railPos, exit);

        // Same level, one up, or one down all count as a valid continuation
        // to support transitions involving slopes.
        if (World.BlockAccessor.GetBlock(ahead) is BlockRail) return true;
        if (World.BlockAccessor.GetBlock(ahead.UpCopy()) is BlockRail) return true;
        if (World.BlockAccessor.GetBlock(ahead.DownCopy()) is BlockRail) return true;

        return false;
    }

    private static BlockPos OffsetPos(BlockPos pos, BlockFacing facing)
    {
        if (facing == BlockFacing.NORTH) return pos.NorthCopy();
        if (facing == BlockFacing.SOUTH) return pos.SouthCopy();
        if (facing == BlockFacing.EAST) return pos.EastCopy();
        if (facing == BlockFacing.WEST) return pos.WestCopy();
        return pos.Copy();
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

    public void StopMovementImmediately()
    {
        Pos.Motion.Set(0, 0, 0);
        ServerPos.Motion.Set(0, 0, 0);
        travelDirection = null;
    }

    public override void OnInteract(EntityAgent byEntity, ItemSlot slot, Vec3d hitPosition, EnumInteractMode mode)
    {
        if (Api.Side != EnumAppSide.Server) return;

        if (mode == EnumInteractMode.Attack)
        {
            DestroyAndDropMinecart(byEntity);
            return;
        }

        if (mode != EnumInteractMode.Interact) return;
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
        {
            StopMovementImmediately();
            byEntity.TryUnmount();
        }
    }

    public void TryAttachStorageCart(ItemSlot slot)
    {
        EntityProperties? props = World.GetEntityType(new AssetLocation("vintagecarts:minecart-storage"));
        if (props == null) return;

        // Spawn the storage cart 1 block behind the minecart (opposite to facing direction).
        // VS forward vector is (-sin(yaw), cos(yaw)), so back is (sin(yaw), -cos(yaw)).
        double backX = Math.Sin(Pos.Yaw);
        double backZ = -Math.Cos(Pos.Yaw);
        Vec3d spawnPos = new Vec3d(
            Pos.X + backX * 1.0,
            Pos.Y,
            Pos.Z + backZ * 1.0
        );

        EntityStorageMinecart storageCart = (EntityStorageMinecart)World.ClassRegistry.CreateEntity(props);
        storageCart.ServerPos.SetPos(spawnPos);
        storageCart.Pos.SetPos(spawnPos);
        storageCart.ServerPos.Yaw = Pos.Yaw;
        storageCart.Pos.Yaw = Pos.Yaw;
        storageCart.LeaderId = EntityId;

        World.SpawnEntity(storageCart);

        slot.TakeOut(1);
        slot.MarkDirty();
    }

    protected virtual AssetLocation DropItemLocation => new AssetLocation("vintagecarts:minecart");

    protected virtual void DestroyAndDropMinecart(EntityAgent byEntity)
    {
        if (_isBeingDestroyed) return;
        _isBeingDestroyed = true;

        // Ensure rider is unmounted before despawn so controls don't stick.
        if (_seats?[0].Passenger != null)
            (_seats[0].Passenger as EntityAgent)?.TryUnmount();

        Item? minecartItem = World.GetItem(DropItemLocation);
        if (minecartItem != null)
        {
            World.SpawnItemEntity(new ItemStack(minecartItem, 1), Pos.XYZ);
        }

        // Use Removed to avoid engine death-drop duplication; we already spawned 1 item above.
        Die(EnumDespawnReason.Removed, null);
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
        _rollingSound?.Stop();
        _rollingSound?.Dispose();
        _rollingSound = null;
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
