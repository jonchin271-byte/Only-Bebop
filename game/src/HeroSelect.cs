using System;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;

namespace OnlyBebop;

/// systems.hero_select: Deadlock's own roster cards; Bebop playable, the rest "Coming soon".
public partial class HeroSelect : Control
{
    public JsonObject Deadlock = null!;
    public Action<string>? Picked;
    public Sounds? Sounds;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Color = new Color(0.07f, 0.07f, 0.09f), AnchorRight = 1, AnchorBottom = 1 });
        var v = new VBoxContainer { AnchorRight = 1, AnchorBottom = 1, OffsetLeft = 40, OffsetTop = 30, OffsetRight = -40, OffsetBottom = -30 };
        AddChild(v);
        var title = new Label { Text = "ONLY BEBOP - choose your climber", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 36);
        v.AddChild(title);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        v.AddChild(scroll);
        var grid = new GridContainer { Columns = 8, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 12);
        scroll.AddChild(grid);

        var roster = (Deadlock["roster"] as JsonArray ?? new JsonArray())
            .OrderByDescending(h => h!["playable"]!.GetValue<bool>()).ThenBy(h => h!["name"]!.GetValue<string>());
        foreach (var h in roster)
        {
            var key = h!["key"]!.GetValue<string>();
            var playable = h["playable"]!.GetValue<bool>();
            var card = new Button { CustomMinimumSize = new Vector2(150, 230), Disabled = !playable, ClipContents = true, TooltipText = playable ? "" : "Coming soon" };
            var cardPath = h["card"]?.GetValue<string>();
            if (cardPath is not null)
            {
                var img = Image.LoadFromFile(ContentCache.Abs("deadlock", cardPath));
                if (img is not null)
                    card.AddChild(new TextureRect { Texture = ImageTexture.CreateFromImage(img), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore, Modulate = playable ? Colors.White : new Color(0.35f, 0.35f, 0.35f) });
            }
            var name = new Label { Text = h["name"]!.GetValue<string>() + (playable ? "" : "\nComing soon"), AnchorTop = 1, AnchorBottom = 1, AnchorRight = 1, OffsetTop = -52, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
            name.AddThemeColorOverride("font_outline_color", Colors.Black);
            name.AddThemeConstantOverride("outline_size", 6);
            card.AddChild(name);
            if (playable) card.Pressed += () => { Sounds?.Play("bebop_pick_vo"); Picked?.Invoke(key); };
            grid.AddChild(card);
        }
    }
}
