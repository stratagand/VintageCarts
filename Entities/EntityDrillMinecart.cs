using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using VintageCarts.Blocks;

namespace VintageCarts.Entities;

/// <summary>
/// A minecart that can optionally drill through soft terrain and lay rail.
/// Right-clicking the red/green toggle button on the east face activates or
/// deactivates drill mode. While active the speed is capped to DrillMaxSpeed
/// and the cart breaks blocks and places rail at the track end.
/// </summary>
public class EntityDrillMinecart : EntityMinecart
{
    public override byte[] LightHsv => new byte[] { 10, 6, 22 };

    private static readonly AssetLocation SoundDrill = new("vintagecarts:sounds/drill");
    private const float DrillMaxSpeed = 2.0f;

    private bool _lastDrillActive;
    private bool _lastJump;
    private ILoadedSound? _drillSound;

    protected override bool SpacebarBrakes => false;

    protected override AssetLocation DropItemLocation =>
        new AssetLocation("vintagecarts:minecart-drill");

    public bool DrillActive
    {
        get => WatchedAttributes.GetBool("drillActive", false);
        private set => WatchedAttributes.SetBool("drillActive", value);
    }

    public override void OnGameTick(float dt)
    {
        base.OnGameTick(dt);

        if (Api.Side == EnumAppSide.Server)
        {
            if (DrillActive && !AnyMounted())
                DrillActive = false;

            if (DrillActive)
                ClampMotionToDrillSpeed();

            bool jumpNow = AnyMounted() && ControllingControls.Jump;
            if (jumpNow && !_lastJump)
                DrillDir = DrillDir == DrillDirection.Level   ? DrillDirection.Ascend
                         : DrillDir == DrillDirection.Ascend  ? DrillDirection.Descend
                         :                                       DrillDirection.Level;
            _lastJump = jumpNow;
        }

        if (Api.Side == EnumAppSide.Client)
        {
            SyncDrillAnimation();
            UpdateDrillSound();
        }
    }

    private void UpdateDrillSound()
    {
        if (Api is not ICoreClientAPI capi) return;

        if (_drillSound == null)
        {
            _drillSound = capi.World.LoadSound(new SoundParams
            {
                Location = SoundDrill,
                ShouldLoop = true,
                Position = new Vec3f((float)Pos.X, (float)Pos.Y, (float)Pos.Z),
                RelativePosition = false,
                DisposeOnFinish = false,
                Volume = 1.0f
            });
        }

        if (DrillActive)
        {
            _drillSound.Params.Position = new Vec3f((float)Pos.X, (float)Pos.Y, (float)Pos.Z);
            if (!_drillSound.IsPlaying)
                _drillSound.Start();
        }
        else
        {
            if (_drillSound.IsPlaying)
                _drillSound.Stop();
        }
    }

    private void SyncDrillAnimation()
    {
        if (AnimManager == null) return;
        bool active = DrillActive;
        if (active == _lastDrillActive) return;
        _lastDrillActive = active;

        if (active)
        {
            AnimManager.StartAnimation(new AnimationMetaData
            {
                Animation = "DrillActivate",
                Code = "drillactivate",
                AnimationSpeed = 50f
            });
            AnimManager.StartAnimation(new AnimationMetaData
            {
                Animation = "drilling",
                Code = "drilling",
                EaseInSpeed = 5f,
                EaseOutSpeed = 5f
            });
        }
        else
        {
            AnimManager.StopAnimation("drillactivate");
            AnimManager.StopAnimation("drilling");
        }
    }

    private void ClampMotionToDrillSpeed()
    {
        double mx = Pos.Motion.X, mz = Pos.Motion.Z;
        double speed = Math.Sqrt(mx * mx + mz * mz);
        if (speed > DrillMaxSpeed)
        {
            double scale = DrillMaxSpeed / speed;
            Pos.Motion.X = mx * scale;
            Pos.Motion.Z = mz * scale;
        }
    }

    public override void OnInteract(EntityAgent byEntity, ItemSlot slot, Vec3d hitPosition, EnumInteractMode mode)
    {
        if (Api.Side != EnumAppSide.Server) return;

        // Sneak + right-click toggles drill mode (drill carts have no fuel GUI).
        if (mode == EnumInteractMode.Interact && byEntity.Controls.Sneak)
        {
            DrillActive = !DrillActive;
            if (byEntity is EntityPlayer ep && Api is ICoreServerAPI sapi2
                && sapi2.World.PlayerByUid(ep.PlayerUID) is IServerPlayer sp)
            {
                sp.SendMessage(0,
                    DrillActive ? "Drill Minecart: Drill mode ON" : "Drill Minecart: Drill mode OFF",
                    EnumChatType.Notification);
            }
            return;
        }

        bool wasMounted = AnyMounted();
        base.OnInteract(byEntity, slot, hitPosition, mode);
        if (!wasMounted && AnyMounted())
            DrillDir = DrillDirection.Level;
    }

    public enum DrillDirection { Level = 0, Ascend = 1, Descend = 2 }

    public DrillDirection DrillDir
    {
        get => (DrillDirection)WatchedAttributes.GetInt("drillDir", 0);
        set => WatchedAttributes.SetInt("drillDir", (int)value);
    }

    protected override bool OnReachedRailEnd(BlockFacing travelFacing, BlockPos railPos)
    {
        if (!DrillActive) return false;

        BlockFacing? turnFacing = GetTurnFacing(travelFacing);
        if (turnFacing != null)
            return DrillTurn(travelFacing, railPos, turnFacing);

        return DrillDir switch
        {
            DrillDirection.Ascend  => DrillUp(travelFacing, railPos),
            DrillDirection.Descend => DrillDown(travelFacing, railPos),
            _                      => DrillFlat(travelFacing, railPos),
        };
    }

    // Returns the perpendicular turn direction when the rider presses Left or Right
    // (exclusive — Left takes priority if both are held; Forward is ignored so the
    // player can hold W+A to move and turn simultaneously).
    private BlockFacing? GetTurnFacing(BlockFacing travelFacing)
    {
        if (!AnyMounted()) return null;

        bool goLeft  = ControllingControls.Left;
        bool goRight = ControllingControls.Right && !goLeft;
        if (!goLeft && !goRight) return null;

        // Since AngleMode = PushYaw the rider's yaw matches the cart's travel direction,
        // so Left/Right input is naturally relative to the travel direction.
        if (goLeft)
        {
            if (travelFacing == BlockFacing.NORTH) return BlockFacing.WEST;
            if (travelFacing == BlockFacing.SOUTH) return BlockFacing.EAST;
            if (travelFacing == BlockFacing.EAST)  return BlockFacing.NORTH;
            return BlockFacing.SOUTH; // WEST
        }
        else
        {
            if (travelFacing == BlockFacing.NORTH) return BlockFacing.EAST;
            if (travelFacing == BlockFacing.SOUTH) return BlockFacing.WEST;
            if (travelFacing == BlockFacing.EAST)  return BlockFacing.SOUTH;
            return BlockFacing.NORTH; // WEST
        }
    }

    private bool DrillTurn(BlockFacing travelFacing, BlockPos railPos, BlockFacing turnFacing)
    {
        BlockPos frontPos = GetDrillFrontPos(railPos, travelFacing);
        BlockPos abovePos = frontPos.UpCopy();

        Block frontBlock = World.BlockAccessor.GetBlock(frontPos);
        Block aboveBlock = World.BlockAccessor.GetBlock(abovePos);

        if (!CanDrill(frontBlock, frontPos) || !CanDrill(aboveBlock, abovePos)) return false;
        if (!TryConsumeRailFromPlayer()) return false;

        if (aboveBlock.Id != 0) World.BlockAccessor.BreakBlock(abovePos, null);
        if (frontBlock.Id != 0) World.BlockAccessor.BreakBlock(frontPos, null);

        PlaceCurveRail(frontPos, travelFacing, turnFacing);
        World.PlaySoundAt(SoundDrill, Pos.X, Pos.Y, Pos.Z, null, randomizePitch: false);
        return true;
    }

    private void PlaceCurveRail(BlockPos pos, BlockFacing travelFacing, BlockFacing turnFacing)
    {
        string variantType = GetCurveVariant(travelFacing, turnFacing);
        AssetLocation code = new($"vintagecarts:rail-{variantType}");
        Block block = World.BlockAccessor.GetBlock(code);
        if (block == null || block.Id == 0) return;

        World.BlockAccessor.SetBlock(block.Id, pos);
        if (block is BlockRail br)
            br.UpdateNeighborRailsPublic(World, pos);
    }

    // Exact block code suffixes as used in BlockRail.DetermineRailVariant.
    // Note: north+west = "curved_wn" and south+east = "curved_es" (not nw/se).
    private static string GetCurveVariant(BlockFacing a, BlockFacing b)
    {
        bool n = a == BlockFacing.NORTH || b == BlockFacing.NORTH;
        bool s = a == BlockFacing.SOUTH || b == BlockFacing.SOUTH;
        bool e = a == BlockFacing.EAST  || b == BlockFacing.EAST;
        bool w = a == BlockFacing.WEST  || b == BlockFacing.WEST;

        if (n && e) return "curved_ne";
        if (n && w) return "curved_wn";
        if (s && e) return "curved_es";
        return "curved_sw";
    }

    /// <summary>
    /// Returns the position where the next rail should be drilled, accounting for the
    /// current rail being a slope.  After an ascending slope the target is one block up;
    /// after a descending slope it is one block down; flat stays at the same Y.
    /// </summary>
    private BlockPos GetDrillFrontPos(BlockPos railPos, BlockFacing travelFacing)
    {
        Block block = World.BlockAccessor.GetBlock(railPos);
        if (block.Variant.ContainsKey("type"))
        {
            bool ascending = block.Variant["type"] switch
            {
                "raised_ns" => travelFacing == BlockFacing.NORTH,
                "raised_sn" => travelFacing == BlockFacing.SOUTH,
                "raised_ew" => travelFacing == BlockFacing.EAST,
                "raised_we" => travelFacing == BlockFacing.WEST,
                _ => false
            };
            // After an ascending slope the cart exits one block higher; after a descending
            // slope the exit Y equals the slope block's own Y (low end = block Y + 0),
            // so no vertical offset is needed — the flat case covers it.
            if (ascending) return OffsetPos(railPos, travelFacing).UpCopy();
        }
        return OffsetPos(railPos, travelFacing);
    }

    private bool DrillFlat(BlockFacing travelFacing, BlockPos railPos)
    {
        BlockPos frontPos = GetDrillFrontPos(railPos, travelFacing);
        BlockPos abovePos = frontPos.UpCopy();

        Block frontBlock = World.BlockAccessor.GetBlock(frontPos);
        Block aboveBlock = World.BlockAccessor.GetBlock(abovePos);

        if (!CanDrill(frontBlock, frontPos) || !CanDrill(aboveBlock, abovePos)) return false;
        if (!TryConsumeRailFromPlayer()) return false;

        if (aboveBlock.Id != 0) World.BlockAccessor.BreakBlock(abovePos, null);
        if (frontBlock.Id != 0) World.BlockAccessor.BreakBlock(frontPos, null);

        PlaceFlatRail(frontPos);
        World.PlaySoundAt(SoundDrill, Pos.X, Pos.Y, Pos.Z, null, randomizePitch: false);
        return true;
    }

    private bool DrillUp(BlockFacing travelFacing, BlockPos railPos)
    {
        // Slope block sits at the same Y as the current rail end and rises one block.
        // Two blocks above must be clear for rider headroom at the slope peak.
        BlockPos slopePos  = GetDrillFrontPos(railPos, travelFacing);
        BlockPos clearPos1 = slopePos.UpCopy();
        BlockPos clearPos2 = slopePos.UpCopy(2);

        Block slopeBlock  = World.BlockAccessor.GetBlock(slopePos);
        Block clear1Block = World.BlockAccessor.GetBlock(clearPos1);
        Block clear2Block = World.BlockAccessor.GetBlock(clearPos2);

        if (!CanDrill(slopeBlock, slopePos) || !CanDrill(clear1Block, clearPos1) || !CanDrill(clear2Block, clearPos2))
            return false;
        if (!TryConsumeRailFromPlayer()) return false;

        if (clear2Block.Id != 0) World.BlockAccessor.BreakBlock(clearPos2, null);
        if (clear1Block.Id != 0) World.BlockAccessor.BreakBlock(clearPos1, null);
        if (slopeBlock.Id  != 0) World.BlockAccessor.BreakBlock(slopePos,  null);

        PlaceSlopeRail(slopePos, ascentFacing: travelFacing);
        World.PlaySoundAt(SoundDrill, Pos.X, Pos.Y, Pos.Z, null, randomizePitch: false);
        return true;
    }

    private bool DrillDown(BlockFacing travelFacing, BlockPos railPos)
    {
        // frontPos is at the exit elevation of the current rail (same Y for flat/descending,
        // Y+1 for ascending — handled by GetDrillFrontPos).
        // The slope block lives one Y-level below frontPos; its ascent faces opposite to
        // travel so its high end aligns with the current rail surface level.
        // Three blocks must be clear: headroom above frontPos, frontPos itself, and slopePos.
        BlockPos frontPos = GetDrillFrontPos(railPos, travelFacing);
        BlockPos abovePos = frontPos.UpCopy();
        BlockPos slopePos = frontPos.DownCopy();

        Block aboveBlock = World.BlockAccessor.GetBlock(abovePos);
        Block frontBlock = World.BlockAccessor.GetBlock(frontPos);
        Block slopeBlock = World.BlockAccessor.GetBlock(slopePos);

        if (!CanDrill(aboveBlock, abovePos) || !CanDrill(frontBlock, frontPos) || !CanDrill(slopeBlock, slopePos))
            return false;
        if (!TryConsumeRailFromPlayer()) return false;

        if (aboveBlock.Id != 0) World.BlockAccessor.BreakBlock(abovePos, null);
        if (frontBlock.Id != 0) World.BlockAccessor.BreakBlock(frontPos, null);
        if (slopeBlock.Id != 0) World.BlockAccessor.BreakBlock(slopePos, null);

        PlaceSlopeRail(slopePos, ascentFacing: travelFacing.Opposite);
        World.PlaySoundAt(SoundDrill, Pos.X, Pos.Y, Pos.Z, null, randomizePitch: false);
        return true;
    }

    private void PlaceFlatRail(BlockPos pos)
    {
        Block seed = World.BlockAccessor.GetBlock(new AssetLocation("vintagecarts:rail-flat_ns"));
        if (seed is not BlockRail railBlock) return;

        string variantCode = railBlock.DetermineRailVariant(World, pos);
        Block target = World.BlockAccessor.GetBlock(new AssetLocation(variantCode));
        if (target != null && target.Id != 0)
        {
            World.BlockAccessor.SetBlock(target.Id, pos);
            railBlock.UpdateNeighborRailsPublic(World, pos);
        }
    }

    private void PlaceSlopeRail(BlockPos pos, BlockFacing ascentFacing)
    {
        // Slopes are rail-raised_* variants, not a separate block type.
        // Variant name encodes the ascent direction as two-letter pair (high end first).
        string variantType = ascentFacing == BlockFacing.NORTH ? "raised_ns"
                           : ascentFacing == BlockFacing.SOUTH ? "raised_sn"
                           : ascentFacing == BlockFacing.EAST  ? "raised_ew"
                           : "raised_we"; // WEST
        AssetLocation slopeCode = new($"vintagecarts:rail-{variantType}");
        Block block = World.BlockAccessor.GetBlock(slopeCode);
        if (block == null || block.Id == 0) return;

        World.BlockAccessor.SetBlock(block.Id, pos);
        if (block is BlockRail br)
            br.UpdateNeighborRailsPublic(World, pos);
    }

    private static readonly AssetLocation RailItemCode = new("vintagecarts:rail");

    private bool TryConsumeRailFromPlayer()
    {
        if (Controller is not EntityPlayer rider) return false;

        foreach (string invClass in new[] { "hotbar", "backpack" })
        {
            IInventory inv = rider.Player.InventoryManager.GetOwnInventory(invClass);
            if (inv == null) continue;

            for (int i = 0; i < inv.Count; i++)
            {
                ItemSlot invSlot = inv[i];
                if (invSlot.Itemstack?.Collectible?.Code?.Equals(RailItemCode) == true)
                {
                    invSlot.TakeOut(1);
                    invSlot.MarkDirty();
                    return true;
                }
            }
        }

        NotifyNoRails();
        return false;
    }

    private void NotifyNoRails()
    {
        if (Api is not ICoreServerAPI sapi) return;
        if (Controller is not EntityPlayer rider) return;
        if (sapi.World.PlayerByUid(rider.PlayerUID) is not IServerPlayer serverPlayer) return;

        serverPlayer.SendMessage(0,
            "No Minecart Rails in your inventory for the Drill Minecart to place.",
            EnumChatType.Notification);
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        _drillSound?.Stop();
        _drillSound?.Dispose();
        _drillSound = null;
        base.OnEntityDespawn(despawn);
    }

    private bool CanDrill(Block block, BlockPos pos)
    {
        if (block.Id == 0) return true;
        if (block is BlockRail) return false;
        if (block.Resistance < 0) return false;

        if (World is IServerWorldAccessor serverWorld)
        {
            var claims = serverWorld.Claims.Get(pos);
            if (claims != null && claims.Length > 0)
                return false;
        }

        return true;
    }
}
