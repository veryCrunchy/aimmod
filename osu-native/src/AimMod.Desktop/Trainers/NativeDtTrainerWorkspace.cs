using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;

namespace AimMod.Desktop.Trainers;

public partial class NativeDtTrainerWorkspace : Container
{
    private readonly ILocalLibrarySource library;
    private readonly Func<DtProgressStore> store;
    private readonly Func<int> account;
    private readonly Action<LocalReplay, int, Action<TrainerResult?>, Action<string>> launch;
    private readonly FillFlowContainer<Drawable> maps = column(), detail = column();
    private readonly Container browser, inspector;
    private readonly Container browserResults;
    private readonly FillFlowContainer<Drawable> toolbar;
    private readonly AimModScrollContainer mapScroll;
    private readonly TextFlowContainer mapStatus = text("Loading installed maps...", 12);
    private readonly AimModButton changeMap, returnToSession;
    private readonly Dictionary<string, AimModButton> difficultyButtons = [];
    private bool browsing = true;
    private bool? compactLayout;
    private readonly AimModSearchBox search = new() { RelativeSizeAxes = Axes.X, SearchHint = "Search installed maps by title, artist or mapper" };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? query;
    private LocalReplay? selected;
    private DtProgress? progress;
    private bool busy;
    private int page, owner;
    private string message = "";

    public NativeDtTrainerWorkspace(ILocalLibrarySource library, Func<DtProgressStore> store, Func<int> account,
        Action<LocalReplay, int, Action<TrainerResult?>, Action<string>> launch, Action back)
    {
        this.library = library; this.store = store; this.account = account; this.launch = launch; owner = account();
        RelativeSizeAxes = Axes.Both;
        var heading = new Container { RelativeSizeAxes = Axes.X, Height = 58, Children = [
            new SpriteIcon { Icon = FontAwesome.Solid.FastForward, Size = new(26), Y = 4, Colour = AimModPalette.Accent },
            new Container { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Padding = new MarginPadding { Left = 40 }, Child = new FillFlowContainer<Drawable> {
                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(4), Children = [
                    text("Double Time", 22), text("Build from normal speed to 150%, one attempt at a time.", 13) ] } }
        ] };
        toolbar = column();
        toolbar.Add(text("Choose your map", 18));
        toolbar.Add(new Container { RelativeSizeAxes = Axes.X, Height = 36, Children = [
            new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Right = 72 }, Child = search },
            new AimModButton("Clear", () => { search.Current.Value = ""; page = 0; findMaps(); }) { Anchor = Anchor.TopRight, Origin = Anchor.TopRight }
        ] });
        toolbar.Add(mapStatus);
        toolbar.Add(returnToSession = new AimModButton("Return to selected map", () => { browsing = false; layout(); }));
        browser = new Container { Children = [toolbar,
            browserResults = new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Top = 142 }, Child = mapScroll = new AimModScrollContainer {
                RelativeSizeAxes = Axes.Both, Child = maps } } ] };
        var session = column();
        session.Padding = new MarginPadding { Right = 10, Bottom = 16 };
        session.Add(changeMap = new AimModButton("Change map", () => { browsing = true; layout(); }));
        session.Add(detail);
        inspector = new Container { Child = new AimModScrollContainer { RelativeSizeAxes = Axes.Both, Child = session } };
        Children = [heading, browser, inspector];
        maps.Padding = new MarginPadding { Right = 10, Bottom = 16 };
        search.Current.BindValueChanged(_ => { page = 0; findMaps(); });
        renderDetail();
    }

    protected override void LoadComplete() { base.LoadComplete(); findMaps(); }
    protected override void Update()
    {
        base.Update();
        layout();
        if (owner != account())
        {
            owner = account(); selected = null; progress = null; busy = false; browsing = true;
            message = "Account changed. Choose a map to load your practice progress."; renderDetail();
        }
    }

    private void layout()
    {
        bool compact = DrawWidth < 920;
        float rail = Math.Clamp(DrawWidth * .43f, 420, 540);
        browser.Position = new(0, 74);
        browser.Size = new(compact ? DrawWidth : Math.Max(0, DrawWidth - rail - 24), Math.Max(0, DrawHeight - 74));
        inspector.Position = new(compact ? 0 : DrawWidth - rail, 74);
        inspector.Size = new(compact ? DrawWidth : rail, Math.Max(0, DrawHeight - 74));
        browser.Alpha = !compact || browsing ? 1 : 0;
        inspector.Alpha = !compact || !browsing ? 1 : 0;
        changeMap.Alpha = compact && selected is not null ? 1 : 0;
        returnToSession.Alpha = compact && selected is not null ? 1 : 0;
        browserResults.Padding = new MarginPadding { Top = toolbar.DrawHeight + 16 };
        if (compactLayout != compact) { compactLayout = compact; renderDetail(); }
    }

    private void findMaps()
    {
        query?.Cancel(); query?.Dispose(); query = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = query.Token; string value = search.Current.Value; int currentPage = page;
        mapStatus.Text = "Searching installed maps...";
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(220, token);
                var found = await library.SearchBeatmapSetsAsync(new LocalLibraryQuery(value, "osu", Offset: currentPage * 12, Limit: 12), token);
                Schedule(() =>
                {
                    if (token.IsCancellationRequested) return;
                    maps.Clear(); difficultyButtons.Clear(); mapScroll.ScrollToStart();
                    mapStatus.Text = $"{found.Total:N0} installed sets | Choose a difficulty you can finish comfortably.";
                    foreach (var set in found.Items)
                    {
                        var group = new AimModChoiceGroup(set.Title, $"{set.Artist} | mapped by {set.Creator}");
                        foreach (var d in set.Difficulties.Where(d => d.RulesetShortName == "osu"))
                        {
                            var map = new LocalReplay(Guid.Empty, set.SetId, d.BeatmapId, set.Title, set.Artist, d.Name, "osu", "", DateTimeOffset.UnixEpoch,
                                d.StarRating, 0, 0, 0, 0, null, [], false, BeatmapHash: d.BeatmapHash, BeatmapPath: d.BeatmapPath, Origin: d.Origin, OnlineBeatmapId: d.OnlineId);
                            var button = new AimModButton(d.Name, () => select(map));
                            button.SetVisualContent(new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Horizontal = 10 }, Children = [
                                new SpriteText { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Text = $"{d.StarRating:0.00}", Font = new FontUsage(size: 14, weight: "SemiBold"), Colour = AimModPalette.Accent },
                                new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Left = 46 }, Child = new osu.Game.Graphics.Sprites.TruncatingSpriteText {
                                    RelativeSizeAxes = Axes.X, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft,
                                    Text = d.Name, Font = new FontUsage(size: 13), Colour = AimModPalette.Text } }
                            ] }, 36);
                            button.Width = 238;
                            button.SetSelected(selected is not null && MapKey(selected) == MapKey(map));
                            difficultyButtons[MapKey(map)] = button;
                            group.Choices.Add(button);
                        }
                        if (group.Choices.Count > 0) maps.Add(group);
                    }
                    if (found.Items.Count == 0) maps.Add(text("No installed maps found. Try another search or connect your osu! library in Settings."));
                    var nav = row();
                    if (currentPage > 0) nav.Add(new AimModButton("Previous maps", () => { page--; findMaps(); }));
                    if (found.HasMore) nav.Add(new AimModButton("More maps", () => { page++; findMaps(); }));
                    maps.Add(nav);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!IsDisposed) Schedule(() => { if (!token.IsCancellationRequested) { maps.Clear(); mapStatus.Text = "Could not load maps. Check your osu! library in Settings."; maps.Add(new AimModButton("Retry", findMaps)); } }); }
        }, token);
    }

    internal static string MapKey(LocalReplay map) => string.IsNullOrEmpty(map.BeatmapHash) ? $"{map.Origin}:{map.BeatmapId}" : map.BeatmapHash.ToLowerInvariant();
    private async void select(LocalReplay map)
    {
        if (busy) return;
        selected = map; progress = null; busy = true; browsing = false; message = "Loading progress...";
        foreach (var (key, button) in difficultyButtons) button.SetSelected(key == MapKey(map));
        renderDetail(); layout();
        int expectedOwner = owner;
        try
        {
            var saved = await Task.Run(() => store().LoadAsync(MapKey(map), lifetime.Token));
            if (!IsDisposed) Schedule(() => { if (owner != expectedOwner || selected != map) return; progress = saved; busy = false; message = ""; renderDetail(); });
        }
        catch (Exception) { if (!IsDisposed) Schedule(() => { if (owner != expectedOwner || selected != map) return; busy = false; message = "Progress could not load. Choose the map again to retry."; renderDetail(); }); }
    }

    private void renderDetail()
    {
        detail.Clear();
        if (selected is null)
        {
            detail.Add(text("Your path to DT", 20));
            detail.Add(speedTrack(100));
            detail.Add(text("Start with a map you know", 18));
            detail.Add(text("Choose an installed difficulty. Your first attempt plays the full map at normal speed."));
            detail.Add(new AimModChoiceGroup("01  Play at your current speed", "Use your usual osu! controls and skin. Finish the map to record an attempt."));
            detail.Add(new AimModChoiceGroup("02  Build consistency", "Two runs with 98% accuracy and no misses raise the speed by 2%. Very clean runs earn 3%."));
            detail.Add(new AimModChoiceGroup("03  Work towards 150%", "Struggling lowers the speed a little. Your progress is saved for each map."));
            if (message.Length > 0) detail.Add(text(message));
            return;
        }
        detail.Add(text("YOUR NEXT ATTEMPT", 12));
        detail.Add(text(selected.Title, 20));
        detail.Add(text($"{selected.Artist} | {selected.Difficulty} | {selected.StarRating:0.00} stars", 13));
        if (progress is { } p)
        {
            var metrics = row();
            metrics.Add(new AimModStatTile("PLAY SPEED", $"{p.Speed}%", "of normal speed"));
            metrics.Add(new AimModStatTile("CLEAN RUNS", $"{p.CleanRuns}/2", "at this speed"));
            metrics.Add(new AimModStatTile("TARGET", "150%", p.Completed ? "DT reached" : "Double Time"));
            detail.Add(metrics);
            detail.Add(speedTrack(p.Speed));
            var play = new AimModButton(busy ? "Preparing..." : $"Play at {p.Speed}% speed", start, true)
                { AutoSizeAxes = Axes.None, RelativeSizeAxes = Axes.X, Height = 44 };
            detail.Add(play);
            detail.Add(text(p.Completed ? "DT reached. Keep practising here or choose another map."
                : p.CleanRuns == 1 ? "One more clean run to increase your speed. Aim for 98% accuracy and no misses."
                : "Finish two runs at 98% accuracy with no misses to increase the speed."));
            if (p.History.LastOrDefault() is { } last)
            {
                detail.Add(text("LAST ATTEMPT", 12));
                detail.Add(text(last.Reason, 16));
            }
            if (message.Length > 0) detail.Add(text(message, 13));
            detail.Add(text("Recent attempts", 18));
            if (p.History.Length == 0) detail.Add(text("Your completed runs will appear here."));
            else
            {
                detail.Add(attemptRow("SPEED", "ACCURACY", "MISSES", "NEXT", true));
                foreach (var attempt in p.History.TakeLast(6).Reverse())
                    detail.Add(attemptRow($"{attempt.Speed}%", $"{attempt.Accuracy:0.00}%", $"{attempt.Misses}", $"{attempt.NextSpeed}%"));
            }
            detail.Add(text("Speed adjusts after each run: +2-3% for consistent clean plays, -3-5% if accuracy or misses slip. Other runs keep the same speed.", 12));
            detail.Add(text("No Fail is on. Quitting keeps your speed. Practice scores stay off osu! leaderboards.", 12));
            if (p.Speed > 100 || p.History.Length > 0)
                detail.Add(new AimModButton("Restart at 100%", reset));
        }
        else if (message.Length > 0) detail.Add(text(message));
    }

    private static Container attemptRow(string speed, string accuracy, string misses, string next, bool header = false)
    {
        var result = new Container { RelativeSizeAxes = Axes.X, Height = header ? 22 : 32 };
        if (!header) result.Add(new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel });
        string[] values = [speed, accuracy, misses, next];
        for (int i = 0; i < values.Length; i++)
            result.Add(new Container { RelativeSizeAxes = Axes.Both, RelativePositionAxes = Axes.X, X = i * .25f, Width = .25f,
                Padding = new MarginPadding { Left = 8 }, Child = new SpriteText {
                    Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Text = values[i], Font = new FontUsage(size: header ? 11 : 14),
                    Colour = header ? AimModPalette.Muted : i == 3 ? AimModPalette.Accent : AimModPalette.Text } });
        return result;
    }

    private static Container speedTrack(int speed)
    {
        var track = new Container { RelativeSizeAxes = Axes.X, Height = 54, Padding = new MarginPadding { Horizontal = 8 } };
        track.Add(new Box { RelativeSizeAxes = Axes.X, Height = 4, Y = 12, Colour = AimModPalette.PanelRaised });
        track.Add(new Box { RelativeSizeAxes = Axes.X, Width = (speed - 100) / 50f, Height = 4, Y = 12, Colour = AimModPalette.Accent });
        for (int i = 0; i <= 5; i++)
        {
            int mark = 100 + i * 10;
            track.Add(new Circle { RelativePositionAxes = Axes.X, X = i / 5f, Y = 14, Origin = Anchor.Centre, Size = new(8),
                Colour = mark <= speed ? AimModPalette.Accent : AimModPalette.PanelRaised });
            track.Add(new SpriteText { RelativePositionAxes = Axes.X, X = i / 5f, Y = 28,
                Origin = i == 0 ? Anchor.TopLeft : i == 5 ? Anchor.TopRight : Anchor.TopCentre,
                Text = $"{mark}%", Font = new FontUsage(size: 11), Colour = mark <= speed ? AimModPalette.Accent : AimModPalette.Muted });
        }
        return track;
    }

    private void start()
    {
        if (busy || selected is null || progress is null) return;
        var map = selected; var before = progress; int expectedOwner = owner;
        var targetStore = store(); busy = true; message = "Preparing the original map..."; renderDetail();
        launch(map, before.Speed, async result =>
        {
            var next = DtProgression.Apply(before, before.Speed, result);
            try
            {
                if (next != before) await Task.Run(() => targetStore.SaveAsync(next));
                if (!IsDisposed) Schedule(() =>
                {
                    if (owner != expectedOwner || selected != map) return;
                    busy = false; progress = next;
                    message = result is null ? "Practice ended early. Your speed has not changed."
                        : next == before ? "This run could not change your speed. Complete a map with at least 20 objects to progress." : "Progress saved. Ready for your next attempt.";
                    renderDetail();
                });
            }
            catch (Exception) { if (!IsDisposed) Schedule(() => { if (owner != expectedOwner) return; busy = false; message = "Progress could not be saved. Your previous speed has been kept."; renderDetail(); }); }
        }, error => { if (owner != expectedOwner || IsDisposed) return; busy = false; message = error; renderDetail(); });
    }

    private async void reset()
    {
        if (busy || progress is null) return;
        busy = true; int expectedOwner = owner; var before = progress;
        var next = before with { Speed = 100, CleanRuns = 0, Completed = false };
        var target = store();
        try { await Task.Run(() => target.SaveAsync(next)); if (!IsDisposed) Schedule(() => { if (owner != expectedOwner) return; busy = false; progress = next; message = "Restarted at normal speed. Previous attempts are kept below."; renderDetail(); }); }
        catch (Exception) { if (!IsDisposed) Schedule(() => { if (owner != expectedOwner) return; busy = false; message = "Could not reset progress. Try again."; renderDetail(); }); }
    }

    private static FillFlowContainer<Drawable> column() => new() { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical, Spacing = new(10) };
    private static FillFlowContainer<Drawable> row() => new() { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(8) };
    private static TextFlowContainer text(string value, float size = 14) => new(t => { t.Font = new FontUsage(size: size); t.Colour = size >= 18 ? AimModPalette.Text : AimModPalette.Muted; }) { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = value };
    protected override void Dispose(bool isDisposing) { lifetime.Cancel(); query?.Cancel(); query?.Dispose(); lifetime.Dispose(); base.Dispose(isDisposing); }
}
