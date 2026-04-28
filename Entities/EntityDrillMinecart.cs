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
    private const float DrillMaxSpeed = 1.5f;

    private bool _lastDrillActive;
    private ILoadedSound? _drillSound;

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

        base.OnInteract(byEntity, slot, hitPosition, mode);
    }

    protected override bool OnReachedRailEnd(BlockFacing travelFacing, BlockPos railPos)
    {
        if (!DrillActive) return false;

        BlockPos frontPos = OffsetPos(railPos, travelFacing);
        BlockPos abovePos = frontPos.UpCopy();

        Block frontBlock = World.BlockAccessor.GetBlock(frontPos);
        Block aboveBlock = World.BlockAccessor.GetBlock(abovePos);

        if (!CanDrill(frontBlock, frontPos) || !CanDrill(aboveBlock, abovePos))
            return false;

        if (!TryConsumeRailFromPlayer())
            return false;

        if (aboveBlock.Id != 0)
            World.BlockAccessor.BreakBlock(abovePos, null);
        if (frontBlock.Id != 0)
            World.BlockAccessor.BreakBlock(frontPos, null);

        Block seed = World.BlockAccessor.GetBlock(new AssetLocation("vintagecarts:rail-flat_ns"));
        if (seed is BlockRail railBlock)
        {
            string variantCode = railBlock.DetermineRailVariant(World, frontPos);
            Block target = World.BlockAccessor.GetBlock(new AssetLocation(variantCode));
            if (target != null && target.Id != 0)
            {
                World.BlockAccessor.SetBlock(target.Id, frontPos);
                railBlock.UpdateNeighborRailsPublic(World, frontPos);
            }
        }

        World.PlaySoundAt(SoundDrill, Pos.X, Pos.Y, Pos.Z, null, randomizePitch: false);
        return true;
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
