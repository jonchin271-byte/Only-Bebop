using System;
using Godot;

namespace OnlyBebop;

/// systems.camera: Deadlock-style over-the-shoulder camera; also the player's input source.
public partial class ShoulderCamera : Node3D, IClimberInput
{
    public Climber Target = null!;
    public float Sensitivity = 0.0025f;
    float _yaw, _pitch = -0.15f;
    SpringArm3D _arm = null!;
    public Camera3D Camera = null!;
    bool _jumpPressed, _dashPressed, _a1, _a3;

    public override void _Ready()
    {
        TopLevel = true;
        _arm = new SpringArm3D { SpringLength = 4.2f, Margin = 0.2f, Position = new Vector3(0.9f, 0, 0) };
        AddChild(_arm);
        Camera = new Camera3D { Fov = 80, Current = true };
        _arm.AddChild(Camera);
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            _yaw -= mm.Relative.X * Sensitivity;
            _pitch = Math.Clamp(_pitch - mm.Relative.Y * Sensitivity, -1.45f, 1.2f);
        }
    }

    public override void _Process(double delta)
    {
        _arm.AddExcludedObject(Target.GetRid());
        GlobalPosition = Target.GlobalPosition + Vector3.Up * 2.0f;
        Rotation = new Vector3(_pitch, _yaw, 0);
    }

    public override void _PhysicsProcess(double delta)
    {
        // latch edge-triggered inputs so the physics step sees each press exactly once
        _jumpPressed |= Input.IsActionJustPressed("jump");
        _dashPressed |= Input.IsActionJustPressed("dash");
        _a1 |= Input.IsActionJustPressed("ability_1");
        _a3 |= Input.IsActionJustPressed("ability_3");
    }

    public void Consume() { _jumpPressed = _dashPressed = _a1 = _a3 = false; }

    public Vector2 Move => new(Input.GetAxis("move_left", "move_right"), Input.GetAxis("move_back", "move_forward"));
    public float Yaw => _yaw;
    public Vector3 AimOrigin => Camera.GlobalPosition;
    public Vector3 AimDir => -Camera.GlobalBasis.Z;
    public bool JumpPressed => _jumpPressed;
    public bool JumpHeld => Input.IsActionPressed("jump");
    public bool DashPressed => _dashPressed;
    public bool SprintHeld => Input.IsActionPressed("sprint");
    public bool Ability1Pressed => _a1;
    public bool Ability3Pressed => _a3;
}
