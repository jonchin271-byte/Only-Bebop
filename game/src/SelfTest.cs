using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;

namespace OnlyBebop;

/// Headless oracle (--selftest): drives the real Climber/Hook/Uppercut/LevelLoader/SteamLocator code on a test
/// tower and checks the numbers against the movement and abilities sheets. Exit code = number of failures.
public static class SelfTest
{
    sealed class Script : IClimberInput
    {
        public Vector2 Move { get; set; }
        public float Yaw { get; set; }
        public Vector3 AimOrigin { get; set; }
        public Vector3 AimDir { get; set; } = Vector3.Forward;
        public bool JumpPressed { get; set; }
        public bool JumpHeld { get; set; }
        public bool DashPressed { get; set; }
        public bool SprintHeld { get; set; }
        public bool Ability1Pressed { get; set; }
        public bool Ability3Pressed { get; set; }
        public void Clear() { JumpPressed = DashPressed = Ability1Pressed = Ability3Pressed = false; }
    }

    static int _fail;
    static readonly List<string> Report = new();
    static void Check(string name, bool ok, string detail)
    {
        var line = $"{(ok ? "PASS" : "FAIL")} {name}: {detail}";
        Report.Add(line); GD.Print(line);
        if (!ok) _fail++;
    }
    static bool Near(double a, double b, double tol) => Math.Abs(a - b) <= Math.Abs(b) * tol;

    public static async Task Run(Boot boot)
    {
        var tree = boot.GetTree();
        try
        {
            TestLaunchArgs();
            TestSteamLocator();
            var t = Tuning.From(null);
            GD.Print($"tuning: run {t.RunSpeed} jump {t.JumpSpeed:0.00} g {t.Gravity:0.00} dash {t.GroundDashSpeed:0.00} hook {t.HookRange}m");

            var world = new Node3D();
            boot.AddChild(world);
            var floor = Box(world, new Vector3(0, -0.5f, 0), new Vector3(200, 1, 200));
            Box(world, new Vector3(0, 1.3f, -20), new Vector3(10, 2.6f, 4));      // mantle block, top 2.6 m
            Box(world, new Vector3(40, 19.5f, 0), new Vector3(6, 1, 6));          // hook platform, top 20 m

            var input = new Script();
            var c = new Climber { T = t, In = input };
            world.AddChild(c);
            async Task Frames(int n) { for (int i = 0; i < n; i++) { await boot.ToSignal(tree, SceneTree.SignalName.PhysicsFrame); input.Clear(); } }
            async Task Settle() { c.Teleport(new Vector3(0, 0.1f, 0)); input.Move = Vector2.Zero; input.JumpHeld = false; await Frames(30); }

            // jump apex = v^2 / 2g (movement.jump, movement.gravity)
            await Settle();
            input.JumpPressed = true; float top = 0;
            for (int i = 0; i < 120; i++) { await Frames(1); top = Math.Max(top, c.GlobalPosition.Y); }
            var expect = t.JumpSpeed * t.JumpSpeed / (2 * t.Gravity);
            Check("jump apex", Near(top, expect, 0.12), $"{top:0.00} m vs {expect:0.00} m");

            // double jump adds height and spends stamina (movement.air_jump)
            await Settle();
            var st0 = c.Stamina;
            input.JumpPressed = true; await Frames(45); input.JumpPressed = true; top = 0;
            for (int i = 0; i < 150; i++) { await Frames(1); top = Math.Max(top, c.GlobalPosition.Y); }
            Check("air jump", top > expect * 1.4 && c.Stamina <= st0 - 0.9f, $"apex {top:0.00} m, stamina {st0:0.0}->{c.Stamina:0.0}");

            // ground dash distance (movement.ground_dash)
            await Settle();
            var x0 = c.GlobalPosition; input.Yaw = -Mathf.Pi / 2; // face +X
            input.Move = new Vector2(0, 1); await Frames(1); input.Move = Vector2.Zero;
            var afterAccel = c.GlobalPosition;
            input.DashPressed = true; await Frames((int)(t.GroundDashTime * 120) + 1);
            var dashDist = (c.GlobalPosition - afterAccel).Length();
            Check("ground dash", Near(dashDist, Sheets.Movement.GroundDash.Value, 0.15), $"{dashDist:0.00} m vs {Sheets.Movement.GroundDash.Value} m");

            // uppercut apex (abilities.uppercut)
            await Settle();
            input.Ability1Pressed = true; top = 0;
            for (int i = 0; i < 160; i++) { await Frames(1); top = Math.Max(top, c.GlobalPosition.Y); }
            var upExpect = t.UppercutSpeed * t.UppercutSpeed / (2 * t.Gravity);
            Check("uppercut apex", Near(top, upExpect, 0.12) && c.Uppercut.Cooldown > 0, $"{top:0.00} m vs {upExpect:0.00} m, cooldown {c.Uppercut.Cooldown:0.0}s");

            // hook onto the platform 40 m away? No - out of range (30 m). Then from 15 m: reaches it.
            await Settle();
            input.AimOrigin = c.GlobalPosition + Vector3.Up * 2; input.AimDir = (new Vector3(40, 19.6f, 0) - input.AimOrigin).Normalized();
            input.Ability3Pressed = true; await Frames(2);
            Check("hook out of range misses", !c.Hook.Active, $"distance {(new Vector3(40, 20, 0) - input.AimOrigin).Length():0.0} m > {t.HookRange} m");
            c.Teleport(new Vector3(30, 0.1f, 0)); c.Hook.Cooldown = 0; await Frames(10);
            input.AimOrigin = c.GlobalPosition + Vector3.Up * 2; input.AimDir = (new Vector3(39, 19.9f, 0) - input.AimOrigin).Normalized();
            input.Ability3Pressed = true; await Frames(1);
            var fired = c.Hook.Active;
            for (int i = 0; i < 360; i++) { await Frames(1); }
            Check("hook reels onto platform", fired && c.GlobalPosition.Y > 19.5f && Math.Abs(c.GlobalPosition.X - 40) < 3.5f, $"fired {fired}, ended at {c.GlobalPosition}");

            // mantle onto the 2.6 m block: run at it, jump, hold jump (movement.mantle)
            await Settle();
            input.Yaw = 0; input.Move = new Vector2(0, 1); input.JumpHeld = true;
            c.Teleport(new Vector3(0, 0.1f, -14));
            await Frames(30); input.JumpPressed = true;
            var stoodOnTop = false;
            for (int i = 0; i < 150; i++)
            {
                await Frames(1);
                if (c.IsOnFloor() && c.GlobalPosition.Y > 2.4f) { stoodOnTop = true; input.Move = Vector2.Zero; }
            }
            input.Move = Vector2.Zero; input.JumpHeld = false; await Frames(30);
            Check("mantle onto ledge", stoodOnTop && c.GlobalPosition.Y > 2.4f, $"ended at y={c.GlobalPosition.Y:0.00}");

            // stamina refills on the ground (movement.stamina)
            await Settle(); c.Stamina = 0; await Frames((int)(120 / t.StaminaRegen) + 10);
            Check("stamina regen", c.Stamina >= 0.99f, $"{c.Stamina:0.00} after {1 / t.StaminaRegen:0.0}s");

            world.QueueFree();
            await TestLevelLoader(boot);
        }
        catch (Exception ex) { Check("exception", false, ex.ToString()); }

        GD.Print($"SELFTEST {(_fail == 0 ? "OK" : "FAILED")} {Report.Count - _fail}/{Report.Count}");
        try { Directory.CreateDirectory(ContentCache.Root); File.WriteAllLines(Path.Combine(ContentCache.Root, "selftest.txt"), Report); }
        catch (Exception ex) { GD.Print($"could not write report: {ex.Message}"); }
        tree.Quit(_fail);
    }

    static StaticBody3D Box(Node parent, Vector3 center, Vector3 size)
    {
        var b = new StaticBody3D { Position = center };
        b.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        b.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size } });
        parent.AddChild(b);
        return b;
    }

    static void TestLaunchArgs()
    {
        var a = LaunchArgs.Parse(new[] { "--deadlock", "\"D:\\Steam\\steamapps\\common\\Deadlock\"", "--selftest" });
        Check("launch args", a.DeadlockDir == "D:\\Steam\\steamapps\\common\\Deadlock" && a.SelfTest, a.ToString());
    }

    static void TestSteamLocator()
    {
        var root = Path.Combine(Path.GetTempPath(), "onlybebop-steam-" + Guid.NewGuid().ToString("N")[..8]);
        var lib2 = Path.Combine(root, "lib2");
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        Directory.CreateDirectory(Path.Combine(lib2, "steamapps", "common", "Only Up"));
        Directory.CreateDirectory(Path.Combine(lib2, "steamapps", "common", "Deadlock"));
        File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\"\n{{\n\t\"0\"\n\t{{\n\t\t\"path\"\t\t\"{root}\"\n\t}}\n\t\"1\"\n\t{{\n\t\t\"path\"\t\t\"{lib2}\"\n\t}}\n}}\n");
        File.WriteAllText(Path.Combine(lib2, "steamapps", "appmanifest_1422450.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"1422450\"\n\t\"installdir\"\t\t\"Deadlock\"\n}\n");
        System.Environment.SetEnvironmentVariable("ONLYBEBOP_STEAM_ROOT", root);
        var dl = SteamLocator.FindApp(Sheets.Games.Deadlock.SteamAppid);
        var ou = SteamLocator.FindApp(Sheets.Games.Onlyup.SteamAppid, "^only ?up");
        Check("steam locator (appmanifest)", dl == Path.Combine(lib2, "steamapps", "common", "Deadlock"), dl ?? "null");
        Check("steam locator (folder name fallback)", ou == Path.Combine(lib2, "steamapps", "common", "Only Up"), ou ?? "null");
        System.Environment.SetEnvironmentVariable("ONLYBEBOP_STEAM_ROOT", null);
        Directory.Delete(root, true);
    }

    /// Writes a tiny tower in the extractor's cache format (glb + manifest) and loads it through LevelLoader.
    static async Task TestLevelLoader(Boot boot)
    {
        var home = Path.Combine(Path.GetTempPath(), "onlybebop-home-" + Guid.NewGuid().ToString("N")[..8]);
        System.Environment.SetEnvironmentVariable("ONLYBEBOP_HOME", home);
        var dir = ContentCache.Dir("onlyup");
        Directory.CreateDirectory(Path.Combine(dir, "meshes"));
        var scene = new Node3D();
        scene.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(4, 1, 4) }, Name = "Slab" });
        var doc = new GltfDocument(); var st = new GltfState();
        doc.AppendFromScene(scene, st);
        doc.WriteToFilesystem(st, Path.Combine(dir, "meshes", "00000_Slab.glb"));
        scene.Free();
        var instances = new JsonArray();
        for (int i = 0; i < 5; i++)
            instances.Add(new JsonObject { ["mesh"] = "00000_Slab", ["m"] = new JsonArray(1, 0, 0, 0, 1, 0, 0, 0, 1, i * 5f, i * 3f, 0) });
        var manifest = new JsonObject
        {
            ["meshes"] = new JsonObject { ["00000_Slab"] = "meshes/00000_Slab.glb" },
            ["instances"] = instances,
            ["spawn"] = new JsonObject { ["pos"] = new JsonArray(0f, 1f, 0f) },
        };
        var lvl = new LevelLoader();
        boot.AddChild(lvl);
        lvl.Load(manifest, s => GD.Print(s));
        await boot.ToSignal(boot.GetTree(), SceneTree.SignalName.PhysicsFrame);
        await boot.ToSignal(boot.GetTree(), SceneTree.SignalName.PhysicsFrame);
        var space = lvl.GetWorld3D().DirectSpaceState;
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(new Vector3(10, 30, 0), new Vector3(10, -30, 0)));
        var y = hit.Count > 0 ? ((Vector3)hit["position"]).Y : float.NaN;
        Check("level loader pieces + colliders", lvl.Pieces == 5 && Math.Abs(y - 6.5f) < 0.05f, $"{lvl.Pieces} pieces, top of piece #3 at y={y:0.00} (expect 6.50)");
        lvl.QueueFree();
        System.Environment.SetEnvironmentVariable("ONLYBEBOP_HOME", null);
        Directory.Delete(home, true);
    }
}
