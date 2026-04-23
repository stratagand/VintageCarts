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
	private const double RailVisualTopOffset = 0.125;

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

		double spawnY = blockSel.Position.Y + RailVisualTopOffset;
		if (targetBlock.Variant.ContainsKey("type") && targetBlock.Variant["type"].StartsWith("raised_"))
		{
			spawnY = blockSel.Position.Y + 1.0 + RailVisualTopOffset;
		}

		// Spawn directly at the visual rail top for stable initial placement.
        Vec3d spawnPos = new Vec3d(
            blockSel.Position.X + 0.5,
			spawnY,
			blockSel.Position.Z + 0.5);

		string entityCode = Attributes?["entityCode"].AsString("vintagecarts:minecart") ?? "vintagecarts:minecart";
		EntityProperties? props = byEntity.World.GetEntityType(new AssetLocation(entityCode));
		if (props == null) return;

		Entity entity = byEntity.World.ClassRegistry.CreateEntity(props);
		entity.Pos.SetPos(spawnPos);
		entity.Pos.SetPos(spawnPos);
		entity.Pos.Motion.Set(0, 0, 0);
		entity.Pos.Motion.Set(0, 0, 0);

		// Align the cart visually with the rail it's being placed on.
		string railOrientation = ExtractRailOrientation(targetBlock);
		float spawnYaw = railOrientation switch
		{
			"ew" or "e" => (float)(Math.PI * 1.5), // East
			"w"         => (float)(Math.PI * 0.5), // West
			"n"         => (float)Math.PI,          // North
			"we" or "s" => 0f,                       // South (ns, s, ew variants)
						"se" or "ne" or "nw" or "sw" => 0f,     // Curve variants - default to South
			_           => 0f                        // Default South
		};
		entity.Pos.Yaw = spawnYaw;
		entity.Pos.Yaw = spawnYaw;

		byEntity.World.SpawnEntity(entity);
		byEntity.World.Logger.Notification("[VintageCarts] Spawned minecart entity {0} at {1:F2},{2:F2},{3:F2}", entity.EntityId, spawnPos.X, spawnPos.Y, spawnPos.Z);

		itemslot.TakeOut(1);
		itemslot.MarkDirty();
		handling = EnumHandHandling.PreventDefault;
	}

	private static string ExtractRailOrientation(Block railBlock)
	{
		// For slope rails (railslope-*), use "orientation" variant key
		if (railBlock.Code.Path.StartsWith("railslope"))
			return railBlock.Variant.ContainsKey("orientation")
				? railBlock.Variant["orientation"] : "ns";

		// For switch rails (railswitch-*), use "orientation" variant key
		if (railBlock.Code.Path.StartsWith("railswitch"))
			return railBlock.Variant.ContainsKey("orientation")
				? railBlock.Variant["orientation"] : "ns";

		// For main rails (rail-*), use "type" variant key and extract direction component
		if (railBlock.Variant.ContainsKey("type"))
		{
			string typeValue = railBlock.Variant["type"];
			// Extract direction from patterns like "curved_es", "flat_ns", "raised_we"
			int underscore = typeValue.LastIndexOf('_');
			if (underscore >= 0 && underscore < typeValue.Length - 1)
			{
				string direction = typeValue[(underscore + 1)..];
				// Normalize curve orientations to match BlockRail.GetConnections format
				// "es" (East-South) → "se", "wn" (West-North) → "nw"
				direction = direction switch
				{
					"es" => "se",
					"wn" => "nw",
					"we" => "ew",
					_ => direction
				};
				return direction;
			}
		}

		return "ns"; // fallback
	}
}
