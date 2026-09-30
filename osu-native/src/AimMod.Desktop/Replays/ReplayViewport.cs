using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osuTK;

namespace AimMod.Desktop.Replays;

/// <summary>
/// Frames the embedded official replay player. The game surface keeps a 16:9 aspect, is laid out at
/// no less than osu!'s 1024x768 reference resolution and is scaled uniformly to fit the frame, centred
/// with letterbox bars. The playfield and HUD are therefore always fully visible, never cropped.
/// </summary>
public partial class ReplayViewport : CompositeDrawable
{
    /// <summary>Aspect of the game surface. osu!'s gameplay HUD is laid out for widescreen.</summary>
    public const float SurfaceAspect = 16f / 9f;

    /// <summary>The minimum draw size osu! lays gameplay and HUD out for (see osu!'s scaling container).</summary>
    public static readonly Vector2 ReferenceSize = new(1024, 768);

    private readonly Container surfaceFrame;
    private readonly DrawSizePreservingFillContainer surface;
    private Vector2 laidOutSize = new(-1);

    /// <summary>Container sized to the visible game surface, in osu!'s reference coordinate space.</summary>
    internal DrawSizePreservingFillContainer Surface => surface;

    /// <summary>The letterboxed game surface within the frame.</summary>
    internal Drawable SurfaceFrame => surfaceFrame;

    public ReplayViewport(Drawable gameSurface, Drawable statusOverlay)
    {
        InternalChild = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Masking = true,
            CornerRadius = Visuals.AimModVisualStyle.CardRadius,
            BorderThickness = 1,
            BorderColour = AimModPalette.Border,
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = Colour4.Black },
                surfaceFrame = new Container
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    // The game surface itself stays inside its box; nothing osu! draws can spill into the letterbox.
                    Masking = true,
                    Child = surface = new DrawSizePreservingFillContainer
                    {
                        TargetDrawSize = ReferenceSize,
                        Strategy = DrawSizePreservationStrategy.Minimum,
                        Child = gameSurface,
                    },
                },
                statusOverlay,
            },
        };
    }

    protected override void Update()
    {
        base.Update();
        Vector2 size = Fit(DrawWidth, DrawHeight);
        if (size != laidOutSize)
        {
            laidOutSize = size;
            surfaceFrame.Size = size;
        }
    }

    /// <summary>Largest surface of <see cref="SurfaceAspect"/> that fits within the given bounds.</summary>
    public static Vector2 Fit(float availableWidth, float availableHeight)
    {
        float width = Math.Max(0, availableWidth);
        float height = Math.Max(0, availableHeight);
        if (width <= 0 || height <= 0)
            return Vector2.Zero;

        return width / height > SurfaceAspect
            ? new Vector2(height * SurfaceAspect, height)
            : new Vector2(width, width / SurfaceAspect);
    }
}

/// <summary>Vertical arrangement of the playback column: viewport with the transport attached below.</summary>
internal readonly record struct ReplayPlaybackLayout(Vector2 ViewportSize, float TransportY)
{
    public const float TransportHeight = 94;
    public const float Gap = 8;

    public float Height => TransportY + TransportHeight;

    /// <param name="width">Playback column width.</param>
    /// <param name="height">Height available for the viewport and transport together.</param>
    public static ReplayPlaybackLayout Calculate(float width, float height)
    {
        float viewportHeight = Math.Max(0, Math.Min(Math.Max(0, width) / ReplayViewport.SurfaceAspect, height - TransportHeight - Gap));
        return new ReplayPlaybackLayout(new Vector2(Math.Max(0, width), viewportHeight), viewportHeight + Gap);
    }
}
