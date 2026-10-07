using System;
using Godot;

namespace OnlyBebop;

/// systems.climber: Deadlock's movement kit (movement sheet) on a CharacterBody3D.
/// Input comes through IClimberInput so the headless bench can drive it exactly like a player.
public interface IClimberInput
{
    Vector2 Move { get; }          // x = right, y = forward
    float Yaw { get; }             // radians, camera heading
    Vector3 AimOrigin { get; }
    Vector3 AimDir { get; }
    bool JumpPressed { get; }
    bool JumpHeld { get; }
    bool DashPressed { get; }
    bool SprintHeld { get; }
    bool Ability1Pressed { get; }  // Uppercut
    bool Ability3Pressed { get; }  // Hook
}

public partial class Climber : CharacterBody3D
{
    public const float Radius = 0.55f, Height = 2.1f;
    public Tuning T = null!;
    public IClimberInput In = null!;
    public HookAbility Hook = null!;
    public UppercutAbility Uppercut = null!;
    public Action<string>? PlaySound;

    public float Stamina;
    public int AirJumpsLeft;
    public bool Dashing => _dashTime > 0;
    public bool Mantling => _mantleTime > 0;
    public string State = "idle";

    float _dashTime;
    Vector3 _dashVel;
    float _jumpCooldown;
    float _mantleTime;
    Vector3 _mantleFrom, _mantleTo;

    public override void _Ready()
    {
        var shape = new CollisionShape3D { Shape = new CapsuleShape3D { Radius = Radius, Height = Height }, Position = new Vector3(0, Height / 2, 0) };
        AddChild(shape);
        FloorMaxAngle = Mathf.DegToRad(50);
        FloorSnapLength = 0.3f;
        Stamina = T.StaminaMax;
        Hook = new HookAbility { Climber = this };
        Uppercut = new UppercutAbility { Climber = this };
        AddChild(Hook);
        AddChild(Uppercut);
    }

    public Vector3 Forward => new Vector3(-Mathf.Sin(In.Yaw), 0, -Mathf.Cos(In.Yaw));
    public Vector3 Right => new Vector3(Mathf.Cos(In.Yaw), 0, -Mathf.Sin(In.Yaw));

    public override void _PhysicsProcess(double delta) => Step((float)delta);

    public void Step(float dt)
    {
        StepInner(dt);
        (In as ShoulderCamera)?.Consume();
    }

    void StepInner(float dt)
    {
        var onFloor = IsOnFloor();
        _jumpCooldown = Math.Max(0, _jumpCooldown - dt);
        if (onFloor)
        {
            AirJumpsLeft = T.AirJumps;
            Stamina = Math.Min(T.StaminaMax, Stamina + (float)T.StaminaRegen * dt);
        }

        if (Mantling) { StepMantle(dt); return; }

        if (Hook.Active) { Hook.Step(dt); MoveAndSlide(); State = "hook"; return; }
        if (In.Ability3Pressed) Hook.TryFire();
        if (In.Ability1Pressed) Uppercut.TryFire();

        var wish = (Right * In.Move.X + Forward * In.Move.Y);
        if (wish.LengthSquared() > 1) wish = wish.Normalized();

        if (In.DashPressed && !Dashing)
        {
            if (Stamina >= 1)
            {
                Stamina -= 1;
                var dir = wish.LengthSquared() > 0.01f ? wish.Normalized() : Forward;
                _dashTime = (float)(onFloor ? T.GroundDashTime : T.AirDashTime);
                _dashVel = dir * (float)(onFloor ? T.GroundDashSpeed : T.AirDashSpeed);
                PlaySound?.Invoke(onFloor ? "dash_ground" : "dash_air");
            }
            else PlaySound?.Invoke("stamina_drained");
        }

        var v = Velocity;
        if (Dashing)
        {
            _dashTime -= dt;
            v.X = _dashVel.X; v.Z = _dashVel.Z;
            v.Y = onFloor ? 0 : Math.Max(v.Y, 0); // air dash holds altitude
            State = "dash";
        }
        else
        {
            var speed = (float)(T.RunSpeed + (In.SprintHeld && onFloor ? T.SprintBonus : 0));
            var target = wish * speed;
            var accel = onFloor ? 60f : 12f; // air control weaker, like Deadlock
            v.X = Mathf.MoveToward(v.X, target.X, accel * dt);
            v.Z = Mathf.MoveToward(v.Z, target.Z, accel * dt);
            v.Y -= (float)T.Gravity * dt;

            if (In.JumpPressed && _jumpCooldown <= 0)
            {
                if (onFloor) { v.Y = (float)T.JumpSpeed; _jumpCooldown = (float)Sheets.Movement.Jump.Secondary; }
                else if (AirJumpsLeft > 0 && Stamina >= Sheets.Movement.AirJump.StaminaCost)
                {
                    AirJumpsLeft--; Stamina -= Sheets.Movement.AirJump.StaminaCost;
                    v.Y = (float)T.AirJumpSpeed; PlaySound?.Invoke("dash_jump");
                }
            }
            State = onFloor ? (wish.LengthSquared() > 0.01f ? "run" : "idle") : (v.Y > 0 ? "jump" : "fall");
        }
        Velocity = v;
        MoveAndSlide();

        if (!IsOnFloor() && In.JumpHeld && wish.LengthSquared() > 0.1f) TryMantle(wish.Normalized());
    }

    /// Ledge in front of the chest whose top is within MantleReach above the hands: climb onto it.
    void TryMantle(Vector3 dir)
    {
        var space = GetWorld3D().DirectSpaceState;
        var chest = GlobalPosition + Vector3.Up * (Height * 0.6f);
        var wallHit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(chest, chest + dir * (Radius + 0.5f), 1, new Godot.Collections.Array<Rid> { GetRid() }));
        if (wallHit.Count == 0) return;
        var top = GlobalPosition + Vector3.Up * (Height + (float)T.MantleReach) + dir * (Radius + 0.45f);
        var down = space.IntersectRay(PhysicsRayQueryParameters3D.Create(top, top + Vector3.Down * (Height + (float)T.MantleReach - 0.3f), 1, new Godot.Collections.Array<Rid> { GetRid() }));
        if (down.Count == 0) return;
        var n = (Vector3)down["normal"];
        if (n.Y < 0.7f) return;
        _mantleFrom = GlobalPosition;
        _mantleTo = (Vector3)down["position"] + Vector3.Up * 0.05f;
        _mantleTime = (float)T.MantleTime;
        Velocity = Vector3.Zero;
        State = "mantle";
    }

    void StepMantle(float dt)
    {
        _mantleTime -= dt;
        var k = 1f - Math.Max(0, _mantleTime) / (float)T.MantleTime;
        var p = _mantleFrom.Lerp(_mantleTo, k);
        p.Y = Mathf.Lerp(_mantleFrom.Y, _mantleTo.Y, Math.Min(1, k * 1.6f));
        GlobalPosition = p;
        if (_mantleTime <= 0) { Velocity = Vector3.Zero; State = "idle"; }
    }

    public void Teleport(Vector3 p) { GlobalPosition = p; Velocity = Vector3.Zero; _dashTime = 0; _mantleTime = 0; Hook?.Cancel(); }
}

/// systems.hook: abilities.hook - the hook bites the level and reels Bebop in. When it bites a wall or the
/// underside of a platform, Bebop is reeled to just outside the ledge and then up onto its top.
public partial class HookAbility : Node3D
{
    public Climber Climber = null!;
    public bool Active { get; private set; }
    public float Cooldown;
    public Vector3 Target;                       // where the hook bit
    readonly System.Collections.Generic.List<Vector3> _waypoints = new(); // feet positions to reel through
    float _time;
    MeshInstance3D? _chain;

    public override void _Process(double delta)
    {
        Cooldown = Math.Max(0, Cooldown - (float)delta);
        UpdateChain();
    }

    Godot.Collections.Dictionary Ray(Vector3 from, Vector3 to) =>
        Climber.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to, 1, new Godot.Collections.Array<Rid> { Climber.GetRid() }));

    public bool TryFire()
    {
        if (Cooldown > 0 || Active) return false;
        var t = Climber.T;
        var from = Climber.In.AimOrigin;
        var hit = Ray(from, from + Climber.In.AimDir.Normalized() * (float)t.HookRange);
        if (hit.Count == 0) { Cooldown = 0.75f; return false; } // miss: short cooldown
        Target = (Vector3)hit["position"];
        var normal = (Vector3)hit["normal"];
        _waypoints.Clear();
        if (normal.Y > 0.6f) _waypoints.Add(Target);             // bit a top surface: straight onto it
        else if (FindLedge(Target) is { } ledge) { _waypoints.Add(ledge.outside); _waypoints.Add(ledge.top); }
        else _waypoints.Add(Target - Climber.In.AimDir.Normalized() * 0.8f - Vector3.Up * (Climber.Height * 0.5f));
        Active = true;
        _time = 0;
        Cooldown = (float)t.HookCooldown;
        Climber.PlaySound?.Invoke("bebop_hook_hit");
        return true;
    }

    /// The walkable top above a wall/underside hit, and a point just outside its edge facing Bebop.
    (Vector3 outside, Vector3 top)? FindLedge(Vector3 bite)
    {
        var flat = bite - Climber.GlobalPosition; flat.Y = 0;
        if (flat.LengthSquared() < 0.01f) { flat = Climber.Forward; }
        var dir = flat.Normalized();
        // look for a top surface within 3 m above the bite, a little beyond it
        var down = Ray(bite + Vector3.Up * 3f + dir * 1.0f, bite + dir * 1.0f - Vector3.Up * 0.2f);
        if (down.Count == 0 || ((Vector3)down["normal"]).Y < 0.7f) return null;
        var topY = ((Vector3)down["position"]).Y;
        // march from Bebop toward the bite to find the first column that is solid at topY: that's the edge
        var start = new Vector3(Climber.GlobalPosition.X, topY, Climber.GlobalPosition.Z);
        var end = new Vector3(bite.X, topY, bite.Z) + dir * 1.0f;
        var len = start.DistanceTo(end);
        for (float d = 0; d <= len; d += 0.25f)
        {
            var p = start + dir * d;
            var col = Ray(p + Vector3.Up * 0.6f, p - Vector3.Up * 0.4f);
            if (col.Count > 0 && ((Vector3)col["normal"]).Y > 0.7f)
            {
                var edge = p;
                var outside = edge - dir * (Climber.Radius + 0.5f) + Vector3.Up * 1.2f;
                var top = edge + dir * (Climber.Radius + 0.6f) + Vector3.Up * 0.1f;
                return (outside, top);
            }
        }
        return null;
    }

    public void Step(float dt)
    {
        _time += dt;
        var target = _waypoints[0];
        var to = target - Climber.GlobalPosition;
        var dist = to.Length();
        var last = _waypoints.Count == 1;
        var stuck = _time > 0.25f && Climber.GetRealVelocity().Length() < 1f;
        if (dist <= (last ? 0.6f : 0.8f) || _time > 3f || stuck)
        {
            if (!last && !stuck) { _waypoints.RemoveAt(0); _time = 0.1f; return; }
            var up = last && !stuck ? 2f : Mathf.Sqrt(2 * (float)Climber.T.Gravity * 1.0f); // pop over the lip if we fell short
            Climber.Velocity = (dist > 0.01f ? to / dist : Vector3.Zero) * (float)Climber.T.HookSpeed * 0.25f + Vector3.Up * up;
            Cancel();
            return;
        }
        Climber.Velocity = to / dist * (float)Climber.T.HookSpeed;
    }

    public void Cancel() { Active = false; _waypoints.Clear(); }

    void UpdateChain()
    {
        if (!IsInsideTree()) return;
        if (!Active) { if (_chain is not null) _chain.Visible = false; return; }
        if (_chain is null)
        {
            _chain = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.04f, BottomRadius = 0.04f, Height = 1 }, TopLevel = true };
            _chain.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.33f, 0.3f), Metallic = 0.8f, Roughness = 0.4f };
            AddChild(_chain);
        }
        var hand = Climber.GlobalPosition + Vector3.Up * (Climber.Height * 0.7f);
        var len = hand.DistanceTo(Target);
        _chain.Visible = len > 0.1f;
        if (len <= 0.1f) return;
        var up = (Target - hand).Normalized();
        var side = Mathf.Abs(up.Dot(Vector3.Forward)) < 0.95f ? up.Cross(Vector3.Forward).Normalized() : up.Cross(Vector3.Right).Normalized();
        _chain.GlobalTransform = new Transform3D(new Basis(side, up * len, side.Cross(up)), (hand + Target) / 2);
    }
}

/// systems.uppercut: abilities.uppercut - Bebop uppercuts the air and launches upward.
public partial class UppercutAbility : Node
{
    public Climber Climber = null!;
    public float Cooldown;

    public override void _Process(double delta) => Cooldown = Math.Max(0, Cooldown - (float)delta);

    public bool TryFire()
    {
        if (Cooldown > 0) return false;
        var v = Climber.Velocity;
        v.Y = (float)Climber.T.UppercutSpeed;
        Climber.Velocity = v;
        Cooldown = (float)Climber.T.UppercutCooldown;
        Climber.PlaySound?.Invoke("bebop_uppercut");
        return true;
    }
}
