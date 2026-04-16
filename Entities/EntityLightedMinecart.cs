using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageCarts.Entities;

/// <summary>
/// A minecart that permanently emits lantern-level light.
/// Behaves identically to a regular minecart except for the light emission
/// and the drop item when destroyed.
/// </summary>
public class EntityLightedMinecart : EntityMinecart
{
    // Warm yellowish light equivalent to a lantern (~level 22 brightness).
    // Hue 10 = yellow-orange on VS's 0-39 hue wheel, saturation 6, brightness 22.
    public override byte[] LightHsv => new byte[] { 10, 6, 22 };

    protected override AssetLocation DropItemLocation => new AssetLocation("vintagecarts:minecart-lighted");
}
