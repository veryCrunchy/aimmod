using System.Globalization;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Threading;

namespace AimMod.Desktop.Creator;

/// <summary>Score-first lookup, with optional on-demand timestamp frame previews.</summary>
public partial class NativeFootageWorkspace : Container
{
    private readonly ILocalLibrarySource? scores;
    private readonly FootageLibraryStore store;
    private readonly Action<Uri> openUrl;
    private readonly Action<string> copy;
    private readonly Func<OsuScoreAddress, CancellationToken, Task<LocalReplay>> onlineScore;
    private readonly FillFlowContainer<Drawable> body;
    private readonly AimModScrollContainer scroll;
    private readonly Container scrollRegion;
    private readonly Container filters;
    private readonly FillFlowContainer<Drawable> navigation;
    private readonly TextFlowContainer status;
    private readonly AimModTextBox search;
    private readonly AimModTextBox scoreLink;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? queryLifetime;
    private ScheduledDelegate? debounce;
    private FootageLibrary library = FootageLibrary.Empty;
    private LocalLibraryPage<LocalReplay>? page;
    private LocalReplay? selected;
    private bool loaded;
    private bool saving;
    private bool recordingsPage;
    private bool editingRecording;
    private bool querying;
    private int queryRevision;
    private readonly TimestampThumbnailService? thumbnails;

    public NativeFootageWorkspace(ILocalLibrarySource? scores, FootageLibraryStore store, Action back,
        Action<Uri> openUrl, Action<string> copy,
        Func<OsuScoreAddress, CancellationToken, Task<LocalReplay>> onlineScore, LocalReplay? selected = null,
        ITwitchVodDiscovery? twitch = null, FootageChannel? automaticChannel = null, TimestampThumbnailService? thumbnails = null, CreatorAccountAccess? accountAccess = null)
    {
        this.scores = scores;
        this.store = store;
        this.openUrl = openUrl;
        this.copy = copy;
        this.onlineScore = onlineScore;
        this.selected = selected;
        this.twitch = twitch;
        this.automaticChannel = automaticChannel;
        this.thumbnails = thumbnails;
        this.accountAccess = accountAccess;
        RelativeSizeAxes = Axes.Both;
        Children =
        [
            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas },
            new AimModSectionHeader("Find footage", "Choose a score, find its recording, and jump to the play."),
            navigation = new FillFlowContainer<Drawable>
            {
                Y = 66, RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(8),
                Children = [new AimModButton("Back to replays", back),
                    new AimModButton("Choose score", () => { debounce?.Cancel(); cancelQuery(); accountsPage = false; recordingsPage = false; this.selected = null; render(); loadScores(0); }),
                    new AimModButton("Recordings & VODs", () => { debounce?.Cancel(); cancelQuery(); accountsPage = false; recordingsPage = true; render(); }),
                    new AimModButton("Your accounts", openAccounts)],
            },
            filters = new Container
            {
                Y = 114, RelativeSizeAxes = Axes.X, Height = 82,
                Children = [search = new AimModTextBox { RelativeSizeAxes = Axes.X, PlaceholderText = "Search saved plays by map, player or mods" },
                    new Container { RelativeSizeAxes = Axes.X, Y = 44, Height = 36,
                        Padding = new MarginPadding { Right = 128 },
                        Child = scoreLink = new AimModTextBox { RelativeSizeAxes = Axes.X, PlaceholderText = "Or paste an osu! score link" } },
                    new AimModButton("Look up score", lookupOnline) { Anchor = Anchor.TopRight, Origin = Anchor.TopRight, Y = 44 }],
            },
            scrollRegion = new Container
            {
                RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Top = 212, Bottom = 12 },
                // Padding belongs outside the scroll viewport. Padding on the scroll
                // itself leaves its mask covering the fixed header and filters.
                Child = scroll = new AimModScrollContainer
                {
                    RelativeSizeAxes = Axes.Both, Masking = true, Child = body = flow(),
                },
            },
        ];
        status = text("Opening your footage library...");
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        search.Current.BindValueChanged(_ =>
        {
            debounce?.Cancel();
            cancelQuery();
            debounce = Scheduler.AddDelayed(() => loadScores(0), 250);
        });
        scoreLink.OnCommit += (_, _) => lookupOnline();
        render();
        _ = initialise();
        if (twitch is not null) _ = restoreTwitch();
    }

    protected override void Update()
    {
        base.Update();
        float top = Math.Max(114, navigation.Y + navigation.DrawHeight + 12);
        filters.Y = top;
        scrollRegion.Padding = new MarginPadding { Top = recordingsPage || accountsPage ? top : top + 98, Bottom = 12 };
    }

    private async Task initialise()
    {
        try
        {
            FootageLibrary result = await Task.Run(() => store.LoadAsync(lifetime.Token), lifetime.Token).ConfigureAwait(false);
            if (!IsDisposed) Schedule(() =>
            {
                if (lifetime.IsCancellationRequested) return;
                library = result; loaded = true;
                if (automaticChannel is { } channel)
                    library = library with { Channels = library.Channels.Where(c => !string.Equals(c.Player, channel.Player, StringComparison.OrdinalIgnoreCase)).Append(channel).ToArray() };
                status.Text = "";
                render();
                if (selected is null && !recordingsPage) loadScores(0);
                maybeImportArchives();
                if (accountAccess is not null) { if (selected is null) openAccounts(); refreshOwnScores(); }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { showError("Your footage library could not be opened. " + safeError(e)); }
    }

    private void cancelQuery()
    {
        queryRevision++;
        queryLifetime?.Cancel(); queryLifetime?.Dispose(); queryLifetime = null;
        querying = false;
    }

    private void loadScores(int offset)
    {
        if (!loaded) return;
        ILocalLibrarySource? scoreSource = useOwnScores && ownScoreAccount == accountAccess?.Current()?.UserId ? ownScores : scores;
        if (scoreSource is null) return;
        cancelQuery();
        queryLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        CancellationToken token = queryLifetime.Token;
        int revision = queryRevision;
        string value = search.Current.Value;
        querying = true;
        status.Text = "Loading saved plays...";
        _ = Task.Run(async () =>
        {
            try
            {
                LocalLibraryPage<LocalReplay> result = await scoreSource.SearchReplaysAsync(new(
                    SearchText: value, RulesetShortName: "", Sort: LocalLibrarySort.RecentlyPlayed, Offset: offset, Limit: 40), token).ConfigureAwait(false);
                if (!IsDisposed) Schedule(() =>
                {
                    if (token.IsCancellationRequested || revision != queryRevision) return;
                    page = result; querying = false; selected = null;
                    status.Text = result.Warning ?? "";
                    render();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (!token.IsCancellationRequested && revision == queryRevision) showError("Saved plays could not be loaded. " + safeError(e)); }
        }, token);
    }

    private void lookupOnline()
    {
        if (!loaded) return;
        if (!OsuScoreAddress.TryParse(scoreLink.Current.Value, out OsuScoreAddress? address))
        { status.Text = "Paste a score link such as https://osu.ppy.sh/scores/123456."; return; }
        var saved = ownScoreAccount == accountAccess?.Current()?.UserId ? ownScoreRecords.FirstOrDefault(s => s.OnlineScoreId == address!.Id
            && (s.LegacyScore ? s.RulesetShortName : null) == address.LegacyRuleset) : null;
        if (saved is not null) { debounce?.Cancel(); cancelQuery(); selected = saved; accountsPage = recordingsPage = false; render(); return; }
        debounce?.Cancel(); cancelQuery();
        queryLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        CancellationToken token = queryLifetime.Token;
        int revision = queryRevision;
        querying = true;
        status.Text = "Looking up this score...";
        _ = Task.Run(async () =>
        {
            try
            {
                LocalReplay score = await onlineScore(address!, token).ConfigureAwait(false);
                if (!IsDisposed) Schedule(() =>
                {
                    if (token.IsCancellationRequested || revision != queryRevision) return;
                    selected = score; querying = false; recordingsPage = false; status.Text = ""; render();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (!token.IsCancellationRequested && revision == queryRevision) showError(safeError(e)); }
        }, token);
    }

    private void render()
    {
        editingRecording = false;
        // Status is retained while the rest of the scrollable content changes.
        if (status.Parent is not null) body.Remove(status, false);
        body.Clear(); body.Add(status);
        filters.Alpha = recordingsPage || accountsPage ? 0 : 1;
        scrollRegion.Padding = new MarginPadding { Top = recordingsPage || accountsPage ? 114 : 212, Bottom = 12 };
        if (!loaded) return;
        if (accountsPage) { renderAccounts(); return; }
        if (recordingsPage) { renderRecordings(); return; }
        if (selected is not null) { renderScore(selected); return; }
        if (accountAccess is not null)
            body.Add(row(new AimModButton("Your online scores", () => { useOwnScores = true; if (accountAccess.Current() is null) openAccounts(); else if (ownScores is null) refreshOwnScores(); else loadScores(0); }, useOwnScores),
                new AimModButton("Local plays", () => { useOwnScores = false; loadScores(0); }, !useOwnScores)));
        body.Add(text("1. Choose the play you want footage of", 18, true));
        if (page is null || page.Items.Count == 0)
            body.Add(text(querying ? "Loading saved plays..." : "No saved plays found. Search another map or paste an osu! score link."));
        else
        {
            body.Add(text($"{page.Offset + 1}-{page.Offset + page.Items.Count} of {page.Total:N0} saved plays"));
            body.Add(row(new AimModButton("Previous", () => loadScores(Math.Max(0, page.Offset - 40))) { Alpha = page.Offset > 0 ? 1 : 0 },
                new AimModButton("Next", () => loadScores(page.Offset + page.Items.Count)) { Alpha = page.HasMore ? 1 : 0 }));
            foreach (LocalReplay score in page.Items)
            {
                var content = flow();
                content.Add(text($"{score.Title} [{score.Difficulty}]", 15, true));
                content.Add(text(describe(score), 12));
                content.Add(new AimModButton("Find this play", () => { debounce?.Cancel(); cancelQuery(); selected = score; render(); }));
                body.Add(card(content));
            }
        }
    }

    private static string describe(LocalReplay score) =>
        $"{score.Player} | {score.PlayedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz} | {score.Accuracy:P2} | {score.MissCount} misses | {(score.Mods.Count == 0 ? "No mods" : string.Join(", ", score.Mods))}";

    internal static Uri? ScoreUri(LocalReplay score)
    {
        if (score.OnlineScoreId <= 0) return null;
        if (score.LegacyScore && score.RulesetShortName is not ("osu" or "taiko" or "fruits" or "mania")) return null;
        return new Uri("https://osu.ppy.sh/scores/" + (score.LegacyScore ? score.RulesetShortName + "/" : "")
            + score.OnlineScoreId.ToString(CultureInfo.InvariantCulture));
    }

    private void renderScore(LocalReplay score)
    {
        body.Add(text($"{score.Title} [{score.Difficulty}]", 20, true));
        body.Add(text(describe(score)));
        renderTwitchSearch(score);
        body.Add(text("2. Open the matching part of a recording", 18, true));
        IReadOnlyList<FootageMatch> matches = FootageIndex.Find(score, library);
        if (matches.Count == 0)
        {
            body.Add(text("No recording covers this player's score timestamp yet."));
            body.Add(new AimModButton("Add a recording or VOD", () => { recordingsPage = true; render(); }, true));
        }
        foreach (FootageMatch match in matches)
        {
            FootageRecording recording = match.Recording;
            var content = flow();
            content.Add(text("Matched score", 12, true));
            content.Add(text($"{score.Artist} - {score.Title} [{score.Difficulty}]", 18, true));
            content.Add(text($"{score.Player} · {score.Accuracy:P2} · {(score.PerformancePoints is { } pp ? $"{pp:0.##} PP" : "PP unavailable")} · {score.MaxCombo:N0}x combo · {score.MissCount} misses"));
            content.Add(text($"{score.PlayedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz} · {(score.Mods.Count == 0 ? "No mods" : string.Join(", ", score.Mods))}", 12));
            if (ScoreUri(score) is { } scoreUri)
                content.Add(new AimModButton("Open osu! score", () => openUrl(scoreUri)));
            if (!score.HasReplayFile) content.Add(text("Score details only. No replay file is loaded.", 12));
            content.Add(text("Matching broadcast", 12, true));
            content.Add(text(recording.Title, 16, true));
            content.Add(text($"{FootageIndex.Timecode(match.Seconds)} | {(match.Confirmed ? "Confirmed timestamp" : "Estimated from the score timestamp")}"));
            double seek = Math.Max(recording.VideoStartSeconds, match.Seconds - (match.Confirmed ? 0 : 30));
            content.Add(row(openButton(recording, seek, match.Confirmed ? "Open footage" : "Open 30 seconds earlier"),
                new AimModButton("Copy timestamp", () => copyMoment(recording, match.Seconds))));
            var timestamp = field(content, "After checking the footage, save the exact moment", FootageIndex.Timecode(match.Seconds));
            if (thumbnails is not null && (!FootageIndex.TryVideoUri(recording.Location, out var videoUri)
                || videoUri!.Host is "twitch.tv" or "www.twitch.tv"))
            {
                var preview = new TimestampThumbnailPreview(thumbnails, recording.Location, match.Seconds);
                content.Add(preview);
                content.Add(new AimModButton("Preview selected time", () => { if (readPosition(timestamp, recording, out double time)) preview.ShowTime(time); }));
                timestamp.OnCommit += (_, _) => { if (readPosition(timestamp, recording, out double time)) preview.ShowTime(time); };
            }
            content.Add(row(new AimModButton("Save confirmed timestamp", () =>
            {
                if (!readPosition(timestamp, recording, out double time)) return;
                var moment = new FootageMoment(Guid.NewGuid(), recording.Id, $"{score.Title} [{score.Difficulty}]", time, FootageIndex.ScoreKey(score));
                save(library with { Moments = library.Moments.Where(m => m.RecordingId != recording.Id || m.ScoreKey != moment.ScoreKey).Append(moment).ToArray() });
            }), new AimModButton("Adjust recording", () => renderRecordingEditor(recording))));
            body.Add(card(content));
        }
        if (matches.Count > 0) body.Add(text("Estimates use the saved score time, which can fall near the beginning or end of a play. Check the footage before saving the exact moment."));
    }

    private void renderRecordings()
    {
        body.Add(text("Recordings & VODs", 20, true));
        body.Add(text("Add the recording that covers your score's date. For a paused or edited recording, add each continuous part separately."));
        body.Add(row(new AimModButton("Add local recording", () => renderRecordingEditor(null, true), true),
            new AimModButton("Add VOD link", () => renderRecordingEditor(null)),
            new AimModButton("Copy confirmed moments as CSV", () => { copy(FootageIndex.ExportCsv(library)); status.Text = "Copied confirmed moments. Local file paths are excluded."; })));
        if (library.Recordings.Length == 0) body.Add(text("No recordings added yet. Your videos stay where they are."));
        var list = flow();
        var filter = field(body, "Find a recording", "", "Search by title or player");
        void populate()
        {
            list.Clear();
            FootageRecording[] found = library.Recordings.Where(r => (r.Title + " " + r.Player).Contains(filter.Current.Value.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.WallClockStart).ToArray();
            list.Add(text($"{found.Length} recordings"));
            foreach (FootageRecording recording in found.Take(50))
            {
                var content = flow();
                content.Add(text(recording.Title, 16, true));
                content.Add(text($"{recording.Player} | {recording.WallClockStart.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz} | {FootageIndex.Timecode(recording.VideoStartSeconds)}-{FootageIndex.Timecode(recording.VideoEndSeconds)}"));
                content.Add(row(new AimModButton("Edit timeline", () => renderRecordingEditor(recording)),
                    openButton(recording, recording.VideoStartSeconds, "Open recording")));
                foreach (FootageMoment moment in library.Moments.Where(m => m.RecordingId == recording.Id).OrderBy(m => m.Seconds).Take(10))
                    content.Add(row(new AimModButton($"{FootageIndex.Timecode(moment.Seconds)} · {moment.Title[..Math.Min(moment.Title.Length, 35)]}", () => copyMoment(recording, moment.Seconds)),
                        new AimModButton("Remove marker", () => save(library with { Moments = library.Moments.Where(m => m.Id != moment.Id).ToArray() }))));
                list.Add(card(content));
            }
            if (found.Length > 50) list.Add(text("Showing 50 recordings. Narrow your search to find older ones."));
        }
        filter.Current.BindValueChanged(_ => populate());
        body.Add(list); populate();
    }

    private void renderRecordingEditor(FootageRecording? existing, bool local = false)
    {
        if (saving) return;
        debounce?.Cancel(); cancelQuery();
        editingRecording = true;
        accountsPage = false; recordingsPage = true; filters.Hide();
        scrollRegion.Padding = new MarginPadding { Top = 114, Bottom = 12 };
        body.Remove(status, false); body.Clear(); body.Add(status);
        local = local || existing is not null && !FootageIndex.TryVideoUri(existing.Location, out _);
        body.Add(text(existing is null ? local ? "Add local recording" : "Add recording or VOD" : "Adjust recording timeline", 20, true));
        var title = field(body, "Recording title", existing?.Title ?? "");
        var player = field(body, "osu! player", existing?.Player ?? selected?.Player ?? accountAccess?.Current()?.Username ?? "");
        var location = field(body, local ? "Video file path" : "Twitch VOD link, YouTube video link, or local video path", existing?.Location ?? "");
        if (local) body.Add(text("Paste the full path to your recording. MP4, MKV, MOV and other common video formats are supported. The file stays on your computer."));
        var wall = field(body, "Date and time at the beginning of this part (include timezone)", existing?.WallClockStart.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) ?? "",
            "YYYY-MM-DD HH:MM:SS +02:00");
        var start = field(body, "Beginning of this part in the video (HH:MM:SS)", FootageIndex.Timecode(existing?.VideoStartSeconds ?? 0));
        var end = field(body, "End of this part in the video (HH:MM:SS)", existing is null ? "" : FootageIndex.Timecode(existing.VideoEndSeconds));
        body.Add(text(local ? "Use the time you started recording. A copied file's creation date may be different. For pauses or cuts, add each continuous part separately."
            : "Use the broadcast start time, not its upload date. Include your timezone, for example +02:00."));
        AimModTextBox? anchor = null;
        LocalReplay? alignmentScore = selected;
        if (alignmentScore is not null)
        {
            body.Add(text($"Or align using {alignmentScore.Title} [{alignmentScore.Difficulty}], saved at {alignmentScore.PlayedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}. Leave the date field blank to use this instead."));
            anchor = field(body, "Video time corresponding to this score's saved timestamp", "", "HH:MM:SS");
        }
        body.Add(row(new AimModButton("Save recording", () =>
        {
            if (!FootageIndex.TryTimecode(start.Current.Value, out double from)
                || !FootageIndex.TryTimecode(end.Current.Value, out double to))
            { status.Text = "Enter a date with timezone and video times in HH:MM:SS."; return; }
            DateTimeOffset at;
            if (string.IsNullOrWhiteSpace(wall.Current.Value) && alignmentScore is not null && anchor is not null
                && FootageIndex.TryTimecode(anchor.Current.Value, out double anchorTime) && anchorTime >= from && anchorTime < to)
                at = alignmentScore.PlayedAt.AddSeconds(from - anchorTime);
            else if (!FootageIndex.TryWallClock(wall.Current.Value, out at))
            { status.Text = "Enter a date with timezone, or align using a video timestamp for the selected score."; return; }
            if (string.IsNullOrWhiteSpace(player.Current.Value)) { status.Text = "Enter the osu! player in this recording."; return; }
            var recording = new FootageRecording(existing?.Id ?? Guid.NewGuid(), title.Current.Value.Trim(), location.Current.Value.Trim().Trim('"'),
                at, from, to, player.Current.Value.Trim());
            if (!FootageIndex.TryVideoUri(recording.Location, out _))
            {
                try { LocalFootagePlayer.ValidateFile(recording.Location); }
                catch (InvalidOperationException error) { status.Text = error.Message; return; }
            }
            save(library with { Recordings = library.Recordings.Where(r => r.Id != recording.Id).Append(recording).ToArray() }, returnToScore: selected is not null);
        }, true), new AimModButton("Cancel", render)));
        if (existing is not null)
        {
            body.Add(text("Removing this entry keeps the video file and VOD untouched."));
            var remove = new AimModButton("Remove recording entry", () => { });
            remove.Action = () =>
            {
                remove.SetCaption("Confirm removal");
                remove.Action = () => save(library with { Recordings = library.Recordings.Where(r => r.Id != existing.Id).ToArray(),
                    Moments = library.Moments.Where(m => m.RecordingId != existing.Id).ToArray() });
            };
            body.Add(remove);
        }
    }

    private AimModButton openButton(FootageRecording recording, double seconds, string caption) => new(
        caption, () =>
        {
            Uri? url = FootageIndex.TimestampUrl(recording.Location, seconds);
            if (url is not null) openUrl(url);
            else
            {
                try
                {
                    copyMoment(recording, seconds);
                    bool sought = LocalFootagePlayer.Open(recording.Location, seconds);
                    status.Text = sought ? $"Opened local recording at {FootageIndex.Timecode(seconds)}."
                        : $"Opened your video player. Jump to {FootageIndex.Timecode(seconds)} (copied). Install VLC or mpv to jump there automatically.";
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
                { status.Text = error is InvalidOperationException ? error.Message : "The recording could not open. Check its file path and video player."; }
            }
        }, true);

    private void copyMoment(FootageRecording recording, double seconds)
    {
        copy(FootageIndex.TimestampUrl(recording.Location, seconds)?.AbsoluteUri ?? $"{recording.Location}\n{FootageIndex.Timecode(seconds)}");
        status.Text = "Copied.";
    }

    private bool readPosition(AimModTextBox field, FootageRecording recording, out double seconds)
    {
        if (!FootageIndex.TryTimecode(field.Current.Value, out seconds)) { status.Text = "Enter a video time in HH:MM:SS."; return false; }
        try { FootageIndex.ValidatePosition(recording, seconds); return true; }
        catch (ArgumentException e) { status.Text = e.Message; return false; }
    }

    private void save(FootageLibrary next, bool returnToScore = false, string? successMessage = null, bool background = false)
    {
        if (!loaded || saving) return;
        try { FootageIndex.Validate(next); }
        catch (Exception e) { status.Text = safeError(e); return; }
        saving = true; status.Text = "Saving...";
        int saveRevision = queryRevision;
        _ = Task.Run(async () =>
        {
            try
            {
                await store.SaveAsync(next, lifetime.Token).ConfigureAwait(false);
                if (!IsDisposed) Schedule(() =>
                {
                    if (lifetime.IsCancellationRequested) return;
                    library = next; saving = false; status.Text = successMessage ?? "Saved.";
                    if (returnToScore && saveRevision == queryRevision) recordingsPage = false;
                    if (!background || !editingRecording) render();
                    maybeImportArchives();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { showError("Changes were not saved. " + safeError(e)); }
        }, lifetime.Token);
    }

    private void showError(string message)
    {
        if (!IsDisposed) Schedule(() => { if (!lifetime.IsCancellationRequested) { saving = false; querying = false; status.Text = message; } });
    }

    private static string safeError(Exception e) => e is InvalidDataException or ArgumentException or InvalidOperationException ? e.Message
        : e is UnauthorizedAccessException ? "Check file permissions and try again."
        : e is HttpRequestException or TaskCanceledException ? "The online request failed. Try again shortly."
        : "Please try again. Your existing entries have been kept.";

    private static FillFlowContainer<Drawable> flow() => new() { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(10) };
    private static FillFlowContainer<Drawable> row(params Drawable[] children) => new() { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(8), Children = children };
    private static TextFlowContainer text(string value, float size = 13, bool bold = false) => new(t =>
    { t.Font = new FontUsage(size: size, weight: bold ? "SemiBold" : "Regular"); t.Colour = bold ? AimModPalette.Text : AimModPalette.Muted; })
    { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = value };
    private static AimModTextBox field(FillFlowContainer<Drawable> parent, string label, string value, string placeholder = "")
    {
        parent.Add(text(label, 12));
        var field = new AimModTextBox { RelativeSizeAxes = Axes.X, PlaceholderText = placeholder };
        field.Current.Value = value; parent.Add(field); return field;
    }
    private static Container card(Drawable content) => new()
    {
        RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Masking = true, CornerRadius = AimModVisualStyle.CardRadius,
        Children = [new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
            new Container { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Padding = new MarginPadding(14), Child = content }],
    };

    protected override void Dispose(bool isDisposing)
    {
        lifetime.Cancel(); debounce?.Cancel(); cancelQuery(); lifetime.Dispose();
        twitchLinkLifetime?.Cancel(); twitchLinkLifetime?.Dispose();
        archiveLifetime?.Cancel(); archiveLifetime?.Dispose();
        base.Dispose(isDisposing);
    }
}
