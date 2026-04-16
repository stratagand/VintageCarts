namespace VintageCarts;

/// <summary>
/// Gameplay settings for minecarts, loaded from ModConfig/vintagecarts.json.
/// Edit that file to tune behaviour without recompiling.
/// </summary>
public class MinecartConfig
{
    /// <summary>
    /// Maximum travel speed of a minecart in metres per second.
    /// Default: 100
    /// </summary>
    public float MaxSpeed { get; set; } = 100f;

    /// <summary>
    /// Speed lost per second due to rail friction (m/s²).
    /// Default: 0.5
    /// </summary>
    public float Friction { get; set; } = 0.5f;

    /// <summary>
    /// Speed gained (or lost) per second when the rider applies directional input (m/s²).
    /// Default: 10.0
    /// </summary>
    public float Acceleration { get; set; } = 10.0f;
}
