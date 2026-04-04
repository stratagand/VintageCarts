using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VintageCarts.Entities;

/// <summary>
/// Single passenger seat for the minecart entity.
/// </summary>
public class MinecartSeat : IMountableSeat
{
    private readonly EntityMinecart _cart;
    private Entity? _passenger;
    private readonly EntityControls _controls = new EntityControls();

    public MinecartSeat(EntityMinecart cart)
    {
        _cart = cart;
        Config = new SeatConfig
        {
            SeatId      = "seat0",
            APName      = "seatpoint",
            SelectionBox = "seatpoint",
            Controllable = true,
            AngleMode   = EnumMountAngleMode.FixateYaw,
            EyeHeight   = 1.0f,
            Animation   = "sit",
        };
        SeatId = "seat0";
    }

    // ── IMountableSeat ────────────────────────────────────────────────────

    public SeatConfig Config { get; set; }
    public string SeatId { get; set; }
    public long PassengerEntityIdForInit { get; set; }
    public bool DoTeleportOnUnmount { get; set; } = true;

    public Entity Entity   => _cart;
    public Entity? Passenger => _passenger;
    public IMountable MountSupplier => _cart;

    public bool CanControl => true;
    public EnumMountAngleMode AngleMode => EnumMountAngleMode.FixateYaw;
    public AnimationMetaData? SuggestedAnimation => null;
    public bool SkipIdleAnimation => false;
    public float FpHandPitchFollow => 0f;

    public Vec3f LocalEyePos => new Vec3f(0, Config.EyeHeight, 0);

    public EntityPos SeatPosition
    {
        get
        {
            EntityPos p = _cart.Pos.Copy();
            p.Y += 0.35f;
            return p;
        }
    }

    public Matrixf RenderTransform => null!;

    public EntityControls Controls => _controls;

    // ── Mount / Unmount ────────────────────────────────────────────────────

    public bool CanMount(EntityAgent entityAgent)
        => _passenger == null && _cart.Alive;

    public bool CanUnmount(EntityAgent entityAgent)
        => _passenger == entityAgent;

    public void DidMount(EntityAgent entityAgent)
        => _passenger = entityAgent;

    public void DidUnmount(EntityAgent entityAgent)
        => _passenger = null;

    // ── Persistence ────────────────────────────────────────────────────────

    public void MountableToTreeAttributes(TreeAttribute tree)
    {
        tree.SetString("className", "vintagecarts-minecart");
        tree.SetLong("entityId", _cart.EntityId);
        tree.SetString("seatId", SeatId);
    }
}
