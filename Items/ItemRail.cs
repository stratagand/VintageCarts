using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VintageCarts.Blocks;

namespace VintageCarts.Items;

/// <summary>
/// Smart rail placement item. Inspects neighboring blocks and automatically
/// selects the best-fitting rail variant (straight, curve, switch, or slope).
/// All rail block variants drop this item when broken.
/// </summary>
public class ItemRail : Item
{
    public override void OnHeldInteractStart(
        ItemSlot itemslot, EntityAgent byEntity,
        BlockSelection blockSel, EntitySelection entitySel,
        bool firstEvent, ref EnumHandHandling handling)
    {
        if (blockSel == null) return;

        // Always suppress VS default behaviour so it doesn't attempt its own block placement.
        handling = EnumHandHandling.PreventDefault;

        // Only perform placement on the server; the client receives the block-update packet.
        if (byEntity.World.Side != EnumAppSide.Server) return;

        IWorldAccessor world = byEntity.World;

        // The rail goes in the block adjacent to the clicked face.
        Vec3i n = blockSel.Face.Normali;
        BlockPos placePos = blockSel.Position.AddCopy(n.X, n.Y, n.Z);

        // Do not place inside a solid or liquid block.
        if (world.BlockAccessor.GetBlock(placePos).Replaceable < 6000) return;

        string variantCode = DetermineVariant(world, placePos);
        Block target = world.BlockAccessor.GetBlock(new AssetLocation(variantCode));
        if (target == null || target.Id == 0) return;

        world.BlockAccessor.SetBlock(target.Id, placePos);

        IPlayer? player = (byEntity as EntityPlayer)?.Player;
        world.PlaySoundAt(
            new AssetLocation("game:sounds/block/planks"),
            placePos.X + 0.5, placePos.Y + 0.5, placePos.Z + 0.5,
            player);

        itemslot.TakeOut(1);
        itemslot.MarkDirty();

        // Re-evaluate flat neighbours so they connect to the newly placed rail.
        UpdateNeighborRails(world, placePos);
    }

    /// <summary>
    /// Determines the best-fitting rail block code for <paramref name="pos"/> by
    /// scanning same-level and one-block-up cardinal neighbors.
    ///
    /// Slope rules (checked first):
    ///   A rail one block ABOVE a neighbor means this block is at the foot of a slope
    ///   rising toward that direction → place the matching raised_* variant.
    ///
    /// Flat rules (checked when no single elevated neighbor found):
    ///   Mirrors the logic in BlockRail.DetermineRailVariant.
    /// </summary>
    private static string DetermineVariant(IWorldAccessor world, BlockPos pos)
    {
        bool sameN = IsRailAt(world, pos.NorthCopy());
        bool sameS = IsRailAt(world, pos.SouthCopy());
        bool sameE = IsRailAt(world, pos.EastCopy());
        bool sameW = IsRailAt(world, pos.WestCopy());

        bool upN = IsRailAt(world, pos.NorthCopy().UpCopy());
        bool upS = IsRailAt(world, pos.SouthCopy().UpCopy());
        bool upE = IsRailAt(world, pos.EastCopy().UpCopy());
        bool upW = IsRailAt(world, pos.WestCopy().UpCopy());

        int upCount = (upN ? 1 : 0) + (upS ? 1 : 0) + (upE ? 1 : 0) + (upW ? 1 : 0);

        // Exactly one elevated neighbor → slope rising toward that direction.
        if (upCount == 1)
        {
            if (upN) return "vintagecarts:rail-raised_ns"; // ascends toward north
            if (upS) return "vintagecarts:rail-raised_sn"; // ascends toward south
            if (upE) return "vintagecarts:rail-raised_ew"; // ascends toward east
            return         "vintagecarts:rail-raised_we";  // ascends toward west
        }

        // Flat variant based on same-level neighbors.
        int flatCount = (sameN ? 1 : 0) + (sameS ? 1 : 0) + (sameE ? 1 : 0) + (sameW ? 1 : 0);

        if (flatCount >= 4) return "vintagecarts:railswitch-cross";
        if (flatCount == 3)
        {
            if (!sameN) return "vintagecarts:railswitch-t-n";
            if (!sameS) return "vintagecarts:railswitch-t-s";
            if (!sameE) return "vintagecarts:railswitch-t-e";
            return             "vintagecarts:railswitch-t-w";
        }
        if (flatCount == 2)
        {
            if (sameN && sameS) return "vintagecarts:rail-flat_ns";
            if (sameE && sameW) return "vintagecarts:rail-flat_we";
            if (sameN && sameE) return "vintagecarts:rail-curved_ne";
            if (sameN && sameW) return "vintagecarts:rail-curved_wn";
            if (sameS && sameE) return "vintagecarts:rail-curved_es";
            return                     "vintagecarts:rail-curved_sw";
        }

        // 0 or 1 flat neighbor — default to the matching axis, or NS when isolated.
        if (sameN || sameS) return "vintagecarts:rail-flat_ns";
        if (sameE || sameW) return "vintagecarts:rail-flat_we";
        return "vintagecarts:rail-flat_ns";
    }

    private static bool IsRailAt(IWorldAccessor world, BlockPos pos)
        => world.BlockAccessor.GetBlock(pos) is BlockRail;

    /// <summary>
    /// Re-evaluates all flat cardinal neighbors so they re-connect to the
    /// newly placed rail, identical to the logic in BlockRail.UpdateNeighborRails.
    /// </summary>
    private static void UpdateNeighborRails(IWorldAccessor world, BlockPos pos)
    {
        BlockPos[] neighbors = { pos.NorthCopy(), pos.SouthCopy(), pos.EastCopy(), pos.WestCopy() };
        foreach (BlockPos nPos in neighbors)
        {
            if (world.BlockAccessor.GetBlock(nPos) is BlockRail { IsSlope: false } rail)
            {
                string newCode = rail.DetermineRailVariant(world, nPos);
                Block target = world.BlockAccessor.GetBlock(new AssetLocation(newCode));
                if (target != null && target.Id != 0)
                    world.BlockAccessor.SetBlock(target.Id, nPos);
            }
        }
    }
}
