using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;

namespace AimMod.Desktop.Trainers;

public partial class NativeTrainersWorkspace
{
    public ILocalLibrarySource? SongLibrary { get; set; }
    public LocalReplay? SelectedSong { get; private set; }
    private FillFlowContainer<Drawable> musicControls = null!;
    private FillFlowContainer<Drawable> songControls = null!;
    private AimModSearchBox songSearch = null!;
    private AimModDropdown<LocalReplay> songSelector = null!;
    private osu.Game.Graphics.Containers.OsuTextFlowContainer musicDescription = null!;
    private osu.Game.Graphics.Containers.OsuTextFlowContainer songStatus = null!;
    private CancellationTokenSource? songSearchCancellation;
    private bool preparing;
    private AimModDropdown<string> musicSelector = null!;
    private AimModDropdown<int> songStartSelector = null!;
    private int songPage;
    private int songTotal;
    private Drawable cueControl = null!, musicField = null!;
    private AimModTrainerSwitch shuffleToggle = null!;
    private AimModDropdown<string> cueSelector = null!;

    public void SetPreparationStatus(bool busy, string message, bool reveal = false)
    {
        preparing = busy;
        start.SetCaption(busy ? "Preparing..." : "Start practice");
        status.Text = message;
        if (warmupStatus is not null) warmupStatus.Text = message;
        // The status line sits with the pinned session actions, so it is already on screen.
        _ = reveal;
    }

    private void buildMusicControls(FillFlowContainer<Drawable> body)
    {
        musicControls = column(); musicControls.Depth = -9;
        var row = flow();
        var musicOptions = new Dictionary<string, string> { ["AimMod cues"] = "cues" };
        foreach (var song in TrainerMusicCatalog.Songs) musicOptions.Add(song.Value, song.Key);
        musicOptions.Add("Installed beatmap song", "song");
        row.Add(musicField = stretch(selector("MUSIC", musicOptions, settings.Music, chooseMusic, 285, d => musicSelector = d), .6f));
        row.Add(cueControl = stretch(selector("CUE SOUND", new Dictionary<string, string>
        { ["Pulse · warm bass"] = "pulse", ["Glass · bright pluck"] = "glass", ["Snap · short attack"] = "snap" },
            "pulse", cue => { Suspend(); settings = settings with { Cue = cue }; refreshHistory(); }, 215, d => cueSelector = d), .4f));
        row.Add(shuffleToggle = new AimModTrainerSwitch("Shuffle", "", () => {
            preferences = preferences with { ShuffleMusic = !preferences.ShuffleMusic }; saveTrainerPreferences(); refreshShuffle();
        }) { RelativeSizeAxes = Axes.X, Width = .4f, Margin = new MarginPadding { Top = selectorLabelSpacing - 4 }, TooltipText = "Play a different AimMod song each run." });
        musicControls.Add(row);
        musicControls.Add(musicDescription = paragraph(""));
        refreshShuffle();
        songControls = column(); songControls.Depth = -1; songControls.Hide();
        songSearch = new AimModSearchBox { RelativeSizeAxes = Axes.X, SearchHint = "Search installed songs" };
        songSearch.Current.BindValueChanged(change => { songPage = 0; _ = searchSongsAsync(); });
        songControls.Add(songSearch);
        songSelector = new TrainerDropdown<LocalReplay>(s => s is null ? "Choose a song" : $"{s.Artist} — {s.Title}")
        { RelativeSizeAxes = Axes.X, Items = Array.Empty<LocalReplay>(), Depth = -2 };
        cardMenus.Add((ITrainerMenu)songSelector);
        songSelector.Current.BindValueChanged(e =>
        {
            SelectedSong = e.NewValue;
            settings = settings with { SongTitle = e.NewValue is {} song ? $"{song.Title} [{song.Difficulty}]" : "", SongIdentity = e.NewValue?.BeatmapHash is { Length: > 0 } hash ? hash : e.NewValue?.BeatmapId.ToString() ?? "" };
            refreshHistory();
        });
        songControls.Add(songSelector);
        var navigation = flow();
        navigation.Add(stretch(selector("START IN SONG", new Dictionary<string, int>
        { ["First notes"] = 0, ["+30 seconds"] = 30, ["+1 minute"] = 60, ["+2 minutes"] = 120, ["+3 minutes"] = 180 },
            0, seconds => { settings = settings with { SongStartSeconds = seconds }; refreshHistory(); }, 170, d => songStartSelector = d), .5f));
        songControls.Add(navigation);
        var paging = flow();
        foreach (var (caption, action) in new (string, Action)[] {
            ("Previous", () => { if (songPage > 0) { songPage--; _ = searchSongsAsync(); } }),
            ("More", () => { if ((songPage + 1) * 30 < songTotal) { songPage++; _ = searchSongsAsync(); } }),
            ("Clear search", () => { songPage = 0; songSearch.Current.Value = ""; _ = searchSongsAsync(); }) })
            paging.Add(new AimModButton(caption, action) { Height = AimModVisualStyle.CompactControlHeight });
        songControls.Add(paging);
        songControls.Add(songStatus = paragraph("Search your installed osu! library."));
        musicControls.Add(songControls);
        body.Add(musicControls);
    }

    private void chooseMusic(string music)
    {
        Suspend();
        int bpm = TrainerMusicCatalog.IsSong(music) ? TrainerMusicCatalog.Tempos.MinBy(b => Math.Abs(b - settings.Bpm)) : settings.Bpm;
        tempoSelector.Items = TrainerMusicCatalog.IsSong(music) ? TrainerMusicCatalog.Tempos : Enumerable.Range(6, 19).Select(i => i * 10);
        settings = settings with { Music = music, Bpm = bpm };
        tempoSelector.Current.Value = bpm;
        cueControl.Alpha = music == "cues" ? 1 : 0;
        musicField.Width = music == "cues" || TrainerMusicCatalog.IsSong(music) && practiceIntent == PracticeIntent.Quick ? .6f : 1;
        songControls.Alpha = music == "song" ? 1 : 0;
        shuffleToggle.Alpha = TrainerMusicCatalog.IsSong(music) ? 1 : 0;
        refreshMusicDescription();
        if (music == "song") _ = searchSongsAsync();
        updateInstruction(); refreshHistory();
    }

    private void refreshShuffle()
    {
        shuffleToggle.SetValue(preferences.ShuffleMusic);
        refreshMusicDescription();
    }

    private void refreshMusicDescription()
    {
        if (musicDescription is null) return;
        bool fixedMusic = practiceIntent != PracticeIntent.Quick;
        shuffleToggle.Alpha = !fixedMusic && TrainerMusicCatalog.IsSong(settings.Music) ? 1 : 0;
        if (musicField is not null) musicField.Width = settings.Music == "cues" || shuffleToggle.Alpha > 0 ? .6f : 1;
        musicDescription.Alpha = settings.Music == "song" || fixedMusic && TrainerMusicCatalog.IsSong(settings.Music) ? 1 : 0;
        musicDescription.Text = settings.Music switch
        {
            "song" => preferences.AdaptiveDifficulty ? "Follows the map's notes, holds and tempo changes." : "Your drill follows the map's tempo changes.",
            "cues" => "A cue on every note · four-beat count-in.",
            _ => (preferences.AdaptiveDifficulty ? "Patterns follow the music." : $"{settings.Bpm} BPM · fixed note rate.")
                + (fixedMusic ? " Same song for the whole plan." : ""),
        };
    }

    private async Task searchSongsAsync()
    {
        songSearchCancellation?.Cancel(); songSearchCancellation?.Dispose();
        var cancellation = songSearchCancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        string query = songSearch.Current.Value;
        int page = songPage;
        songStatus.Text = "Searching installed songs...";
        try
        {
            await Task.Delay(250, token).ConfigureAwait(false);
            var library = SongLibrary;
            if (library is null) { Schedule(() => { if (!token.IsCancellationRequested) songStatus.Text = "Connect your osu! library in Settings to choose a song."; }); return; }
            var found = await library.SearchBeatmapSetsAsync(new LocalLibraryQuery(query, "osu", Offset: page * 30, Limit: 30), token).ConfigureAwait(false);
            var songs = TrainerSongChoices.FromSets(found.Items);
            Schedule(() =>
            {
                if (token.IsCancellationRequested) return;
                songTotal = found.Total;
                // Keep an explicit selection visible while browsing another page.
                var selection = SelectedSong is {} previous ? songs.FirstOrDefault(s => s.SetId == previous.SetId) ?? previous : null;
                songSelector.Items = selection is not null && !songs.Contains(selection) ? new[] { selection }.Concat(songs) : songs;
                if (selection is not null) songSelector.Current.Value = selection;
                songStatus.Text = songs.Length == 0 ? "No installed songs found. Try another search or install a map in osu!."
                    : $"{page * 30 + 1}–{page * 30 + found.Items.Count} of {found.Total} songs";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        { Schedule(() => { if (!token.IsCancellationRequested) songStatus.Text = "Your library could not be read. Check the osu! connection in Settings and search again."; }); }
    }
}
