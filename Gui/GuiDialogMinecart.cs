using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VintageCarts.Entities;
using VintageCarts.Network;

namespace VintageCarts.Gui;

/// <summary>
/// Client-side dialog for managing a minecart's fuel inventory.
/// Opens via network packet when the player sneak-right-clicks a minecart.
/// </summary>
public class GuiDialogMinecart : GuiDialogGeneric
{
    private readonly EntityMinecart _cart;
    private static readonly double Padding = GuiStyle.ElementToDialogPadding;

    public GuiDialogMinecart(ICoreClientAPI capi, EntityMinecart cart)
        : base("Minecart Fuel", capi)
    {
        _cart = cart;
        ComposeDialog();
    }

    private void ComposeDialog()
    {
        double slotSize = GuiElementPassiveItemSlot.unscaledSlotSize + GuiElementItemSlotGridBase.unscaledSlotPadding;
        double width = 200;

        ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, width, 150).WithFixedPadding(Padding);
        ElementBounds bgBounds = new ElementBounds().WithSizing(ElementSizing.FitToChildren).WithFixedPadding(Padding * 1.5);

        ElementBounds titleBounds = ElementBounds.Fixed(0, 0, width, 30);
        ElementBounds fuelLabelBounds = ElementBounds.Fixed(0, 40, width, 20);
        ElementBounds slotBounds = ElementBounds.Fixed(width / 2 - slotSize / 2, 70, slotSize, slotSize);
        ElementBounds fuelBarBounds = ElementBounds.Fixed(0, 110, width, 16);
        ElementBounds takeButtonBounds = ElementBounds.Fixed(0, 130, 90, 24);
        ElementBounds fillButtonBounds = ElementBounds.Fixed(width - 90, 130, 90, 24);

        SingleComposer = capi.Gui
            .CreateCompo("vintagecarts-fuel-" + _cart.EntityId, ElementStdBounds.AutosizedMainDialog)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar("Minecart Fuel", OnTitleBarClose)
            .BeginChildElements(bgBounds)
                .AddStaticText("Fuel Slot", CairoFont.WhiteSmallText(), fuelLabelBounds)
                .AddItemSlotGrid(_cart.FuelSlot.Inventory, DoSendPacket, 1,
                    new[] { 0 }, slotBounds, "fuelslot")
                .AddDynamicText(GetFuelText(), CairoFont.WhiteSmallText(), fuelBarBounds, "fueltext")
                .AddSmallButton("Take Out", OnTakeFuel, takeButtonBounds)
                .AddSmallButton("Fill Slot", OnFillSlot, fillButtonBounds)
            .EndChildElements()
            .Compose();
    }

    private void DoSendPacket(object obj) { }

    private string GetFuelText()
    {
        float secs = _cart.FuelSecondsRemaining;
        if (secs <= 0) return "No fuel burning";
        int mins = (int)(secs / 60);
        int s = (int)(secs % 60);
        return $"Burning: {mins}m {s:D2}s remaining";
    }

    private bool OnTakeFuel()
    {
        capi.Network.GetChannel(VintageCartsModSystem.ChannelName)
            .SendPacket(new FuelSlotChangedPacket { EntityId = _cart.EntityId, Action = 0 });
        return true;
    }

    private bool OnFillSlot()
    {
        capi.Network.GetChannel(VintageCartsModSystem.ChannelName)
            .SendPacket(new FuelSlotChangedPacket { EntityId = _cart.EntityId, Action = 1 });
        return true;
    }

    private void OnTitleBarClose() => TryClose();

    public override bool PrefersUngrabbedMouse => false;
    public override bool DisableMouseGrab => true;

    public override void OnRenderGUI(float deltaTime)
    {
        // Update fuel text every frame
        SingleComposer.GetDynamicText("fueltext")?.SetNewText(GetFuelText());
        base.OnRenderGUI(deltaTime);
    }
}
