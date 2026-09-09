using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;
using osuTK;

namespace AimMod.Desktop.Visuals;

public enum WorkspaceIllustrationKind { Coaching, Trainers, Replays, Targets, Statistics, Beatmaps, Skins, Settings }

/// <summary>Symbolic feature illustrations. Motion runs only while the containing choice is hovered.</summary>
public partial class AimModWorkspaceIllustration(WorkspaceIllustrationKind kind, Func<bool> active) : Container
{
    private readonly Container drawing = new() { Size = new(144, 96), Anchor = Anchor.Centre, Origin = Anchor.Centre };
    private Drawable? moving;
    private double phase;
    private Vector2[] motionPath = [];

    public AimModWorkspaceIllustration(WorkspaceIllustrationKind kind) : this(kind, () => false) { }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        Child = drawing;
        build();
    }

    protected override void Update()
    {
        base.Update();
        drawing.Scale = new(Math.Min(DrawWidth / 144, DrawHeight / 96));
        if (moving is null) return;
        bool hovered = active();
        moving.Alpha = hovered ? 1 : 0;
        if (hovered) phase += Time.Elapsed / 1800;
        // Follow the numbered route in order. Reset at the end instead of reversing through it.
        float step = (float)(phase % 1) * (motionPath.Length - 1);
        int segment = Math.Min((int)step, motionPath.Length - 2);
        moving.Position = Vector2.Lerp(motionPath[segment], motionPath[segment + 1], step - segment);
    }

    private void build()
    {
        panel(0, 0, 144, 96, AimModPalette.Canvas);
        switch (kind)
        {
            case WorkspaceIllustrationKind.Coaching:
                // Select notes from the source map, then separate tapping and movement.
                text("SECTION", 10, 8, 9, AimModPalette.Muted);
                panel(44, 24, 66, 30, AimModPalette.AccentMuted);
                line(new(12, 39), new(132, 39), AimModPalette.Muted.Opacity(.3f));
                for (int i = 0; i < 6; i++) ring(16 + i * 22, 39, 14, $"{i + 1}");
                line(new(78, 55), new(78, 63), AimModPalette.Accent);
                line(new(78, 63), new(37, 69), AimModPalette.Accent.Opacity(.4f));
                line(new(78, 63), new(111, 69), AimModPalette.Accent.Opacity(.4f));
                icon(FontAwesome.Solid.Keyboard, 17, 74, 14); text("TAP", 36, 76, 10, AimModPalette.Text);
                icon(FontAwesome.Solid.Crosshairs, 84, 73, 14); text("AIM", 104, 76, 10, AimModPalette.Text);
                break;
            case WorkspaceIllustrationKind.Trainers:
                line(new(25,30),new(70,19),AimModPalette.Accent.Opacity(.3f));
                line(new(70,19),new(111,48),AimModPalette.Accent.Opacity(.3f));
                ring(25,30,24,"1"); ring(70,19,20,"2"); ring(111,48,24,"3");
                key(25,68,"Z"); key(59,68,"X");
                animate(dot(25,30,5,AimModPalette.Text),new(25,30),new(70,19),new(111,48));
                break;
            case WorkspaceIllustrationKind.Replays:
                panel(8, 7, 128, 62, AimModPalette.Panel);
                line(new(28,31),new(72,21),AimModPalette.Accent.Opacity(.3f));
                line(new(72,21),new(116,47),AimModPalette.Accent.Opacity(.3f));
                ring(28,31,22,"1"); ring(72,21,18,"2"); ring(116,47,22,"3");
                icon(FontAwesome.Solid.Play,12,79,10);
                box(31,82,99,3,AimModPalette.Muted.Opacity(.3f));
                box(31,82,45,3,AimModPalette.Accent);
                dot(76,83,7,AimModPalette.Text);
                // A stationary player thumbnail avoids implying that an unrelated scrubber tracks this cursor.
                break;
            case WorkspaceIllustrationKind.Targets:
                ring(47, 43, 58); ring(47, 43, 39);
                centeredText("PP", 47, 43, 19, AimModPalette.Text);
                icon(FontAwesome.Solid.Crosshairs, 93, 17, 17);
                icon(FontAwesome.Solid.Check, 94, 47, 15);
                centeredText("SKILL FIT", 72, 85, 10, AimModPalette.Accent);
                break;
            case WorkspaceIllustrationKind.Beatmaps:
                for(int i=0;i<3;i++)
                {
                    float y=8+i*28;
                    panel(9,y,126,23,i==0 ? AimModPalette.AccentMuted : AimModPalette.Panel);
                    panel(13,y+4,17,15,AimModPalette.Accent.Opacity(.18f+i*.08f));
                    icon(FontAwesome.Solid.Music,18,y+8,7);
                    box(37,y+6,51+i*7,3,AimModPalette.Text.Opacity(.7f));
                    box(37,y+13,29,2,AimModPalette.Muted.Opacity(.5f));
                    icon(FontAwesome.Solid.Download,117,y+8,8);
                }
                break;
            case WorkspaceIllustrationKind.Statistics:
                for(int i=0;i<3;i++) box(12,20+i*23,121,1,AimModPalette.Muted.Opacity(.13f));
                Vector2[] points=[new(14,74),new(36,59),new(56,64),new(78,43),new(99,47),new(127,21)];
                for(int i=0;i<points.Length;i++)
                {
                    if(i>0) line(points[i-1],points[i],AimModPalette.Accent,2.5f);
                    box(points[i].X-4,points[i].Y+6,8,Math.Max(1,84-points[i].Y-6),AimModPalette.Accent.Opacity(.13f));
                    dot(points[i].X,points[i].Y,5,AimModPalette.Accent);
                }
                text("PLAYS", 13, 84, 8, AimModPalette.Muted);
                break;
            case WorkspaceIllustrationKind.Skins:
                // Compare two treatments of the same hit object. Draw approach rings first.
                ring(39, 39, 53);
                dot(39, 39, 40, AimModPalette.AccentMuted);
                centeredText("1", 39, 39, 13, AimModPalette.Text, "Bold");
                ring(103, 39, 53);
                ring(103, 39, 38, "1");
                panel(14, 74, 52, 14, AimModPalette.AccentMuted);
                centeredText("FILLED", 40, 81, 9, AimModPalette.Accent);
                panel(78, 74, 52, 14, AimModPalette.PanelRaised);
                centeredText("RING", 104, 81, 9, AimModPalette.Text);
                break;
            case WorkspaceIllustrationKind.Settings:
                for(int i=0;i<3;i++)
                {
                    box(15,17+i*19,114,3,AimModPalette.Muted.Opacity(.25f));
                    box(15,17+i*19,30+i*19,3,AimModPalette.Accent.Opacity(.6f));
                    dot(45+i*19,18+i*19,10,AimModPalette.Accent);
                }
                key(24,74,"Z"); key(58,74,"X"); icon(FontAwesome.Solid.VolumeUp,106,79,13);
                break;
        }
    }

    private void animate(Drawable drawable, params Vector2[] path) { moving=drawable; motionPath=path; }
    private void text(string value, float x, float y, float size, Colour4 colour) => drawing.Add(new OsuSpriteText
        { Text=value,Position=new(x,y),Font=new FontUsage(size:size,weight:"SemiBold"),Colour=colour });
    private void centeredText(string value, float x, float y, float size, Colour4 colour, string weight = "SemiBold") => drawing.Add(new OsuSpriteText
    {
        Text = value,
        Position = new(x, y),
        Origin = Anchor.Centre,
        // Use the visible characters rather than the font's reserved line height.
        UseFullGlyphHeight = false,
        Font = new FontUsage(size: size, weight: weight),
        Colour = colour
    });
    private void box(float x,float y,float width,float height,Colour4 colour) => drawing.Add(new Box { Position=new(x,y), Size=new(width,height), Colour=colour });
    private void panel(float x,float y,float width,float height,Colour4 colour) => drawing.Add(new Container { Position=new(x,y), Size=new(width,height), Masking=true, CornerRadius=5, Child=new Box { RelativeSizeAxes=Axes.Both,Colour=colour } });
    private Circle dot(float x,float y,float size,Colour4 colour) { var c=new Circle { Position=new(x,y),Origin=Anchor.Centre,Size=new(size),Colour=colour }; drawing.Add(c); return c; }
    private void ring(float x,float y,float diameter,string? caption=null)
    {
        dot(x,y,diameter,AimModPalette.Accent.Opacity(.8f)); dot(x,y,diameter-3,AimModPalette.Canvas);
        if(caption is not null) centeredText(caption, x, y, 13, AimModPalette.Text, "Bold");
    }
    private void key(float x,float y,string caption)
    {
        panel(x,y,25,17,AimModPalette.PanelRaised);
        centeredText(caption, x + 12.5f, y + 8.5f, 10, AimModPalette.Accent, "Bold");
    }
    private void icon(IconUsage icon,float x,float y,float size) => drawing.Add(new SpriteIcon { Icon=icon,Position=new(x,y),Size=new(size),Colour=AimModPalette.Accent });
    private void line(Vector2 from,Vector2 to,Colour4 colour,float thickness=1.5f)
    {
        var delta=to-from;
        drawing.Add(new Box { Position=from,Origin=Anchor.CentreLeft,Width=delta.Length,Height=thickness,Rotation=MathF.Atan2(delta.Y,delta.X)*180/MathF.PI,EdgeSmoothness=new(1),Colour=colour });
    }
}
