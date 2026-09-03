using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.Visuals;

public partial class AimModLoadingOverlay : Container
{
    private readonly LoadingLayer loadingLayer;
    private readonly SpriteText title;
    private readonly SpriteText detail;
    private readonly ProgressBar progressBar;
    private bool indeterminate;

    public AimModLoadingOverlay()
    {
        RelativeSizeAxes = Axes.Both;
        Depth = -1000;
        Alpha = 0;

        Children = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = AimModPalette.Canvas,
                Alpha = 0.94f,
            },
            loadingLayer = new LoadingLayer(dimBackground: false, withBox: false)
            {
                BlockNonPositionalInput = true,
            },
            new Container
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Width = 560,
                Height = 150,
                Y = 82,
                Children = new Drawable[]
                {
                    title = new SpriteText
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Font = new FontUsage(size: 24, weight: "Bold"),
                        Colour = AimModPalette.Text,
                    },
                    detail = new SpriteText
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Y = 42,
                        Font = new FontUsage(size: 13, weight: "SemiBold"),
                        Colour = AimModPalette.Muted,
                    },
                    progressBar = new ProgressBar(allowSeek: false)
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Y = 78,
                        Width = 420,
                        Height = 7,
                        FillColour = AimModPalette.Pink,
                        BackgroundColour = AimModPalette.PanelRaised,
                        EndTime = 1,
                    },
                },
            },
        };
    }

    public void ShowLoading(string heading, string state, int? completed = null, int? total = null)
    {
        title.Text = heading;
        detail.Text = state;
        indeterminate = completed is null || total is null || total <= 0;
        if (!indeterminate)
            progressBar.CurrentTime = Math.Clamp((double)completed!.Value / total!.Value, 0, 1);
        loadingLayer.Show();
        this.FadeIn(180, Easing.OutQuint);
    }

    public void SetProgress(string state, int completed, int total)
    {
        detail.Text = state;
        indeterminate = total <= 0;
        progressBar.CurrentTime = total <= 0 ? 0 : Math.Clamp((double)completed / total, 0, 1);
    }

    public void HideLoading()
    {
        indeterminate = false;
        loadingLayer.Hide();
        this.FadeOut(180, Easing.OutQuint);
    }

    protected override void Update()
    {
        base.Update();
        if (indeterminate)
            progressBar.CurrentTime = 0.08 + 0.84 * (0.5 + 0.5 * Math.Sin(Time.Current / 520));
    }
}
