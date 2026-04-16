using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using VintageCarts.Entities;

namespace VintageCarts.Gui;

/// <summary>
/// Client-side dialog for a storage minecart's 24-slot inventory.
/// Displays only the storage grid (like a chest). Shift+left-click routing
/// between this inventory and the player's own inventory is enabled by
/// registering the storage with the player's inventory manager on open.
/// </summary>
public class GuiDialogStorageMinecart : GuiDialogGeneric
{
    private readonly EntityStorageMinecart _cart;

    private static readonly AssetLocation SoundOpen  = new AssetLocation("game:sounds/block/largechestopen");
    private static readonly AssetLocation SoundClose = new AssetLocation("game:sounds/block/largechestclose");

    private const int Cols = 6;
    private const int Rows = 4;

    public GuiDialogStorageMinecart(ICoreClientAPI capi, EntityStorageMinecart cart)
        : base("Storage Cart", capi)
    {
        _cart = cart;
        ComposeDialog();
    }

    private void ComposeDialog()
    {
        double pad    = GuiStyle.ElementToDialogPadding;
        double slotSz = GuiElementPassiveItemSlot.unscaledSlotSize
                      + GuiElementItemSlotGridBase.unscaledSlotPadding;

        double gridW = Cols * slotSz;
        double gridH = Rows * slotSz;

        ElementBounds bgBounds   = new ElementBounds().WithSizing(ElementSizing.FitToChildren).WithFixedPadding(pad);
        ElementBounds gridBounds = ElementBounds.Fixed(0, 30, gridW, gridH);

        int[] allSlots = Enumerable.Range(0, EntityStorageMinecart.StorageSlotCount).ToArray();

        SingleComposer = capi.Gui
            .CreateCompo("vintagecarts-storage-" + _cart.EntityId, ElementStdBounds.AutosizedMainDialog)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar("Storage Cart", OnTitleBarClose)
            .BeginChildElements(bgBounds)
                .AddItemSlotGrid(_cart.StorageInventory, SendStoragePacket, Cols, allSlots, gridBounds, "storagegrid")
            .EndChildElements()
            .Compose();
    }

    /// <summary>
    /// Routes storage slot changes to the server entity via VS's entity packet system.
    /// EntityStorageMinecart.OnReceivedClientPacket passes them to InvNetworkUtil.HandleClientPacket.
    /// </summary>
    private void SendStoragePacket(object packet)
    {
        capi.Network.SendEntityPacket(_cart.EntityId, packet);
    }

    public override bool TryOpen()
    {
        bool opened = base.TryOpen();
        if (opened)
        {
            // Register the storage inventory with the player inventory manager so that
            // VS's shift+click routing knows this inventory is "open" and transfers items to/from it.
            capi.World.Player.InventoryManager.OpenInventory(_cart.StorageInventory);

            capi.World.PlaySoundAt(SoundOpen, _cart.Pos.X, _cart.Pos.Y, _cart.Pos.Z, null, true, 16f);
            _cart.AnimManager?.StartAnimation(new AnimationMetaData
            {
                Animation      = "lidopen",
                Code           = "lidopen",
                AnimationSpeed = 2f,
                EaseInSpeed    = float.MaxValue,
                EaseOutSpeed   = float.MaxValue,
                Weight         = 10f,
                BlendMode      = EnumAnimationBlendMode.Add
            });
        }
        return opened;
    }

    public override bool TryClose()
    {
        bool closed = base.TryClose();
        if (closed)
        {
            // Unregister so shift+click stops routing here.
            capi.World.Player.InventoryManager.CloseInventory(_cart.StorageInventory);

            capi.World.PlaySoundAt(SoundClose, _cart.Pos.X, _cart.Pos.Y, _cart.Pos.Z, null, true, 16f);
            // Stopping "lidopen" triggers onActivityStopped: "Rewind" in the shape, which plays the lid close.
            _cart.AnimManager?.StopAnimation("lidopen");
        }
        return closed;
    }

    public override void OnRenderGUI(float deltaTime)
    {
        if (!_cart.Alive)
        {
            TryClose();
            return;
        }
        base.OnRenderGUI(deltaTime);
    }

    private void OnTitleBarClose() => TryClose();

    public override bool PrefersUngrabbedMouse => false;
    public override bool DisableMouseGrab => true;
}
