using System;
using System.Linq;
using Godot;
using OnlyBebop.Sheets;

namespace OnlyBebop;

/// systems.hud: stamina pips, ability icons with cooldowns, height above spawn, first-seconds prompts.
public partial class Hud : CanvasLayer
{
    public Climber Climber = null!;
    public float SpawnY;
    public Texture2D? HookIcon, UppercutIcon;
    Label _height = null!, _prompt = null!, _banner = null!;
    HBoxContainer _pips = null!;
    TextureRect _hook = null!, _upper = null!;
    Label _hookCd = null!, _upperCd = null!;
    double _t;
    float _best;
    bool _bannerShown;
    static readonly string[] PromptOrder = { "move", "jump", "dash", "ability_3", "ability_1" };

    public override void _Ready()
    {
        _height = new Label { Position = new Vector2(24, 20) };
        _height.AddThemeFontSizeOverride("font_size", 26);
        AddChild(_height);

        _prompt = new Label { AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.72f, AnchorBottom = 0.72f, HorizontalAlignment = HorizontalAlignment.Center, GrowHorizontal = Control.GrowDirection.Both };
        _prompt.AddThemeFontSizeOverride("font_size", 28);
        _prompt.AddThemeColorOverride("font_outline_color", Colors.Black);
        _prompt.AddThemeConstantOverride("outline_size", 8);
        AddChild(_prompt);

        _banner = new Label { AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.2f, AnchorBottom = 0.2f, HorizontalAlignment = HorizontalAlignment.Center, GrowHorizontal = Control.GrowDirection.Both, Visible = false };
        _banner.AddThemeFontSizeOverride("font_size", 44);
        _banner.AddThemeColorOverride("font_outline_color", Colors.Black);
        _banner.AddThemeConstantOverride("outline_size", 10);
        AddChild(_banner);

        _pips = new HBoxContainer { AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 1, AnchorBottom = 1, OffsetTop = -60, GrowHorizontal = Control.GrowDirection.Both };
        AddChild(_pips);
        for (int i = 0; i < Climber.T.StaminaMax; i++)
            _pips.AddChild(new ColorRect { CustomMinimumSize = new Vector2(36, 10), Color = new Color(0.4f, 0.9f, 1f) });

        var bar = new HBoxContainer { AnchorLeft = 1, AnchorRight = 1, AnchorTop = 1, AnchorBottom = 1, OffsetLeft = -230, OffsetTop = -110 };
        AddChild(bar);
        (_upper, _upperCd) = Slot(bar, UppercutIcon, "1");
        (_hook, _hookCd) = Slot(bar, HookIcon, "3");
    }

    static (TextureRect, Label) Slot(HBoxContainer bar, Texture2D? icon, string key)
    {
        var box = new Control { CustomMinimumSize = new Vector2(96, 96) };
        bar.AddChild(box);
        var tr = new TextureRect { Texture = icon, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, Size = new Vector2(88, 88) };
        box.AddChild(tr);
        if (icon is null) box.AddChild(new ColorRect { Size = new Vector2(88, 88), Color = new Color(0.2f, 0.2f, 0.25f) });
        var k = new Label { Text = key, Position = new Vector2(4, 0) };
        box.AddChild(k);
        var cd = new Label { Position = new Vector2(30, 28) };
        cd.AddThemeFontSizeOverride("font_size", 28);
        box.AddChild(cd);
        return (tr, cd);
    }

    public override void _Process(double delta)
    {
        _t += delta;
        var h = Climber.GlobalPosition.Y - SpawnY;
        _best = Math.Max(_best, h);
        _height.Text = $"{h:0} m   best {_best:0} m";

        // prompts: inputs sheet rows, ~1.6 s each, first 8 seconds
        var idx = (int)(_t / 1.6);
        _prompt.Text = idx < PromptOrder.Length ? Inputs.All.First(r => r.Id == PromptOrder[idx]).Prompt : "";

        for (int i = 0; i < _pips.GetChildCount(); i++)
            ((ColorRect)_pips.GetChild(i)).Color = Climber.Stamina >= i + 1 ? new Color(0.4f, 0.9f, 1f) : new Color(0.4f, 0.9f, 1f, 0.2f);

        _hookCd.Text = Climber.Hook.Cooldown > 0 ? $"{Climber.Hook.Cooldown:0.0}" : "";
        _upperCd.Text = Climber.Uppercut.Cooldown > 0 ? $"{Climber.Uppercut.Cooldown:0.0}" : "";
        _hook.Modulate = Climber.Hook.Cooldown > 0 ? new Color(1, 1, 1, 0.35f) : Colors.White;
        _upper.Modulate = Climber.Uppercut.Cooldown > 0 ? new Color(1, 1, 1, 0.35f) : Colors.White;

        // level.stretch_end
        if (!_bannerShown && h >= 120) { _bannerShown = true; _banner.Text = "You reached the first landmark!"; _banner.Visible = true; }
    }
}
