using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Common.Entities;
using VintageCarts.BlockEntities;
using VintageCarts.Entities;

namespace VintageCarts.Blocks;

/// <summary>
/// Rail block. Auto-connects to adjacent rails when placed or removed.
/// Block variants: vintagecarts:rail-{orientation} (ns,ew,ne,nw,se,sw)
///                 vintagecarts:railswitch-{orientation} (t-n,t-s,t-e,t-w,cross)
///                 vintagecarts:railslope-{orientation} (n,s,e,w)
/// </summary>
public class BlockRail : Block
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    public static bool IsRailBlock(Block b) => b is BlockRail;

    public bool IsRail(IWorldAccessor world, BlockPos pos)
        => IsRailBlock(world.BlockAccessor.GetBlock(pos));

    /// <summary>Returns true if this block variant is a switch type (T or cross).</summary>
    public bool IsSwitch => Code.Path.StartsWith("railswitch");

    /// <summary>Returns true if this block variant is a slope type.</summary>
    public bool IsSlope => Code.Path.StartsWith("railslope");

    // ── Variant Determination ─────────────────────────────────────────────

    /// <summary>
    /// Scans cardinal neighbours and returns the best-fitting flat rail
    /// block code (does not handle slope – those are placed manually).
    /// </summary>
    public string DetermineRailVariant(IWorldAccessor world, BlockPos pos)
    {
        bool north = IsRail(world, pos.NorthCopy());
        bool south = IsRail(world, pos.SouthCopy());
        bool east  = IsRail(world, pos.EastCopy());
        bool west  = IsRail(world, pos.WestCopy());

        int count = (north ? 1 : 0) + (south ? 1 : 0)
                  + (east  ? 1 : 0) + (west  ? 1 : 0);

        if (count >= 4)
            return "vintagecarts:railswitch-cross";

        if (count == 3)
        {
            if (!north) return "vintagecarts:railswitch-t-n";   // blocked N → S,E,W
            if (!south) return "vintagecarts:railswitch-t-s";   // blocked S → N,E,W
            if (!east)  return "vintagecarts:railswitch-t-e";   // blocked E → N,S,W
            return          "vintagecarts:railswitch-t-w";      // blocked W → N,S,E
        }

        if (count == 2)
        {
            if (north && south) return "vintagecarts:rail-ns";
            if (east  && west)  return "vintagecarts:rail-ew";
            if (north && east)  return "vintagecarts:rail-ne";
            if (north && west)  return "vintagecarts:rail-nw";
            if (south && east)  return "vintagecarts:rail-se";
            return "vintagecarts:rail-sw";
        }

        // 0 or 1 neighbours: default straight aligned toward the single neighbour
        if (count == 1)
        {
            if (north || south) return "vintagecarts:rail-ns";
            return "vintagecarts:rail-ew";
        }

        return "vintagecarts:rail-ns"; // isolated default
    }

    private void PlaceVariant(IWorldAccessor world, BlockPos pos, string blockCode)
    {
        Block target = world.BlockAccessor.GetBlock(new AssetLocation(blockCode));
        if (target != null && target.Id != 0 && target.Id != world.BlockAccessor.GetBlock(pos).Id)
        {
            world.BlockAccessor.SetBlock(target.Id, pos);
        }
    }

    /// <summary>Re-evaluates and updates all cardinal rail neighbours.</summary>
    private void UpdateNeighborRails(IWorldAccessor world, BlockPos pos)
    {
        BlockPos[] neighbors =
        {
            pos.NorthCopy(), pos.SouthCopy(),
            pos.EastCopy(), pos.WestCopy()
        };

        foreach (BlockPos nPos in neighbors)
        {
            if (world.BlockAccessor.GetBlock(nPos) is BlockRail rail && !rail.IsSlope)
            {
                string newCode = rail.DetermineRailVariant(world, nPos);
                PlaceVariant(world, nPos, newCode);
            }
        }
    }

    // ── Block Overrides ───────────────────────────────────────────────────

    public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer,
        ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
    {
        bool placed = base.TryPlaceBlock(world, byPlayer, itemstack, blockSel, ref failureCode);
        if (!placed) return false;

        // Only auto-connect flat rails (not slopes)
        if (!IsSlope)
        {
            string variantCode = DetermineRailVariant(world, blockSel.Position);
            PlaceVariant(world, blockSel.Position, variantCode);
            UpdateNeighborRails(world, blockSel.Position);
        }

        return true;
    }

    public override void OnBlockBroken(IWorldAccessor world, BlockPos pos,
        IPlayer byPlayer, float dropQuantityMultiplier = 1)
    {
        base.OnBlockBroken(world, pos, byPlayer, dropQuantityMultiplier);
        if (!IsSlope)
            UpdateNeighborRails(world, pos);
    }

    /// <summary>
    /// Returns the two cardinal directions this rail variant connects.
        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (world.Side == EnumAppSide.Server)
            {
                ItemSlot held = byPlayer.InventoryManager.ActiveHotbarSlot;
                bool holdingMinecart = held?.Itemstack?.Collectible?.Code
                    ?.Equals(new AssetLocation("vintagecarts:minecart")) == true;

                if (holdingMinecart)
                {
                    // Let the held minecart item handle spawning to avoid duplicate and low-height placement.
                    return false;
                }

                if (IsSwitch && world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityRailSwitch sw)
                {
                    sw.Toggle();
                    world.PlaySoundAt(new AssetLocation("game:sounds/block/toggleswitch"),
                        blockSel.Position, 0.5, byPlayer);
                    return true;
                }

                // Fallback: allow interacting with a cart on this rail even if the click ray hits the rail block.
                if (byPlayer.Entity is EntityAgent agent)
                {
                    Vec3d center = new Vec3d(blockSel.Position.X + 0.5, blockSel.Position.Y + 0.5, blockSel.Position.Z + 0.5);
                    Entity[] nearby = world.GetEntitiesAround(center, 1.2f, 1.2f,
                        e =>
                        {
                            if (e is EntityMinecart) return true;
                            return false;
                        });

                    if (nearby.Length > 0 && nearby[0] is EntityMinecart cart)
                    {
                        cart.OnInteract(agent, held, center, EnumInteractMode.Interact);
                        return true;
                    }
                }
            }

            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }

        /// <summary>
        /// Returns the two cardinal directions this rail variant connects.
    /// Used by the minecart movement code.
    /// </summary>
    public static (BlockFacing from, BlockFacing to) GetConnections(string orientation)
    {
        return orientation switch
        {
            "ns"    => (BlockFacing.NORTH, BlockFacing.SOUTH),
            "ew"    => (BlockFacing.EAST,  BlockFacing.WEST),
            "ne"    => (BlockFacing.NORTH, BlockFacing.EAST),
            "nw"    => (BlockFacing.NORTH, BlockFacing.WEST),
            "se"    => (BlockFacing.SOUTH, BlockFacing.EAST),
            "sw"    => (BlockFacing.SOUTH, BlockFacing.WEST),
            // T junctions: return the two ends of the "default straight" path
            "t-n"   => (BlockFacing.SOUTH, BlockFacing.WEST),  // cross-bar S-W (default)
            "t-s"   => (BlockFacing.NORTH, BlockFacing.EAST),
            "t-e"   => (BlockFacing.NORTH, BlockFacing.SOUTH),
            "t-w"   => (BlockFacing.NORTH, BlockFacing.SOUTH),
            "cross" => (BlockFacing.NORTH, BlockFacing.SOUTH),
            // Slopes – horizontal components
            "n"     => (BlockFacing.NORTH, BlockFacing.SOUTH),
            "s"     => (BlockFacing.NORTH, BlockFacing.SOUTH),
            "e"     => (BlockFacing.EAST,  BlockFacing.WEST),
            "w"     => (BlockFacing.EAST,  BlockFacing.WEST),
            _       => (BlockFacing.NORTH, BlockFacing.SOUTH)
        };
    }

    /// <summary>
    /// Given the entry direction of the cart, return the exit direction
    /// for this rail variant (honouring switch state for T/cross).
    /// </summary>
    public BlockFacing GetExitFacing(string orientation, BlockFacing entry, int switchState, IWorldAccessor world, BlockPos pos)
    {
        // Straight / slope: opposite direction
        if (orientation is "ns" or "n" or "s")
            return entry == BlockFacing.NORTH ? BlockFacing.SOUTH : BlockFacing.NORTH;
        if (orientation is "ew" or "e" or "w")
            return entry == BlockFacing.EAST ? BlockFacing.WEST : BlockFacing.EAST;

        // Curves: the other connected facing
        var (f1, f2) = GetConnections(orientation);
        if (entry == f1) return f2;
        if (entry == f2) return f1;

        // T junctions / cross – use switch state (stored in BlockEntityRailSwitch)
        return GetTExitFacing(orientation, entry, switchState);
    }

    private static BlockFacing GetTExitFacing(string orientation, BlockFacing entry, int switchState)
    {
        // Determine all three connected facings for this T/cross
        List<BlockFacing> connected = new();
        switch (orientation)
        {
            case "t-n":   connected.AddRange(new[] { BlockFacing.SOUTH, BlockFacing.EAST, BlockFacing.WEST }); break;
            case "t-s":   connected.AddRange(new[] { BlockFacing.NORTH, BlockFacing.EAST, BlockFacing.WEST }); break;
            case "t-e":   connected.AddRange(new[] { BlockFacing.NORTH, BlockFacing.SOUTH, BlockFacing.WEST }); break;
            case "t-w":   connected.AddRange(new[] { BlockFacing.NORTH, BlockFacing.SOUTH, BlockFacing.EAST }); break;
            case "cross": connected.AddRange(new[] { BlockFacing.NORTH, BlockFacing.SOUTH, BlockFacing.EAST, BlockFacing.WEST }); break;
        }

        // Remove the entry direction (cart came from entry, entering means opposite of entry is where it's going from)
        connected.Remove(entry.Opposite);
        connected.Remove(entry);

        if (connected.Count == 0) return entry.Opposite; // fallback

        // switchState selects between options
        int idx = switchState % connected.Count;
        return connected[idx];
    }
}
