using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using System;

namespace VintageCarts.Items;

/// <summary>
/// Minecart placement item. Right-click on a rail to spawn a cart.
/// </summary>
public class ItemMinecart : Item
{
	public override void OnHeldInteractStart(ItemSlot itemslot, EntityAgent byEntity,
		BlockSelection blockSel, EntitySelection entitySel, bool firstEvent,
		ref EnumHandHandling handling)
	{
		if (blockSel == null) return;
		if (byEntity.World.Side != EnumAppSide.Server)
		{
			handling = EnumHandHandling.PreventDefault;
			return;
		}

		Block targetBlock = byEntity.World.BlockAccessor.GetBlock(blockSel.Position);
		if (targetBlock is not Blocks.BlockRail) return;

        // Spawn one full block above the rail so gravity settles the cart onto the rail surface.
        Vec3d spawnPos = new Vec3d(
            blockSel.Position.X + 0.5,
            blockSel.Position.Y + 1.0,
			blockSel.Position.Z + 0.5);

		EntityProperties? props = byEntity.World.GetEntityType(new AssetLocation("vintagecarts:minecart"));
		if (props == null) return;

		Entity entity = byEntity.World.ClassRegistry.CreateEntity(props);
		entity.ServerPos.SetPos(spawnPos);
		entity.Pos.SetPos(spawnPos);
		entity.ServerPos.Motion.Set(0, 0, 0);
		entity.Pos.Motion.Set(0, 0, 0);

		// Align the cart visually with the rail it's being placed on.
		string railOrientation = targetBlock.Variant.ContainsKey("orientation")
			? targetBlock.Variant["orientation"] : "ns";
		float spawnYaw = railOrientation switch
		{
			"ew" or "e" => (float)(Math.PI * 1.5), // East
			"w"         => (float)(Math.PI * 0.5), // West
			"n"         => (float)Math.PI,          // North
			_           => 0f                        // South (ns, s, curves)
		};
		entity.ServerPos.Yaw = spawnYaw;
		entity.Pos.Yaw = spawnYaw;

		byEntity.World.SpawnEntity(entity);
		byEntity.World.Logger.Notification("[VintageCarts] Spawned minecart entity {0} at {1:F2},{2:F2},{3:F2}", entity.EntityId, spawnPos.X, spawnPos.Y, spawnPos.Z);

		itemslot.TakeOut(1);
		itemslot.MarkDirty();
		handling = EnumHandHandling.PreventDefault;
	}
}
