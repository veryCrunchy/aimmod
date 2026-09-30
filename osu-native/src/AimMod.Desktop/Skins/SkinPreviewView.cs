using AimMod.Desktop.Visuals;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Lines;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Game.Graphics.Sprites;
using osuTK;

namespace AimMod.Desktop.Skins;

/// <summary>
/// Draws hit objects with a skin's own elements on a fixed virtual canvas that scales to fit.
/// Everything is read in the background loader, so callers must load it asynchronously
/// (a delayed-load wrapper for cards, LoadComponentAsync for the inspector).
/// </summary>
public abstract partial class SkinCompositionView : CompositeDrawable
{
    private const float legacy_circle_size = 128;

    private readonly InstalledLazerSkin? skin;
    private readonly SkinTextureCache? textures;
    private readonly Vector2 canvasSize;
    private readonly bool fill;
    private readonly Container canvas;

    protected SkinCompositionView(InstalledLazerSkin? skin, SkinTextureCache? textures, Vector2 canvasSize, bool fill)
    {
        this.skin = skin;
        this.textures = textures;
        this.canvasSize = canvasSize;
        this.fill = fill;
        RelativeSizeAxes = Axes.Both;
        Masking = true;
        InternalChild = canvas = new Container
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            Size = canvasSize,
        };
    }

    protected SkinPreviewAssets Assets { get; private set; } = SkinPreviewAssets.Empty;

    /// <summary>True when the drawing uses the skin's own hit circle rather than stand-in shapes.</summary>
    public bool UsesSkinElements { get; private set; }

    [BackgroundDependencyLoader]
    private void load()
    {
        if (skin is not null)
        {
            try
            {
                Assets = SkinPreviewAssets.LoadAsync(skin).GetAwaiter().GetResult();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Assets = SkinPreviewAssets.Empty;
            }
        }
        UsesSkinElements = textures is not null && Assets.HasGameplayElements && texture("hitcircle") is not null;
        canvas.AddRange(Compose());
    }

    protected abstract IEnumerable<Drawable> Compose();

    protected override void Update()
    {
        base.Update();
        if (DrawWidth <= 0 || DrawHeight <= 0)
            return;
        float x = DrawWidth / canvasSize.X, y = DrawHeight / canvasSize.Y;
        canvas.Scale = new Vector2(fill ? Math.Max(x, y) : Math.Min(x, y));
    }

    protected Texture? texture(string name) => textures?.Get(Assets.Find(name));

    /// <summary>Legacy elements are authored for a 128px circle; scaling by the same unit keeps overlays aligned.</summary>
    protected Drawable? element(string name, Vector2 position, float diameter, Colour4? tint = null, float scale = 1, float alpha = 1)
    {
        SkinElementFile? file = Assets.Find(name);
        Texture? found = textures?.Get(file);
        if (file is null || found is null)
            return null;
        float unit = diameter / legacy_circle_size / (file.HighResolution ? 2 : 1);
        return new Sprite
        {
            Texture = found,
            Anchor = Anchor.TopLeft,
            Origin = Anchor.Centre,
            Position = position,
            Size = new Vector2(found.Width, found.Height) * unit * scale,
            Colour = tint ?? Colour4.White,
            Alpha = alpha,
        };
    }

    /// <summary>A hit circle with overlay and number; stand-in shapes when the skin has no circle art.</summary>
    protected IEnumerable<Drawable> hitCircle(Vector2 position, float diameter, Colour4 colour, int number, float alpha = 1, string baseName = "hitcircle")
    {
        Drawable? circle = element(baseName, position, diameter, colour, alpha: alpha) ?? element("hitcircle", position, diameter, colour, alpha: alpha);
        if (circle is null)
        {
            yield return fallbackCircle(position, diameter, colour, alpha);
            yield return fallbackNumber(position, diameter, number, alpha);
            yield break;
        }
        yield return circle;
        Drawable? overlay = element(baseName + "overlay", position, diameter, alpha: alpha) ?? element("hitcircleoverlay", position, diameter, alpha: alpha);
        if (overlay is not null)
            yield return overlay;
        yield return number < 0 ? Empty() : numberSprite(Assets.HitCirclePrefix, number.ToString(System.Globalization.CultureInfo.InvariantCulture), position, diameter * 0.8f, alpha)
                                            ?? fallbackNumber(position, diameter, number, alpha);
    }

    protected Drawable approachCircle(Vector2 position, float diameter, Colour4 colour, float scale)
    {
        return element("approachcircle", position, diameter, colour, scale, 0.85f) ?? new CircularContainer
        {
            Origin = Anchor.Centre,
            Position = position,
            Size = new Vector2(diameter * scale),
            Masking = true,
            BorderThickness = diameter * 0.05f,
            BorderColour = colour,
            Alpha = 0.85f,
            Child = new Box { RelativeSizeAxes = Axes.Both, Alpha = 0, AlwaysPresent = true },
        };
    }

    protected Drawable cursor(Vector2 position, float diameter)
    {
        return element("cursor", position, diameter) ?? new CircularContainer
        {
            Origin = Anchor.Centre,
            Position = position,
            Size = new Vector2(diameter * 0.32f),
            Masking = true,
            BorderThickness = diameter * 0.05f,
            BorderColour = Colour4.White,
            Child = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Yellow },
        };
    }

    /// <summary>Digits drawn from a numbered font ("default-1", "score-7"); null when any glyph is missing.</summary>
    protected Drawable? numberSprite(string prefix, string text, Vector2 position, float diameter, float alpha = 1, Anchor origin = Anchor.Centre)
    {
        var glyphs = new List<(Texture Texture, bool HighResolution)>();
        foreach (char character in text)
        {
            string suffix = character switch
            {
                ',' => "comma",
                '.' => "dot",
                'x' => "x",
                '%' => "percent",
                _ => character.ToString(),
            };
            SkinElementFile? file = Assets.Find($"{prefix}-{suffix}");
            Texture? glyph = textures?.Get(file);
            if (file is null || glyph is null)
            {
                if (character is ',' or '.')
                    continue;
                return null;
            }
            glyphs.Add((glyph, file.HighResolution));
        }
        if (glyphs.Count == 0)
            return null;

        float unit = diameter / legacy_circle_size;
        var flow = new FillFlowContainer
        {
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Horizontal,
            Origin = origin,
            Position = position,
            Alpha = alpha,
            Spacing = new Vector2(-2 * unit, 0),
        };
        foreach ((Texture glyph, bool highResolution) in glyphs)
        {
            float scale = unit / (highResolution ? 2 : 1);
            flow.Add(new Sprite { Texture = glyph, Size = new Vector2(glyph.Width, glyph.Height) * scale });
        }
        return flow;
    }

    private static Drawable fallbackCircle(Vector2 position, float diameter, Colour4 colour, float alpha) => new CircularContainer
    {
        Origin = Anchor.Centre,
        Position = position,
        Size = new Vector2(diameter * 0.92f),
        Masking = true,
        BorderThickness = diameter * 0.07f,
        BorderColour = Colour4.White,
        Alpha = alpha,
        Child = new Box { RelativeSizeAxes = Axes.Both, Colour = ColourInfo.GradientVertical(colour.Lighten(0.2f), colour.Darken(0.35f)) },
    };

    private static Drawable fallbackNumber(Vector2 position, float diameter, int number, float alpha) => number < 0
        ? Empty()
        : new OsuSpriteText
        {
            Origin = Anchor.Centre,
            Position = position,
            Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Font = new FontUsage(size: diameter * 0.42f, weight: "Bold"),
            Colour = Colour4.White,
            Alpha = alpha,
        };

    protected static Drawable playfieldBackdrop(Colour4 tint) => new Box
    {
        RelativeSizeAxes = Axes.Both,
        Colour = ColourInfo.GradientVertical(AimModPalette.Canvas.Opacity(1), tint.Darken(2.6f)),
    };
}

/// <summary>Small card thumbnail: two circles, the approach circle and the cursor in the skin's own art.</summary>
public partial class SkinThumbnailView(InstalledLazerSkin skin, SkinTextureCache? textures)
    : SkinCompositionView(skin, textures, new Vector2(320, 150), fill: true)
{
    protected override IEnumerable<Drawable> Compose()
    {
        const float diameter = 82;
        Colour4 first = Assets.Combo(0);
        var drawables = new List<Drawable>
        {
            playfieldBackdrop(first),
            new Box
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Size = new Vector2(320, 150),
                Colour = ColourInfo.GradientHorizontal(first.Opacity(0.14f), Assets.Combo(1).Opacity(0.06f)),
            },
        };
        drawables.AddRange(hitCircle(new Vector2(214, 64), diameter, Assets.Combo(1), 2, alpha: 0.8f));
        drawables.AddRange(hitCircle(new Vector2(110, 86), diameter, first, 1));
        drawables.Add(approachCircle(new Vector2(110, 86), diameter, first, 1.42f));
        drawables.Add(cursor(new Vector2(146, 116), diameter));
        return drawables;
    }
}

/// <summary>
/// Inspector preview: a slider, circles in two combo colours, an approach circle, the cursor,
/// hit judgements, score and combo digits and the health bar, all from the selected skin.
/// </summary>
public partial class SkinPlayfieldView(InstalledLazerSkin? skin, SkinTextureCache? textures)
    : SkinCompositionView(skin, textures, new Vector2(480, 270), fill: false)
{
    private readonly InstalledLazerSkin? selectedSkin = skin;

    protected override IEnumerable<Drawable> Compose()
    {
        const float diameter = 66;
        Colour4 first = Assets.Combo(0);
        Colour4 second = Assets.Combo(1);
        var drawables = new List<Drawable> { playfieldBackdrop(first) };

        // Lazer resolves only menu backgrounds as previews; stable may fall back to other art, which is not a backdrop.
        if (selectedSkin is { HasPreview: true } withPreview
            && (withPreview.Origin == InstalledSkinOrigin.Lazer || System.IO.Path.GetFileName(withPreview.PreviewPath).StartsWith("menu-background", StringComparison.OrdinalIgnoreCase)))
        {
            drawables.Add(new AimModLocalArtwork(withPreview.PreviewPath, cropForPanel: false) { Colour = new Colour4(0.3f, 0.3f, 0.3f, 1) });
        }

        drawables.AddRange(healthBar());

        // Slider from the first circle along a gentle curve; the ball and cursor ride it.
        Vector2[] curve = Enumerable.Range(0, 24).Select(i =>
        {
            float t = i / 23f;
            return new Vector2(92 + 170 * t, 172 - 70 * MathF.Sin(t * MathF.PI * 0.85f));
        }).ToArray();
        Colour4 border = Assets.SliderBorder ?? Colour4.White;
        Colour4 track = Assets.SliderTrack ?? first.Darken(0.9f);
        drawables.Add(sliderPath(curve, diameter * 0.46f, border));
        drawables.Add(sliderPath(curve, diameter * 0.46f * 0.84f, track.Opacity(0.92f)));
        drawables.AddRange(hitCircle(curve[^1], diameter, first, -1, alpha: 0.9f));
        drawables.AddRange(hitCircle(curve[0], diameter, first, 1, baseName: "sliderstartcircle"));
        Vector2 ball = curve[15];
        drawables.Add(element("sliderfollowcircle", ball, diameter, alpha: 0.9f) ?? Empty());
        drawables.Add(element("sliderb0", ball, diameter) ?? element("sliderb", ball, diameter) ?? Empty());

        drawables.AddRange(hitCircle(new Vector2(342, 172), diameter, first, 2));
        drawables.Add(approachCircle(new Vector2(342, 172), diameter, first, 1.55f));
        drawables.AddRange(hitCircle(new Vector2(414, 122), diameter, second, 1, alpha: 0.55f));

        for (int i = 1; i <= 4; i++)
        {
            Vector2 trail = Vector2.Lerp(ball, curve[8], i / 4f);
            drawables.Add(element("cursortrail", trail, diameter, alpha: 1 - i * 0.2f) ?? Empty());
        }
        drawables.Add(cursor(ball + new Vector2(6, 4), diameter));

        drawables.AddRange(judgements(diameter));
        drawables.Add(numberSprite(Assets.ScorePrefix, "00847210", new Vector2(472, 10), diameter * 0.72f, origin: Anchor.TopRight) ?? scoreText("00847210", new Vector2(472, 10), Anchor.TopRight));
        drawables.Add(numberSprite(Assets.ScorePrefix, "248x", new Vector2(10, 262), diameter * 0.85f, origin: Anchor.BottomLeft) ?? scoreText("248x", new Vector2(10, 262), Anchor.BottomLeft));
        return drawables;
    }

    private IEnumerable<Drawable> healthBar()
    {
        SkinElementFile? backgroundFile = Assets.Find("scorebar-bg");
        Texture? background = texture("scorebar-bg");
        Texture? colour = texture("scorebar-colour");
        if (backgroundFile is null || background is null)
        {
            yield return new Container
            {
                Position = new Vector2(10, 10),
                Size = new Vector2(190, 7),
                Masking = true,
                CornerRadius = 3.5f,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = Colour4.Black.Opacity(0.5f) },
                    new Box { RelativeSizeAxes = Axes.Both, Width = 0.72f, Colour = Assets.Combo(0) },
                },
            };
            yield break;
        }
        // HUD art is authored for a 768px-tall screen; the virtual canvas is 270 tall.
        float scale = 270f / 768 / (backgroundFile.HighResolution ? 2 : 1);
        yield return new Sprite { Texture = background, Position = Vector2.Zero, Size = new Vector2(background.Width, background.Height) * scale };
        if (colour is not null)
        {
            SkinElementFile colourFile = Assets.Find("scorebar-colour")!;
            float colourScale = 270f / 768 / (colourFile.HighResolution ? 2 : 1);
            yield return new Container
            {
                Position = new Vector2(5, 5.5f) * (270f / 768) * 2,
                Size = new Vector2(colour.Width * colourScale * 0.72f, colour.Height * colourScale),
                Masking = true,
                Child = new Sprite { Texture = colour, Size = new Vector2(colour.Width, colour.Height) * colourScale },
            };
        }
    }

    private IEnumerable<Drawable> judgements(float diameter)
    {
        (string Name, string Label, Colour4 Colour)[] results =
        [
            ("hit300", "300", AimModPalette.Cyan),
            ("hit100", "100", AimModPalette.Success),
            ("hit50", "50", AimModPalette.Yellow),
            ("hit0", "×", AimModPalette.Danger),
        ];
        for (int i = 0; i < results.Length; i++)
        {
            var position = new Vector2(128 + i * 58, 246);
            yield return element(results[i].Name, position, diameter * 0.62f) ?? new OsuSpriteText
            {
                Origin = Anchor.Centre,
                Position = position,
                Text = results[i].Label,
                Font = new FontUsage(size: 17, weight: "Bold"),
                Colour = results[i].Colour,
            };
        }
    }

    private static Drawable scoreText(string text, Vector2 position, Anchor origin) => new OsuSpriteText
    {
        Origin = origin,
        Position = position,
        Text = text,
        Font = new FontUsage(size: 20, weight: "Bold"),
        Colour = Colour4.White,
    };

    private static Drawable sliderPath(Vector2[] vertices, float radius, Colour4 colour)
    {
        var path = new SmoothPath { PathRadius = radius, Colour = colour };
        path.Vertices = vertices;
        // Vertices are drawn relative to the path's bounding box; shift it so they land at canvas coordinates.
        path.Position = -path.PositionInBoundingBox(Vector2.Zero);
        return path;
    }
}
