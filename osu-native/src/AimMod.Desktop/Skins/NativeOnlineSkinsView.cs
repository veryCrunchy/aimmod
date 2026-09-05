using AimMod.Desktop.Skins.Online;
using AimMod.Desktop.Visuals;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Framework.Threading;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.Skins;

public partial class NativeOnlineSkinsView : CompositeDrawable
{
    private const float gap = AimModVisualStyle.RelatedSpacing;

    private readonly OnlineSkinCatalogBackend? backend;
    private IOnlineSkinArchiveDestination? destination;
    private readonly string saveDirectory;
    private readonly Action<Uri> openExternal;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Bindable<string> provider = new("All providers");
    private readonly Bindable<OnlineSkinRuleset> ruleset = new(OnlineSkinRuleset.Standard);
    private readonly Bindable<OnlineSkinSort> sort = new(OnlineSkinSort.Newest);
    private readonly OsuTextBox search;
    private readonly Container filterBand;
    private readonly Container searchGroup;
    private readonly Container providerGroup;
    private readonly Container rulesetGroup;
    private readonly Container sortGroup;
    private readonly TruncatingSpriteText status;
    private readonly Container resultViewport;
    private readonly Container listPanel;
    private readonly FillFlowContainer results;
    private readonly OnlineListState listState;
    private readonly Container detailPanel;
    private readonly Container detailContent;
    private readonly OnlinePreviewGallery gallery;
    private readonly TruncatingSpriteText selectedName;
    private readonly TruncatingSpriteText selectedCreator;
    private readonly TruncatingSpriteText selectedMetadata;
    private readonly TruncatingSpriteText attribution;
    private readonly OnlineActionButton previewButton;
    private readonly OnlineActionButton saveButton;
    private readonly OnlineActionButton importButton;
    private readonly OnlineActionButton sourceButton;
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
            filterBand = new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = 72,
                Depth = -20,
                Children = new Drawable[]
                {
                    new CircularContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        Masking = true,
                        CornerRadius = AimModVisualStyle.ControlRadius,
                        Depth = 10,
                        Child = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                    },
                    searchGroup = filterField("SEARCH", search = new OsuTextBox
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = AimModVisualStyle.CompactControlHeight,
                        PlaceholderText = "Skin name or creator",
                    }),
                    providerGroup = filterField("PROVIDER", new OsuDropdown<string>
                    {
                        RelativeSizeAxes = Axes.X,
                        Items = providerItems,
                        Current = provider,
                    }),
                    rulesetGroup = filterField("MODE", new OsuDropdown<OnlineSkinRuleset>
                    {
                        RelativeSizeAxes = Axes.X,
                        Items = Enum.GetValues<OnlineSkinRuleset>(),
                        Current = ruleset,
                    }),
                    sortGroup = filterField("SORT", new OsuDropdown<OnlineSkinSort>
                    {
                        RelativeSizeAxes = Axes.X,
                        Items = Enum.GetValues<OnlineSkinSort>(),
                        Current = sort,
                    }),
                },
            },
            status = new TruncatingSpriteText
            {
                Y = 84,
                Text = backend is null ? "Online catalog unavailable" : "Loading online skin catalogs",
                Font = new FontUsage(size: 11, weight: "SemiBold"),
                Colour = AimModPalette.Muted,
            },
            resultViewport = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = 108 },
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
                                Child = results = new FillFlowContainer
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Direction = FillDirection.Vertical,
                                    Spacing = new(gap),
                                    Padding = new MarginPadding { Left = gap, Right = AimModVisualStyle.SectionSpacing, Vertical = gap },
                                },
                            },
                            listState = new OnlineListState(),
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
                            new AimModScrollContainer
                            {
                                RelativeSizeAxes = Axes.Both,
                                Child = detailContent = new Container
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Children = new Drawable[]
                                    {
                                        new Container
                                        {
                                            RelativeSizeAxes = Axes.X,
                                            Height = 258,
                                            Child = gallery = new OnlinePreviewGallery(),
                                        },
                                        new FillFlowContainer
                                        {
                                            RelativeSizeAxes = Axes.X,
                                            AutoSizeAxes = Axes.Y,
                                            Y = 272,
                                            Padding = new MarginPadding { Horizontal = 16, Bottom = 16 },
                                            Direction = FillDirection.Vertical,
                                            Spacing = new(gap),
                                            Children = new Drawable[]
                                            {
                                                selectedName = text(19, AimModPalette.Text, "Bold", "Select an online skin"),
                                                selectedCreator = text(12, AimModPalette.Cyan, "SemiBold", "Screenshots and source details appear here."),
                                                selectedMetadata = text(11, AimModPalette.Muted, "Regular", string.Empty),
                                                attribution = text(10, AimModPalette.Muted, "Regular", string.Empty),
                                                previewButton = new OnlineActionButton(FontAwesome.Solid.Download, "Prepare preview", prepareSelected),
                                                saveButton = new OnlineActionButton(FontAwesome.Solid.Save, "Save .osk", saveSelected),
                                                importButton = new OnlineActionButton(FontAwesome.Solid.ExternalLinkAlt, "Import into osu!", importSelected, AimModPalette.Pink),
                                                sourceButton = new OnlineActionButton(FontAwesome.Solid.Globe, "Open source page", openSource),
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
        updateDetails();
    }

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
        loading.ShowLoading("Loading skin details", "Reading the selected skin");
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
                    loading.HideLoading();
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
                Schedule(() => showError(requestRevision, error.Message));
        }
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        search.Current.BindValueChanged(_ => scheduleSearch());
        provider.BindValueChanged(_ => scheduleSearch());
        ruleset.BindValueChanged(_ => scheduleSearch());
        sort.BindValueChanged(_ => scheduleSearch());
        linkRoutingReady = true;
        if (pendingLink is not null)
            openPendingLink();
        else
            searchCatalog();
    }

    protected override void Update()
    {
        base.Update();
        float width = Math.Max(640, DrawWidth);
        const float inset = 12;
        bool compactFilters = width < 980;
        float available = width - inset * 2;
        if (compactFilters)
        {
            float column = (available - gap) / 2;
            filterBand.Height = 128;
            place(searchGroup, inset, 8, column);
            place(providerGroup, inset + column + gap, 8, column);
            place(rulesetGroup, inset, 68, column);
            place(sortGroup, inset + column + gap, 68, column);
            status.Y = 140;
            resultViewport.Padding = new MarginPadding { Top = 164 };
        }
        else
        {
            float searchWidth = Math.Clamp(available * 0.38f, 300, 480);
            float dropdownWidth = (available - searchWidth - gap * 3) / 3;
            place(searchGroup, inset, 8, searchWidth);
            place(providerGroup, inset + searchWidth + gap, 8, dropdownWidth);
            place(rulesetGroup, inset + searchWidth + gap * 2 + dropdownWidth, 8, dropdownWidth);
            place(sortGroup, inset + searchWidth + gap * 3 + dropdownWidth * 2, 8, dropdownWidth);
            filterBand.Height = 72;
            status.Y = 84;
            resultViewport.Padding = new MarginPadding { Top = 108 };
        }

        float panelWidth = Math.Max(300, (DrawWidth - AimModVisualStyle.SectionSpacing) * 0.6f);
        panelWidth = Math.Min(panelWidth, Math.Max(0, DrawWidth - 300 - AimModVisualStyle.SectionSpacing));
        listPanel.Width = panelWidth;
        listPanel.RelativeSizeAxes = Axes.Y;
        detailPanel.Width = Math.Max(0, DrawWidth - panelWidth - AimModVisualStyle.SectionSpacing);
        detailPanel.RelativeSizeAxes = Axes.Y;
        status.MaxWidth = panelWidth;
        float detailTextWidth = Math.Max(0, detailContent.DrawWidth - 32);
        selectedName.MaxWidth = detailTextWidth;
        selectedCreator.MaxWidth = detailTextWidth;
        selectedMetadata.MaxWidth = detailTextWidth;
        attribution.MaxWidth = detailTextWidth;
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
        status.Text = "Searching online skin catalogs";
        listState.SetState(FontAwesome.Solid.Search, "Searching skin catalogs", "Reading cached provider pages and public metadata.", true);
        loading.ShowLoading("Loading online skins", provider.Value == "All providers" ? "Searching osuskins.net and skins.osuck.net" : $"Searching {provider.Value}");
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
                Schedule(() => showError(requestRevision, error.Message));
        }
    }

    private void showResults(int requestRevision, OnlineSkinCatalogSearchResult response)
    {
        if (requestRevision != revision)
            return;
        loading.HideLoading();
        loaded = response.Items
            .GroupBy(item => $"{item.Name}\n{item.Creator}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        string[] unavailable = response.Providers.Where(item => item.Page.Status != OnlineSkinCatalogStatus.Success).Select(item => item.ProviderName).ToArray();
        status.Text = unavailable.Length == 0
            ? $"{loaded.Count:N0} skins from {response.Providers.Count:N0} public catalogs"
            : $"{loaded.Count:N0} skins; {string.Join(", ", unavailable)} unavailable";
        listState.SetState(
            FontAwesome.Solid.Search,
            "No online skins found",
            unavailable.Length == response.Providers.Count ? "The providers are unavailable. Open a source site or try again later." : "Try a different search, mode, provider, or sort order.",
            loaded.Count == 0);
        if (selected is null || loaded.All(item => item.ProviderId != selected.ProviderId || item.Id != selected.Id))
            select(loaded.FirstOrDefault());
        else
            refreshRows();
    }

    private void showError(int requestRevision, string message)
    {
        if (requestRevision != revision)
            return;
        loading.HideLoading();
        loaded = [];
        results.Clear();
        status.Text = $"Online skin search failed: {message}";
        listState.SetState(FontAwesome.Solid.ExclamationTriangle, "Could not search skin catalogs", "Try again or open a provider site in your browser.", true);
    }

    private void refreshRows()
    {
        results.Clear();
        results.AddRange(loaded.Select(item => new OnlineSkinRow(item, ReferenceEquals(item, selected), () => select(item))));
    }

    private void select(OnlineSkinCatalogEntry? item)
    {
        selectionCancellation?.Cancel();
        selectionCancellation?.Dispose();
        selectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        preparing = false;
        loading.HideLoading();
        _ = releasePreparedPreview();
        selected = item;
        handoffUri = null;
        refreshRows();
        updateDetails();
        if (item is not null)
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
                        status.Text = $"Could not load skin details: {error.Message}";
                });
        }
    }

    private void updateDetails()
    {
        OnlineSkinCatalogEntry? item = selected;
        gallery.SetImages(item?.PreviewUris ?? []);
        selectedName.Text = item?.Name ?? "Select an online skin";
        selectedCreator.Text = item is null ? "Screenshots and source details appear here." : $"by {item.Creator}";
        selectedMetadata.Text = item is null
            ? string.Empty
            : string.Join("  ·  ", metadata(item));
        attribution.Text = item?.Attribution.Notice ?? string.Empty;
        bool direct = item?.Download?.Kind is OnlineSkinDownloadKind.DirectHttps or OnlineSkinDownloadKind.GoogleDrive;
        bool available = preparedPreview?.IsAvailable == true;
        previewButton.SetState(!preparing && item?.Download is not null && !available,
            !direct ? "Open download page" : item?.IsSensitive == true ? "Confirm & download" : "Download skin");
        bool canPrepare = direct && item?.IsSensitive != true;
        saveButton.SetState(!preparing && (available || canPrepare), available ? "Save .osk" : "Download & save");
        importButton.SetState(!preparing && (available || canPrepare) && destination is not null, available ? "Import into osu!" : "Download & import");
        sourceButton.SetState(item is not null, handoffUri is null ? "Open source page" : "Open download page");
    }

    private static IEnumerable<string> metadata(OnlineSkinCatalogEntry item)
    {
        if (item.IsSensitive)
            yield return "Sensitive content";
        if (!string.IsNullOrWhiteSpace(item.Variant))
            yield return item.Variant;
        if (item.FileSizeBytes is long bytes)
            yield return $"{bytes / (1024d * 1024):0.#} MB";
        if (item.DownloadCount is long downloads)
            yield return $"{downloads:N0} downloads";
        if (item.SupportedRulesets.Count > 0)
            yield return string.Join(", ", item.SupportedRulesets);
        yield return item.Attribution.ProviderName;
    }

    private void prepareSelected()
    {
        prepareSelected(null);
    }

    private void prepareSelected(Action? afterPrepared)
    {
        if (selected?.Download is null || backend is null || preparing)
            return;
        if (selected.Download.Kind is not (OnlineSkinDownloadKind.DirectHttps or OnlineSkinDownloadKind.GoogleDrive))
        {
            handoffUri = selected.Download.BrowserHandoffUri ?? selected.Download.Uri;
            openSource();
            return;
        }
        preparing = true;
        loading.ShowLoading("Preparing skin preview", "Downloading and validating the .osk archive");
        updateDetails();
        _ = prepareAsync(selected, selectionCancellation?.Token ?? lifetime.Token, afterPrepared);
    }

    private async Task prepareAsync(OnlineSkinCatalogEntry item, CancellationToken cancellationToken, Action? afterPrepared)
    {
        try
        {
            OnlineSkinPreviewResult result = await backend!.Previews.PrepareAsync(item, allowSensitive: item.IsSensitive, cancellationToken).ConfigureAwait(false);
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
                    handoffUri = result.Status == OnlineSkinDownloadStatus.ExternalBrowserRequired ? result.ExternalUri : null;
                    status.Text = result.Status switch
                    {
                        OnlineSkinDownloadStatus.Success => $"{item.Name} is downloaded, validated, and ready.",
                        OnlineSkinDownloadStatus.ExternalBrowserRequired => result.Message ?? "This download must be completed in your browser.",
                        _ => result.Message ?? "The skin package could not be prepared.",
                    };
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
                    status.Text = $"Could not prepare skin: {error.Message}";
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
                    status.Text = $"Could not save skin: {error.Message}";
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
        loading.ShowLoading("Importing skin", "Opening the validated .osk in your selected osu! client");
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
                    status.Text = $"Could not import skin: {error.Message}";
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
                status.Text = $"Could not open browser: {error.Message}";
            }
        }
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

    private static Container filterField(string label, Drawable control) => new()
    {
        Height = 54,
        Children = new[]
        {
            new OsuSpriteText { Text = label, Font = new FontUsage(size: 8, weight: "Bold"), Colour = AimModPalette.Cyan },
            control.With(drawable => drawable.Y = 17),
        },
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

    private partial class OnlineSkinRow : AimModInteractiveSurface
    {
        private readonly TruncatingSpriteText name;
        private readonly TruncatingSpriteText creator;

        public OnlineSkinRow(OnlineSkinCatalogEntry skin, bool selected, Action action)
        {
            RelativeSizeAxes = Axes.X;
            Height = 92;
            CornerRadius = AimModVisualStyle.ControlRadius;
            BackgroundColour = selected ? AimModPalette.PanelHover : AimModPalette.PanelRaised;
            Action = action;
            Children = new Drawable[]
            {
                new Container
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = 142,
                    Masking = true,
                    Child = skin.PreviewUris.FirstOrDefault() is Uri preview
                        ? new AimModOnlineArtworkHost(preview) { RelativeSizeAxes = Axes.Both }
                        : new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelHover },
                },
                new Box
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = 3,
                    Colour = selected ? AimModPalette.Pink : AimModPalette.Cyan,
                },
                name = text(14, AimModPalette.Text, "SemiBold", skin.Name).With(drawable => drawable.Position = new(158, 18)),
                creator = text(11, AimModPalette.Muted, "Regular", $"{skin.Creator}  ·  {skin.Attribution.ProviderName}").With(drawable => drawable.Position = new(158, 45)),
                new AimModPill(skin.IsSensitive ? "sensitive" : "online", skin.IsSensitive ? AimModPillTone.Accent : AimModPillTone.Info)
                {
                    Anchor = Anchor.BottomRight,
                    Origin = Anchor.BottomRight,
                    Margin = new MarginPadding(10),
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            name.MaxWidth = Math.Max(80, DrawWidth - 270);
            creator.MaxWidth = Math.Max(80, DrawWidth - 270);
        }
    }

    private partial class OnlinePreviewGallery : CompositeDrawable
    {
        private readonly Container artwork;
        private readonly FillFlowContainer thumbnails;

        public OnlinePreviewGallery()
        {
            RelativeSizeAxes = Axes.Both;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                artwork = new Container { RelativeSizeAxes = Axes.X, Height = 198, Masking = true },
                thumbnails = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 52,
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
            thumbnails.AddRange(images.Take(5).Select(uri => new PreviewThumbnail(uri, () => show(uri))));
        }

        private void show(Uri uri)
        {
            artwork.Clear();
            artwork.Add(new AimModOnlineArtworkHost(uri) { RelativeSizeAxes = Axes.Both });
        }

        private partial class PreviewThumbnail : AimModInteractiveSurface
        {
            public PreviewThumbnail(Uri uri, Action action)
            {
                Width = 76;
                RelativeSizeAxes = Axes.Y;
                CornerRadius = AimModVisualStyle.ControlRadius;
                Action = action;
                Child = new AimModOnlineArtworkHost(uri) { RelativeSizeAxes = Axes.Both };
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

    private partial class OnlineActionButton : AimModInteractiveSurface
    {
        private readonly Action action;
        private readonly SpriteIcon icon;
        private readonly TruncatingSpriteText label;
        private readonly Colour4 enabledColour;
        private bool enabled;

        public OnlineActionButton(IconUsage icon, string label, Action action, Colour4? enabledColour = null)
        {
            this.action = action;
            this.enabledColour = enabledColour ?? AimModPalette.PanelHover;
            RelativeSizeAxes = Axes.X;
            Height = AimModVisualStyle.CompactControlHeight;
            CornerRadius = AimModVisualStyle.ControlRadius;
            Children = new Drawable[]
            {
                this.icon = new SpriteIcon { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, X = 13, Icon = icon, Size = new(14), Colour = AimModPalette.Text },
                this.label = text(11, AimModPalette.Text, "Bold", label).With(drawable =>
                {
                    drawable.Anchor = Anchor.CentreLeft;
                    drawable.Origin = Anchor.CentreLeft;
                    drawable.X = 36;
                }),
            };
        }

        public void SetState(bool enabled, string value)
        {
            this.enabled = enabled;
            label.Text = value;
            BackgroundColour = enabled ? enabledColour : AimModPalette.PanelHover;
            this.FadeTo(enabled ? 1 : 0.5f, AimModVisualStyle.FastTransition);
        }

        protected override void Update()
        {
            base.Update();
            label.MaxWidth = Math.Max(40, DrawWidth - 50);
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
