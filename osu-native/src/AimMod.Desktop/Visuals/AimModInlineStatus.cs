using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.Visuals;

/// <summary>
/// Non-blocking status strip for search and refresh flows: spinner and text, or a friendly error
/// with wrapped, expandable detail and Retry / Cancel actions. Collapses to zero height when hidden.
/// </summary>
public partial class AimModInlineStatus : CompositeDrawable
{
    private readonly Container strip;
    private readonly Box background;
    private readonly Box accent;
    private readonly LoadingSpinner spinner;
    private readonly OsuTextFlowContainer message;
    private readonly OsuTextFlowContainer detail;
    private readonly FillFlowContainer actions;
    private readonly InlineButton primaryButton;
    private readonly InlineButton detailsButton;
    private readonly InlineButton cancelButton;
    private string messageText = string.Empty;
    private bool detailExpanded;
    private bool hasDetail;
    private bool showing;
    private Action? retry;
    private Action? cancelAction;

    public AimModInlineStatus()
    {
        RelativeSizeAxes = Axes.X;
        AutoSizeAxes = Axes.Y;

        InternalChild = strip = new Container
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Masking = true,
            CornerRadius = AimModVisualStyle.ControlRadius,
            Alpha = 0,
            Children = new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = AimModPalette.Panel,
                },
                accent = new Box
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = 3,
                    Colour = AimModPalette.Accent,
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Padding = new MarginPadding { Left = 14, Right = 12, Vertical = 9 },
                    Spacing = new(AimModVisualStyle.RelatedSpacing),
                    Children = new Drawable[]
                    {
                        new Container
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Children = new Drawable[]
                            {
                                spinner = new LoadingSpinner
                                {
                                    Size = new(16),
                                    Anchor = Anchor.TopLeft,
                                    Origin = Anchor.TopLeft,
                                },
                                message = new OsuTextFlowContainer(t =>
                                {
                                    t.Font = AimModVisualStyle.BodyFont;
                                    t.Colour = AimModPalette.Text;
                                })
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Padding = new MarginPadding { Left = 26 },
                                },
                            },
                        },
                        detail = new OsuTextFlowContainer(t =>
                        {
                            t.Font = AimModVisualStyle.CaptionFont;
                            t.Colour = AimModPalette.Muted;
                        })
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Alpha = 0,
                        },
                        actions = new FillFlowContainer
                        {
                            AutoSizeAxes = Axes.Both,
                            Direction = FillDirection.Horizontal,
                            Spacing = new(AimModVisualStyle.RelatedSpacing),
                            Children = new Drawable[]
                            {
                                primaryButton = new InlineButton("Retry", () => retry?.Invoke()),
                                cancelButton = new InlineButton("Cancel", () => cancelAction?.Invoke()),
                                detailsButton = new InlineButton("Details", toggleDetail),
                            },
                        },
                    },
                },
            },
        };
        strip.AutoSizeAxes = Axes.None;
        strip.Height = 0;
    }

    public bool IsShowing => showing;

    public bool IsError { get; private set; }

    public string MessageText => messageText;

    public void ShowLoading(string text, Action? cancel = null)
    {
        cancelAction = cancel;
        retry = null;
        hasDetail = false;
        detailExpanded = false;
        IsError = false;
        setMessage(text);
        background.Colour = AimModPalette.Panel;
        accent.Colour = AimModPalette.Accent;
        spinner.Show();
        message.Padding = new MarginPadding { Left = 26 };
        apply();
    }

    /// <summary>Updates loading text in place without restarting the strip.</summary>
    public void SetLoadingText(string text)
    {
        if (!showing || IsError)
            return;
        setMessage(text);
    }

    public void ShowError(string text, string? detailText = null, Action? retryAction = null)
    {
        cancelAction = null;
        retry = retryAction;
        hasDetail = !string.IsNullOrWhiteSpace(detailText) && detailText != text;
        detailExpanded = false;
        IsError = true;
        setMessage(text);
        detail.Text = hasDetail ? detailText! : string.Empty;
        background.Colour = AimModPalette.PanelRaised;
        accent.Colour = AimModPalette.Danger;
        spinner.Hide();
        message.Padding = new MarginPadding();
        apply();
    }

    public void ShowError(Exception error, string action, Action? retryAction = null)
    {
        (string text, string? detailText) = AimModFriendlyError.Describe(error, action);
        ShowError(text, detailText, retryAction);
    }

    public void ShowMessage(string text, Action? retryAction = null)
    {
        cancelAction = null;
        retry = retryAction;
        hasDetail = false;
        detailExpanded = false;
        IsError = false;
        setMessage(text);
        background.Colour = AimModPalette.Panel;
        accent.Colour = AimModPalette.Cyan;
        spinner.Hide();
        message.Padding = new MarginPadding();
        apply();
    }

    public void Dismiss()
    {
        if (!showing)
            return;
        showing = false;
        IsError = false;
        cancelAction = null;
        retry = null;
        spinner.Hide();
        strip.FadeOut(AimModVisualStyle.FastTransition * 2, Easing.OutQuint);
        // Collapse immediately so following content moves up with the fade.
        strip.AutoSizeAxes = Axes.None;
        strip.Height = 0;
    }

    private void setMessage(string text)
    {
        if (messageText == text)
            return;
        messageText = text;
        message.Text = text;
    }

    private void toggleDetail()
    {
        detailExpanded = !detailExpanded;
        apply();
    }

    private void apply()
    {
        showing = true;
        strip.AutoSizeAxes = Axes.Y;
        primaryButton.Alpha = retry is null ? 0 : 1;
        cancelButton.Alpha = cancelAction is null ? 0 : 1;
        detailsButton.Alpha = hasDetail ? 1 : 0;
        detailsButton.Text = detailExpanded ? "Hide details" : "Details";
        detail.Alpha = hasDetail && detailExpanded ? 1 : 0;
        actions.Alpha = retry is null && cancelAction is null && !hasDetail ? 0 : 1;
        strip.FadeIn(AimModVisualStyle.FastTransition * 2, Easing.OutQuint);
    }

    private partial class InlineButton : AimModInteractiveSurface
    {
        private readonly OsuSpriteText label;

        public InlineButton(string text, Action action)
        {
            Size = new(96, AimModVisualStyle.CompactControlHeight - 6);
            BackgroundColour = AimModPalette.PanelHover;
            Action = action;
            Child = label = new OsuSpriteText
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Font = AimModVisualStyle.CaptionStrongFont,
                Colour = AimModPalette.Text,
                Text = text,
            };
        }

        public string Text
        {
            set => label.Text = value;
        }
    }
}
