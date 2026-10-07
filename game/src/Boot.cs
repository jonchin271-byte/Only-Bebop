using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;
using OnlyBebop.Sheets;

namespace OnlyBebop;

/// systems.boot: find both games, prepare the cache from the player's installs, then hero select -> climb.
public partial class Boot : Node
{
    LaunchArgs _args = null!;
    Control? _screen;
    StreamWriter? _log;
    Sounds _sounds = null!;
    Climb? _climb;

    public override void _Ready()
    {
        InputSetup.Apply();
        _args = LaunchArgs.Parse(OS.GetCmdlineUserArgs().Length > 0 ? OS.GetCmdlineUserArgs() : OS.GetCmdlineArgs());
        Directory.CreateDirectory(Path.Combine(ContentCache.Root, "logs"));
        _log = new StreamWriter(Path.Combine(ContentCache.Root, "logs", "latest.log"), false) { AutoFlush = true };
        Log($"Only Bebop starting; args: {string.Join(' ', OS.GetCmdlineUserArgs())}");
        _sounds = new Sounds();
        AddChild(_sounds);
        if (_args.SelfTest) { _ = SelfTest.Run(this); return; }
        _ = Start();
    }

    readonly object _logLock = new();
    public void Log(string s) { GD.Print(s); lock (_logLock) _log?.WriteLine($"{DateTime.Now:HH:mm:ss} {s}"); }

    async Task Start()
    {
        // games.deadlock.locate
        var deadlock = _args.DeadlockDir ?? SteamLocator.FindApp(Games.Deadlock.SteamAppid);
        if (deadlock is null || !File.Exists(Path.Combine(deadlock, Games.Deadlock.Archive)))
        {
            Log($"Deadlock not found (arg: {_args.DeadlockDir ?? "none"})");
            ShowMessage(Games.Deadlock.MissingMessage, null);
            return;
        }
        // games.onlyup.locate
        var onlyUp = _args.OnlyUpDir ?? Settings.Get("onlyup_dir") ?? SteamLocator.FindApp(Games.Onlyup.SteamAppid, "^only ?up");
        if (onlyUp is null || !Directory.Exists(onlyUp))
        {
            Log("Only Up! not found");
            ShowMessage(Games.Onlyup.MissingMessage, "Choose Only Up! folder...", ChooseOnlyUpFolder);
            return;
        }
        Log($"Deadlock: {deadlock}\nOnly Up!: {onlyUp}");

        if (!ContentCache.DeadlockFresh(deadlock))
        {
            var r = await RunWithProgress("Reading Bebop from your Deadlock install...", (p, l) => DeadlockExtractor.Run(deadlock, p, l));
            if (r.ExitCode != 0) { ShowMessage($"Couldn't read Deadlock's files ({r.LastLine}).\nDetails: {Path.Combine(ContentCache.Root, "logs", "latest.log")}", null); return; }
        }
        if (!ContentCache.OnlyUpFresh(onlyUp))
        {
            var r = await RunWithProgress("Reading the tower from your Only Up! install... (first run only)", (p, l) => OnlyUpExtractor.Run(onlyUp, p, l));
            if (r.ExitCode != 0)
            {
                var why = r.ExitCode switch
                {
                    3 => "Only Up!'s game files weren't found in that folder.",
                    5 => "Only Up!'s files are encrypted and this version can't open them yet.",
                    6 => "Only Up!'s files use a compression this version can't open yet.",
                    7 => "No level could be read from Only Up!'s files.",
                    _ => r.LastLine,
                };
                ShowMessage($"Couldn't read Only Up!: {why}\nDetails: {Path.Combine(ContentCache.Root, "logs", "latest.log")}", "Choose Only Up! folder...", ChooseOnlyUpFolder);
                return;
            }
        }
        ShowHeroSelect();
    }

    async Task<ExtractResult> RunWithProgress(string title, Func<Action<int, string>, Action<string>, Task<ExtractResult>> run)
    {
        var (root, label, bar) = ProgressScreen(title);
        SetScreen(root);
        var r = await run((pct, what) => CallDeferred(nameof(SetProgress), bar, label, pct, what), Log);
        Log($"extractor exit {r.ExitCode}: {r.LastLine}");
        return r;
    }

    void SetProgress(ProgressBar bar, Label label, int pct, string what) { bar.Value = pct; label.Text = what; }

    void ShowHeroSelect()
    {
        var dl = ContentCache.Manifest("deadlock")!;
        _sounds.Load(dl);
        var hs = new HeroSelect { Deadlock = dl, Sounds = _sounds };
        hs.Picked += key => CallDeferred(nameof(StartClimb), key);
        SetScreen(hs);
    }

    void StartClimb(string heroKey)
    {
        SetScreen(null);
        _climb = new Climb { Sounds = _sounds, Deadlock = ContentCache.Manifest("deadlock")!, OnlyUp = ContentCache.Manifest("onlyup")!, Boot = this };
        AddChild(_climb);
    }

    public void BackToHeroSelect() { _climb?.QueueFree(); _climb = null; Input.MouseMode = Input.MouseModeEnum.Visible; ShowHeroSelect(); }

    void ChooseOnlyUpFolder()
    {
        var fd = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenDir, Access = FileDialog.AccessEnum.Filesystem, Title = "Choose your Only Up! folder", UseNativeDialog = true };
        AddChild(fd);
        fd.DirSelected += dir => { Settings.Set("onlyup_dir", dir); _ = Start(); };
        fd.PopupCentered(new Vector2I(900, 600));
    }

    void SetScreen(Control? c) { _screen?.QueueFree(); _screen = c; if (c is not null) AddChild(c); }

    void ShowMessage(string text, string? button, Action? onButton = null)
    {
        Input.MouseMode = Input.MouseModeEnum.Visible;
        var root = new Control(); root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(new ColorRect { Color = new Color(0.07f, 0.07f, 0.09f), AnchorRight = 1, AnchorBottom = 1 });
        var v = new VBoxContainer { AnchorLeft = 0.2f, AnchorRight = 0.8f, AnchorTop = 0.3f, AnchorBottom = 0.8f };
        root.AddChild(v);
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center };
        l.AddThemeFontSizeOverride("font_size", 24);
        v.AddChild(l);
        if (button is not null) { var b = new Button { Text = button }; b.Pressed += () => onButton?.Invoke(); v.AddChild(b); }
        var q = new Button { Text = "Quit" }; q.Pressed += () => GetTree().Quit(); v.AddChild(q);
        SetScreen(root);
    }

    static (Control, Label, ProgressBar) ProgressScreen(string title)
    {
        var root = new Control(); root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(new ColorRect { Color = new Color(0.07f, 0.07f, 0.09f), AnchorRight = 1, AnchorBottom = 1 });
        var v = new VBoxContainer { AnchorLeft = 0.2f, AnchorRight = 0.8f, AnchorTop = 0.4f, AnchorBottom = 0.7f };
        root.AddChild(v);
        var t = new Label { Text = title, HorizontalAlignment = HorizontalAlignment.Center }; t.AddThemeFontSizeOverride("font_size", 26); v.AddChild(t);
        var bar = new ProgressBar { MinValue = 0, MaxValue = 100, CustomMinimumSize = new Vector2(0, 24) }; v.AddChild(bar);
        var l = new Label { HorizontalAlignment = HorizontalAlignment.Center }; v.AddChild(l);
        return (root, l, bar);
    }
}

/// %LOCALAPPDATA%/OnlyBebop/settings.json
public static class Settings
{
    static string PathJson => System.IO.Path.Combine(ContentCache.Root, "settings.json");
    static JsonObject Read() { try { return JsonNode.Parse(File.ReadAllText(PathJson)) as JsonObject ?? new(); } catch { return new(); } }
    public static string? Get(string k) => Read()[k]?.GetValue<string>();
    public static void Set(string k, string v) { var o = Read(); o[k] = v; Directory.CreateDirectory(ContentCache.Root); File.WriteAllText(PathJson, o.ToJsonString()); }
}

/// The climb scene: Only Up!'s tower, Bebop, camera, HUD, music, pause.
public partial class Climb : Node3D
{
    public Sounds Sounds = null!;
    public JsonObject Deadlock = null!, OnlyUp = null!;
    public Boot Boot = null!;
    public Climber Climber = null!;
    public LevelLoader Level = null!;
    Control? _pause;

    public override void _Ready()
    {
        AddChild(new WorldEnvironment { Environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky, Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky, TonemapMode = Godot.Environment.ToneMapper.Filmic,
        } });
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50, 30, 0), ShadowEnabled = true });

        Level = new LevelLoader();
        AddChild(Level);
        Level.Load(OnlyUp, Boot.Log);
        Boot.Log($"tower: {Level.Pieces} pieces, spawn {Level.Spawn}");

        var tuning = Tuning.From(Deadlock);
        var cam = new ShoulderCamera();
        Climber = new Climber { T = tuning, In = cam, PlaySound = Sounds.Play };
        cam.Target = Climber;
        AddChild(cam);       // camera first: it latches input before the climber's physics step
        AddChild(Climber);
        Climber.Teleport(Level.Spawn);

        if (Deadlock["models"]?["bebop"]?.GetValue<string>() is { } glb && BebopVisual.Load(ContentCache.Abs("deadlock", glb), Climber) is { } vis)
        { Climber.AddChild(vis); Boot.Log("bebop clips: " + string.Join(", ", vis.Clips)); }
        else Boot.Log("bebop model missing");

        Texture2D? Icon(string id) => Deadlock["icons"]?[id]?.GetValue<string>() is { } p && Image.LoadFromFile(ContentCache.Abs("deadlock", p)) is { } img ? ImageTexture.CreateFromImage(img) : null;
        AddChild(new Hud { Climber = Climber, SpawnY = Level.Spawn.Y, HookIcon = Icon("hook"), UppercutIcon = Icon("uppercut") });
        var music = new MusicPlayer();
        AddChild(music);
        Boot.Log(music.Start(OnlyUp) ? "music playing" : "no music");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (Climber.GlobalPosition.Y < Level.LowestY - 50) Climber.Teleport(Level.Spawn); // level.falls
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!e.IsActionPressed("menu")) return;
        if (_pause is null) ShowPause(); else HidePause();
    }

    void ShowPause()
    {
        Input.MouseMode = Input.MouseModeEnum.Visible;
        GetTree().Paused = true;
        var layer = new CanvasLayer { ProcessMode = ProcessModeEnum.Always };
        var v = new VBoxContainer { AnchorLeft = 0.4f, AnchorRight = 0.6f, AnchorTop = 0.4f, AnchorBottom = 0.6f };
        _pause = v;
        layer.AddChild(v);
        AddChild(layer);
        void B(string t, Action a) { var b = new Button { Text = t }; b.Pressed += a; v.AddChild(b); }
        B("Resume", HidePause);
        B("Back to hero select", () => { GetTree().Paused = false; Boot.BackToHeroSelect(); });
        B("Quit", () => GetTree().Quit());
    }

    void HidePause()
    {
        _pause?.GetParent().QueueFree(); _pause = null;
        GetTree().Paused = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }
}
