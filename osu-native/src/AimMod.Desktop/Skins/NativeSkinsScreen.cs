using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
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
    private readonly SkinListState listState;
    private readonly Container detailPanel;
    private readonly SkinPreview preview;
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
            searchPanel = new Container
            {
                Y = 96,
                Width = 560,
                Height = AimModVisualStyle.ControlHeight,
                Child = searchBox = new OsuTextBox
                {
                    RelativeSizeAxes = Axes.Both,
                    PlaceholderText = "Search skin name or creator",
                },
            },
            status = new TruncatingSpriteText
            {
                Y = 148,
                Text = source is null ? "osu!lazer library not connected" : "Reading installed skins",
                Font = new FontUsage(size: 11, weight: "SemiBold"),
                Colour = AimModPalette.Muted,
            },
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = 174 },
                Children = new Drawable[]
                {
                    listPanel = new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Width = 0.6f,
                        Masking = true,
                        CornerRadius = AimModVisualStyle.CardRadius,
                        Children = new Drawable[]
                        {
                            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                            new AimModScrollContainer
                            {
                                RelativeSizeAxes = Axes.Both,
                                Child = list = new FillFlowContainer
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Padding = new MarginPadding
                                    {
                                        Left = AimModVisualStyle.RowSpacing,
                                        Right = AimModVisualStyle.RelatedSpacing,
                                        Vertical = AimModVisualStyle.RowSpacing,
                                    },
                                    Direction = FillDirection.Vertical,
                                    Spacing = new(AimModVisualStyle.RelatedSpacing),
                                },
                            },
                            listState = new SkinListState(
                                source is null ? FontAwesome.Solid.Link : FontAwesome.Solid.PaintBrush,
                                source is null ? "Connect osu!lazer" : "Reading installed skins",
                                source is null
                                    ? "AimMod will show the skins from your local osu!lazer library here."
                                    : "Your installed skins will appear here."),
                        },
                    },
                    detailPanel = new Container
                    {
                        Anchor = Anchor.TopRight,
                        Origin = Anchor.TopRight,
                        RelativeSizeAxes = Axes.Both,
                        Width = 0.385f,
                        Masking = true,
                        CornerRadius = AimModVisualStyle.CardRadius,
                        Children = new Drawable[]
                        {
                            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                            new Container
                            {
                                RelativeSizeAxes = Axes.X,
                                Height = 220,
                                Child = preview = new SkinPreview(),
                            },
                            new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Y = 236,
                                Padding = new MarginPadding { Horizontal = 16 },
                                Direction = FillDirection.Vertical,
                                Spacing = new(AimModVisualStyle.RelatedSpacing),
                                Children = new Drawable[]
                                {
                                    selectedName = truncatingDetailText(20, AimModPalette.Text, "Bold", "Select a skin"),
                                    selectedCreator = truncatingDetailText(12, AimModPalette.Cyan, "SemiBold", "Installed skin details appear here."),
                                    selectedDetails = truncatingDetailText(11, AimModPalette.Muted, "Regular", string.Empty),
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
        const float panelGap = AimModVisualStyle.SectionSpacing;
        float listWidth = Math.Max(300, (availableWidth - panelGap) * 0.6f);
        listWidth = Math.Min(listWidth, Math.Max(0, availableWidth - 280 - panelGap));
        listPanel.Width = listWidth;
        listPanel.RelativeSizeAxes = Axes.Y;

        searchPanel.Width = listWidth;
        status.MaxWidth = listWidth;

        detailPanel.Width = Math.Max(0, availableWidth - listWidth - panelGap);
        detailPanel.RelativeSizeAxes = Axes.Y;

        float detailTextWidth = Math.Max(80, detailPanel.DrawWidth - 32);
        selectedName.MaxWidth = detailTextWidth;
        selectedCreator.MaxWidth = detailTextWidth;
        selectedDetails.MaxWidth = detailTextWidth;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        searchBox.Current.BindValueChanged(_ => loadSkins());
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
        status.Text = "Reading installed skins";
        listState.SetState(FontAwesome.Solid.PaintBrush, "Reading installed skins", "Your local osu!lazer library is being refreshed.", true);
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
        listState.SetState(
            FontAwesome.Solid.Search,
            searchBox.Current.Value.Length == 0 ? "No installed skins found" : "No matching skins",
            searchBox.Current.Value.Length == 0
                ? "Install a skin in osu!lazer, then return here to use it for replay playback."
                : "Try a different skin name or creator.",
            page.Items.Count == 0);
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
        listState.SetState(FontAwesome.Solid.ExclamationTriangle, "Installed skins unavailable", "Reconnect osu!lazer and try again.", true);
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
        if (selected is null)
        {
            preview.SetSkin(null);
            selectedName.Text = "Select a skin";
            selectedCreator.Text = "Installed skin details appear here.";
            selectedDetails.Text = string.Empty;
            applyButton.SetState(false, "Select a skin");
            return;
        }

        preview.SetSkin(selected);
        selectedName.Text = selected.Name;
        selectedCreator.Text = selected.Creator.Length > 0 ? $"by {selected.Creator}" : "Creator not specified";
        selectedDetails.Text = selected.IsBuiltIn
            ? "Built into the pinned osu runtime. No copy is needed."
            : $"{selected.Summary.FileCount:N0} local files  ·  copied once into AimMod when applied";
        applyButton.SetState(
            applySkin is not null && selected.SkinId != appliedExternalSkinId,
            selected.SkinId == appliedExternalSkinId ? "Active in AimMod" : "Use for replay playback");
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

    private partial class SkinPreview : CompositeDrawable
    {
        private readonly Container artwork;
        private readonly SpriteIcon stateIcon;
        private readonly OsuSpriteText stateTitle;
        private readonly OsuSpriteText stateDetail;

        public SkinPreview()
        {
            RelativeSizeAxes = Axes.Both;
            Masking = true;
            InternalChildren = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = AimModPalette.PanelRaised,
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    AutoSizeAxes = Axes.Y,
                    RelativeSizeAxes = Axes.X,
                    Padding = new MarginPadding { Horizontal = 24 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(AimModVisualStyle.RelatedSpacing),
                    Children = new Drawable[]
                    {
                        stateIcon = new SpriteIcon
                        {
                            Anchor = Anchor.TopCentre,
                            Origin = Anchor.TopCentre,
                            Icon = FontAwesome.Solid.PaintBrush,
                            Size = new(30),
                            Colour = AimModPalette.Muted,
                        },
                        stateTitle = new OsuSpriteText
                        {
                            Anchor = Anchor.TopCentre,
                            Origin = Anchor.TopCentre,
                            Font = new FontUsage(size: 15, weight: "Bold"),
                            Colour = AimModPalette.Text,
                        },
                        stateDetail = new OsuSpriteText
                        {
                            Anchor = Anchor.TopCentre,
                            Origin = Anchor.TopCentre,
                            Font = new FontUsage(size: 11),
                            Colour = AimModPalette.Muted,
                        },
                    },
                },
                artwork = new Container { RelativeSizeAxes = Axes.Both },
            };

            SetSkin(null);
        }

        public void SetSkin(InstalledLazerSkin? skin)
        {
            artwork.Clear();
            if (skin?.HasPreview == true)
            {
                artwork.Add(new AimModLocalArtwork(skin.PreviewPath)
                {
                    RelativeSizeAxes = Axes.Both,
                });
                stateIcon.Icon = FontAwesome.Solid.Image;
                stateTitle.Text = "Artwork unavailable";
                stateDetail.Text = "The preview image could not be opened.";
                return;
            }

            stateIcon.Icon = skin is null ? FontAwesome.Solid.PaintBrush : FontAwesome.Solid.Image;
            stateTitle.Text = skin is null ? "Select a skin" : "Artwork unavailable";
            stateDetail.Text = skin is null
                ? "Preview its menu artwork and details."
                : "This skin does not include a menu background.";
        }
    }

    private partial class SkinListState : CompositeDrawable
    {
        private readonly SpriteIcon icon;
        private readonly OsuSpriteText title;
        private readonly OsuSpriteText detail;

        public SkinListState(IconUsage initialIcon, string initialTitle, string initialDetail)
        {
            RelativeSizeAxes = Axes.Both;
            InternalChild = new FillFlowContainer
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Width = 420,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new(AimModVisualStyle.RowSpacing),
                Children = new Drawable[]
                {
                    icon = new SpriteIcon
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Icon = initialIcon,
                        Size = new(26),
                        Colour = AimModPalette.Cyan,
                    },
                    title = new OsuSpriteText
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Text = initialTitle,
                        Font = new FontUsage(size: 18, weight: "Bold"),
                        Colour = AimModPalette.Text,
                    },
                    detail = new OsuSpriteText
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Text = initialDetail,
                        Font = new FontUsage(size: 12),
                        Colour = AimModPalette.Muted,
                    },
                },
            };
        }

        public void SetState(IconUsage stateIcon, string stateTitle, string stateDetail, bool visible)
        {
            icon.Icon = stateIcon;
            title.Text = stateTitle;
            detail.Text = stateDetail;
            this.FadeTo(visible ? 1 : 0, 120);
        }
    }

    private partial class SkinRow : AimModInteractiveSurface
    {
        private readonly TruncatingSpriteText name;
        private readonly TruncatingSpriteText creator;
        private readonly FillFlowContainer<Drawable> badgeFlow;

        public SkinRow(InstalledLazerSkin skin, bool selected, bool activeInLazer, bool activeInAimMod, Action action)
        {
            RelativeSizeAxes = Axes.X;
            Height = 68;
            CornerRadius = AimModVisualStyle.ControlRadius;
            BackgroundColour = selected ? AimModPalette.PanelHover : AimModPalette.PanelRaised;
            Action = action;
            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = 3,
                    Colour = activeInAimMod ? AimModPalette.Success : activeInLazer ? AimModPalette.Cyan : AimModPalette.Pink,
                    Alpha = selected || activeInLazer || activeInAimMod ? 1 : 0,
                },
                name = truncatingDetailText(14, AimModPalette.Text, "SemiBold", skin.Name).With(text => text.Position = new(16, 14)),
                creator = truncatingDetailText(11, AimModPalette.Muted, "Regular", skin.Creator.Length > 0 ? skin.Creator : "Creator not specified")
                    .With(text => text.Position = new(16, 38)),
                badgeFlow = new FillFlowContainer<Drawable>
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    AutoSizeAxes = Axes.Both,
                    Margin = new MarginPadding { Right = 12 },
                    Direction = FillDirection.Horizontal,
                    Spacing = new(AimModVisualStyle.RelatedSpacing),
                    Children = badges(skin, activeInLazer, activeInAimMod),
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            float textWidth = Math.Max(60, DrawWidth - badgeFlow.DrawWidth - 46);
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

    }

    private partial class ApplyButton : AimModInteractiveSurface
    {
        private readonly Action action;
        private readonly SpriteText label;
        private bool enabled;

        public ApplyButton(Action action)
        {
            this.action = action;
            RelativeSizeAxes = Axes.X;
            Height = AimModVisualStyle.ControlHeight;
            Margin = new MarginPadding { Top = AimModVisualStyle.RowSpacing };
            CornerRadius = AimModVisualStyle.ControlRadius;
            BackgroundColour = AimModPalette.PanelHover;
            Child = label = detailText(13, AimModPalette.Text, "Bold", "Select a skin").With(text =>
            {
                text.Anchor = Anchor.Centre;
                text.Origin = Anchor.Centre;
            });
        }

        public void SetState(bool enabled, string text)
        {
            this.enabled = enabled;
            label.Text = text;
            BackgroundColour = enabled ? AimModPalette.Pink : AimModPalette.PanelHover;
            this.FadeTo(enabled ? 1 : 0.65f, AimModVisualStyle.FastTransition);
        }

        protected override bool OnClick(ClickEvent e)
        {
            if (enabled)
                action();
            base.OnClick(e);
            return true;
        }
    }
}
