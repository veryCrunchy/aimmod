using System.Diagnostics;
using AimMod.Desktop.LocalLibrary;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osuTK.Input;

namespace AimMod.Desktop.Visuals;

/// <summary>
/// Modal progress for work that must finish before the covered area is usable. Prefer
/// <see cref="AimModInlineStatus"/> for searches and refreshes. A cancellable request shows
/// a Cancel button and answers Escape.
/// </summary>
public partial class AimModLoadingOverlay : Container
{
    private const float max_panel_width = 560;

    private readonly FillFlowContainer statusPanel;
    private readonly LoadingSpinner spinner;
    private readonly TruncatingSpriteText title;
    private readonly TruncatingSpriteText detail;
    private readonly ProgressBar progressBar;
    private readonly AimModInteractiveSurface cancelButton;
    private bool indeterminate;
    private bool loading;
    private readonly Stopwatch elapsed = new();
    private readonly TruncatingSpriteText timing;
    private Func<LocalLibraryProgress?>? readProgress;
    private string currentState = string.Empty;
    private Action? cancelRequested;
    private AimModLayout.ChangeTracker<float> widthTracker;
    private AimModLayout.ChangeTracker<int> timingTracker;
    private AimModLayout.ChangeTracker<(string, int, int)> progressTracker;
    public string? LoadingHint { get; set; }
    private readonly TruncatingSpriteText hint;

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
                Alpha = 0.88f,
            },
            new InputBlocker { RelativeSizeAxes = Axes.Both },
            statusPanel = new FillFlowContainer
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                AutoSizeAxes = Axes.Y,
                Width = max_panel_width,
                Direction = FillDirection.Vertical,
                Spacing = new(AimModVisualStyle.RowSpacing),
                Children = new Drawable[]
                {
                    spinner = new LoadingSpinner
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Size = new(52),
                    },
                    title = new TruncatingSpriteText
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Font = AimModVisualStyle.HeadingFont,
                        Colour = AimModPalette.Text,
                    },
                    detail = new TruncatingSpriteText
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Font = new FontUsage(size: 12, weight: "SemiBold"),
                        Colour = AimModPalette.Muted,
                    },
                    progressBar = new ProgressBar(allowSeek: false)
                    {
                        RelativeSizeAxes = Axes.None,
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Width = 420,
                        Height = 7,
                        FillColour = AimModPalette.Accent,
                        BackgroundColour = AimModPalette.PanelRaised,
                        EndTime = 1,
                    },
                    timing = new TruncatingSpriteText
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Font = new FontUsage(size: 12),
                        Colour = AimModPalette.Muted,
                    },
                    hint = new TruncatingSpriteText
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Font = new FontUsage(size: 12),
                        Colour = AimModPalette.Muted,
                    },
                    cancelButton = new AimModInteractiveSurface
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Size = new(120, AimModVisualStyle.CompactControlHeight),
                        BackgroundColour = AimModPalette.PanelRaised,
                        Alpha = 0,
                        Child = new OsuSpriteText
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            Text = "Cancel",
                            Font = AimModVisualStyle.BodyStrongFont,
                            Colour = AimModPalette.Text,
                        },
                    },
                },
            },
        };

        cancelButton.Action = cancel;
    }

    public bool IsLoading => loading;

    public void ShowLoading(string heading, string state, int? completed = null, int? total = null,
        Func<LocalLibraryProgress?>? progress = null, Action? onCancel = null)
    {
        title.Text = heading;
        currentState = state;
        readProgress = progress;
        setDetail(state);
        progressTracker.Reset();
        cancelRequested = onCancel;
        cancelButton.Alpha = onCancel is null ? 0 : 1;
        indeterminate = completed is null || total is null || total <= 0;
        if (!indeterminate)
            progressBar.CurrentTime = Math.Clamp((double)completed!.Value / total!.Value, 0, 1);
        if (loading) return;
        loading = true;
        elapsed.Restart();
        timingTracker.Reset();
        this.FinishTransforms();
        spinner.Show();
        this.FadeTo(0.01f, 50)
            .Then()
            .FadeIn(LoadingSpinner.TRANSITION_DURATION, Easing.OutQuint);
    }

    public void SetProgress(string state, int completed, int total)
    {
        if (progressTracker.Update((state, completed, total)))
        {
            currentState = total > 0 ? $"{state}  {completed:N0} / {total:N0}" : state;
            setDetail(currentState);
        }

        indeterminate = total <= 0;
        if (!indeterminate)
            progressBar.CurrentTime = Math.Clamp((double)completed / total, 0, 1);
    }

    public void HideLoading()
    {
        loading = false;
        elapsed.Stop();
        readProgress = null;
        cancelRequested = null;
        cancelButton.Alpha = 0;
        indeterminate = false;
        this.FinishTransforms();
        this.FadeOut(LoadingSpinner.TRANSITION_DURATION / 2, Easing.OutQuint);
        spinner.Hide();
    }

    private void cancel()
    {
        Action? callback = cancelRequested;
        if (callback is null)
            return;
        HideLoading();
        callback();
    }

    public override bool HandleNonPositionalInput => loading && cancelRequested is not null;

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (loading && cancelRequested is not null && e.Key == Key.Escape)
        {
            cancel();
            return true;
        }

        return base.OnKeyDown(e);
    }

    private void setDetail(string value)
    {
        if (detail.Text != value)
            detail.Text = value;
    }

    protected override void Update()
    {
        base.Update();
        float panelWidth = AimModLayout.ClampPanelWidth(DrawWidth, max_panel_width);
        if (widthTracker.Update(panelWidth))
        {
            statusPanel.Width = panelWidth;
            title.MaxWidth = panelWidth;
            detail.MaxWidth = panelWidth;
            timing.MaxWidth = panelWidth;
            hint.MaxWidth = panelWidth;
            progressBar.Width = Math.Max(1, Math.Min(420, panelWidth - 48));
        }

        string hintText = LoadingHint ?? string.Empty;
        if (hint.Text != hintText)
            hint.Text = hintText;

        if (!loading)
            return;

        if (readProgress?.Invoke() is { } progress)
            SetProgress(progress.State, progress.Completed, progress.Total);

        int seconds = (int)elapsed.Elapsed.TotalSeconds;
        if (timingTracker.Update(seconds))
        {
            timing.Text = seconds < 15 || !string.IsNullOrEmpty(LoadingHint)
                ? $"Elapsed {elapsed.Elapsed:mm\\:ss}"
                : $"Elapsed {elapsed.Elapsed:mm\\:ss}  -  Taking longer than usual";
        }

        if (indeterminate)
            progressBar.CurrentTime = 0.08 + 0.84 * (0.5 + 0.5 * Math.Sin(Time.Current / 520));
    }

    private partial class InputBlocker : ClickableContainer
    {
        protected override bool OnClick(ClickEvent e) => true;
    }
}
