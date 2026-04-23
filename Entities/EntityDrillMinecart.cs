using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using VintageCarts.Blocks;

namespace VintageCarts.Entities;

/// <summary>
/// A minecart that automatically drills through soft terrain and lays rail
/// when it reaches the end of an existing track. It breaks the block directly
/// ahead and the block above it, then places a new rail section so travel
/// continues uninterrupted on the next tick.
///
/// Only soft materials (soil, gravel, sand, snow, leaves, wood, plants) are
/// drillable. Stone, ore, metal, liquid, ice, and existing rails are not.
/// </summary>
public class EntityDrillMinecart : EntityMinecart
{
    private static readonly AssetLocation SoundDrill = new("vintagecarts:sounds/drill");

    // Set to true by OnReachedRailEnd during this server tick; cleared before each tick.
    private bool _isDrillingThisTick;

    protected override AssetLocation DropItemLocation =>
        new AssetLocation("vintagecarts:minecart-drill");

    // Walking-pace cap applied after a drill tick so the cart can't race through terrain.
    private const float DrillMaxSpeed = 1.5f;

    public override void OnGameTick(float dt)
    {
        _isDrillingThisTick = false;
        base.OnGameTick(dt); // may set _isDrillingThisTick = true via OnReachedRailEnd

        if (Api.Side == EnumAppSide.Server && _isDrillingThisTick)
            ClampMotionToDrillSpeed();
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
            Pos.Motion.X = Pos.Motion.X;
            Pos.Motion.Z = Pos.Motion.Z;
        }
    }

    protected override bool OnReachedRailEnd(BlockFacing travelFacing, BlockPos railPos)
    {
        BlockPos frontPos = OffsetPos(railPos, travelFacing);
        BlockPos abovePos = frontPos.UpCopy();

        Block frontBlock = World.BlockAccessor.GetBlock(frontPos);
        Block aboveBlock = World.BlockAccessor.GetBlock(abovePos);

        if (!CanDrill(frontBlock, frontPos) || !CanDrill(aboveBlock, abovePos))
            return false;

        // Require a rail item from the rider's inventory before proceeding.
        if (!TryConsumeRailFromPlayer())
            return false;

        // Break above first so gravel/sand above does not fall into the
        // freshly cleared front position before the rail is placed.
        if (aboveBlock.Id != 0)
            World.BlockAccessor.BreakBlock(abovePos, null);
        if (frontBlock.Id != 0)
            World.BlockAccessor.BreakBlock(frontPos, null);

        // Place the best-fitting rail variant at the cleared position.
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

        // Play sound server-side; VS replicates to nearby clients automatically.
        World.PlaySoundAt(SoundDrill, Pos.X, Pos.Y, Pos.Z, null, randomizePitch: false);

        _isDrillingThisTick = true;
        return true; // suppress default stop; cart continues on the next tick
    }

    private static readonly AssetLocation RailItemCode = new("vintagecarts:rail");

    /// <summary>
    /// Finds a rail item in the rider's inventory, removes one, and returns true.
    /// Returns false and notifies the rider if no rails are available.
    /// </summary>
    private bool TryConsumeRailFromPlayer()
    {
        if (Controller is not EntityPlayer rider) return false;

        foreach (string invClass in new[] { "hotbar", "backpack" })
        {
            IInventory inv = rider.Player.InventoryManager.GetOwnInventory(invClass);
            if (inv == null) continue;

            for (int i = 0; i < inv.Count; i++)
            {
                ItemSlot slot = inv[i];
                if (slot.Itemstack?.Collectible?.Code?.Equals(RailItemCode) == true)
                {
                    slot.TakeOut(1);
                    slot.MarkDirty();
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

        serverPlayer.SendMessage(
            0, // GeneralChatGroup
            "No Minecart Rails in your inventory for the Drill Minecart to place.",
            EnumChatType.Notification);
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        base.OnEntityDespawn(despawn);
    }

    private bool CanDrill(Block block, BlockPos pos)
    {
        // Air needs no breaking — always fine to place rail here.
        if (block.Id == 0) return true;

        // Never destroy existing track.
        if (block is BlockRail) return false;

        // Never break indestructible blocks (bedrock, barrier blocks, etc.).
        if (block.Resistance < 0) return false;

        // Never break blocks in a protected/claimed area.
        if (World is IServerWorldAccessor serverWorld)
        {
            var claims = serverWorld.Claims.Get(pos);
            if (claims != null && claims.Length > 0)
                return false;
        }

        return true;
    }
}
