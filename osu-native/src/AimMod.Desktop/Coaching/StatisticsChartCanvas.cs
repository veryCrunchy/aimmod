using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Textures;
using osuTK;

namespace AimMod.Desktop.Coaching;

/// <summary>
/// Draws many chart marks (grid, bars, dots, lines) in one draw node, so a chart with thousands of plays
/// stays cheap. All coordinates are in the canvas' local space.
/// </summary>
public sealed partial class StatisticsChartCanvas : Drawable
{
    public readonly record struct Line(Vector2[] Points, float Thickness, Colour4 Colour, float Dash = 0);

    public readonly record struct Dot(Vector2 Position, float Radius, Colour4 Colour);

    public readonly record struct Bar(RectangleF Bounds, Colour4 Colour);

    /// <summary>A filled region between two polylines with matching X positions.</summary>
    public readonly record struct Band(Vector2[] Upper, Vector2[] Lower, Colour4 Colour);

    private Texture texture = null!;
    private IShader shader = null!;
    private Line[] lines = [];
    private Dot[] dots = [];
    private Bar[] bars = [];
    private Band[] bands = [];

    public StatisticsChartCanvas()
    {
        RelativeSizeAxes = Axes.Both;
    }

    [BackgroundDependencyLoader]
    private void load(IRenderer renderer, ShaderManager shaders)
    {
        texture = renderer.WhitePixel;
        shader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.TEXTURE);
    }

    /// <summary>Replaces every mark. Bars draw first, then dots, then lines on top.</summary>
    public void Set(IEnumerable<Bar>? bars = null, IEnumerable<Dot>? dots = null, IEnumerable<Line>? lines = null, IEnumerable<Band>? bands = null)
    {
        this.bands = bands?.ToArray() ?? [];
        this.bars = bars?.ToArray() ?? [];
        this.dots = dots?.ToArray() ?? [];
        this.lines = lines?.ToArray() ?? [];
        Invalidate(Invalidation.DrawNode);
    }

    public int DotCount => dots.Length;

    public int LineCount => lines.Length;

    public int BarCount => bars.Length;

    protected override DrawNode CreateDrawNode() => new CanvasDrawNode(this);

    private sealed class CanvasDrawNode : DrawNode
    {
        private static readonly Vector2[] octagon = Enumerable.Range(0, 8)
                                                              .Select(index => new Vector2(MathF.Cos(index * MathF.PI / 4), MathF.Sin(index * MathF.PI / 4)))
                                                              .ToArray();

        private readonly StatisticsChartCanvas source;
        private Texture texture = null!;
        private IShader shader = null!;
        private Line[] lines = [];
        private Dot[] dots = [];
        private Bar[] bars = [];
        private Band[] bands = [];
        private float alpha;

        public CanvasDrawNode(StatisticsChartCanvas source)
            : base(source)
        {
            this.source = source;
        }

        public override void ApplyState()
        {
            base.ApplyState();
            texture = source.texture;
            shader = source.shader;
            lines = source.lines;
            dots = source.dots;
            bars = source.bars;
            bands = source.bands;
            alpha = DrawColourInfo.Colour.MaxAlpha;
        }

        protected override void Draw(IRenderer renderer)
        {
            base.Draw(renderer);
            if (texture is null || shader is null)
                return;
            shader.Bind();

            foreach (Band band in bands)
            {
                ColourInfo fill = colour(band.Colour);
                for (int index = 1; index < Math.Min(band.Upper.Length, band.Lower.Length); index++)
                {
                    renderer.DrawQuad(texture, new Quad(
                        transform(band.Upper[index - 1]),
                        transform(band.Upper[index]),
                        transform(band.Lower[index - 1]),
                        transform(band.Lower[index])), fill);
                }
            }

            foreach (Bar bar in bars)
            {
                RectangleF bounds = bar.Bounds;
                renderer.DrawQuad(texture, new Quad(
                    transform(bounds.TopLeft),
                    transform(bounds.TopRight),
                    transform(bounds.BottomLeft),
                    transform(bounds.BottomRight)), colour(bar.Colour));
            }

            foreach (Dot dot in dots)
            {
                ColourInfo fill = colour(dot.Colour);
                Vector2 centre = transform(dot.Position);
                for (int index = 0; index < octagon.Length; index++)
                {
                    renderer.DrawTriangle(texture, new Triangle(
                        centre,
                        transform(dot.Position + octagon[index] * dot.Radius),
                        transform(dot.Position + octagon[(index + 1) % octagon.Length] * dot.Radius)), fill);
                }
            }

            foreach (Line line in lines)
            {
                for (int index = 1; index < line.Points.Length; index++)
                {
                    if (line.Dash > 0)
                        drawDashed(renderer, line.Points[index - 1], line.Points[index], line);
                    else
                        drawSegment(renderer, line.Points[index - 1], line.Points[index], line.Thickness, line.Colour);
                }
            }

            shader.Unbind();
        }

        private void drawDashed(IRenderer renderer, Vector2 start, Vector2 end, Line line)
        {
            float length = (end - start).Length;
            if (length <= 0.01f)
                return;
            Vector2 direction = (end - start) / length;
            for (float position = 0; position < length; position += line.Dash * 2)
                drawSegment(renderer, start + direction * position, start + direction * Math.Min(length, position + line.Dash), line.Thickness, line.Colour);
        }

        private void drawSegment(IRenderer renderer, Vector2 start, Vector2 end, float thickness, Colour4 lineColour)
        {
            Vector2 direction = end - start;
            if (direction.LengthSquared <= 0.0001f)
                return;
            direction.Normalize();
            // Extend each segment by half its thickness so joints in a polyline do not show gaps.
            Vector2 extension = direction * thickness / 2;
            Vector2 offset = new Vector2(-direction.Y, direction.X) * thickness / 2;
            start -= extension;
            end += extension;
            renderer.DrawQuad(texture, new Quad(
                transform(start + offset),
                transform(end + offset),
                transform(start - offset),
                transform(end - offset)), colour(lineColour));
        }

        private ColourInfo colour(Colour4 value) => ColourInfo.SingleColour(value.MultiplyAlpha(alpha));

        private Vector2 transform(Vector2 point) => Vector2Extensions.Transform(point, DrawInfo.Matrix);
    }
}
