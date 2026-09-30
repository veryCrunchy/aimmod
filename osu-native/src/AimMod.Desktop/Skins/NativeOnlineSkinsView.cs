using AimMod.Desktop.Skins.Online;
using AimMod.Desktop.Visuals;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Framework.Threading;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Skins;

public partial class NativeOnlineSkinsView : CompositeDrawable
{
    private const float gap = AimModVisualStyle.RelatedSpacing;
    private const string browseProviderId = "browse-provider";
    private const float min_card_width = 200;

    private readonly OnlineSkinCatalogBackend? backend;
    private IOnlineSkinArchiveDestination? destination;
    private readonly string saveDirectory;
    private readonly Action<Uri> openExternal;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Bindable<string> provider = new("All providers");
    private readonly Bindable<OnlineSkinRuleset> ruleset = new(OnlineSkinRuleset.Standard);
    private readonly Bindable<OnlineSkinSort> sort = new(OnlineSkinSort.Newest);
    private readonly AimModTextBox search;
    private readonly AimModInlineStatus searchStatus;
    private readonly KeyedFlow<string, OnlineSkinCatalogEntry, OnlineSkinCard> rows;
    private AimModLayout.ChangeTracker<(float, float)> layoutTracker;
    private AimModLayout.ChangeTracker<(float, int)> gridTracker;
    private AimModLayout.ChangeTracker<(float, float)> detailHeightTracker;
    private readonly Container filterBand;
    private readonly Container searchGroup;
    private readonly Container searchField;
    private readonly Container providerGroup;
    private readonly Container rulesetGroup;
    private readonly Container sortGroup;
    private readonly TruncatingSpriteText status;
    private readonly AimModResetButton resetFilters;
    private readonly Container resultViewport;
    private readonly Container listPanel;
    private readonly FillFlowContainer<Drawable> results;
    private readonly OnlineListState listState;
    private readonly Container detailPanel;
    private readonly Container detailContent;
    private readonly OnlinePreviewGallery gallery;
    private readonly Container artwork;
    private readonly FillFlowContainer detailActions;
    private readonly TruncatingSpriteText selectedName;
    private readonly TruncatingSpriteText selectedCreator;
    private readonly FillFlowContainer badges;
    private readonly FillFlowContainer stats;
    private readonly TruncatingSpriteText attribution;
    private readonly TextFlowContainer downloadStatus;
    private readonly OnlineActionButton previewButton;
    private readonly OnlineActionButton saveButton;
    private readonly OnlineActionButton importButton;
    private readonly OnlineActionButton sourceButton;
    private readonly Container secondaryActions;
    private readonly AimModLoadingOverlay loading;
    private ScheduledDelegate? scheduledSearch;
    private CancellationTokenSource? requestCancellation;
    private IReadOnlyList<OnlineSkinCatalogEntry> loaded = [];
    private OnlineSkinCatalogEntry? selected;
    private OnlineSkinPreview? preparedPreview;
    private Uri? handoffUri;
    private int revision;
    private CancellationTokenSource? selectionCancellation;
    private bool preparing;
    private bool browseOnly;

    public NativeOnlineSkinsView(
        OnlineSkinCatalogBackend? backend,
        IOnlineSkinArchiveDestination? destination,
        string saveDirectory,
        Action<Uri>? openExternal = null)
    {
        this.backend = backend;
        this.destination = destination;
        this.saveDirectory = saveDirectory;
        this.openExternal = openExternal ?? OnlineSkinBrowserHandoff.Open;
        RelativeSizeAxes = Axes.Both;

        string[] providerItems = backend is null
            ? ["All providers"]
            : ["All providers", .. backend.Catalog.Providers.Select(item => item.DisplayName)];
        InternalChildren = new Drawable[]
        {
            // Filters sit ahead of results with a lower depth so open menus draw over the cards.
            filterBand = new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = AimModVisualStyle.ControlHeight,
                Depth = -20,
                Children = new Drawable[]
                {
                    searchGroup = filterField(new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Children = new Drawable[]
                        {
                            searchField = new Container
                            {
                                RelativeSizeAxes = Axes.Both,
                                Child = search = new AimModTextBox
                                {
                                    RelativeSizeAxes = Axes.X,
                                    Height = AimModVisualStyle.ControlHeight,
                                    PlaceholderText = "Search online skins",
                                    FocusOnSearchShortcut = true,
                                },
                            },
                            resetFilters = new AimModResetButton(() =>
                            {
                                search.Current.Value = string.Empty; provider.Value = "All providers";
                                ruleset.Value = OnlineSkinRuleset.Standard; sort.Value = OnlineSkinSort.Newest;
                            }, "Clear") { Width = 72, Height = AimModVisualStyle.ControlHeight, Anchor = Anchor.CentreRight, Origin = Anchor.CentreRight, Alpha = 0 },
                        },
                    }),
                    providerGroup = filterField(new LabelledDropdown<string>(string.Empty)
                    {
                        RelativeSizeAxes = Axes.X,
                        Items = providerItems,
                        Current = provider,
                    }),
                    rulesetGroup = filterField(new LabelledDropdown<OnlineSkinRuleset>("Mode")
                    {
                        RelativeSizeAxes = Axes.X,
                        Items = Enum.GetValues<OnlineSkinRuleset>(),
                        Current = ruleset,
                    }),
                    sortGroup = filterField(new LabelledDropdown<OnlineSkinSort>("Sort")
                    {
                        RelativeSizeAxes = Axes.X,
                        Items = Enum.GetValues<OnlineSkinSort>(),
                        Current = sort,
                    }),
                },
            },
            status = new TruncatingSpriteText
            {
                Y = AimModVisualStyle.ControlHeight + 10,
                Text = backend is null ? "Online catalog unavailable" : "Loading online skin catalogs",
                Font = AimModVisualStyle.CaptionStrongFont,
                Colour = AimModPalette.Muted,
            },
            resultViewport = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = AimModVisualStyle.ControlHeight + 38 },
                Children = new Drawable[]
                {
                    listPanel = new Container
                    {
                        RelativeSizeAxes = Axes.Y,
                        Width = 600,
                        Children = new Drawable[]
                        {
                            new AimModScrollContainer
                            {
                                RelativeSizeAxes = Axes.Both,
                                Child = new FillFlowContainer
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Direction = FillDirection.Vertical,
                                    Spacing = new(gap),
                                    Padding = new MarginPadding { Right = 16, Bottom = gap },
                                    Children = new Drawable[]
                                    {
                                        searchStatus = new AimModInlineStatus(),
                                        results = new FillFlowContainer<Drawable>
                                        {
                                            RelativeSizeAxes = Axes.X,
                                            AutoSizeAxes = Axes.Y,
                                            Direction = FillDirection.Full,
                                            Spacing = new(gap),
                                        },
                                    },
                                },
                            },
                            listState = new OnlineListState(),
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
                                        artwork = new Container
                                        {
                                            RelativeSizeAxes = Axes.X,
                                            Height = 258,
                                            Child = gallery = new OnlinePreviewGallery(backend?.Screenshots),
                                        },
                                        detailActions = new FillFlowContainer
                                        {
                                            RelativeSizeAxes = Axes.X,
                                            AutoSizeAxes = Axes.Y,
                                            Y = 272,
                                            Padding = new MarginPadding { Horizontal = 16, Bottom = 16 },
                                            Direction = FillDirection.Vertical,
                                            Spacing = new(gap),
                                            Children = new Drawable[]
                                            {
                                                selectedName = text(20, AimModPalette.Text, "Bold", "Select an online skin"),
                                                selectedCreator = text(12, AimModPalette.Muted, "Regular", string.Empty),
                                                badges = new FillFlowContainer
                                                {
                                                    AutoSizeAxes = Axes.Both,
                                                    Direction = FillDirection.Horizontal,
                                                    Spacing = new(6),
                                                },
                                                importButton = new OnlineActionButton(FontAwesome.Solid.FileImport, "Import into osu!", importSelected, primary: true),
                                                previewButton = new OnlineActionButton(FontAwesome.Solid.Download, "Download skin", prepareSelected, primary: true),
                                                secondaryActions = new Container
                                                {
                                                    RelativeSizeAxes = Axes.X,
                                                    Height = AimModVisualStyle.CompactControlHeight,
                                                    Children = new Drawable[]
                                                    {
                                                        saveButton = new OnlineActionButton(FontAwesome.Solid.Save, "Save .osk", saveSelected) { Width = 0.5f },
                                                        sourceButton = new OnlineActionButton(FontAwesome.Solid.ExternalLinkAlt, "Source page", openSource)
                                                        {
                                                            Anchor = Anchor.TopRight,
                                                            Origin = Anchor.TopRight,
                                                            Width = 0.5f,
                                                        },
                                                    },
                                                },
                                                downloadStatus = new TextFlowContainer(sprite =>
                                                {
                                                    sprite.Font = osu.Game.Graphics.OsuFont.GetFont(size: 12, weight: osu.Game.Graphics.FontWeight.SemiBold);
                                                    sprite.Colour = AimModPalette.Cyan;
                                                }) { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y },
                                                stats = new FillFlowContainer
                                                {
                                                    RelativeSizeAxes = Axes.X,
                                                    AutoSizeAxes = Axes.Y,
                                                    Direction = FillDirection.Full,
                                                    Spacing = new(gap),
                                                    Margin = new MarginPadding { Top = 4 },
                                                },
                                                attribution = text(AimModVisualStyle.MinReadableFontSize, AimModPalette.Muted, "Regular", string.Empty),
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
            loading = new AimModLoadingOverlay(),
        };
        rows = new KeyedFlow<string, OnlineSkinCatalogEntry, OnlineSkinCard>(results, rowKey,
            item => $"{item.Name}|{item.Creator}|{item.IsSensitive}|{item.PreviewUris.FirstOrDefault()}|{item.DownloadCount}",
            item => new OnlineSkinCard(item, isSelected(item), () => select(item), backend?.Screenshots));
        updateDetails();
    }

    private static string rowKey(OnlineSkinCatalogEntry item) => $"{item.ProviderId}/{item.Id}";

    private bool isSelected(OnlineSkinCatalogEntry item) =>
        selected is not null && selected.ProviderId == item.ProviderId && selected.Id == item.Id;

    private (string Provider, string Id)? pendingLink;
    private bool linkRoutingReady;

    public void OpenSkin(string providerId, string sourceId)
    {
        pendingLink = (providerId, sourceId);
        if (linkRoutingReady)
            openPendingLink();
    }

    private void openPendingLink()
    {
        if (pendingLink is not { } target || backend is null)
            return;
        pendingLink = null;
        scheduledSearch?.Cancel();
        requestCancellation?.Cancel();
        requestCancellation?.Dispose();
        requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        int requestRevision = ++revision;
        select(null);
        loaded = [];
        refreshRows();
        searchStatus.ShowLoading("Loading the linked skin...", cancelSearch);
        _ = openLinkAsync(target.Provider, target.Id, requestRevision, requestCancellation.Token);
    }

    private async Task openLinkAsync(string providerId, string sourceId, int requestRevision, CancellationToken token)
    {
        try
        {
            var details = await backend!.Catalog.GetDetailsAsync(providerId, sourceId, token).ConfigureAwait(false);
            if (!IsDisposed)
                Schedule(() =>
                {
                    if (requestRevision != revision || token.IsCancellationRequested)
                        return;
                    searchStatus.Dismiss();
                    loaded = details is null ? [] : [details];
                    selected = details;
                    status.Text = details is null ? "This skin is unavailable. Try again or visit its source." : details.Name;
                    listState.SetState(FontAwesome.Solid.Search, "Skin unavailable", "The selected skin could not be loaded.", details is null);
                    refreshRows();
                    updateDetails();
                });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!IsDisposed)
                Schedule(() => showError(requestRevision, error, "Loading the linked skin"));
        }
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        search.QueryChanged += _ => searchCatalog();
        search.MoveToResults += () => AimModInteractiveSurface.FocusFirst(results);
        provider.BindValueChanged(_ => scheduleSearch());
        ruleset.BindValueChanged(_ => scheduleSearch());
        sort.BindValueChanged(_ => scheduleSearch());
        linkRoutingReady = true;
        if (pendingLink is not null)
            openPendingLink();
        else
            searchCatalog();
    }

    /// <summary>Below this width the result list and inspector stack vertically.</summary>
    internal const float StackedWidth = 720;

    protected override void Update()
    {
        base.Update();
        bool filtering = search.Current.Value.Length > 0 || provider.Value != "All providers"
                         || ruleset.Value != OnlineSkinRuleset.Standard || sort.Value != OnlineSkinSort.Newest;
        resetFilters.Alpha = filtering ? 1 : 0;
        searchField.Padding = new MarginPadding { Right = filtering ? resetFilters.DrawWidth + gap : 0 };
        layoutGrid();
        fitDetailPanel();
        if (!layoutTracker.Update((DrawWidth, detailContent.DrawWidth)))
            return;

        float width = Math.Max(1, DrawWidth);
        const float control = AimModVisualStyle.ControlHeight;
        if (width < 640)
        {
            // Narrow windows give each filter a full row.
            filterBand.Height = control * 4 + gap * 3;
            place(searchGroup, 0, 0, width);
            place(providerGroup, 0, control + gap, width);
            place(rulesetGroup, 0, (control + gap) * 2, width);
            place(sortGroup, 0, (control + gap) * 3, width);
        }
        else if (width < 980)
        {
            float column = (width - gap * 2) / 3;
            filterBand.Height = control * 2 + gap;
            place(searchGroup, 0, 0, width);
            place(providerGroup, 0, control + gap, column);
            place(rulesetGroup, column + gap, control + gap, column);
            place(sortGroup, (column + gap) * 2, control + gap, column);
        }
        else
        {
            float dropdown = Math.Clamp(width * 0.15f, 170, 220);
            float searchWidth = width - dropdown * 3 - gap * 3;
            place(searchGroup, 0, 0, searchWidth);
            place(providerGroup, searchWidth + gap, 0, dropdown);
            place(rulesetGroup, searchWidth + gap * 2 + dropdown, 0, dropdown);
            place(sortGroup, searchWidth + gap * 3 + dropdown * 2, 0, dropdown);
            filterBand.Height = control;
        }
        status.Y = filterBand.Height + 10;
        resultViewport.Padding = new MarginPadding { Top = filterBand.Height + 38 };

        float panelWidth;
        stacked = width < StackedWidth;
        if (stacked)
        {
            panelWidth = width;
            listPanel.RelativeSizeAxes = Axes.Both;
            listPanel.Width = 1;
            listPanel.Height = 0.5f;
            detailPanel.Anchor = detailPanel.Origin = Anchor.BottomLeft;
            detailPanel.Width = width;
        }
        else
        {
            float detailWidth = Math.Clamp((width - AimModVisualStyle.SectionSpacing) * 0.36f, 330, 480);
            panelWidth = Math.Max(0, width - detailWidth - AimModVisualStyle.SectionSpacing);
            listPanel.RelativeSizeAxes = Axes.Y;
            listPanel.Width = panelWidth;
            listPanel.Height = 1;
            detailPanel.Anchor = detailPanel.Origin = Anchor.TopRight;
            detailPanel.Width = detailWidth;
        }
        status.MaxWidth = Math.Max(0, panelWidth);
        float contentWidth = Math.Max(0, detailContent.DrawWidth);
        if (hasArtwork)
        {
            artwork.Height = OnlinePreviewGallery.HeightFor(contentWidth);
            detailActions.Y = artwork.Height + 14;
        }
        float detailTextWidth = Math.Max(0, contentWidth - 32);
        selectedName.MaxWidth = detailTextWidth;
        selectedCreator.MaxWidth = detailTextWidth;
        attribution.MaxWidth = detailTextWidth;
        layoutStats(detailTextWidth);
        secondaryActions.Children[0].Width = secondaryActions.Children[1].Width = detailTextWidth <= gap ? 0.5f : 0.5f - gap / 2 / detailTextWidth;
    }

    private bool stacked;
    private bool hasArtwork;

    private void layoutGrid()
    {
        float gridWidth = results.DrawWidth;
        if (gridWidth <= 0 || !gridTracker.Update((gridWidth, rows.Keys.Count)))
            return;
        int columns = Math.Max(1, (int)((gridWidth + gap) / (min_card_width + gap)));
        float cardWidth = MathF.Floor((gridWidth - gap * (columns - 1)) / columns - 0.01f);
        foreach (OnlineSkinCard card in rows.Rows)
            card.Width = cardWidth;
    }

    /// <summary>The inspector ends with its content instead of reserving an empty panel below it.</summary>
    private void fitDetailPanel()
    {
        float available = Math.Max(0, resultViewport.ChildSize.Y * (stacked ? 0.48f : 1));
        float content = detailContent.DrawHeight;
        if (!detailHeightTracker.Update((available, content)))
            return;
        // A little slack keeps the scrollbar hidden when everything already fits.
        detailPanel.Height = content <= 0 ? available : Math.Min(available, MathF.Ceiling(content) + 2);
    }

    private void layoutStats(float width)
    {
        int count = stats.Children.Count;
        if (count == 0)
            return;
        float cell = (width - gap * (count - 1)) / count;
        foreach (Drawable stat in stats.Children)
            stat.Width = Math.Max(60, MathF.Floor(cell));
    }

    internal string SelectedProviderForTesting => provider.Value;
    internal OnlineSkinRuleset SelectedRulesetForTesting => ruleset.Value;
    internal OnlineSkinSort SelectedSortForTesting => sort.Value;

    internal void RefreshForTesting() => searchCatalog();

    public void ConfigureDestination(IOnlineSkinArchiveDestination? value)
    {
        destination = value;
        updateDetails();
    }

    private void scheduleSearch()
    {
        scheduledSearch?.Cancel();
        scheduledSearch = Scheduler.AddDelayed(searchCatalog, 320);
    }

    private void searchCatalog()
    {
        if (backend is null)
        {
            listState.SetState(FontAwesome.Solid.ExclamationTriangle, "Online catalog unavailable", "The catalog backend was not configured.", true);
            return;
        }
        requestCancellation?.Cancel();
        requestCancellation?.Dispose();
        requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        int requestRevision = ++revision;
        scheduledSearch?.Cancel();
        status.Text = "Searching online skins…";
        listState.SetState(FontAwesome.Solid.Search, "Searching skin catalogs", "Reading cached provider pages and public metadata.", loaded.Count == 0);
        searchStatus.ShowLoading(provider.Value == "All providers" ? "Searching osuskins.net and skins.osuck.net..." : $"Searching {provider.Value}...", cancelSearch);
        string[]? providers = provider.Value == "All providers"
            ? null
            : backend.Catalog.Providers.Where(item => item.DisplayName == provider.Value).Select(item => item.Id).ToArray();
        var query = new OnlineSkinCatalogQuery(search.Current.Value, ruleset.Value, sort.Value, IncludeSensitive: true, PageSize: 30);
        _ = searchAsync(requestRevision, query, providers, requestCancellation.Token);
    }

    private async Task searchAsync(int requestRevision, OnlineSkinCatalogQuery query, string[]? providers, CancellationToken cancellationToken)
    {
        try
        {
            OnlineSkinCatalogSearchResult result = await backend!.Catalog.SearchAsync(query, providers, cancellationToken).ConfigureAwait(false);
            if (!IsDisposed)
                Schedule(() => showResults(requestRevision, result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!IsDisposed)
                Schedule(() => showError(requestRevision, error, "Searching online skins"));
        }
    }

    private void cancelSearch()
    {
        scheduledSearch?.Cancel();
        requestCancellation?.Cancel();
        ++revision;
        status.Text = "Search cancelled";
        searchStatus.ShowMessage("Search cancelled.", searchCatalog);
        listState.SetState(FontAwesome.Solid.Search, "Search cancelled", "Retry to search the skin catalogs again.", loaded.Count == 0);
    }

    private void showResults(int requestRevision, OnlineSkinCatalogSearchResult response)
    {
        if (requestRevision != revision)
            return;
        searchStatus.Dismiss();
        results.FadeIn(AimModVisualStyle.HoverTransition);
        var skins = response.Items
            .GroupBy(item => $"{item.Name}\n{item.Creator}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        loaded = BrowserFallbackEntries(response).Concat(skins).ToArray();
        string[] unavailable = response.Providers.Where(item => item.Page.Status != OnlineSkinCatalogStatus.Success).Select(item => item.ProviderName).ToArray();
        status.Text = unavailable.Length == 0
            ? $"{skins.Length:N0} skins from {string.Join(" and ", response.Providers.Select(item => item.ProviderName))}"
            : $"{skins.Length:N0} skins · {string.Join(", ", unavailable)} unavailable";
        listState.SetState(
            FontAwesome.Solid.Search,
            "No online skins found",
            unavailable.Length == response.Providers.Count ? "The providers are unavailable. Open a source site or try again later." : "Try a different search, mode, provider, or sort order.",
            loaded.Count == 0);
        refreshRows();
        if (selected is null || loaded.All(item => item.ProviderId != selected.ProviderId || item.Id != selected.Id))
            select(loaded.FirstOrDefault());
    }

    private void showError(int requestRevision, Exception error, string action)
    {
        if (requestRevision != revision)
            return;
        status.Text = "Online skins unavailable";
        searchStatus.ShowError(error, action, searchCatalog);
        // Earlier results stay readable but dimmed until a retry succeeds.
        results.FadeTo(0.45f, AimModVisualStyle.HoverTransition);
        listState.SetState(FontAwesome.Solid.ExclamationTriangle, "Could not search skin catalogs", "Try again or open a provider site in your browser.", loaded.Count == 0);
    }

    private void refreshRows()
    {
        rows.Apply(loaded);
        foreach (OnlineSkinCard row in rows.Rows)
            row.SetSelected(isSelected(row.Entry));
    }

    private void select(OnlineSkinCatalogEntry? item, bool readDetails = true)
    {
        selectionCancellation?.Cancel();
        selectionCancellation?.Dispose();
        selectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        preparing = false;
        browseOnly = item?.Id == browseProviderId;
        loading.HideLoading();
        _ = releasePreparedPreview();
        selected = item;
        handoffUri = null;
        downloadStatus.Clear();
        foreach (OnlineSkinCard row in rows.Rows)
            row.SetSelected(isSelected(row.Entry));
        updateDetails();
        if (item is not null && readDetails && !browseOnly)
            _ = loadDetails(item, selectionCancellation.Token);
    }

    private async Task loadDetails(OnlineSkinCatalogEntry item, CancellationToken cancellationToken)
    {
        try
        {
            OnlineSkinCatalogEntry? details = await backend!.Catalog.GetDetailsAsync(item.ProviderId, item.Id, cancellationToken).ConfigureAwait(false);
            if (details is not null && !IsDisposed)
                Schedule(() =>
                {
                    if (IsDisposed || cancellationToken.IsCancellationRequested || selected?.ProviderId != item.ProviderId || selected.Id != item.Id)
                        return;
                    selected = details;
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
                    if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                        status.Text = AimModFriendlyError.Message(error, "Loading skin details");
                });
        }
    }

    private void updateDetails()
    {
        OnlineSkinCatalogEntry? item = selected;
        bool canBrowse = browseOnly || item is null && browserProvider() is not null;
        hasArtwork = !canBrowse && item?.PreviewUris.Count > 0;
        artwork.Alpha = hasArtwork ? 1 : 0;
        artwork.Height = hasArtwork ? OnlinePreviewGallery.HeightFor(detailContent.DrawWidth) : 0;
        detailActions.Y = hasArtwork ? artwork.Height + 14 : 16;
        gallery.SetImages(item?.PreviewUris ?? []);
        selectedName.Text = canBrowse ? $"Browse {item?.Attribution.ProviderName ?? provider.Value}" : (item is null ? "Select an online skin" : SkinDisplayName.Split(item.Name).Title);
        selectedCreator.Text = canBrowse ? "Pick a skin in the download window." : item is null ? "Screenshots and downloads appear here." : $"by {item.Creator}";
        bool downloadable = canBrowse || backend?.CanPrepare(item) == true;
        bool available = preparedPreview?.IsAvailable == true;
        bool canPrepare = downloadable && item?.IsSensitive != true;

        badges.Clear();
        stats.Clear();
        if (item is not null && !canBrowse)
        {
            if (available)
                badges.Add(new AimModPill("Downloaded", AimModPillTone.Success));
            if (item.IsSensitive)
                badges.Add(new AimModPill("Sensitive content", AimModPillTone.Accent));
            if (!string.IsNullOrWhiteSpace(item.Variant))
                badges.Add(new AimModPill(item.Variant));
            foreach ((string label, string value) in metadata(item))
                stats.Add(new OnlineStatCell(label, value));
        }
        attribution.Text = canBrowse || item is null
            ? string.Empty
            : item.Attribution.Notice.Length > 0 ? item.Attribution.Notice : $"Listed on {item.Attribution.ProviderName}";

        // One primary action: import when an osu! client can take it, otherwise the download step.
        bool firstStep = !available && (canBrowse || item?.IsSensitive == true);
        bool importPrimary = destination is not null && !firstStep;
        importButton.Shown = importPrimary;
        previewButton.Shown = !importPrimary;
        importButton.SetState(!preparing && (available || canPrepare), available ? "Import into osu!" : "Download & import");
        previewButton.SetState(!preparing && downloadable && !available,
            !downloadable ? "Download unavailable" : canBrowse ? "Browse & download" : item?.IsSensitive == true ? "Confirm & download" : available ? "Downloaded" : "Download skin");
        saveButton.SetState(!preparing && (available || canPrepare), "Save .osk");
        sourceButton.SetState(item is not null, handoffUri is null ? "Source page" : "Download page");
        layoutTracker.Reset();
    }

    private static IEnumerable<(string Label, string Value)> metadata(OnlineSkinCatalogEntry item)
    {
        if (item.FileSizeBytes is long bytes)
            yield return ("Size", $"{bytes / (1024d * 1024):0.#} MB");
        if (item.DownloadCount is long downloads)
            yield return ("Downloads", CompactCount(downloads));
        if (item.SupportedRulesets.Count > 0)
            yield return ("Mode", string.Join(", ", item.SupportedRulesets));
    }

    private void prepareSelected()
    {
        prepareSelected(null);
    }

    private void prepareSelected(Action? afterPrepared)
    {
        if (selected is null && !preparing && browserProvider() is { } source)
        {
            select(new(source.Id, "browser-" + Guid.NewGuid().ToString("N"), "Downloaded skin", source.DisplayName,
                source.HomePage, [], new(source.Id, source.DisplayName, source.HomePage, source.DisplayName)), readDetails: false);
            browseOnly = true;
        }
        if (selected is null || backend is null || preparing)
            return;
        if (!backend.CanPrepare(selected))
        {
            setDownloadStatus("No automatic download is available for this source. Try Creator releases.");
            updateDetails();
            return;
        }
        preparing = true;
        loading.ShowLoading("Downloading skin", "Downloading and checking the skin archive", onCancel: cancelPreparation);
        updateDetails();
        _ = prepareAsync(selected, selectionCancellation?.Token ?? lifetime.Token, afterPrepared, browseOnly);
    }

    private IOnlineSkinCatalogProvider? browserProvider() => backend?.Catalog.Providers.FirstOrDefault(source =>
        source.DisplayName == provider.Value && source.Id is "osuskins-net" or "skins-osuck-net");

    internal static IReadOnlyList<OnlineSkinCatalogEntry> BrowserFallbackEntries(OnlineSkinCatalogSearchResult response) =>
        response.Providers.Where(source => source.ProviderId is "osuskins-net" or "skins-osuck-net"
            && (source.Page.Status != OnlineSkinCatalogStatus.Success || source.Page.Items.Count == 0))
        .Select(source => new OnlineSkinCatalogEntry(source.ProviderId, browseProviderId,
            $"Browse {source.ProviderName}", "Download, save or import a skin", source.HomePage, [],
            new(source.ProviderId, source.ProviderName, source.HomePage, string.Empty)))
        .ToArray();

    private async Task prepareAsync(OnlineSkinCatalogEntry item, CancellationToken cancellationToken, Action? afterPrepared, bool browserOnly)
    {
        try
        {
            var progress = new Progress<string>(message =>
            {
                if (!IsDisposed)
                    Schedule(() =>
                    {
                        if (IsDisposed || cancellationToken.IsCancellationRequested || !preparing || selected?.Id != item.Id || selected.ProviderId != item.ProviderId) return;
                        loading.SetProgress(message, 0, 0);
                        setDownloadStatus(message);
                    });
            });
            OnlineSkinPreviewResult result = browserOnly
                ? await backend!.Previews.PrepareFromBrowserAsync(item, cancellationToken, progress).ConfigureAwait(false)
                : await backend!.Previews.PrepareAsync(item, allowSensitive: item.IsSensitive, cancellationToken, progress).ConfigureAwait(false);
            if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                Schedule(() =>
                {
                    if (IsDisposed || cancellationToken.IsCancellationRequested || selected?.ProviderId != item.ProviderId || selected.Id != item.Id)
                    {
                        if (result.Preview is not null)
                            _ = result.Preview.DisposeAsync();
                        return;
                    }
                    loading.HideLoading();
                    preparing = false;
                    preparedPreview = result.Preview;
                    if (result.Preview is not null)
                    {
                        selected = result.Preview.Skin;
                        browseOnly = false;
                    }
                    handoffUri = result.Status == OnlineSkinDownloadStatus.ExternalBrowserRequired ? result.ExternalUri : null;
                    status.Text = result.Status switch
                    {
                        OnlineSkinDownloadStatus.Success => $"{result.Preview!.Skin.Name} is ready.",
                        OnlineSkinDownloadStatus.ExternalBrowserRequired => result.Message ?? "This download must be completed in your browser.",
                        _ => result.Message ?? "The skin package could not be prepared.",
                    };
                    setDownloadStatus(status.Text.ToString());
                    updateDetails();
                    if (preparedPreview?.IsAvailable == true)
                        afterPrepared?.Invoke();
                });
            else if (result.Preview is not null)
                await result.Preview.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!IsDisposed)
                Schedule(() =>
                {
                    if (IsDisposed || cancellationToken.IsCancellationRequested) return;
                    preparing = false;
                    loading.HideLoading();
                    status.Text = AimModFriendlyError.Message(error, "Preparing the skin");
                    setDownloadStatus(status.Text.ToString());
                    updateDetails();
                });
        }
    }

    private void saveSelected()
    {
        if (preparing) return;
        if (preparedPreview is null)
        {
            prepareSelected(saveSelected);
            return;
        }
        preparing = true;
        updateDetails();
        _ = saveAsync(preparedPreview, selectionCancellation?.Token ?? lifetime.Token);
    }

    private void setDownloadStatus(string message)
    {
        downloadStatus.Clear();
        downloadStatus.AddText(message);
    }

    private async Task saveAsync(OnlineSkinPreview preview, CancellationToken cancellationToken)
    {
        try
        {
            string path = await backend!.Previews.SaveAsync(preview, saveDirectory, cancellationToken).ConfigureAwait(false);
            if (!IsDisposed)
                Schedule(() =>
                {
                    if (IsDisposed || cancellationToken.IsCancellationRequested) return;
                    preparing = false;
                    status.Text = $"Saved {Path.GetFileName(path)} to {Path.GetDirectoryName(path)}";
                    setDownloadStatus($"Saved {Path.GetFileName(path)}.");
                    updateDetails();
                });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (!IsDisposed)
                Schedule(() =>
                {
                    if (IsDisposed || cancellationToken.IsCancellationRequested) return;
                    preparing = false;
                    status.Text = AimModFriendlyError.Message(error, "Saving the skin");
                    setDownloadStatus(status.Text.ToString());
                    updateDetails();
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void importSelected()
    {
        if (preparing) return;
        if (destination is null)
            return;
        if (preparedPreview is null)
        {
            prepareSelected(importSelected);
            return;
        }
        loading.ShowLoading("Importing skin", "Opening the validated .osk in your selected osu! client", onCancel: cancelPreparation);
        preparing = true;
        updateDetails();
        _ = importAsync(preparedPreview, selectionCancellation?.Token ?? lifetime.Token);
    }

    private async Task importAsync(OnlineSkinPreview preview, CancellationToken cancellationToken)
    {
        try
        {
            OnlineSkinImportResult result = await backend!.Previews.ImportAsync(preview, destination!, cancellationToken).ConfigureAwait(false);
            if (!IsDisposed)
                Schedule(() =>
                {
                    if (IsDisposed || cancellationToken.IsCancellationRequested) return;
                    loading.HideLoading();
                    preparing = false;
                    status.Text = result.Message ?? (result.Success ? "Skin sent to osu!." : "osu! did not accept the skin.");
                    setDownloadStatus(status.Text.ToString());
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
                    if (IsDisposed || cancellationToken.IsCancellationRequested) return;
                    loading.HideLoading();
                    preparing = false;
                    status.Text = AimModFriendlyError.Message(error, "Importing the skin");
                    setDownloadStatus(status.Text.ToString());
                    updateDetails();
                });
        }
    }

    private void openSource()
    {
        Uri? uri = handoffUri ?? selected?.DetailsUri;
        if (uri is not null)
        {
            try
            {
                openExternal(uri);
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                status.Text = "Could not open your browser. Open the source page manually.";
                setDownloadStatus(status.Text.ToString());
            }
        }
    }

    /// <summary>Stops a download or import and returns the inspector to an idle, retryable state.</summary>
    private void cancelPreparation()
    {
        selectionCancellation?.Cancel();
        selectionCancellation?.Dispose();
        selectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        preparing = false;
        status.Text = "Download cancelled";
        setDownloadStatus("Download cancelled. You can start it again at any time.");
        updateDetails();
    }

    private async Task releasePreparedPreview()
    {
        OnlineSkinPreview? preview = Interlocked.Exchange(ref preparedPreview, null);
        if (preview is not null)
            await preview.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool isDisposing)
    {
        scheduledSearch?.Cancel();
        requestCancellation?.Cancel();
        requestCancellation?.Dispose();
        selectionCancellation?.Cancel();
        selectionCancellation?.Dispose();
        lifetime.Cancel();
        _ = releasePreparedPreview();
        lifetime.Dispose();
        base.Dispose(isDisposing);
    }

    private static Container filterField(Drawable control) => new()
    {
        Height = AimModVisualStyle.ControlHeight,
        Child = control,
    };

    private static void place(Container group, float x, float y, float width)
    {
        group.Position = new(x, y);
        group.Width = width;
    }

    private static TruncatingSpriteText text(float size, Colour4 colour, string weight, string value) => new()
    {
        Text = value,
        Font = new FontUsage(size: size, weight: weight),
        Colour = colour,
    };

    internal static string CompactCount(long value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000d:0.#}M",
        >= 10_000 => $"{value / 1_000d:0}k",
        >= 1_000 => $"{value / 1_000d:0.#}k",
        _ => value.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
    };

    /// <summary>Shared dropdown with an inline label ("Sort · Newest"), so filters need no captions above them.</summary>
    private partial class LabelledDropdown<T> : AimModDropdown<T>
    {
        public LabelledDropdown(string prefix)
        {
            if (Header is AimModDropdownHeader<T> header)
                header.Prefix = prefix;
        }
    }

    private partial class OnlineStatCell : CompositeDrawable
    {
        private readonly TruncatingSpriteText value;

        public OnlineStatCell(string label, string text)
        {
            Width = 100;
            Height = 50;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                new OsuSpriteText { Position = new(10, 8), Text = label, Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted },
                value = new TruncatingSpriteText { Position = new(10, 24), Text = text, Font = new FontUsage(size: 15, weight: "SemiBold"), Colour = AimModPalette.Text, MaxWidth = 80 },
            };
        }

        protected override void Update()
        {
            base.Update();
            value.MaxWidth = Math.Max(20, DrawWidth - 20);
        }
    }

    /// <summary>Online result card: the first screenshot, name, creator, popularity and where it is listed.</summary>
    private partial class OnlineSkinCard : AimModInteractiveSurface, IHasTooltip
    {
        private const float thumbnail_height = 118;

        private readonly TruncatingSpriteText name;
        private readonly TruncatingSpriteText creator;
        private readonly Box selectionBar;
        private AimModLayout.ChangeTracker<float> widthTracker;

        public OnlineSkinCatalogEntry Entry { get; }

        public LocalisableString TooltipText => Entry.Name;

        public OnlineSkinCard(OnlineSkinCatalogEntry skin, bool selected, Action action, SkinScreenshotCache? screenshots)
        {
            Entry = skin;
            Width = 240;
            Height = thumbnail_height + 54;
            CornerRadius = AimModVisualStyle.CardRadius;
            Action = action;
            bool browse = skin.Id == browseProviderId;
            var pills = new FillFlowContainer
            {
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Horizontal,
                Spacing = new(6),
                Margin = new MarginPadding(8),
            };
            if (skin.IsSensitive)
                pills.Add(new AimModPill("Sensitive", AimModPillTone.Accent));
            string meta = browse
                ? "Opens the site to pick a skin"
                : skin.DownloadCount is long downloads
                    ? $"{skin.Creator}  ·  {CompactCount(downloads)} downloads"
                    : skin.Creator;
            Children = new Drawable[]
            {
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = thumbnail_height,
                    Masking = true,
                    Children = new Drawable[]
                    {
                        new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas },
                        skin.PreviewUris.FirstOrDefault() is Uri preview
                            ? new SkinScreenshot(screenshots, preview) { RelativeSizeAxes = Axes.Both }
                            : browse
                                ? new SpriteIcon { Icon = FontAwesome.Solid.Globe, Anchor = Anchor.Centre, Origin = Anchor.Centre, Size = new(30), Colour = AimModPalette.Cyan }
                                : new SpriteIcon { Icon = FontAwesome.Regular.Image, Anchor = Anchor.Centre, Origin = Anchor.Centre, Size = new(26), Colour = AimModPalette.Muted },
                        pills,
                        new AimModPill(skin.Attribution.ProviderName) { Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Margin = new MarginPadding(8) },
                    },
                },
                name = text(14, AimModPalette.Text, "SemiBold", SkinDisplayName.Split(skin.Name).Title).With(drawable => drawable.Position = new(12, thumbnail_height + 9)),
                creator = text(AimModVisualStyle.MinReadableFontSize, AimModPalette.Muted, "Regular", meta).With(drawable => drawable.Position = new(12, thumbnail_height + 30)),
                selectionBar = new Box
                {
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomLeft,
                    RelativeSizeAxes = Axes.X,
                    Height = 3,
                    Colour = AimModPalette.Accent,
                },
            };
            SetSelected(selected);
        }

        public void SetSelected(bool selected)
        {
            BackgroundColour = selected ? AimModPalette.PanelHover : AimModPalette.PanelRaised;
            BorderColour = selected ? AimModPalette.Accent : AimModPalette.Border;
            BorderThickness = selected ? 2 : 1;
            selectionBar.Alpha = selected ? 1 : 0;
        }

        protected override void Update()
        {
            base.Update();
            if (!widthTracker.Update(DrawWidth))
                return;
            name.MaxWidth = Math.Max(40, DrawWidth - 24);
            creator.MaxWidth = Math.Max(40, DrawWidth - 24);
        }
    }

    private partial class OnlinePreviewGallery : CompositeDrawable
    {
        private const float thumbnail_strip = 44;

        private readonly Container artwork;
        private readonly FillFlowContainer thumbnails;
        private readonly SkinScreenshotCache? screenshots;
        private AimModLayout.ChangeTracker<(float, int)> layoutTracker;

        /// <summary>A 16:9 main image plus the thumbnail strip, for an inspector of the given width.</summary>
        public static float HeightFor(float width) => MathF.Round(Math.Max(200, width) * 9 / 16) + thumbnail_strip + 12;

        public OnlinePreviewGallery(SkinScreenshotCache? screenshots)
        {
            this.screenshots = screenshots;
            RelativeSizeAxes = Axes.Both;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas },
                artwork = new Container { RelativeSizeAxes = Axes.X, Height = 198, Masking = true },
                thumbnails = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    Height = thumbnail_strip,
                    Y = 204,
                    Padding = new MarginPadding { Horizontal = 8 },
                    Direction = FillDirection.Horizontal,
                    Spacing = new(6),
                },
            };
        }

        public void SetImages(IReadOnlyList<Uri> images)
        {
            artwork.Clear();
            thumbnails.Clear();
            if (images.Count == 0)
            {
                artwork.Add(new OsuSpriteText
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Text = "No screenshots",
                    Font = new FontUsage(size: 14, weight: "SemiBold"),
                    Colour = AimModPalette.Muted,
                });
                return;
            }
            show(images[0]);
            // A single screenshot needs no strip of one thumbnail.
            if (images.Count > 1)
                thumbnails.AddRange(images.Take(5).Select(uri => new PreviewThumbnail(uri, () => show(uri), screenshots)));
            layoutTracker.Reset();
        }

        protected override void Update()
        {
            base.Update();

            int count = thumbnails.Children.Count;
            if (!layoutTracker.Update((DrawWidth, count))) return;
            float main = MathF.Round(Math.Max(200, DrawWidth) * 9 / 16);
            artwork.Height = main;
            thumbnails.Y = main + 6;
            if (count == 0) return;
            float width = Math.Clamp((DrawWidth - 16 - (count - 1) * 6) / count, 1, 78);
            foreach (Drawable thumbnail in thumbnails.Children) thumbnail.Width = width;
        }

        private void show(Uri uri)
        {
            artwork.Clear();
            artwork.Add(new SkinScreenshot(screenshots, uri, fit: true) { RelativeSizeAxes = Axes.Both });
        }

        private partial class PreviewThumbnail : AimModInteractiveSurface
        {
            public PreviewThumbnail(Uri uri, Action action, SkinScreenshotCache? screenshots)
            {
                Width = 76;
                RelativeSizeAxes = Axes.Y;
                CornerRadius = AimModVisualStyle.ControlRadius;
                Action = action;
                Child = new SkinScreenshot(screenshots, uri) { RelativeSizeAxes = Axes.Both };
            }
        }
    }

    private partial class OnlineListState : CompositeDrawable
    {
        private readonly SpriteIcon icon;
        private readonly OsuSpriteText title;
        private readonly OsuSpriteText detail;

        public OnlineListState()
        {
            RelativeSizeAxes = Axes.Both;
            InternalChild = new FillFlowContainer
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Width = 420,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new(gap),
                Children = new Drawable[]
                {
                    icon = new SpriteIcon { Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre, Icon = FontAwesome.Solid.Search, Size = new(26), Colour = AimModPalette.Cyan },
                    title = new OsuSpriteText { Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre, Font = new FontUsage(size: 18, weight: "Bold"), Colour = AimModPalette.Text },
                    detail = new OsuSpriteText { Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre, Font = new FontUsage(size: 12), Colour = AimModPalette.Muted },
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

    /// <summary>Primary actions are mint with dark text; secondary actions use the neutral outlined surface.</summary>
    private partial class OnlineActionButton : AimModInteractiveSurface
    {
        private readonly Action action;
        private readonly bool primary;
        private readonly SpriteIcon icon;
        private readonly TruncatingSpriteText label;
        private bool enabled;
        private AimModLayout.ChangeTracker<float> widthTracker;

        public OnlineActionButton(IconUsage icon, string label, Action action, bool primary = false)
        {
            this.action = action;
            this.primary = primary;
            RelativeSizeAxes = Axes.X;
            Height = primary ? AimModVisualStyle.ControlHeight : AimModVisualStyle.CompactControlHeight;
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
                    this.icon = new SpriteIcon { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Icon = icon, Size = new(primary ? 14 : 12) },
                    this.label = text(primary ? 14 : 12, AimModPalette.Text, primary ? "Bold" : "SemiBold", label).With(drawable =>
                    {
                        drawable.Anchor = Anchor.CentreLeft;
                        drawable.Origin = Anchor.CentreLeft;
                    }),
                },
            };
        }

        public void SetState(bool enabled, string value)
        {
            this.enabled = enabled;
            label.Text = value;
            Colour4 foreground = primary && enabled ? AimModPalette.Canvas : enabled ? AimModPalette.Text : AimModPalette.Muted;
            BackgroundColour = primary && enabled ? AimModPalette.Accent : AimModPalette.Panel;
            BorderColour = primary && enabled ? AimModPalette.Accent : AimModPalette.Border;
            label.Colour = foreground;
            icon.Colour = foreground;
            applyAlpha();
        }

        /// <summary>Hidden actions leave the flow entirely; visible ones dim while unavailable.</summary>
        public bool Shown
        {
            get => shown;
            set
            {
                shown = value;
                applyAlpha();
            }
        }

        private bool shown = true;

        private void applyAlpha() => this.FadeTo(!shown ? 0 : enabled ? 1 : 0.55f, AimModVisualStyle.FastTransition);

        protected override void Update()
        {
            base.Update();

            if (widthTracker.Update(DrawWidth))
                label.MaxWidth = Math.Max(40, DrawWidth - 44);
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

internal static class OnlineSkinBrowserHandoff
{
    public static void Open(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }
}
