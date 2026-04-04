using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VintageCarts.BlockEntities;

/// <summary>
/// Attached to T-junction and cross rail variants.
/// Stores which exit is active when a cart approaches from each direction,
/// toggled by right-click.
/// </summary>
public class BlockEntityRailSwitch : BlockEntity
{
    /// <summary>0 or 1 – selects which of the available exits to take.</summary>
    private int switchState = 0;

    public int SwitchState => switchState;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
    }

    public void Toggle()
    {
        switchState = 1 - switchState;
        MarkDirty(true);
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetInt("switchState", switchState);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);
        switchState = tree.GetInt("switchState", 0);
    }
}
