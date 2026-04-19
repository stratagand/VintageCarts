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

        // If the clicked block is itself replaceable (grass, plants, snow layers, etc.)
        // place the rail there, replacing it — matching standard VS block-placement behaviour.
        // Otherwise place in the block adjacent to the clicked face.
        Block clickedBlock = world.BlockAccessor.GetBlock(blockSel.Position);
        BlockPos placePos = clickedBlock.Replaceable >= 6000
            ? blockSel.Position.Copy()
            : blockSel.Position.AddCopy(blockSel.Face.Normali.X, blockSel.Face.Normali.Y, blockSel.Face.Normali.Z);

        // Do not place inside a solid or liquid block.
        if (world.BlockAccessor.GetBlock(placePos).Replaceable < 6000) return;

        // Delegate to BlockRail for variant selection and neighbour updates so
        // all logic (slopes, T/cross, up/down neighbours) lives in one place.
        Block defaultRail = world.BlockAccessor.GetBlock(new AssetLocation("vintagecarts:rail-flat_ns"));
        if (defaultRail is not BlockRail railBlock) return;

        string variantCode = railBlock.DetermineRailVariant(world, placePos);
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

        // Re-evaluate all neighbours (same-level and lower-diagonal) via BlockRail.
        railBlock.UpdateNeighborRailsPublic(world, placePos);
    }
}
