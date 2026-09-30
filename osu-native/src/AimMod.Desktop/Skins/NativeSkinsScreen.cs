using AimMod.Desktop.Visuals;
using AimMod.Desktop.Skins.Online;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Platform;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Skins;

public partial class NativeSkinsScreen : CompositeDrawable
{
    private const float toolbar_y = 120;
    private const float source_width = 150;
    private const float sort_width = 190;
    private const float min_card_width = 200;
    private const int maximum_loaded_skins = 1_000;

    private IInstalledSkinSource? source;
    private Func<InstalledLazerSkin, CancellationToken, Task>? applySkin;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? skinSearch;
    private CancellationTokenSource? previewLoad;
    private readonly AimModTextBox searchBox;
    private readonly TruncatingSpriteText status;
    private readonly FillFlowContainer list;
    private readonly KeyedFlow<Guid, InstalledLazerSkin, SkinCardView> rows;
    private AimModLayout.ChangeTracker<(float, float, bool)> layoutTracker;
    private readonly Container toolbar;
    private readonly Container searchPanel;
    private readonly Container searchField;
    private readonly AimModResetButton clearButton;
    private AimModLayout.ChangeTracker<(float, bool, bool)> gridTracker;
    private AimModLayout.ChangeTracker<(float, float)> detailHeightTracker;
    private readonly SkinsDropdown<InstalledSkinSourceFilter> sourceDropdown;
    private readonly SkinsDropdown<InstalledSkinSort> sortDropdown;
    private readonly Container body;
    private readonly Container listPanel;
    private readonly AimModScrollContainer listScroll;
    private readonly SkinListState listState;
    private readonly Container detailPanel;
    private readonly Container detailContent;
    private readonly AimModButton backButton;
    private readonly Container previewFrame;
    private readonly FillFlowContainer detailActions;
    private readonly TruncatingSpriteText selectedName;
    private readonly AimModPill selectedTag;
    private readonly FillFlowContainer densityToggle;
    private readonly AimModButton gridButton;
    private readonly AimModButton listButton;
    private readonly Bindable<SkinListDensity> density = new(SkinListDensity.Grid);
    private readonly TruncatingSpriteText selectedCreator;
    private readonly FillFlowContainer badgeFlow;
    private readonly ApplyButton applyButton;
    private readonly AimModButton openFolderButton;
    private readonly Container actionRow;
    private readonly TruncatingSpriteText applyStatus;
    private readonly FillFlowContainer statsFlow;
    private readonly FillFlowContainer colourFlow;
    private readonly TruncatingSpriteText storedName;
    private readonly Container installedContent;
    private readonly Container onlineContent;
    private readonly NativeOnlineSkinsView onlineView;
    private readonly Bindable<SkinsWorkspaceTab> currentTab = new(SkinsWorkspaceTab.Installed);
    private readonly Bindable<InstalledSkinSourceFilter> sourceFilter = new(InstalledSkinSourceFilter.All);
    private readonly Bindable<InstalledSkinSort> sort = new(InstalledSkinSort.Name);
    private SkinTextureCache? textures;
    private GameHost? host;
    private InstalledLazerSkin? selected;
    private Guid? previewSkinId;
    private Guid? lazerSkinId;
    private Guid? appliedExternalSkinId;
    private int revision;
    private bool stacked;
    private bool detailOpen;
    private bool applying;
    private int totalSkins;

    public NativeSkinsScreen(
        IInstalledSkinSource? source = null,
        Guid? lazerSkinId = null,
        Guid? appliedExternalSkinId = null,
        Func<InstalledLazerSkin, CancellationToken, Task>? applySkin = null,
        OnlineSkinCatalogBackend? onlineBackend = null,
        IOnlineSkinArchiveDestination? onlineDestination = null,
        string? onlineSaveDirectory = null,
        Action<Uri>? openExternal = null)
    {
        this.source = source;
        this.lazerSkinId = lazerSkinId;
        this.appliedExternalSkinId = appliedExternalSkinId;
        this.applySkin = applySkin;
        RelativeSizeAxes = Axes.Both;

        InternalChildren = new Drawable[]
        {
            new AimModSectionHeader("Skins", "Choose the skin AimMod uses when it plays your replays.") { Depth = -110 },
            new AimModTabControl<SkinsWorkspaceTab>
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopLeft,
                Position = new(0, 72),
                Height = AimModVisualStyle.ControlHeight,
                Current = currentTab,
                Depth = -100,
            },
            installedContent = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Children = new Drawable[]
                {
                    // Filters sit ahead of the results so an open menu draws over the cards.
                    toolbar = new Container
                    {
                        Y = toolbar_y,
                        RelativeSizeAxes = Axes.X,
                        Height = AimModVisualStyle.ControlHeight,
                        Depth = -10,
                        Children = new Drawable[]
                        {
                            searchPanel = new Container
                            {
                                Width = 420,
                                Height = AimModVisualStyle.ControlHeight,
                                Children =
                                [
                                    searchField = new Container
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        Child = searchBox = new AimModTextBox
                                        {
                                            RelativeSizeAxes = Axes.X,
                                            Height = AimModVisualStyle.ControlHeight,
                                            PlaceholderText = "Search skins or creators",
                                            FocusOnSearchShortcut = true,
                                        },
                                    },
                                    clearButton = new AimModResetButton(resetFilters, "Clear") { Width = 72, Height = AimModVisualStyle.ControlHeight, Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, Alpha = 0 },
                                ],
                            },
                            sourceDropdown = new SkinsDropdown<InstalledSkinSourceFilter>(string.Empty)
                            {
                                Width = source_width,
                                Items = Enum.GetValues<InstalledSkinSourceFilter>(),
                                Current = sourceFilter,
                            },
                            sortDropdown = new SkinsDropdown<InstalledSkinSort>("Sort")
                            {
                                Width = sort_width,
                                Items = Enum.GetValues<InstalledSkinSort>(),
                                Current = sort,
                            },
                        },
                    },
                    status = new TruncatingSpriteText
                    {
                        Y = toolbar_y + AimModVisualStyle.ControlHeight + 10,
                        Text = source is null ? "No osu! skin library connected" : "Reading installed skins",
                        Font = AimModVisualStyle.CaptionStrongFont,
                        Colour = AimModPalette.Muted,
                    },
                    densityToggle = new FillFlowContainer
                    {
                        Origin = Anchor.TopRight,
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Horizontal,
                        Spacing = new(4),
                        Children = new Drawable[]
                        {
                            gridButton = new AimModButton("Grid", () => density.Value = SkinListDensity.Grid) { Height = 26 },
                            listButton = new AimModButton("List", () => density.Value = SkinListDensity.List) { Height = 26 },
                        },
                    },
                    body = new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Padding = new MarginPadding { Top = toolbar_y + AimModVisualStyle.ControlHeight + AimModVisualStyle.SectionSpacing + 12 },
                        Children = new Drawable[]
                        {
                            listPanel = new Container
                            {
                                RelativeSizeAxes = Axes.Y,
                                Width = 600,
                                Children = new Drawable[]
                                {
                                    listScroll = new AimModScrollContainer
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        Child = list = new FillFlowContainer
                                        {
                                            RelativeSizeAxes = Axes.X,
                                            AutoSizeAxes = Axes.Y,
                                            Padding = new MarginPadding { Right = 16, Bottom = AimModVisualStyle.RowSpacing },
                                            Direction = FillDirection.Full,
                                            Spacing = new(AimModVisualStyle.RelatedSpacing),
                                        },
                                    },
                                    listState = new SkinListState(
                                        source is null ? FontAwesome.Solid.Link : FontAwesome.Solid.PaintBrush,
                                        source is null ? "Connect osu!" : "Reading installed skins",
                                        source is null
                                            ? "Skins from osu!lazer and osu!stable appear here."
                                            : "Your installed skins will appear here.",
                                        "Retry", () => loadSkins()),
                                },
                            },
                            detailPanel = new Container
                            {
                                Anchor = Anchor.TopRight,
                                Origin = Anchor.TopRight,
                                Height = 400,
                                Width = 400,
                                Masking = true,
                                CornerRadius = AimModVisualStyle.CardRadius,
                                Children = new Drawable[]
                                {
                                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                                    new AimModScrollContainer
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        ScrollbarOverlapsContent = true,
                                        Child = detailContent = new Container
                                        {
                                            RelativeSizeAxes = Axes.X,
                                            AutoSizeAxes = Axes.Y,
                                            Children = new Drawable[]
                                            {
                                                backButton = new AimModButton("‹  All skins", closeDetails)
                                                {
                                                    Position = new(12, 12),
                                                    Height = AimModVisualStyle.CompactControlHeight,
                                                    Alpha = 0,
                                                },
                                                previewFrame = new Container
                                                {
                                                    RelativeSizeAxes = Axes.X,
                                                    Height = 220,
                                                    Masking = true,
                                                    Children = new Drawable[]
                                                    {
                                                        new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas },
                                                    },
                                                },
                                                detailActions = new FillFlowContainer
                                                {
                                                    RelativeSizeAxes = Axes.X,
                                                    AutoSizeAxes = Axes.Y,
                                                    Y = 236,
                                                    Padding = new MarginPadding { Horizontal = 16, Bottom = 16 },
                                                    Direction = FillDirection.Vertical,
                                                    Spacing = new(AimModVisualStyle.RelatedSpacing),
                                                    Children = new Drawable[]
                                                    {
                                                        new FillFlowContainer
                                                        {
                                                            AutoSizeAxes = Axes.Both,
                                                            Direction = FillDirection.Horizontal,
                                                            Spacing = new(8),
                                                            Children = new Drawable[]
                                                            {
                                                                selectedTag = new AimModPill(string.Empty) { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Alpha = 0 },
                                                                selectedName = detailText(20, AimModPalette.Text, "Bold", "Select a skin").With(text =>
                                                                {
                                                                    text.Anchor = Anchor.CentreLeft;
                                                                    text.Origin = Anchor.CentreLeft;
                                                                }),
                                                            },
                                                        },
                                                        selectedCreator = detailText(12, AimModPalette.Muted, "Regular", string.Empty),
                                                        badgeFlow = new FillFlowContainer
                                                        {
                                                            AutoSizeAxes = Axes.Both,
                                                            Direction = FillDirection.Horizontal,
                                                            Spacing = new(6),
                                                        },
                                                        actionRow = new Container
                                                        {
                                                            RelativeSizeAxes = Axes.X,
                                                            Height = AimModVisualStyle.ControlHeight,
                                                            Margin = new MarginPadding { Top = 4 },
                                                            Children = new Drawable[]
                                                            {
                                                                applyButton = new ApplyButton(applySelected),
                                                                openFolderButton = new AimModButton("Open folder", openSelectedFolder)
                                                                {
                                                                    Anchor = Anchor.CentreRight,
                                                                    Origin = Anchor.CentreRight,
                                                                },
                                                            },
                                                        },
                                                        applyStatus = detailText(11, AimModPalette.Muted, "SemiBold", string.Empty),
                                                        statsFlow = new FillFlowContainer
                                                        {
                                                            RelativeSizeAxes = Axes.X,
                                                            AutoSizeAxes = Axes.Y,
                                                            Direction = FillDirection.Full,
                                                            Spacing = new(AimModVisualStyle.RelatedSpacing),
                                                            Margin = new MarginPadding { Top = 8 },
                                                        },
                                                        colourFlow = new FillFlowContainer
                                                        {
                                                            AutoSizeAxes = Axes.Both,
                                                            Direction = FillDirection.Horizontal,
                                                            Spacing = new(6),
                                                        },
                                                        storedName = detailText(11, AimModPalette.Muted, "Regular", string.Empty),
                                                    },
                                                },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
            onlineContent = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = toolbar_y },
                Alpha = 0,
                AlwaysPresent = false,
                Child = onlineView = new NativeOnlineSkinsView(
                    onlineBackend,
                    onlineDestination,
                    onlineSaveDirectory ?? Path.Combine(Path.GetTempPath(), "AimMod", "saved-skins"),
                    openExternal)
                {
                    RelativeSizeAxes = Axes.Both,
                },
            },
        };
        rows = new KeyedFlow<Guid, InstalledLazerSkin, SkinCardView>(list, skin => skin.SkinId,
            skin => $"{skin.Name}|{skin.Creator}|{skin.ElementFiles.Count}|{skin.SkinId == lazerSkinId}|{skin.SkinId == appliedExternalSkinId}",
            skin => new SkinCardView(skin, skin.SkinId == selected?.SkinId, skin.SkinId == this.lazerSkinId, skin.SkinId == this.appliedExternalSkinId, textures, () => selectCard(skin))
                .With(card => card.SetCompact(density.Value == SkinListDensity.List)));
        updateDetails();
    }

    /// <summary>Below this width the grid and inspector share one column; a card opens its details with a way back.</summary>
    internal const float StackedWidth = 720;

    [BackgroundDependencyLoader]
    private void load(GameHost gameHost)
    {
        host = gameHost;
        textures = new SkinTextureCache(gameHost);
    }

    protected override void Update()
    {
        base.Update();
        layoutGrid();
        fitDetailPanel();

        if (!layoutTracker.Update((DrawWidth, detailContent.DrawWidth, detailOpen)))
            return;

        float availableWidth = Math.Max(0, DrawWidth);
        const float gap = AimModVisualStyle.SectionSpacing;
        stacked = availableWidth < StackedWidth;
        float listWidth;
        if (stacked)
        {
            listWidth = availableWidth;
            listPanel.Width = availableWidth;
            detailPanel.Width = availableWidth;
            listPanel.Alpha = detailOpen ? 0 : 1;
            detailPanel.Alpha = detailOpen ? 1 : 0;
            toolbar.Alpha = status.Alpha = detailOpen ? 0 : 1;
            body.Padding = new MarginPadding { Top = detailOpen ? toolbar_y : toolbarBottom(twoRows: availableWidth < 560) + 30 };
        }
        else
        {
            float detailWidth = Math.Clamp((availableWidth - gap) * 0.36f, 330, 480);
            listWidth = Math.Max(0, availableWidth - detailWidth - gap);
            listPanel.Width = listWidth;
            detailPanel.Width = detailWidth;
            listPanel.Alpha = toolbar.Alpha = status.Alpha = 1;
            // With nothing to inspect (no skins, or none match) the empty state speaks alone.
            detailPanel.Alpha = selected is null ? 0 : 1;
            body.Padding = new MarginPadding { Top = toolbarBottom(twoRows: false) + 30 };
        }
        detailPanel.AlwaysPresent = !stacked || detailOpen;
        backButton.Alpha = stacked ? 1 : 0;

        bool twoRows = stacked && availableWidth < 560;
        float filters = source_width + sort_width + AimModVisualStyle.RelatedSpacing * 2;
        if (twoRows)
        {
            toolbar.Height = AimModVisualStyle.ControlHeight * 2 + AimModVisualStyle.RelatedSpacing;
            searchPanel.Width = listWidth;
            sourceDropdown.Position = new(0, AimModVisualStyle.ControlHeight + AimModVisualStyle.RelatedSpacing);
            sortDropdown.Position = new(source_width + AimModVisualStyle.RelatedSpacing, sourceDropdown.Y);
        }
        else
        {
            toolbar.Height = AimModVisualStyle.ControlHeight;
            searchPanel.Width = Math.Max(160, listWidth - filters + AimModVisualStyle.RelatedSpacing);
            sourceDropdown.Position = new(searchPanel.Width + AimModVisualStyle.RelatedSpacing, 0);
            sortDropdown.Position = new(sourceDropdown.X + source_width + AimModVisualStyle.RelatedSpacing, 0);
        }
        status.Y = toolbar_y + toolbar.Height + 8;
        densityToggle.Position = new(listWidth - 16, status.Y - 6);
        densityToggle.Alpha = toolbar.Alpha;
        status.MaxWidth = Math.Max(0, listWidth - 16 - densityToggle.DrawWidth - AimModVisualStyle.RelatedSpacing);


        float contentWidth = Math.Max(0, detailContent.DrawWidth);
        float previewTop = stacked ? 56 : 0;
        previewFrame.Y = previewTop;
        previewFrame.Height = MathF.Round(contentWidth * 9 / 16);
        detailActions.Y = previewTop + previewFrame.Height + 16;
        float textWidth = Math.Max(0, contentWidth - 32);
        selectedName.MaxWidth = Math.Max(40, textWidth - (selectedTag.Alpha > 0 ? selectedTag.DrawWidth + 8 : 0));
        selectedCreator.MaxWidth = textWidth;
        applyStatus.MaxWidth = textWidth;
        storedName.MaxWidth = textWidth;
        layoutStats(textWidth);
        layoutActions();
    }

    /// <summary>Sizes cards from the flow's real inner width, which excludes padding and the scrollbar.</summary>
    private void layoutGrid()
    {
        bool filtering = searchBox.Current.Value.Length > 0 || sourceFilter.Value != InstalledSkinSourceFilter.All || sort.Value != InstalledSkinSort.Name;
        clearButton.Alpha = filtering ? 1 : 0;
        searchField.Padding = new MarginPadding { Right = filtering ? clearButton.DrawWidth + AimModVisualStyle.RelatedSpacing : 0 };

        float gridWidth = list.ChildSize.X;
        bool compact = density.Value == SkinListDensity.List;
        if (gridWidth <= 0 || !gridTracker.Update((gridWidth, rows.Keys.Count > 0, compact)))
            return;
        if (compact)
        {
            foreach (SkinCardView row in rows.Rows)
            {
                row.SetCompact(true);
                row.Width = MathF.Floor(gridWidth);
            }
            return;
        }
        int columns = Math.Max(1, (int)((gridWidth + AimModVisualStyle.RelatedSpacing) / (min_card_width + AimModVisualStyle.RelatedSpacing)));
        float cardWidth = MathF.Floor((gridWidth - AimModVisualStyle.RelatedSpacing * (columns - 1)) / columns - 0.01f);
        foreach (SkinCardView card in rows.Rows)
        {
            card.SetCompact(false);
            card.Width = cardWidth;
        }
    }

    /// <summary>The inspector ends with its content instead of reserving an empty panel below it.</summary>
    private void fitDetailPanel()
    {
        applyStatus.Alpha = string.IsNullOrEmpty(applyStatus.Text.ToString()) ? 0 : 1;
        float available = Math.Max(0, body.ChildSize.Y);
        float content = detailContent.DrawHeight;
        if (!detailHeightTracker.Update((available, content)))
            return;
        // A little slack keeps the scrollbar hidden when everything already fits.
        detailPanel.Height = content <= 0 ? available : Math.Min(available, MathF.Ceiling(content) + 2);
    }

    private static float toolbarBottom(bool twoRows) =>
        toolbar_y + (twoRows ? AimModVisualStyle.ControlHeight * 2 + AimModVisualStyle.RelatedSpacing : AimModVisualStyle.ControlHeight);

    private void layoutActions()
    {
        bool folder = selected?.HasFolder == true;
        openFolderButton.Alpha = folder ? 1 : 0;
        float folderWidth = folder ? openFolderButton.DrawWidth + AimModVisualStyle.RelatedSpacing : 0;
        applyButton.Width = Math.Max(0, actionRow.DrawWidth - folderWidth);
    }

    private void layoutStats(float width)
    {
        int count = statsFlow.Children.Count;
        if (count == 0)
            return;
        float cell = (width - AimModVisualStyle.RelatedSpacing * (count - 1)) / count;
        foreach (Drawable stat in statsFlow.Children)
            stat.Width = Math.Max(60, MathF.Floor(cell));
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        currentTab.BindValueChanged(value => showTab(value.NewValue), true);
        searchBox.QueryChanged += _ => loadSkins();
        searchBox.MoveToResults += () => AimModInteractiveSurface.FocusFirst(list);
        sourceFilter.BindValueChanged(_ => showLoaded());
        sort.BindValueChanged(_ => showLoaded());
        density.BindValueChanged(value =>
        {
            gridButton.SetSelected(value.NewValue == SkinListDensity.Grid);
            listButton.SetSelected(value.NewValue == SkinListDensity.List);
            gridTracker.Reset();
            if (selected is not null && rows.TryGet(selected.SkinId, out SkinCardView card))
                Schedule(() => listScroll.ScrollIntoView(card));
        }, true);
        loadSkins();
    }

    public void SetExternalSelection(Guid? skinId)
    {
        if (lazerSkinId == skinId) return;
        lazerSkinId = skinId;
        refreshRows();
        updateDetails();
    }

    public void SetAppliedSelection(Guid? externalSkinId)
    {
        if (appliedExternalSkinId == externalSkinId) return;
        appliedExternalSkinId = externalSkinId;
        refreshRows();
        updateDetails();
    }

    public void Configure(
        IInstalledSkinSource? source,
        Guid? lazerSkinId,
        Guid? appliedExternalSkinId,
        Func<InstalledLazerSkin, CancellationToken, Task>? applySkin)
    {
        bool selectionChanged = this.lazerSkinId != lazerSkinId || this.appliedExternalSkinId != appliedExternalSkinId;
        bool sourceChanged = !ReferenceEquals(this.source, source);
        this.source = source;
        this.lazerSkinId = lazerSkinId;
        this.appliedExternalSkinId = appliedExternalSkinId;
        this.applySkin = applySkin;

        if (sourceChanged)
            loadSkins();
        else if (selectionChanged)
        {
            refreshRows();
            updateDetails();
        }
    }

    public void ConfigureOnlineDestination(IOnlineSkinArchiveDestination? destination) => onlineView.ConfigureDestination(destination);

    internal SkinsWorkspaceTab GetCurrentTabForTesting() => currentTab.Value;

    public void OpenOnlineSkin(string providerId, string sourceId)
    {
        currentTab.Value = SkinsWorkspaceTab.Online;
        showTab(SkinsWorkspaceTab.Online);
        onlineView.OpenSkin(providerId, sourceId);
    }

    internal void SelectTabForTesting(SkinsWorkspaceTab tab) => currentTab.Value = tab;

    private void showTab(SkinsWorkspaceTab tab)
    {
        bool installed = tab == SkinsWorkspaceTab.Installed;
        installedContent.Alpha = installed ? 1 : 0;
        installedContent.AlwaysPresent = installed;
        onlineContent.Alpha = installed ? 0 : 1;
        onlineContent.AlwaysPresent = !installed;
    }

    private void resetFilters()
    {
        searchBox.Current.Value = string.Empty;
        sourceFilter.Value = InstalledSkinSourceFilter.All;
        sort.Value = InstalledSkinSort.Name;
    }

    private void loadSkins()
    {
        skinSearch?.Cancel();
        skinSearch?.Dispose();
        if (source is null) return;
        skinSearch = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        int requestRevision = ++revision;
        status.Text = "Reading installed skins…";
        listState.SetState(FontAwesome.Solid.PaintBrush, "Reading installed skins", "Your osu! skin folders are being read.", loadedSkins.Count == 0);
        var currentSource = source;
        string query = searchBox.Current.Value;
        var token = skinSearch.Token;
        _ = Task.Run(() => loadSkinsAsync(currentSource, requestRevision, query, token));
    }

    private async Task loadSkinsAsync(IInstalledSkinSource currentSource, int requestRevision, string searchText, CancellationToken cancellationToken)
    {
        try
        {
            // Sorting by date or source needs every match, not only the first page by name.
            var items = new List<InstalledLazerSkin>();
            int total = 0;
            while (items.Count < maximum_loaded_skins)
            {
                InstalledLazerSkinPage page = await currentSource.SearchAsync(searchText, items.Count, limit: 100, cancellationToken: cancellationToken).ConfigureAwait(false);
                items.AddRange(page.Items);
                total = page.Total;
                if (page.Items.Count == 0 || !page.HasMore)
                    break;
            }
            var result = new InstalledLazerSkinPage(items, Math.Max(total, items.Count), 0, Math.Max(1, items.Count));
            if (!IsDisposed)
                Schedule(() => showSkins(requestRevision, result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!IsDisposed)
                Schedule(() => showError(requestRevision, error));
        }
    }

    private IReadOnlyList<InstalledLazerSkin> loadedSkins = Array.Empty<InstalledLazerSkin>();
    private IReadOnlyList<InstalledLazerSkin> visibleSkins = Array.Empty<InstalledLazerSkin>();

    private void showSkins(int requestRevision, InstalledLazerSkinPage page)
    {
        if (requestRevision != revision)
            return;

        loadedSkins = page.Items;
        totalSkins = page.Total;
        bool firstLoad = selected is null;
        if (selected is null || page.Items.All(item => item.SkinId != selected.SkinId))
            selected = page.Items.FirstOrDefault(item => item.SkinId == appliedExternalSkinId)
                       ?? page.Items.FirstOrDefault(item => item.SkinId == lazerSkinId)
                       ?? page.Items.FirstOrDefault();
        showLoaded();
        if (firstLoad && selected is not null && rows.TryGet(selected.SkinId, out SkinCardView card))
            Schedule(() => listScroll.ScrollIntoView(card));
    }

    private void showLoaded()
    {
        visibleSkins = InstalledSkinFilters.Apply(loadedSkins, sourceFilter.Value, sort.Value);
        bool filtering = searchBox.Current.Value.Length > 0 || sourceFilter.Value != InstalledSkinSourceFilter.All;
        status.Text = visibleSkins.Count == 0
            ? "No skins match"
            : filtering || visibleSkins.Count != totalSkins
                ? $"{visibleSkins.Count:N0} of {totalSkins:N0} skins"
                : $"{visibleSkins.Count:N0} skins";
        listState.SetState(
            FontAwesome.Solid.Search,
            filtering ? "No matching skins" : "No installed skins yet",
            filtering
                ? "Try another name or source."
                : "Install one in osu!lazer or osu!stable, or get one online.",
            visibleSkins.Count == 0,
            filtering ? "Clear filters" : "Find skins online",
            filtering ? resetFilters : () => currentTab.Value = SkinsWorkspaceTab.Online);
        refreshRows();
        updateDetails();
    }

    private void showError(int requestRevision, Exception error)
    {
        if (requestRevision != revision)
            return;
        loadedSkins = Array.Empty<InstalledLazerSkin>();
        visibleSkins = Array.Empty<InstalledLazerSkin>();
        rows.Clear();
        status.Text = AimModFriendlyError.Message(error, "Reading installed skins");
        listState.SetState(FontAwesome.Solid.ExclamationTriangle, "Installed skins unavailable",
            "Check that osu! is installed and its skins folder is readable.", true, "Retry", () => loadSkins());
    }

    private void refreshRows()
    {
        // Cards are kept by skin; only badge changes rebuild a card, and selection just restyles it.
        rows.Apply(visibleSkins);
        foreach (SkinCardView row in rows.Rows)
            row.SetSelected(row.SkinId == selected?.SkinId);
        layoutTracker.Reset();
        gridTracker.Reset();
    }

    private void select(InstalledLazerSkin skin) => selectSkin(skin, openDetails: false);

    /// <summary>A card click selects the skin and, in the single-column layout, opens its details.</summary>
    private void selectCard(InstalledLazerSkin skin) => selectSkin(skin, openDetails: true);

    private void selectSkin(InstalledLazerSkin skin, bool openDetails)
    {
        selected = skin;
        foreach (SkinCardView row in rows.Rows)
            row.SetSelected(row.SkinId == skin.SkinId);
        if (openDetails && stacked)
            detailOpen = true;
        applyStatus.Text = string.Empty;
        updateDetails();
    }

    private void closeDetails()
    {
        detailOpen = false;
        if (selected is not null && rows.TryGet(selected.SkinId, out SkinCardView card))
            Schedule(() => listScroll.ScrollIntoView(card));
    }

    private void updateDetails()
    {
        badgeFlow.Clear();
        statsFlow.Clear();
        colourFlow.Clear();
        if (selected is null)
        {
            loadPreview(null);
            selectedName.Text = "Select a skin";
            selectedTag.Alpha = 0;
            selectedCreator.Text = source is null ? "Connect osu! to see your skins." : string.Empty;
            storedName.Text = string.Empty;
            applyButton.SetState(ApplyState.Unavailable, "Use for replays");
            detailPanel.Alpha = stacked ? detailPanel.Alpha : 0;
            layoutTracker.Reset();
            return;
        }

        if (!stacked)
            detailPanel.Alpha = 1;
        InstalledLazerSkin skin = selected;
        loadPreview(skin);
        bool inUse = skin.SkinId == appliedExternalSkinId;
        selectedName.Text = skin.DisplayName;
        selectedTag.Text = skin.DisplayTag ?? string.Empty;
        selectedTag.Alpha = skin.DisplayTag is null ? 0 : 1;
        selectedCreator.Text = $"by {SkinCardView.CreatorLabel(skin)}  ·  {(skin.IsBuiltIn ? "built into osu!lazer" : skin.Origin == InstalledSkinOrigin.Stable ? "osu!stable folder" : "osu!lazer library")}";
        storedName.Text = SkinDisplayName.Differs(skin.Name) ? $"Original name: {skin.Name.Trim()}" : string.Empty;
        // "In use" is already the button state; badges only add what the button cannot say.
        if (skin.SkinId == lazerSkinId)
            badgeFlow.Add(new AimModPill("Active in lazer", AimModPillTone.Info));
        // Empty rows leave the flow so they do not add spacing.
        badgeFlow.Alpha = badgeFlow.Count > 0 ? 1 : 0;
        storedName.Alpha = storedName.Text.ToString().Length > 0 ? 1 : 0;

        if (applying)
            applyButton.SetState(ApplyState.Working, "Applying…");
        else if (inUse)
            applyButton.SetState(ApplyState.Active, "In use");
        else
            applyButton.SetState(applySkin is null ? ApplyState.Unavailable : ApplyState.Ready, "Use for replays");

        if (!skin.IsBuiltIn)
            statsFlow.Add(new StatCell("Files", $"{skin.Summary.FileCount:N0}"));
        if (skin.AddedAt is { } added)
            statsFlow.Add(new StatCell("Added", relativeDate(added)));
        _ = loadAssetDetails(skin);
        layoutTracker.Reset();
    }

    private async Task loadAssetDetails(InstalledLazerSkin skin)
    {
        try
        {
            SkinPreviewAssets assets = await SkinPreviewAssets.LoadAsync(skin, lifetime.Token).ConfigureAwait(false);
            if (IsDisposed)
                return;
            Schedule(() =>
            {
                if (selected?.SkinId != skin.SkinId)
                    return;
                if (assets.Version is { } version)
                    statsFlow.Add(new StatCell("Format", version));
                if (assets.ComboColours.Count > 0)
                {
                    colourFlow.Add(new OsuSpriteText
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Text = "Combo colours",
                        Font = AimModVisualStyle.CaptionFont,
                        Colour = AimModPalette.Muted,
                        Margin = new MarginPadding { Right = 4 },
                    });
                    foreach (Colour4 colour in assets.ComboColours)
                        colourFlow.Add(new Circle { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Size = new(14), Colour = colour });
                }
                layoutTracker.Reset();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
    }

    private void loadPreview(InstalledLazerSkin? skin)
    {
        if (skin?.SkinId == previewSkinId && previewFrame.Children.Count > 1)
            return;
        previewSkinId = skin?.SkinId;
        previewLoad?.Cancel();
        previewLoad?.Dispose();
        previewLoad = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        if (!IsLoaded)
        {
            Schedule(() => loadPreview(skin));
            previewSkinId = null;
            return;
        }
        var view = new SkinPlayfieldView(skin, textures) { Alpha = 0 };
        CancellationToken token = previewLoad.Token;
        LoadComponentAsync(view, loaded =>
        {
            if (token.IsCancellationRequested)
                return;
            foreach (Drawable previous in previewFrame.Children.Skip(1).ToArray())
                previous.FadeOut(AimModVisualStyle.FastTransition).Expire();
            previewFrame.Add(loaded);
            loaded.FadeIn(AimModVisualStyle.FastTransition);
        }, token);
    }

    private void applySelected()
    {
        if (selected is null || applySkin is null || applying || selected.SkinId == appliedExternalSkinId)
            return;
        InstalledLazerSkin target = selected;
        applying = true;
        applyStatus.Text = string.Empty;
        updateDetails();
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
                    applying = false;
                    appliedExternalSkinId = target.SkinId;
                    applyStatus.Colour = AimModPalette.Muted;
                    applyStatus.Text = "Replays now play with this skin.";
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
                    applying = false;
                    // Apply-service exceptions carry player-facing reasons; others are summarised.
                    applyStatus.Colour = AimModPalette.Danger;
                    applyStatus.Text = error is ExternalLazerSkinApplyException or InvalidOperationException
                        ? $"Could not use this skin: {error.Message}"
                        : AimModFriendlyError.Message(error, $"Applying {target.DisplayName}");
                    updateDetails();
                });
        }
    }

    private void openSelectedFolder()
    {
        if (selected is not { HasFolder: true } skin || host is null)
            return;
        if (!host.OpenFileExternally(skin.SourcePath))
        {
            applyStatus.Colour = AimModPalette.Danger;
            applyStatus.Text = "Could not open the skin folder.";
        }
    }

    private static string relativeDate(DateTimeOffset value)
    {
        TimeSpan age = DateTimeOffset.UtcNow - value;
        if (age < TimeSpan.Zero || value.Year < 2000)
            return "Unknown";
        if (age.TotalDays < 1)
            return "Today";
        if (age.TotalDays < 2)
            return "Yesterday";
        if (age.TotalDays < 30)
            return $"{(int)age.TotalDays} days ago";
        return value.LocalDateTime.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
    }

    protected override void Dispose(bool isDisposing)
    {
        skinSearch?.Cancel();
        skinSearch?.Dispose();
        previewLoad?.Cancel();
        previewLoad?.Dispose();
        lifetime.Cancel();
        lifetime.Dispose();
        base.Dispose(isDisposing);
        textures?.Dispose();
    }

    private static TruncatingSpriteText detailText(float size, Colour4 colour, string weight, string value) => new()
    {
        Text = value,
        Font = new FontUsage(size: size, weight: weight),
        Colour = colour,
    };

    /// <summary>Shared dropdown with an inline label ("Sort · Name") so the toolbar needs no captions above it.</summary>
    private partial class SkinsDropdown<T> : AimModDropdown<T>
    {
        public SkinsDropdown(string prefix)
        {
            if (Header is AimModDropdownHeader<T> header)
                header.Prefix = prefix;
        }
    }

    private partial class StatCell : CompositeDrawable
    {
        public StatCell(string label, string value)
        {
            Width = 100;
            Height = 50;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                new OsuSpriteText { Position = new(10, 8), Text = label, Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted },
                new TruncatingSpriteText { Position = new(10, 24), Text = value, Font = new FontUsage(size: 15, weight: "SemiBold"), Colour = AimModPalette.Text, MaxWidth = 90 },
            };
        }

        protected override void Update()
        {
            base.Update();
            if (InternalChildren[^1] is TruncatingSpriteText value)
                value.MaxWidth = Math.Max(20, DrawWidth - 20);
        }
    }

    private partial class SkinListState : CompositeDrawable
    {
        private readonly SpriteIcon icon;
        private readonly OsuSpriteText title;
        private readonly OsuSpriteText detail;
        private readonly AimModButton actionButton;
        private Action? action;

        public SkinListState(IconUsage initialIcon, string initialTitle, string initialDetail, string actionCaption, Action? initialAction = null)
        {
            action = initialAction;
            RelativeSizeAxes = Axes.Both;
            InternalChild = new FillFlowContainer
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                RelativeSizeAxes = Axes.X,
                Padding = new MarginPadding { Horizontal = 24 },
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
                    actionButton = new AimModButton(actionCaption, () => action?.Invoke())
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Margin = new MarginPadding { Top = 4 },
                        Alpha = 0,
                    },
                },
            };
        }

        /// <summary>Every empty state offers the one thing the player can do next.</summary>
        public void SetState(IconUsage stateIcon, string stateTitle, string stateDetail, bool visible, string? actionCaption = null, Action? stateAction = null)
        {
            icon.Icon = stateIcon;
            title.Text = stateTitle;
            detail.Text = stateDetail;
            action = stateAction;
            if (actionCaption is not null)
                actionButton.SetCaption(actionCaption);
            actionButton.Alpha = stateAction is null ? 0 : 1;
            this.FadeTo(visible ? 1 : 0, 120);
        }
    }

    internal enum ApplyState
    {
        Unavailable,
        Ready,
        Working,
        Active,
    }

    /// <summary>The one primary action: mint when it can apply, muted mint with a check once the skin is in use.</summary>
    private partial class ApplyButton : AimModInteractiveSurface
    {
        private readonly Action action;
        private readonly SpriteIcon icon;
        private readonly SpriteText label;
        private ApplyState state;

        public ApplyButton(Action action)
        {
            this.action = action;
            RelativeSizeAxes = Axes.None;
            Height = AimModVisualStyle.ControlHeight;
            Width = 200;
            CornerRadius = AimModVisualStyle.ControlRadius;
            Child = new FillFlowContainer
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Horizontal,
                Spacing = new(8),
                Children = new Drawable[]
                {
                    icon = new SpriteIcon { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Size = new(13), Icon = FontAwesome.Solid.Check },
                    label = new OsuSpriteText { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Font = new FontUsage(size: 14, weight: "Bold") },
                },
            };
            SetState(ApplyState.Unavailable, "Use for replays");
        }

        public void SetState(ApplyState value, string text)
        {
            state = value;
            label.Text = text;
            (BackgroundColour, BorderColour, label.Colour) = value switch
            {
                ApplyState.Ready => (AimModPalette.Accent, AimModPalette.Accent, AimModPalette.Canvas),
                ApplyState.Active => (AimModPalette.AccentMuted, AimModPalette.Accent.Opacity(0.45f), AimModPalette.Accent),
                _ => (AimModPalette.PanelHover, AimModPalette.Border, AimModPalette.Muted),
            };
            icon.Colour = label.Colour;
            icon.Icon = value == ApplyState.Active ? FontAwesome.Solid.Check : value == ApplyState.Working ? FontAwesome.Solid.Spinner : FontAwesome.Solid.Play;
            this.FadeTo(value == ApplyState.Unavailable ? 0.6f : 1, AimModVisualStyle.FastTransition);
        }

        protected override bool OnClick(ClickEvent e)
        {
            if (state == ApplyState.Ready)
                action();
            base.OnClick(e);
            return true;
        }
    }

    internal enum SkinListDensity
    {
        Grid,
        List,
    }

    internal enum SkinsWorkspaceTab
    {
        Installed,
        Online,
    }
}
