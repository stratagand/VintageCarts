using System;
using Vintagestory.API.Client;
using VintageCarts.Entities;
using static VintageCarts.Entities.EntityDrillMinecart;

namespace VintageCarts.Gui;

/// <summary>
/// Display-only HUD overlay shown while the player rides a drill minecart.
/// Uses HudElement so it never releases the mouse or locks the camera.
/// </summary>
public class GuiHudDrillMinecart : HudElement
{
    private readonly EntityDrillMinecart _cart;

    public GuiHudDrillMinecart(ICoreClientAPI capi, EntityDrillMinecart cart)
        : base(capi)
    {
        _cart = cart;
        ComposeHud();
    }

    private void ComposeHud()
    {
        const double width = 220;
        const double pad = 8;

        ElementBounds bg = ElementBounds.Fixed(0, 0, width, 82).WithFixedPadding(pad);

        ElementBounds dialog = ElementStdBounds.AutosizedMainDialog
            .WithAlignment(EnumDialogArea.CenterBottom)
            .WithFixedOffset(0, -60);

        ElementBounds statusBounds = ElementBounds.Fixed(0, 0,  width, 20);
        ElementBounds speedBounds  = ElementBounds.Fixed(0, 26, width, 20);
        ElementBounds dirBounds    = ElementBounds.Fixed(0, 52, width, 20);

        SingleComposer = capi.Gui
            .CreateCompo("vintagecarts-drill-hud", dialog)
            .AddShadedDialogBG(bg, false)
            .BeginChildElements(bg)
                .AddDynamicText("", CairoFont.WhiteSmallText(), statusBounds, "status")
                .AddDynamicText("", CairoFont.WhiteSmallText(), speedBounds,  "speed")
                .AddDynamicText("", CairoFont.WhiteSmallText(), dirBounds,    "dir")
            .EndChildElements()
            .Compose();
    }

    public override void OnRenderGUI(float deltaTime)
    {
        string status = _cart.DrillActive ? "Drill: ON" : "Drill: OFF";

        double mx = _cart.Pos.Motion.X, mz = _cart.Pos.Motion.Z;
        double speed = Math.Sqrt(mx * mx + mz * mz);

        string dirLabel = _cart.DrillDir switch
        {
            DrillDirection.Ascend  => "Slope: Ascend [Space]",
            DrillDirection.Descend => "Slope: Descend [Space]",
            _                      => "Slope: Level [Space]",
        };

        SingleComposer.GetDynamicText("status")?.SetNewText(status);
        SingleComposer.GetDynamicText("speed")?.SetNewText($"Speed: {speed:F2} m/s");
        SingleComposer.GetDynamicText("dir")?.SetNewText(dirLabel);

        base.OnRenderGUI(deltaTime);
    }
}
