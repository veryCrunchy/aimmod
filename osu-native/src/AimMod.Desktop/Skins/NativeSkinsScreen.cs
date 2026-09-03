using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.Skins;

public partial class NativeSkinsScreen : CompositeDrawable
{
    private ExternalLazerInstalledSkinSource? source;
    private Func<InstalledLazerSkin, CancellationToken, Task>? applySkin;
    private readonly CancellationTokenSource lifetime = new();
    private readonly OsuTextBox searchBox;
    private readonly TruncatingSpriteText status;
    private readonly FillFlowContainer list;
    private readonly Container searchPanel;
    private readonly Container listPanel;
    private readonly Container detailPanel;
    private readonly Container preview;
    private readonly TruncatingSpriteText selectedName;
    private readonly TruncatingSpriteText selectedCreator;
    private readonly TruncatingSpriteText selectedDetails;
    private readonly ApplyButton applyButton;
    private InstalledLazerSkin? selected;
    private Guid? lazerSkinId;
    private Guid? appliedExternalSkinId;
    private int revision;

    public NativeSkinsScreen(
        ExternalLazerInstalledSkinSource? source = null,
        Guid? lazerSkinId = null,
        Guid? appliedExternalSkinId = null,
        Func<InstalledLazerSkin, CancellationToken, Task>? applySkin = null)
    {
        this.source = source;
        this.lazerSkinId = lazerSkinId;
        this.appliedExternalSkinId = appliedExternalSkinId;
        this.applySkin = applySkin;
        RelativeSizeAxes = Axes.Both;

        InternalChildren = new Drawable[]
        {
            new AimModSectionHeader(
                "Skins",
                "Use skins already installed in osu!lazer. AimMod copies one selected skin into its own native player store.",
                "installed in lazer"),
            new SpriteText
            {
                Y = 78,
                Text = "SEARCH INSTALLED SKINS",
                Font = new FontUsage(size: 10, weight: "Bold"),
                Colour = AimModPalette.Cyan,
            },
            searchPanel = new CircularContainer
            {
                Y = 97,
                Width = 560,
                Height = 46,
                Masking = true,
                BorderThickness = 1,
                BorderColour = AimModPalette.Border,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                    searchBox = new OsuTextBox
                    {
                        RelativeSizeAxes = Axes.Both,
                        PlaceholderText = "Search skin name or creator",
                    },
                },
            },
            status = new TruncatingSpriteText
            {
                Y = 158,
                Text = source is null ? "Waiting for the local lazer library..." : "Loading installed skins...",
                Font = new FontUsage(size: 12, weight: "SemiBold"),
                Colour = AimModPalette.Muted,
            },
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = 188 },
                Children = new Drawable[]
                {
                    listPanel = new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Width = 0.6f,
                        Masking = true,
                        CornerRadius = 12,
                        BorderThickness = 1,
                        BorderColour = AimModPalette.Border,
                        Children = new Drawable[]
                        {
                            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                            new OsuScrollContainer
                            {
                                RelativeSizeAxes = Axes.Both,
                                Padding = new MarginPadding(14),
                                Child = list = new FillFlowContainer
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Direction = FillDirection.Vertical,
                                    Spacing = new(8),
                                },
                            },
                        },
                    },
                    detailPanel = new Container
                    {
                        Anchor = Anchor.TopRight,
                        Origin = Anchor.TopRight,
                        RelativeSizeAxes = Axes.Both,
                        Width = 0.385f,
                        Masking = true,
                        CornerRadius = 12,
                        BorderThickness = 1,
                        BorderColour = AimModPalette.Border,
                        Children = new Drawable[]
                        {
                            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                            new Container
                            {
                                RelativeSizeAxes = Axes.X,
                                Height = 235,
                                Child = preview = new Container
                                {
                                    RelativeSizeAxes = Axes.Both,
                                    Children = new Drawable[]
                                    {
                                        new Box
                                        {
                                            RelativeSizeAxes = Axes.Both,
                                            Colour = ColourInfo.GradientHorizontal(AimModPalette.PinkDark, AimModPalette.CyanDark),
                                        },
                                        new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas, Alpha = 0.44f },
                                        new SpriteIcon
                                        {
                                            Anchor = Anchor.Centre,
                                            Origin = Anchor.Centre,
                                            Icon = FontAwesome.Solid.PaintBrush,
                                            Size = new(42),
                                            Colour = AimModPalette.Muted,
                                        },
                                    },
                                },
                            },
                            new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Y = 255,
                                Padding = new MarginPadding { Horizontal = 22 },
                                Direction = FillDirection.Vertical,
                                Spacing = new(8),
                                Children = new Drawable[]
                                {
                                    selectedName = truncatingDetailText(24, AimModPalette.Text, "Bold", "Select a skin"),
                                    selectedCreator = truncatingDetailText(13, AimModPalette.Cyan, "SemiBold", "Installed skin details appear here."),
                                    selectedDetails = truncatingDetailText(12, AimModPalette.Muted, "Regular", string.Empty),
                                    applyButton = new ApplyButton(applySelected),
                                },
                            },
                        },
                    },
                },
            },
        };
    }

    protected override void Update()
    {
        base.Update();

        float availableWidth = Math.Max(0, DrawWidth);
        searchPanel.Width = Math.Clamp(availableWidth, 280, 560);
        status.MaxWidth = availableWidth;

        const float panelGap = 16;
        float listWidth = Math.Max(300, (availableWidth - panelGap) * 0.6f);
        listWidth = Math.Min(listWidth, Math.Max(0, availableWidth - 280 - panelGap));
        listPanel.Width = listWidth;
        listPanel.RelativeSizeAxes = Axes.Y;

        detailPanel.Width = Math.Max(0, availableWidth - listWidth - panelGap);
        detailPanel.RelativeSizeAxes = Axes.Y;

        float detailTextWidth = Math.Max(80, detailPanel.DrawWidth - 44);
        selectedName.MaxWidth = detailTextWidth;
        selectedCreator.MaxWidth = detailTextWidth;
        selectedDetails.MaxWidth = detailTextWidth;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        searchBox.OnCommit += (_, _) => loadSkins();
        loadSkins();
    }

    public void SetExternalSelection(Guid? skinId)
    {
        lazerSkinId = skinId;
        refreshRows();
    }

    public void SetAppliedSelection(Guid? externalSkinId)
    {
        appliedExternalSkinId = externalSkinId;
        refreshRows();
        updateDetails();
    }

    public void Configure(
        ExternalLazerInstalledSkinSource? source,
        Guid? lazerSkinId,
        Guid? appliedExternalSkinId,
        Func<InstalledLazerSkin, CancellationToken, Task>? applySkin)
    {
        bool sourceChanged = !ReferenceEquals(this.source, source);
        this.source = source;
        this.lazerSkinId = lazerSkinId;
        this.appliedExternalSkinId = appliedExternalSkinId;
        this.applySkin = applySkin;

        if (sourceChanged)
            loadSkins();
        else
        {
            refreshRows();
            updateDetails();
        }
    }

    private void loadSkins()
    {
        if (source is null)
            return;

        int requestRevision = ++revision;
        status.Text = "Reading lazer's installed skins...";
        _ = loadSkinsAsync(requestRevision, searchBox.Current.Value, lifetime.Token);
    }

    private async Task loadSkinsAsync(int requestRevision, string searchText, CancellationToken cancellationToken)
    {
        try
        {
            InstalledLazerSkinPage page = await source!.SearchAsync(searchText, limit: 100, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!IsDisposed)
                Schedule(() => showSkins(requestRevision, page));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!IsDisposed)
                Schedule(() => showError(requestRevision, error.Message));
        }
    }

    private IReadOnlyList<InstalledLazerSkin> loadedSkins = Array.Empty<InstalledLazerSkin>();

    private void showSkins(int requestRevision, InstalledLazerSkinPage page)
    {
        if (requestRevision != revision)
            return;

        loadedSkins = page.Items;
        status.Text = page.Items.Count == 0
            ? "No installed skins match this search."
            : page.HasMore
                ? $"Showing the first {page.Items.Count:N0} of {page.Total:N0} installed skins"
                : $"{page.Total:N0} installed skins";
        if (selected is null || page.Items.All(item => item.SkinId != selected.SkinId))
            selected = page.Items.FirstOrDefault(item => item.SkinId == lazerSkinId) ?? page.Items.FirstOrDefault();
        refreshRows();
        updateDetails();
    }

    private void showError(int requestRevision, string message)
    {
        if (requestRevision != revision)
            return;
        loadedSkins = Array.Empty<InstalledLazerSkin>();
        list.Clear();
        status.Text = $"Could not read installed skins: {message}";
    }

    private void refreshRows()
    {
        list.Clear();
        list.AddRange(loadedSkins.Select(skin => new SkinRow(
            skin,
            skin.SkinId == selected?.SkinId,
            skin.SkinId == lazerSkinId,
            skin.SkinId == appliedExternalSkinId,
            () => select(skin))));
    }

    private void select(InstalledLazerSkin skin)
    {
        selected = skin;
        refreshRows();
        updateDetails();
    }

    private void updateDetails()
    {
        preview.Clear();
        if (selected is null)
        {
            addPreviewPlaceholder();
            selectedName.Text = "Select a skin";
            selectedCreator.Text = "Installed skin details appear here.";
            selectedDetails.Text = string.Empty;
            applyButton.SetState(false, "Select a skin");
            return;
        }

        preview.Add(selected.PreviewPath.Length > 0
            ? new AimModLocalArtwork(selected.PreviewPath) { RelativeSizeAxes = Axes.Both }
            : new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = ColourInfo.GradientHorizontal(AimModPalette.PinkDark, AimModPalette.CyanDark),
                Alpha = 0.68f,
            });
        selectedName.Text = selected.Name;
        selectedCreator.Text = selected.Creator.Length > 0 ? $"by {selected.Creator}" : "Creator not specified";
        selectedDetails.Text = selected.IsBuiltIn
            ? "Built into the pinned osu runtime. No copy is needed."
            : $"{selected.Summary.FileCount:N0} local files  ·  copied once into AimMod when applied";
        applyButton.SetState(
            applySkin is not null && selected.SkinId != appliedExternalSkinId,
            selected.SkinId == appliedExternalSkinId ? "Active in AimMod" : "Use for replay playback");
    }

    private void addPreviewPlaceholder()
    {
        preview.AddRange(new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = ColourInfo.GradientHorizontal(AimModPalette.PinkDark, AimModPalette.CyanDark),
            },
            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas, Alpha = 0.44f },
            new SpriteIcon
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Icon = FontAwesome.Solid.PaintBrush,
                Size = new(42),
                Colour = AimModPalette.Muted,
            },
        });
    }

    private void applySelected()
    {
        if (selected is null || applySkin is null)
            return;
        InstalledLazerSkin target = selected;
        applyButton.SetState(false, "Applying skin...");
        _ = applySelectedAsync(target, lifetime.Token);
    }

    private async Task applySelectedAsync(InstalledLazerSkin target, CancellationToken cancellationToken)
    {
        try
        {
            await applySkin!(target, cancellationToken).ConfigureAwait(false);
            if (!IsDisposed)
                Schedule(() =>
                {
                    appliedExternalSkinId = target.SkinId;
                    status.Text = $"{target.Name} is active for AimMod replay playback.";
                    refreshRows();
                    updateDetails();
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!IsDisposed)
                Schedule(() =>
                {
                    status.Text = $"Could not apply {target.Name}: {error.Message}";
                    updateDetails();
                });
        }
    }

    protected override void Dispose(bool isDisposing)
    {
        lifetime.Cancel();
        lifetime.Dispose();
        base.Dispose(isDisposing);
    }

    private static OsuSpriteText detailText(float size, Colour4 colour, string weight, string value) => new()
    {
        Text = value,
        Font = new FontUsage(size: size, weight: weight),
        Colour = colour,
    };

    private static TruncatingSpriteText truncatingDetailText(float size, Colour4 colour, string weight, string value) => new()
    {
        Text = value,
        Font = new FontUsage(size: size, weight: weight),
        Colour = colour,
    };

    private partial class SkinRow : ClickableContainer
    {
        private readonly Action action;
        private readonly Box background;
        private readonly Colour4 restingColour;
        private readonly TruncatingSpriteText name;
        private readonly TruncatingSpriteText creator;
        private readonly FillFlowContainer<Drawable> badgeFlow;

        public SkinRow(InstalledLazerSkin skin, bool selected, bool activeInLazer, bool activeInAimMod, Action action)
        {
            this.action = action;
            restingColour = selected ? AimModPalette.PanelHover : AimModPalette.PanelRaised;
            RelativeSizeAxes = Axes.X;
            Height = 76;
            Masking = true;
            CornerRadius = 9;
            BorderThickness = 1;
            BorderColour = selected ? AimModPalette.Pink : AimModPalette.Border;
            Children = new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = restingColour,
                },
                new Box
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = 4,
                    Colour = activeInAimMod ? AimModPalette.Success : activeInLazer ? AimModPalette.Cyan : AimModPalette.Pink,
                },
                name = truncatingDetailText(16, AimModPalette.Text, "SemiBold", skin.Name).With(text => text.Position = new(18, 17)),
                creator = truncatingDetailText(11, AimModPalette.Muted, "Regular", skin.Creator.Length > 0 ? skin.Creator : "Creator not specified")
                    .With(text => text.Position = new(18, 43)),
                badgeFlow = new FillFlowContainer<Drawable>
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    AutoSizeAxes = Axes.Both,
                    Margin = new MarginPadding { Right = 14 },
                    Direction = FillDirection.Horizontal,
                    Spacing = new(6),
                    Children = badges(skin, activeInLazer, activeInAimMod),
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            float textWidth = Math.Max(60, DrawWidth - badgeFlow.DrawWidth - 54);
            name.MaxWidth = textWidth;
            creator.MaxWidth = textWidth;
        }

        private static Drawable[] badges(InstalledLazerSkin skin, bool activeInLazer, bool activeInAimMod)
        {
            var result = new List<Drawable>();
            if (skin.IsBuiltIn)
                result.Add(new AimModPill("built-in"));
            if (activeInLazer)
                result.Add(new AimModPill("lazer", AimModPillTone.Info));
            if (activeInAimMod)
                result.Add(new AimModPill("active", AimModPillTone.Success));
            return result.ToArray();
        }

        protected override bool OnClick(ClickEvent e)
        {
            action();
            return true;
        }

        protected override bool OnHover(HoverEvent e)
        {
            background.FadeColour(AimModPalette.PanelHover, 100);
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            background.FadeColour(restingColour, 100);
            base.OnHoverLost(e);
        }
    }

    private partial class ApplyButton : ClickableContainer
    {
        private readonly Action action;
        private readonly Box background;
        private readonly SpriteText label;
        private bool enabled;

        public ApplyButton(Action action)
        {
            this.action = action;
            RelativeSizeAxes = Axes.X;
            Height = 48;
            Margin = new MarginPadding { Top = 14 };
            Masking = true;
            CornerRadius = 9;
            Children = new Drawable[]
            {
                background = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Pink },
                label = detailText(14, AimModPalette.Text, "Bold", "Select a skin").With(text =>
                {
                    text.Anchor = Anchor.Centre;
                    text.Origin = Anchor.Centre;
                }),
            };
        }

        public void SetState(bool enabled, string text)
        {
            this.enabled = enabled;
            label.Text = text;
            background.Colour = enabled ? AimModPalette.Pink : AimModPalette.PanelHover;
            this.FadeTo(enabled ? 1 : 0.7f, 100);
        }

        protected override bool OnClick(ClickEvent e)
        {
            if (enabled)
                action();
            return true;
        }
    }
}
