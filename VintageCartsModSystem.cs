using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using VintageCarts.BlockEntities;
using VintageCarts.Blocks;
using VintageCarts.Entities;
using VintageCarts.Gui;
using VintageCarts.Items;
using VintageCarts.Network;

namespace VintageCarts;

public class VintageCartsModSystem : ModSystem
{
    public const string ChannelName = "vintagecarts";

    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        api.RegisterBlockClass("BlockRail", typeof(BlockRail));
        api.RegisterBlockEntityClass("BlockEntityRailSwitch", typeof(BlockEntityRailSwitch));
        api.RegisterEntity("EntityMinecart", typeof(EntityMinecart));
        api.RegisterItemClass("ItemMinecart", typeof(ItemMinecart));
        api.RegisterItemClass("ItemRail", typeof(ItemRail));

        api.RegisterMountable("vintagecarts-minecart", EntityMinecart.GetMountable);
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        api.Network
            .RegisterChannel(ChannelName)
            .RegisterMessageType<OpenFuelGuiPacket>()
            .RegisterMessageType<FuelSlotChangedPacket>()
            .RegisterMessageType<CartPositionPacket>()
            .SetMessageHandler<FuelSlotChangedPacket>(OnClientFuelSlotChanged);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        api.Network
            .RegisterChannel(ChannelName)
            .RegisterMessageType<OpenFuelGuiPacket>()
            .RegisterMessageType<FuelSlotChangedPacket>()
            .RegisterMessageType<CartPositionPacket>()
            .SetMessageHandler<OpenFuelGuiPacket>(packet => OnOpenFuelGui(api, packet))
            .SetMessageHandler<CartPositionPacket>(packet => OnCartPositionUpdate(api, packet));
    }

    private void OnOpenFuelGui(ICoreClientAPI api, OpenFuelGuiPacket packet)
    {
        var entity = api.World.GetEntityById(packet.EntityId) as EntityMinecart;
        if (entity == null) return;
        var gui = new GuiDialogMinecart(api, entity);
        gui.TryOpen();
    }

    private void OnClientFuelSlotChanged(IServerPlayer fromPlayer, FuelSlotChangedPacket packet)
    {
        var entity = fromPlayer.Entity?.World.GetEntityById(packet.EntityId) as EntityMinecart;
        entity?.HandleFuelSlotPacket(fromPlayer, packet);
    }

    private void OnCartPositionUpdate(ICoreClientAPI api, CartPositionPacket packet)
    {
        if (api.World.GetEntityById(packet.EntityId) is EntityMinecart cart)
                cart.ApplyClientPositionUpdate(packet.X, packet.Y, packet.Z, packet.MotionX, packet.MotionZ, packet.Yaw);
    }
}
