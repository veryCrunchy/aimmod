using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osuTK;

namespace AimMod.Desktop.Visuals;

public sealed record AimModSkillMetric(string Label, double Value);

/// <summary>
/// Compact five-axis demand chart for beatmap and coaching surfaces.
/// </summary>
public sealed partial class AimModSkillRadar : CompositeDrawable
{
    private const float plot_size = 142;
    private const float plot_top = 26;

    public AimModSkillRadar(IReadOnlyList<AimModSkillMetric> metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        if (metrics.Count != 5)
            throw new ArgumentException("A skill radar requires exactly five metrics.", nameof(metrics));

        Width = 300;
        Height = 188;

        float[] values = metrics.Select(metric => (float)Math.Clamp(metric.Value, 0, 1)).ToArray();
        InternalChildren = new Drawable[]
        {
            new RadarMesh(Array.Empty<float>(), drawGrid: true)
            {
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
                Y = plot_top,
                Size = new(plot_size),
                Colour = AimModPalette.Border,
            },
            new RadarMesh(values, drawGrid: false)
            {
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
                Y = plot_top,
                Size = new(plot_size),
                Colour = AimModPalette.Pink,
            },
            metricLabel(metrics[0], Anchor.TopCentre, Anchor.TopCentre, new Vector2(0, 0), TextAlignment.Centre),
            metricLabel(metrics[1], Anchor.TopRight, Anchor.TopRight, new Vector2(0, 34), TextAlignment.Right),
            metricLabel(metrics[2], Anchor.BottomRight, Anchor.BottomRight, new Vector2(0, -3), TextAlignment.Right),
            metricLabel(metrics[3], Anchor.BottomLeft, Anchor.BottomLeft, new Vector2(0, -3), TextAlignment.Left),
            metricLabel(metrics[4], Anchor.TopLeft, Anchor.TopLeft, new Vector2(0, 34), TextAlignment.Left),
        };
    }

    private static Drawable metricLabel(AimModSkillMetric metric, Anchor anchor, Anchor origin, Vector2 position, TextAlignment alignment)
    {
        float width = anchor is Anchor.TopCentre ? 92 : 78;
        return new FillFlowContainer
        {
            Anchor = anchor,
            Origin = origin,
            Position = position,
            Width = width,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, 1),
            Children = new Drawable[]
            {
                new SpriteText
                {
                    Anchor = alignment == TextAlignment.Centre ? Anchor.TopCentre : alignment == TextAlignment.Right ? Anchor.TopRight : Anchor.TopLeft,
                    Origin = alignment == TextAlignment.Centre ? Anchor.TopCentre : alignment == TextAlignment.Right ? Anchor.TopRight : Anchor.TopLeft,
                    Text = metric.Label,
                    Font = new FontUsage(size: 9, weight: "SemiBold"),
                    Colour = AimModPalette.Muted,
                },
                new SpriteText
                {
                    Anchor = alignment == TextAlignment.Centre ? Anchor.TopCentre : alignment == TextAlignment.Right ? Anchor.TopRight : Anchor.TopLeft,
                    Origin = alignment == TextAlignment.Centre ? Anchor.TopCentre : alignment == TextAlignment.Right ? Anchor.TopRight : Anchor.TopLeft,
                    Text = $"{Math.Clamp(metric.Value, 0, 1) * 10:0.0}",
                    Font = new FontUsage(size: 12, weight: "Bold"),
                    Colour = AimModPalette.Text,
                },
            },
        };
    }

    private enum TextAlignment
    {
        Left,
        Centre,
        Right,
    }

    private sealed partial class RadarMesh : Drawable
    {
        private readonly float[] values;
        private readonly bool drawGrid;
        private Texture texture = null!;
        private IShader shader = null!;

        public RadarMesh(float[] values, bool drawGrid)
        {
            this.values = values;
            this.drawGrid = drawGrid;
        }

        [BackgroundDependencyLoader]
        private void load(IRenderer renderer, ShaderManager shaders)
        {
            texture = renderer.WhitePixel;
            shader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.TEXTURE);
        }

        protected override DrawNode CreateDrawNode() => new RadarMeshDrawNode(this);

        private sealed class RadarMeshDrawNode : DrawNode
        {
            private readonly RadarMesh source;
            private Texture texture = null!;
            private IShader shader = null!;
            private Vector2 drawSize;
            private float[] values = Array.Empty<float>();
            private bool drawGrid;

            public RadarMeshDrawNode(RadarMesh source)
                : base(source)
            {
                this.source = source;
            }

            public override void ApplyState()
            {
                base.ApplyState();
                texture = source.texture;
                shader = source.shader;
                drawSize = source.DrawSize;
                values = (float[])source.values.Clone();
                drawGrid = source.drawGrid;
            }

            protected override void Draw(IRenderer renderer)
            {
                base.Draw(renderer);
                shader.Bind();

                Vector2 centre = drawSize / 2;
                float radius = MathF.Min(drawSize.X, drawSize.Y) / 2 - 3;
                Vector2[] outer = points(centre, radius, Enumerable.Repeat(1f, 5).ToArray());

                if (drawGrid)
                {
                    foreach (float ring in new[] { 0.25f, 0.5f, 0.75f, 1f })
                    {
                        Vector2[] ringPoints = points(centre, radius, Enumerable.Repeat(ring, 5).ToArray());
                        for (int i = 0; i < ringPoints.Length; i++)
                            drawLine(renderer, ringPoints[i], ringPoints[(i + 1) % ringPoints.Length], ring == 1 ? 1.25f : 0.75f, ring == 1 ? 0.7f : 0.35f);
                    }

                    foreach (Vector2 point in outer)
                        drawLine(renderer, centre, point, 0.75f, 0.45f);
                }
                else if (values.Length == 5)
                {
                    Vector2[] area = points(centre, radius, values);
                    for (int i = 0; i < area.Length; i++)
                        drawTriangle(renderer, centre, area[i], area[(i + 1) % area.Length]);
                    for (int i = 0; i < area.Length; i++)
                        drawLine(renderer, area[i], area[(i + 1) % area.Length], 2f, 1f);
                }

                shader.Unbind();
            }

            private Vector2[] points(Vector2 centre, float radius, IReadOnlyList<float> levels)
            {
                var result = new Vector2[5];
                for (int i = 0; i < result.Length; i++)
                {
                    float angle = MathF.PI * (-0.5f + i * 0.4f);
                    result[i] = centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius * Math.Clamp(levels[i], 0, 1);
                }
                return result;
            }

            private void drawTriangle(IRenderer renderer, Vector2 centre, Vector2 first, Vector2 second)
            {
                renderer.DrawQuad(texture, new Quad(
                    transform(centre),
                    transform(first),
                    transform(second),
                    transform(second)), DrawColourInfo.Colour.MultiplyAlpha(0.18f));
            }

            private void drawLine(IRenderer renderer, Vector2 start, Vector2 end, float thickness, float alpha)
            {
                Vector2 direction = end - start;
                if (direction.LengthSquared <= 0.001f)
                    return;

                direction.Normalize();
                Vector2 perpendicular = new(-direction.Y, direction.X);
                Vector2 offset = perpendicular * thickness / 2;
                renderer.DrawQuad(texture, new Quad(
                    transform(start + offset),
                    transform(end + offset),
                    transform(start - offset),
                    transform(end - offset)), DrawColourInfo.Colour.MultiplyAlpha(alpha));
            }

            private Vector2 transform(Vector2 point) => Vector2Extensions.Transform(point, DrawInfo.Matrix);
        }
    }
}
