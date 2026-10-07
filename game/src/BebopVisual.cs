using System;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;

namespace OnlyBebop;

/// heroes.bebop.model from the cache, scaled to the climber's capsule, with Deadlock's own animations
/// picked by name for each climber state.
public partial class BebopVisual : Node3D
{
    public Climber Climber = null!;
    AnimationPlayer? _anim;
    string _playing = "";
    public string[] Clips = Array.Empty<string>();

    public static BebopVisual? Load(string glbPath, Climber c)
    {
        var doc = new GltfDocument();
        var state = new GltfState();
        if (doc.AppendFromFile(glbPath, state) != Error.Ok) return null;
        var model = (Node3D)doc.GenerateScene(state);
        var v = new BebopVisual { Climber = c };
        v.AddChild(model);
        // fit to capsule height, feet at origin
        var aabb = Bounds(model);
        if (aabb.Size.Y > 0.01f)
        {
            var s = (Climber.Height * 1.1f) / aabb.Size.Y;
            model.Scale = Vector3.One * s;
            model.Position = new Vector3(0, -aabb.Position.Y * s, 0);
        }
        v._anim = LevelLoader.Find<AnimationPlayer>(model);
        v.Clips = v._anim?.GetAnimationList() ?? Array.Empty<string>();
        return v;
    }

    static Aabb Bounds(Node n)
    {
        Aabb? box = null;
        void Walk(Node x, Transform3D t)
        {
            var tt = x is Node3D n3 ? t * n3.Transform : t;
            if (x is MeshInstance3D mi && mi.Mesh is not null) { var b = tt * mi.Mesh.GetAabb(); box = box is null ? b : box.Value.Merge(b); }
            foreach (var c in x.GetChildren()) Walk(c, tt);
        }
        Walk(n, Transform3D.Identity);
        return box ?? new Aabb();
    }

    static readonly (string state, string pattern)[] StatePatterns =
    {
        ("run", "(^|_)run(_|$)|run_n|sprint"), ("idle", "(^|_)idle"), ("jump", "jump"), ("fall", "fall|air"),
        ("dash", "dash|roll"), ("hook", "hook"), ("mantle", "mantle|climb"),
    };

    public override void _Process(double delta)
    {
        var flat = new Vector3(Climber.Velocity.X, 0, Climber.Velocity.Z);
        if (flat.LengthSquared() > 0.5f) Rotation = new Vector3(0, Mathf.Atan2(flat.X, flat.Z), 0);
        else Rotation = new Vector3(0, Climber.In.Yaw + Mathf.Pi, 0);
        if (_anim is null) return;
        var pat = StatePatterns.FirstOrDefault(p => p.state == Climber.State).pattern ?? "idle";
        var clip = Clips.FirstOrDefault(c => Regex.IsMatch(c, pat, RegexOptions.IgnoreCase))
                   ?? Clips.FirstOrDefault(c => Regex.IsMatch(c, "idle", RegexOptions.IgnoreCase));
        if (clip is not null && clip != _playing) { _anim.Play(clip, 0.15); _playing = clip; }
    }
}
