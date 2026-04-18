using System;
using System.IO;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using VintageCarts.Network;

namespace VintageCarts.Entities;

/// <summary>
/// A storage cart that follows an attached leading minecart.
/// Must be spawned via right-clicking a minecart while holding the storage cart item.
/// Has a 24-slot inventory accessible by right-clicking.
/// Inventory is networked via VS's standard InventoryBase packet system routed through
/// entity packets, so the client GUI gets full drag-and-drop slot interaction.
/// </summary>
public class EntityStorageMinecart : Entity
{
    public const int StorageSlotCount = 24;

    private InventoryGeneric storageInventory = null!;
    private long _leaderId = -1;
    private long _followerCartId = -1;
    private bool _isBeingDestroyed = false;

    public long LeaderId
    {
        get => _leaderId;
        set => _leaderId = value;
    }

    public long FollowerCartId
    {
        get => _followerCartId;
        set => _followerCartId = value;
    }

    // Returns the last EntityStorageMinecart in this cart's forward chain (may be this).
    public EntityStorageMinecart FindTail()
    {
        if (_followerCartId < 0) return this;
        if (World.GetEntityById(_followerCartId) is EntityStorageMinecart follower)
            return follower.FindTail();
        _followerCartId = -1;
        return this;
    }

    // Traverses leader chain to find the primary EntityMinecart.
    public EntityMinecart? GetPrimaryCart()
    {
        Entity? leader = World.GetEntityById(_leaderId);
        if (leader is EntityMinecart primary) return primary;
        if (leader is EntityStorageMinecart storageLeader) return storageLeader.GetPrimaryCart();
        return null;
    }

    // Returns the entity whose motion vector should be used for "behind" direction.
    // Storage carts always have zero motion, so chained followers must read from the root.
    private Entity GetMotionSource()
    {
        Entity? leader = World.GetEntityById(_leaderId);
        if (leader is EntityStorageMinecart storageLeader) return storageLeader.GetMotionSource();
        return leader ?? this;
    }

    /// <summary>Exposed for the GUI dialog to reference the inventory directly.</summary>
    public InventoryGeneric StorageInventory => storageInventory;

    public override void Initialize(EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
    {
        base.Initialize(properties, api, InChunkIndex3d);
        storageInventory = new InventoryGeneric(StorageSlotCount, "vintagecarts-storage-" + EntityId, api);
        Pos.Motion.Set(0, 0, 0);
        ServerPos.Motion.Set(0, 0, 0);
    }

    public override void OnGameTick(float dt)
    {
        base.OnGameTick(dt);

        if (Api.Side != EnumAppSide.Server || _leaderId < 0) return;

        Entity? leader = World.GetEntityById(_leaderId);
        if (leader == null || !leader.Alive)
        {
            _leaderId = -1;
            return;
        }

        // Derive the "behind" direction from the root minecart's motion vector.
        // Storage carts always have zero motion, so chained followers must climb the
        // leader chain to find the EntityMinecart that carries the real velocity.
        Entity motionSource = GetMotionSource();
        double mx = motionSource.ServerPos.Motion.X;
        double mz = motionSource.ServerPos.Motion.Z;
        double horizSpeed = Math.Sqrt(mx * mx + mz * mz);

        double backX, backZ;
        if (horizSpeed > 0.001)
        {
            // Opposite of travel direction.
            backX = -mx / horizSpeed;
            backZ = -mz / horizSpeed;
        }
        else
        {
            // Stationary: fall back to yaw. VS forward = (-sin, cos), so back = (sin, -cos).
            backX = Math.Sin(leader.ServerPos.Yaw);
            backZ = -Math.Cos(leader.ServerPos.Yaw);
        }

        double targetX = leader.ServerPos.X + backX;
        double targetY = leader.ServerPos.Y;
        double targetZ = leader.ServerPos.Z + backZ;

        Pos.SetPos(targetX, targetY, targetZ);
        ServerPos.SetPos(targetX, targetY, targetZ);
        Pos.Yaw = leader.ServerPos.Yaw;
        ServerPos.Yaw = leader.ServerPos.Yaw;

        // Keep motion zeroed: interpolateposition client behavior uses Pos.Motion for
        // lag-compensation extrapolation, which would overshoot since EntityMinecart uses
        // custom movement without interpolateposition. Pure position tracking is correct here.
        Pos.Motion.Set(0, 0, 0);
        ServerPos.Motion.Set(0, 0, 0);
    }

    public override void OnInteract(EntityAgent byEntity, ItemSlot slot, Vec3d hitPosition, EnumInteractMode mode)
    {
        if (Api.Side != EnumAppSide.Server) return;

        if (mode == EnumInteractMode.Attack)
        {
            DestroyAndDrop(byEntity);
            return;
        }

        if (mode != EnumInteractMode.Interact) return;
        if (byEntity is not EntityPlayer entityPlayer) return;
        IServerPlayer player = (IServerPlayer)entityPlayer.Player;

        // Register player as viewing this inventory; VS InvNetworkUtil pushes all slots to them.
        storageInventory.Open(player);

        // Tell the client to open the GUI.
        ((ICoreServerAPI)Api).Network
            .GetChannel(VintageCartsModSystem.ChannelName)
            .SendPacket(new OpenStorageGuiPacket { EntityId = EntityId }, player);
    }

    /// <summary>
    /// Called by VS when the client sends an entity packet (via capi.Network.SendEntityPacket).
    /// Routes inventory slot-change packets through VS's standard InvNetworkUtil so drag-and-drop
    /// and shift-click work identically to a chest.
    /// </summary>
    public override void OnReceivedClientPacket(IServerPlayer player, int packetid, byte[] data)
    {
        storageInventory.InvNetworkUtil.HandleClientPacket(player, packetid, data);
    }

    private void DestroyAndDrop(EntityAgent byEntity)
    {
        if (_isBeingDestroyed) return;
        _isBeingDestroyed = true;

        // Remove this cart from its leader's forward link so the chain doesn't dangle.
        Entity? leader = World.GetEntityById(_leaderId);
        if (leader is EntityMinecart minecartLeader)
            minecartLeader.FollowerCartId = -1;
        else if (leader is EntityStorageMinecart storageLeader)
            storageLeader.FollowerCartId = -1;

        // Close the inventory for all players watching it, then drop contents.
        if (Api is ICoreServerAPI sapi)
        {
            foreach (IServerPlayer p in sapi.World.AllOnlinePlayers)
                storageInventory.Close(p);
        }

        for (int i = 0; i < storageInventory.Count; i++)
        {
            if (storageInventory[i].Itemstack == null) continue;
            World.SpawnItemEntity(storageInventory[i].Itemstack, Pos.XYZ);
            storageInventory[i].Itemstack = null;
        }

        Item? cartItem = World.GetItem(new AssetLocation("vintagecarts:minecart-storage"));
        if (cartItem != null)
            World.SpawnItemEntity(new ItemStack(cartItem, 1), Pos.XYZ);

        Die(EnumDespawnReason.Removed, null);
    }

    public override void ToBytes(BinaryWriter writer, bool forClient)
    {
        base.ToBytes(writer, forClient);
        writer.Write(_leaderId);
        var tree = new TreeAttribute();
        storageInventory.ToTreeAttributes(tree);
        tree.ToBytes(writer);
        writer.Write(_followerCartId);
    }

    public override void FromBytes(BinaryReader reader, bool isSync)
    {
        base.FromBytes(reader, isSync);
        try
        {
            _leaderId = reader.ReadInt64();
            var tree = new TreeAttribute();
            tree.FromBytes(reader);
            storageInventory?.FromTreeAttributes(tree);
            _followerCartId = reader.ReadInt64();
        }
        catch { }
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        // Safety: drop anything still in the inventory (e.g. server-side despawn without attack).
        if (Api.Side == EnumAppSide.Server && !_isBeingDestroyed)
        {
            for (int i = 0; i < storageInventory.Count; i++)
            {
                if (storageInventory[i].Itemstack == null) continue;
                World.SpawnItemEntity(storageInventory[i].Itemstack, Pos.XYZ);
                storageInventory[i].Itemstack = null;
            }
        }
        base.OnEntityDespawn(despawn);
    }
}
