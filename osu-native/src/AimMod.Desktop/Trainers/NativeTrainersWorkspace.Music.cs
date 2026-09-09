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
    private Drawable cueControl = null!;
    private AimModButton shuffleToggle = null!;
    private AimModDropdown<string> cueSelector = null!;

    public void SetPreparationStatus(bool busy, string message)
    {
        preparing = busy;
        start.SetCaption(busy ? "Preparing..." : "Start practice");
        status.Text = message;
    }

    private void buildMusicControls(FillFlowContainer<Drawable> body)
    {
        musicControls = column(); musicControls.Depth = -9;
        var row = flow();
        var musicOptions = new Dictionary<string, string> { ["AimMod cues"] = "cues" };
        foreach (var song in TrainerMusicCatalog.Songs) musicOptions.Add(song.Value, song.Key);
        musicOptions.Add("Installed beatmap song", "song");
        row.Add(selector("PRACTISE WITH", musicOptions, settings.Music, chooseMusic, 285, d => musicSelector = d));
        shuffleToggle = new AimModButton("", () => {
            preferences = preferences with { ShuffleMusic = !preferences.ShuffleMusic }; saveTrainerPreferences(); refreshShuffle();
        });
        row.Add(new Container { Width = 155, Height = 56, Child = shuffleToggle });
        shuffleToggle.Y = 20;
        refreshShuffle();
        row.Add(cueControl = selector("CUE SOUND", new Dictionary<string, string>
        { ["Pulse · warm bass"] = "pulse", ["Glass · bright pluck"] = "glass", ["Snap · short attack"] = "snap" },
            "pulse", cue => { Suspend(); settings = settings with { Cue = cue }; refreshHistory(); }, 215, d => cueSelector = d));
        musicControls.Add(row);
        musicControls.Add(musicDescription = paragraph("A clear cue on every target, with four beats to count you in."));
        songControls = column(); songControls.Depth = -1; songControls.Hide();
        songSearch = new AimModSearchBox { RelativeSizeAxes = Axes.X, SearchHint = "Search installed songs by title, artist or mapper" };
        songSearch.Current.BindValueChanged(change => { songPage = 0; _ = searchSongsAsync(); });
        songControls.Add(songSearch);
        songSelector = new TrainerDropdown<LocalReplay>(s => s is null ? "Choose a song" : $"{s.Artist} — {s.Title}")
        { RelativeSizeAxes = Axes.X, Items = Array.Empty<LocalReplay>(), Depth = -2 };
        songSelector.Current.BindValueChanged(e =>
        {
            SelectedSong = e.NewValue;
            settings = settings with { SongTitle = e.NewValue is {} song ? $"{song.Title} [{song.Difficulty}]" : "", SongIdentity = e.NewValue?.BeatmapHash is { Length: > 0 } hash ? hash : e.NewValue?.BeatmapId.ToString() ?? "" };
            refreshHistory();
        });
        songControls.Add(songSelector);
        var navigation = flow();
        navigation.Add(new AimModButton("Previous songs", () => { if (songPage > 0) { songPage--; _ = searchSongsAsync(); } }));
        navigation.Add(new AimModButton("More songs", () => { if ((songPage + 1) * 30 < songTotal) { songPage++; _ = searchSongsAsync(); } }));
        navigation.Add(new AimModButton("Clear search", () => { songPage = 0; songSearch.Current.Value = ""; _ = searchSongsAsync(); }));
        navigation.Add(selector("START IN SONG", new Dictionary<string, int>
        { ["First notes"] = 0, ["+30 seconds"] = 30, ["+1 minute"] = 60, ["+2 minutes"] = 120, ["+3 minutes"] = 180 },
            0, seconds => { settings = settings with { SongStartSeconds = seconds }; refreshHistory(); }, 170, d => songStartSelector = d));
        songControls.Add(navigation);
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
        songControls.Alpha = music == "song" ? 1 : 0;
        shuffleToggle.Alpha = TrainerMusicCatalog.IsSong(music) ? 1 : 0;
        refreshMusicDescription();
        if (music == "song") _ = searchSongsAsync();
        updateInstruction(); refreshHistory();
    }

    private void refreshShuffle()
    {
        shuffleToggle.SetCaption($"Shuffle: {(preferences.ShuffleMusic ? "On" : "Off")}");
        shuffleToggle.SetSelected(preferences.ShuffleMusic);
        refreshMusicDescription();
    }

    private void refreshMusicDescription()
    {
        if (musicDescription is null) return;
        bool fixedMusic = practiceIntent != PracticeIntent.Quick;
        shuffleToggle.Alpha = !fixedMusic && TrainerMusicCatalog.IsSong(settings.Music) ? 1 : 0;
        musicDescription.Text = settings.Music switch
        {
            "song" => "Your selected drill follows the song's timing, including tempo changes. Sections start from the map's first notes and stop at its end.",
            "cues" => "A clear cue on every target, with four beats to count you in.",
            _ => $"{TrainerMusicCatalog.Songs[settings.Music]} · {settings.Bpm} BPM. " + (fixedMusic ? "This song stays the same throughout your comparison or progression."
                : preferences.ShuffleMusic ? "Shuffle picks a different song each run." : "This song stays selected for your next run."),
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
                    : $"{page * 30 + 1}–{page * 30 + found.Items.Count} of {found.Total} songs. Choose a song to start practising.";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        { Schedule(() => { if (!token.IsCancellationRequested) songStatus.Text = "Your library could not be read. Check the osu! connection in Settings and search again."; }); }
    }
}
