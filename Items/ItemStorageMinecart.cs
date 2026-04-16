using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using VintageCarts.Entities;

namespace VintageCarts.Items;

/// <summary>
/// The storage minecart item. Cannot be placed on rails directly —
/// it must be attached to an existing minecart by right-clicking one while holding this item.
/// Attachment is handled here rather than in EntityMinecart.OnInteract because VS evaluates
/// the held item's OnHeldInteractStart before sending entity-interaction packets to the server,
/// so a PreventDefault return from the item suppresses the entity interaction entirely.
/// </summary>
public class ItemStorageMinecart : Item
{
    public override void OnHeldInteractStart(ItemSlot itemslot, EntityAgent byEntity,
        BlockSelection blockSel, EntitySelection entitySel, bool firstEvent,
        ref EnumHandHandling handling)
    {
        // Attachment: right-click an existing minecart while holding this item.
        if (entitySel?.Entity is EntityMinecart cart)
        {
            if (byEntity.World.Side == EnumAppSide.Server)
                cart.TryAttachStorageCart(itemslot);

            handling = EnumHandHandling.PreventDefault;
            return;
        }

        // Prevent accidental placement on blocks/rails.
        handling = EnumHandHandling.PreventDefault;
    }
}
