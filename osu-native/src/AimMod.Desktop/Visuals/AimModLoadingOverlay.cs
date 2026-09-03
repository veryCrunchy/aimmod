using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.Visuals;

public partial class AimModLoadingOverlay : Container
{
    private readonly Container statusPanel;
    private readonly SpriteIcon spinner;
    private readonly TruncatingSpriteText title;
    private readonly TruncatingSpriteText detail;
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
                Alpha = 1,
            },
            new InputBlocker { RelativeSizeAxes = Axes.Both },
            statusPanel = new Container
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Width = 560,
                Height = 168,
                Children = new Drawable[]
                {
                    spinner = new SpriteIcon
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Icon = FontAwesome.Solid.CircleNotch,
                        Size = new(38),
                        Colour = AimModPalette.Pink,
                    },
                    title = new TruncatingSpriteText
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Y = 54,
                        Font = new FontUsage(size: 24, weight: "Bold"),
                        Colour = AimModPalette.Text,
                    },
                    detail = new TruncatingSpriteText
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Y = 92,
                        Font = new FontUsage(size: 13, weight: "SemiBold"),
                        Colour = AimModPalette.Muted,
                    },
                    progressBar = new ProgressBar(allowSeek: false)
                    {
                        RelativeSizeAxes = Axes.None,
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Y = 128,
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
        this.FadeOut(180, Easing.OutQuint);
    }

    protected override void Update()
    {
        base.Update();
        float panelWidth = Math.Clamp(DrawWidth - 64, 280, 560);
        statusPanel.Width = panelWidth;
        title.MaxWidth = panelWidth;
        detail.MaxWidth = panelWidth;
        progressBar.Width = Math.Max(220, panelWidth - 140);
        spinner.Rotation = (float)(Time.Current / 3.2);
        if (indeterminate)
            progressBar.CurrentTime = 0.08 + 0.84 * (0.5 + 0.5 * Math.Sin(Time.Current / 520));
    }

    private partial class InputBlocker : ClickableContainer
    {
        protected override bool OnClick(ClickEvent e) => true;
    }
}
