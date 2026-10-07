using System.Text.Json.Nodes;
using OnlyBebop.Sheets;

namespace OnlyBebop;

/// Numbers the climber uses, in metres and seconds: the player's own Deadlock values when the cache has them,
/// otherwise the movement/abilities sheet fallbacks.
public sealed class Tuning
{
    public double UnitsPerMeter = Games.Deadlock.UnitsPerMeter;
    public double RunSpeed, SprintBonus, CrouchSpeed, JumpSpeed, AirJumpSpeed, Gravity;
    public double GroundDashSpeed, GroundDashTime, AirDashSpeed, AirDashTime;
    public int StaminaMax; public double StaminaRegen;
    public double MantleReach, MantleTime;
    public double HookRange, HookCooldown, HookSpeed, HookRelease;
    public double UppercutCooldown, UppercutSpeed;
    public int AirJumps;

    public static Tuning From(JsonObject? deadlockManifest)
    {
        var stats = deadlockManifest?["stats"] as JsonObject;
        double S(string id, double fallback)
        {
            var v = stats?[id]?["value"];
            return v is null ? fallback : double.Parse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
        }
        var t = new Tuning();
        t.RunSpeed = S("run", Movement.Run.Value);
        t.CrouchSpeed = Movement.Run.Secondary;
        t.SprintBonus = S("sprint", Movement.Sprint.Value);
        t.Gravity = Movement.Gravity.Value / t.UnitsPerMeter;
        t.JumpSpeed = S("jump", Movement.Jump.Value) / t.UnitsPerMeter;
        t.AirJumpSpeed = t.JumpSpeed * S("air_jump", Movement.AirJump.Value) / 100.0;
        t.AirJumps = (int)Movement.AirJump.Secondary;
        t.GroundDashTime = Movement.GroundDash.Secondary;
        t.GroundDashSpeed = S("ground_dash", Movement.GroundDash.Value) / t.GroundDashTime;
        t.AirDashTime = Movement.AirDash.Secondary;
        t.AirDashSpeed = S("air_dash", Movement.AirDash.Value) / t.AirDashTime;
        t.StaminaMax = (int)S("stamina", Movement.Stamina.Value);
        t.StaminaRegen = Movement.Stamina.Secondary;
        t.MantleReach = Movement.Mantle.Value;
        t.MantleTime = Movement.Mantle.Secondary;
        t.HookRange = S("hook.range", Abilities.Hook.DeadlockRangeM);
        t.HookCooldown = Abilities.Hook.ClimbCooldownS;
        t.HookSpeed = Abilities.Hook.ClimbSpeedMps;
        t.HookRelease = Abilities.Hook.ClimbReleaseM;
        t.UppercutCooldown = Abilities.Uppercut.ClimbCooldownS;
        t.UppercutSpeed = Abilities.Uppercut.ClimbSpeedMps;
        return t;
    }
}
