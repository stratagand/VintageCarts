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
    public bool IsSwitch => Code.Path.StartsWith("railswitch")
        || (Variant.ContainsKey("type") && (Variant["type"].StartsWith("t_") || Variant["type"] == "cross"));

    /// <summary>Returns true if this block variant is a slope type.</summary>
    public bool IsSlope => Code.Path.StartsWith("railslope");

    // ── Variant Determination ─────────────────────────────────────────────

    /// <summary>
    /// Scans cardinal neighbours and returns the best-fitting rail block code.
    /// Considers both same-level and +1 neighbours so rails can form curves/T/cross
    /// with sloped neighbours while still selecting raised variants when needed.
    /// </summary>
    public string DetermineRailVariant(IWorldAccessor world, BlockPos pos)
    {
        bool northSame = IsRail(world, pos.NorthCopy());
        bool southSame = IsRail(world, pos.SouthCopy());
        bool eastSame  = IsRail(world, pos.EastCopy());
        bool westSame  = IsRail(world, pos.WestCopy());

        bool northUp = IsRail(world, pos.NorthCopy().UpCopy());
        bool southUp = IsRail(world, pos.SouthCopy().UpCopy());
        bool eastUp  = IsRail(world, pos.EastCopy().UpCopy());
        bool westUp  = IsRail(world, pos.WestCopy().UpCopy());

        bool northDown = IsRail(world, pos.NorthCopy().DownCopy());
        bool southDown = IsRail(world, pos.SouthCopy().DownCopy());
        bool eastDown  = IsRail(world, pos.EastCopy().DownCopy());
        bool westDown  = IsRail(world, pos.WestCopy().DownCopy());

        bool north = northSame || northUp || northDown;
        bool south = southSame || southUp || southDown;
        bool east  = eastSame || eastUp || eastDown;
        bool west  = westSame || westUp || westDown;

        int count = (north ? 1 : 0) + (south ? 1 : 0)
                  + (east  ? 1 : 0) + (west  ? 1 : 0);

        if (count >= 4)
            return "vintagecarts:rail-cross";

        if (count == 3)
        {
            if (!north) return "vintagecarts:rail-t_n";   // blocked N → S,E,W
            if (!south) return "vintagecarts:rail-t_s";   // blocked S → N,E,W
            if (!east)  return "vintagecarts:rail-t_e";   // blocked E → N,S,W
            return          "vintagecarts:rail-t_w";      // blocked W → N,S,E
        }

        if (count == 2)
        {
            if (north && south)
            {
                if (northUp && !southUp) return "vintagecarts:rail-raised_ns";
                if (southUp && !northUp) return "vintagecarts:rail-raised_sn";
                return "vintagecarts:rail-flat_ns";
            }

            if (east && west)
            {
                if (westUp && !eastUp) return "vintagecarts:rail-raised_we";
                if (eastUp && !westUp) return "vintagecarts:rail-raised_ew";
                return "vintagecarts:rail-flat_we";
            }

            if (north && east)  return "vintagecarts:rail-curved_ne";
            if (north && west)  return "vintagecarts:rail-curved_wn";
            if (south && east)  return "vintagecarts:rail-curved_es";
            return "vintagecarts:rail-curved_sw";
        }

        // 0 or 1 neighbours: default straight aligned toward the single neighbour
        if (count == 1)
        {
            if (north)
            {
                if (northUp) return "vintagecarts:rail-raised_ns";
                return "vintagecarts:rail-flat_ns";
            }

            if (south)
            {
                if (southUp) return "vintagecarts:rail-raised_sn";
                return "vintagecarts:rail-flat_ns";
            }

            if (east)
            {
                if (eastUp) return "vintagecarts:rail-raised_ew";
                return "vintagecarts:rail-flat_we";
            }

            if (westUp) return "vintagecarts:rail-raised_we";
            return "vintagecarts:rail-flat_we";
        }

        return "vintagecarts:rail-flat_ns"; // isolated default
    }

    private void PlaceVariant(IWorldAccessor world, BlockPos pos, string blockCode)
    {
        Block target = world.BlockAccessor.GetBlock(new AssetLocation(blockCode));
        if (target != null && target.Id != 0 && target.Id != world.BlockAccessor.GetBlock(pos).Id)
        {
            world.BlockAccessor.SetBlock(target.Id, pos);
        }
    }

    /// <summary>Re-evaluates and updates all cardinal rail neighbours, including
    /// lower-diagonal positions so flat rails below a newly placed rail become slopes.</summary>
    public void UpdateNeighborRailsPublic(IWorldAccessor world, BlockPos pos)
        => UpdateNeighborRails(world, pos);

    private void UpdateNeighborRails(IWorldAccessor world, BlockPos pos)
    {
        // Same-level neighbours.
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

        // Lower-diagonal neighbours (one cardinal step + one down).
        // A flat rail there should become a slope rising toward pos.
        BlockPos[] lowerNeighbors =
        {
            pos.NorthCopy().DownCopy(), pos.SouthCopy().DownCopy(),
            pos.EastCopy().DownCopy(),  pos.WestCopy().DownCopy()
        };

        foreach (BlockPos nPos in lowerNeighbors)
        {
            if (world.BlockAccessor.GetBlock(nPos) is BlockRail rail && !rail.IsSlope)
            {
                string newCode = rail.DetermineRailVariant(world, nPos);
                PlaceVariant(world, nPos, newCode);
            }
        }

        // Upper-diagonal neighbours (one cardinal step + one up).
        // Needed so upper rails adapt (e.g. straight -> curve/T) when a lower
        // rail is placed and becomes a slope connection target.
        BlockPos[] upperNeighbors =
        {
            pos.NorthCopy().UpCopy(), pos.SouthCopy().UpCopy(),
            pos.EastCopy().UpCopy(),  pos.WestCopy().UpCopy()
        };

        foreach (BlockPos nPos in upperNeighbors)
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
        // Use an explicit drop path so every rail variant state reliably drops
        // the common rail item regardless of variantgroup metadata resolution.
        if (world.Side == EnumAppSide.Server && byPlayer?.WorldData.CurrentGameMode != EnumGameMode.Creative)
        {
            Item? railItem = world.GetItem(new AssetLocation("vintagecarts:rail"));
            if (railItem != null)
            {
                int quantity = Math.Max(1, GameMath.RoundRandom(world.Rand, dropQuantityMultiplier));
                world.SpawnItemEntity(new ItemStack(railItem, quantity), pos.ToVec3d().Add(0.5, 0.25, 0.5));
            }
        }

        // Suppress base-generated drops to avoid duplicates with the explicit drop above.
        base.OnBlockBroken(world, pos, byPlayer, 0);
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

        // T junctions / cross must use explicit branch logic.
        if (orientation.StartsWith("t-") || orientation == "cross")
            return GetTExitFacing(orientation, entry, switchState);

        // Curves: the other connected facing
        var (f1, f2) = GetConnections(orientation);
        if (entry == f1) return f2;
        if (entry == f2) return f1;

        return entry.Opposite;
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

        // Preserve momentum by default: if straight-through is possible on this
        // junction, continue in the same travel axis.
        if (connected.Contains(entry.Opposite))
        {
            return entry.Opposite;
        }

        // Otherwise, pick one of the remaining branches (excluding where we came from).
        connected.Remove(entry);

        if (connected.Count == 0) return entry.Opposite; // fallback

        // switchState selects between options
        int idx = switchState % connected.Count;
        return connected[idx];
    }
}
