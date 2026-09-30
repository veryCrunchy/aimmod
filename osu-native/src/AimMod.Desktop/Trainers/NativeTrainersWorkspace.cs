using AimMod.Desktop.Visuals;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Platform;
using osu.Framework.Localisation;
using osu.Framework.Timing;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osuTK;
using osuTK.Input;

namespace AimMod.Desktop.Trainers;

public partial class NativeTrainersWorkspace : CompositeDrawable
{
    [Resolved] private AudioManager audio { get; set; } = null!;
    [Resolved] private GameHost host { get; set; } = null!;
    private readonly Func<TrainerHistoryStore> history;
    private readonly Action openCoaching;
    public Action<TrainerSettings, bool, double>? LaunchOsuSession { get; set; }
    public Func<Action, Drawable>? DtTrainerFactory { get; set; }
    private readonly AimModButton dtEntry;
    private readonly AimModButton skillEntry;
    private readonly Container skillPage;
    private readonly Container modPage;
    public Func<Action<TrainerResult>?>? BeginTrainingSync { get; set; }
    private Action<TrainerResult>? recordTraining;
    private TrainerSettings settings = new(Music: TrainerMusicCatalog.RandomSong());
    private TrainerOsuSettings? inheritedSettings;
    private bool customSettings;
    private bool applyingSettings;
    private bool mouseButtons = true;
    private readonly Dictionary<TrainerKind, AimModButton> exerciseButtons = [];
    private AimModDropdown<int> durationSelector = null!;
    private AimModDropdown<int> tempoSelector = null!;
    private readonly FillFlowContainer<Drawable> exerciseChoices;
    private readonly FillFlowContainer<Drawable> advanced;
    private readonly AimModButton advancedToggle;
    private readonly AimModButton stop;
    private readonly AimModButton restoreControls;
    private readonly OsuSpriteText exerciseTitle;
    private bool advancedOpen;
    private AimModDropdown<string> keySelector = null!;
    private AimModDropdown<int> offsetSelector = null!;
    private InterpolatingFramedClock? audioClock;
    private TrainerHistoryStore? activeHistory;
    private TrainerSession? tapping;
    private PointerTrainerSession? pointer;
    private Track? track;
    private ITrackStore? tracks;
    private readonly TrainerAudio resource = new();
    private bool running;
    private double began;
    private double lastAudioPosition;
    private double lastAudioAdvance;
    private double volume = .85;
    private readonly OsuTextFlowContainer instruction;
    private OsuTextFlowContainer status = null!;
    private readonly OsuSpriteText feedback;
    private readonly FillFlowContainer<Drawable> results;
    private readonly FillFlowContainer<Drawable> recent;
    private readonly AimModButton start;
    private readonly TrainerField field;
    private readonly FillFlowContainer<Drawable> timingControls;
    private readonly FillFlowContainer<Drawable> controls;
    private readonly Container setup;
    private readonly AimModScrollContainer contentScroll;
    private bool showingResults;
    private bool isTiming => settings.Kind <= TrainerKind.Rhythm;
    private double elapsed => isTiming ? audioClock?.CurrentTime ?? 0 : Time.Current - began;

    public NativeTrainersWorkspace(Func<TrainerHistoryStore> history, Action openCoaching)
    {
        this.history = history;
        this.openCoaching = openCoaching;
        preferences = history().LoadPreferences();
        settings = settings with { RandomizePatterns = preferences.RandomizePatterns, GuidedCues = preferences.GuidedCues, AdaptiveDifficulty = preferences.AdaptiveDifficulty };
        freshAimLayout = preferences.FreshLayout;
        RelativeSizeAxes = Axes.Both;
        pageBody = new Container { RelativeSizeAxes = Axes.X, Padding = new MarginPadding { Right = 14 } };
        InternalChildren = [new AimModSectionHeader("Trainers", "Focused drills for timing, control and reading."),
            skillPage = new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Top = 120 }, Children = [
                contentScroll = new AimModScrollContainer { RelativeSizeAxes = Axes.Both, Child = pageBody },
                actionsLayer = new Container { RelativeSizeAxes = Axes.Both },
            ] }];
        var tabs = new FillFlowContainer<Drawable> { Y = 72, AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(8) };
        tabs.Add(skillEntry = new AimModButton("Skill trainers", showSkillTrainers));
        tabs.Add(dtEntry = new AimModButton("Mod trainers", openDtTrainer));
        skillEntry.SetSelected(true);
        AddInternal(tabs);
        AddInternal(modPage = new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Top = 120 }, Alpha = 0 });
        buildWarmupTab(tabs);
        pageBody.Add(results = column());
        results.Padding = new MarginPadding { Bottom = 24 };
        pageBody.Add(setup = new Container { RelativeSizeAxes = Axes.X });
        setup.Add(choices = column()); choices.RelativeSizeAxes = Axes.None; choices.Spacing = new(12);
        setup.Add(progress = column()); progress.RelativeSizeAxes = Axes.None; progress.Spacing = new(12);
        buildSessionCard();
        var body = choices;
        body.Add(new AimModSubsectionHeader("Skill"));
        body.Add(exerciseChoices = flow());
        exerciseChoices.Margin = new MarginPadding { Top = -4 };
        foreach (var kind in Enum.GetValues<TrainerKind>())
        {
            var button = skillChoice(kind);
            exerciseButtons[kind] = button; exerciseChoices.Add(button);
        }
        body.Add(exerciseTitle = text(DisplayName(settings.Kind), 20, AimModPalette.Text));
        exerciseTitle.Hide();
        body.Add(instruction = paragraph(""));
        instruction.Hide();
        buildPresetControls(body);
        buildGuidedControls(body);
        controls = flow(); controls.Depth = -10;
        controls.RelativeSizeAxes = Axes.None; controls.Width = 142; controls.Anchor = controls.Origin = Anchor.TopRight;
        controls.Add(stretch(selector("LENGTH", new[] { 15, 30, 60, 120, 180 }.Select(s => new KeyValuePair<string, int>(s < 60 ? $"{s} seconds" : $"{s / 60} minute{(s == 60 ? "" : "s")}", s)),
            settings.Seconds, s => { Suspend(); settings = settings with { Seconds = s }; refreshHistory(); }, 150, d => durationSelector = d), 1));
        sessionHeader.Add(controls);
        timingControls = flow(); timingControls.Depth = -9.9f;
        timingControls.Add(stretch(selector("TEMPO", Enumerable.Range(6, 19).Select(i => new KeyValuePair<string, int>($"{i * 10} BPM", i * 10)),
            settings.Bpm, b => { Suspend(); settings = settings with { Bpm = b }; refreshMusicDescription(); refreshHistory(); }, 150, d => tempoSelector = d), .5f));
        sessionBody.Add(timingControls);
        buildAdaptiveControls(sessionBody);
        buildStarControls(sessionBody);
        buildMusicControls(sessionBody);
        practiceOptionsToggle = new AimModButton("Adjust patterns & difficulty", TogglePracticeOptions);
        practiceOptions = column();
        practiceOptions.Depth = -9;
        buildPatternControls(practiceOptions);
        buildAimControls(practiceOptions);
        body.Add(practiceOptionsToggle);
        body.Add(practiceOptions);
        body.Add(readingControls);
        buildObjectAndGuideControls(body, sessionBody);
        practiceOptions.Hide();
        advanced = column(); advanced.Depth = -8; advanced.Alpha = 0;
        start = new AimModButton("Start practice", startSelectedPractice, true) { AutoSizeAxes = Axes.None, RelativeSizeAxes = Axes.X, Height = 44 };
        stop = new AimModButton("Stop session", () => Suspend()) { Alpha = 0 };
        advancedToggle = new AimModButton("Controls & audio", () =>
        { advancedOpen = !advancedOpen; advanced.Alpha = advancedOpen ? 1 : 0; advancedToggle!.SetSelected(advancedOpen); });
        var audioControls = flow();
        audioControls.Add(stretch(selector("TAPPING KEYS", new[] { "Z / X", "D / F", "J / K" }.Select(s => new KeyValuePair<string, string>(s, s)),
            settings.Keys, s => { Suspend(); settings = settings with { Keys = s }; if (!applyingSettings) { customSettings = true; refreshInputChips(); } updateInstruction(); refreshHistory(); }, 150, d => keySelector = d), .5f));
        audioControls.Add(stretch(selector("AUDIO OFFSET", Enumerable.Range(-50, 101).Select(i => new KeyValuePair<string, int>($"{i * 10:+0;-0;0} ms", i * 10)),
            0, o => { Suspend(); settings = settings with { OffsetMs = o }; if (!applyingSettings) { customSettings = true; refreshInputChips(); } refreshHistory(); }, 150, d => offsetSelector = d), .5f));
        audioControls.Add(stretch(selector("MUSIC VOLUME", new[] { 0, 25, 50, 70, 85, 100 }.Select(i => new KeyValuePair<string, int>($"{i}%", i)),
            85, v => { volume = v / 100.0; if (track is not null) track.Volume.Value = volume; }, 165), .5f));
        advanced.Add(audioControls);
        advanced.Add(restoreControls = new AimModButton("Restore osu! controls", () => { customSettings = false; if (inheritedSettings is {} inherited) ApplyOsuSettings(inherited); }) { Alpha = 0 });
        sessionBody.Add(advanced);
        buildSessionActions();
        body.Add(field = new TrainerField(this) { RelativeSizeAxes = Axes.X, Height = 210 });
        body.Add(feedback = text("", 18, AimModPalette.Accent));
        progress.Add(historyHeader = new AimModSubsectionHeader("Progress"));
        progress.Add(recent = column());
        chooseMusic(settings.Music);
        updateInstruction(); refreshHistory();
        buildReactionStage();
    }

    public static string DisplayName(TrainerKind kind) => kind switch
    {
        TrainerKind.Steady => "Tapping accuracy", TrainerKind.Alternating => "Streams & alternating",
        TrainerKind.Bursts => "Burst control", TrainerKind.Rhythm => "Rhythm changes",
        TrainerKind.Aim => "Aim control", TrainerKind.Reading => "Reading order", TrainerKind.Spinner => "Spinner control", _ => "Reaction",
    };

    private void updateInstruction()
    {
        timingControls.Alpha = settings.Kind != TrainerKind.Reaction && settings.Music != "song" ? 1 : 0;
        instruction.Text = settings.Kind switch
        {
            TrainerKind.Spinner => $"Hold either {settings.Keys} and draw smooth circles around the spinner centre. Keep one direction until it ends.",
            TrainerKind.Steady => $"Follow the moving circles and tap {settings.Keys} in time. Keep your motion relaxed through rests and changes in spacing.",
            TrainerKind.Alternating => $"Alternate {settings.Keys} evenly through each stream. Finish the last tap before relaxing during a rest.",
            TrainerKind.Bursts => $"Finish every burst with {settings.Keys}, including its last tap. Use the rest to relax and move to the next group.",
            TrainerKind.Rhythm => $"Listen for changes in spacing with {settings.Keys}. Keep the pulse through rests and return on time.",
            TrainerKind.Aim when settings.AimStyle == TrainerAimStyle.Flow => "Follow each curve with a smooth movement. Tap as the approach circle closes, then continue through the next target.",
            TrainerKind.Aim when settings.AimStyle == TrainerAimStyle.SmallCorrections => "Make small, precise corrections between nearby circles. Settle on each target without overshooting.",
            TrainerKind.Aim when settings.AimStyle == TrainerAimStyle.DirectionChanges => "Brake on each target before reversing direction. Keep each tap separate from the next movement.",
            TrainerKind.Aim => $"Follow the jumps and press {settings.Keys} as each approach circle closes. Land on the target before tapping.",
            TrainerKind.Reading => $"Read the approach circles and hit each note in order with {settings.Keys}. Look ahead while finishing the current target.",
            _ => $"Wait for the field to turn mint, then press {settings.Keys} or click. The delay changes each time; early taps count as false starts.",
        };
        if (exerciseTitle is not null) exerciseTitle.Text = DisplayName(settings.Kind);
        foreach (var (kind, button) in exerciseButtons) button.SetSelected(kind == settings.Kind);
        if (status is not null) status.Text = settings.Kind == TrainerKind.Reaction ? "Wait for mint. Escape ends the session." : settings.Music == "song" ? "Listen to the song lead-in. Escape ends the session." : "Four-beat count-in. Escape ends the session.";
        if (aimControls is not null) aimControls.Alpha = settings.Kind is not (TrainerKind.Reaction or TrainerKind.Spinner) ? 1 : 0;
        if (aimStyleControl is not null) aimStyleControl.Alpha = settings.Kind == TrainerKind.Aim ? 1 : 0;
        if (patternControls is not null) patternControls.Alpha = settings.Kind is not (TrainerKind.Reaction or TrainerKind.Spinner) ? 1 : 0;
        if (geometryControls is not null) geometryControls.Alpha = settings.Kind is not (TrainerKind.Reaction or TrainerKind.Spinner) ? 1 : 0;
        if (pathControl is not null) pathControl.Alpha = settings.Kind is TrainerKind.Aim or TrainerKind.Reading ? 0 : 1;
        if (reactionControls is not null) reactionControls.Alpha = settings.Kind == TrainerKind.Reaction ? 1 : 0;
        if (readingControls is not null) readingControls.Alpha = settings.Kind == TrainerKind.Reading ? 1 : 0;
        if (musicControls is not null) musicControls.Alpha = settings.Kind == TrainerKind.Reaction ? 0 : 1;
        if (timingControls is not null) timingControls.Alpha = settings.Kind != TrainerKind.Reaction && settings.Music != "song" ? 1 : 0;
        advancedToggle?.SetCaption(settings.Kind == TrainerKind.Reaction ? "Controls" : "Controls & audio");
        refreshObjectAndGuideControls();
        refreshPracticeIntent();
        field?.Reset();
        if (field is not null) field.Alpha = 0;
    }

    public void ApplyOsuSettings(TrainerOsuSettings inherited)
    {
        inheritedSettings = inherited;
        restoreControls.Show();
        if (running || customSettings) return;
        var keys = TrainerSettings.ParseKeys(inherited.Keys);
        if (keys.Length != 2 || keys[0] == keys[1]) return;
        applyingSettings = true;
        try
        {
            if (!keySelector.Items.Contains(inherited.Keys)) keySelector.Items = keySelector.Items.Append(inherited.Keys).ToArray();
            if (!offsetSelector.Items.Contains(inherited.OffsetMs)) offsetSelector.Items = offsetSelector.Items.Append(inherited.OffsetMs).ToArray();
            keySelector.Current.Value = inherited.Keys; offsetSelector.Current.Value = inherited.OffsetMs;
            settings = settings with { Keys = inherited.Keys, OffsetMs = inherited.OffsetMs };
            mouseButtons = inherited.MouseButtons;
            refreshInputChips();
            updateInstruction(); refreshHistory();
        }
        finally { applyingSettings = false; }
    }

    public void RefreshHistory() => refreshHistory();

    public void CompleteOsuSession(TrainerResult? result)
    {
        if (completeWarmup(result)) return;
        results.Clear();
        if (result is null)
        {
            bool guided = pendingGuidedRun is not null;
            pendingGuidedRun = null; recordTraining = null;
            if (guided) showGuidedOverview();
            else { showingResults = false; setup.Show(); }
            status.Text = "Session stopped. Start again when you are ready.";
            return;
        }
        result = annotateGuidedResult(result);
        status.Text = "Session complete.";
        showResult(result);
        showingResults = true; setup.Hide(); results.FadeInFromZero(220);
        if (!result.Assisted) recordTraining?.Invoke(result);
        recordTraining = null;
        try { (activeHistory ?? history()).Add(result); refreshHistory(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { status.Text = "Your result is shown below, but history could not be saved."; }
    }

    public void Start()
    {
        if (running || preparing) return;
        pendingGuidedRun = null;
        if (showingResults) { showingResults = false; setup.Show(); }
        if (settings.Kind != TrainerKind.Reaction && settings.Music == "song" && SelectedSong is null)
        { status.Text = "Choose an installed song below the music selector."; return; }
        results.Clear(); feedback.Text = "";
        if (!customSettings && inheritedSettings is {} inherited) ApplyOsuSettings(inherited);
        if (freshAimLayout || settings.PatternSeed == 0) settings = settings with { PatternSeed = Random.Shared.Next(1, int.MaxValue) };
        if (settings.Kind != TrainerKind.Reaction && preferences.ShuffleMusic && TrainerMusicCatalog.IsSong(settings.Music)) musicSelector.Current.Value = TrainerMusicCatalog.RandomSong(settings.Music);
        var sessionSettings = adaptiveSettings(settings);
        activeHistory = history();
        recordTraining = BeginTrainingSync?.Invoke();
        if (settings.Kind != TrainerKind.Reaction && LaunchOsuSession is {} launch)
        { launch(sessionSettings, mouseButtons, volume); return; }
        tapping = null; pointer = null; reaction = null;
        if (isTiming)
        {
            tapping = new(sessionSettings);
            try
            {
                resource.Wave = TrainerAudio.Render(tapping);
                tracks ??= audio.GetTrackStore(resource);
                track?.Dispose(); track = tracks.Get($"session-{Guid.NewGuid():N}.wav");
                if (track is null) { status.Text = "Audio could not start. Check your output device and try again."; return; }
                track.Volume.Value = volume; audioClock = new InterpolatingFramedClock(new FramedClock(track)) { DriftRecoveryHalfLife = 80 }; track.Start();
            }
            catch (Exception error) when (error is InvalidOperationException or IOException)
            { status.Text = "Audio could not start. Check your output device and try again."; return; }
        }
        else if (settings.Kind == TrainerKind.Reaction) reaction = new(sessionSettings);
        else if (settings.Kind == TrainerKind.Spinner) { status.Text = "Open the osu! gameplay connection to practise spinners."; return; }
        else pointer = new(sessionSettings);
        began = Time.Current; running = true; liveText.Clear();
        lastAudioPosition = 0; lastAudioAdvance = Time.Current;
        controls.Hide(); timingControls.Hide(); exerciseChoices.Hide(); advanced.Hide(); advancedToggle.Hide(); stop.Show();
        start.SetCaption("Session running"); field.Reset();
        if (reaction is not null)
        {
            setup.Hide(); reactionStage.Show(); reactionInstructions.Text = reactionHelp()
                + (mouseButtons ? " Left / right mouse buttons also map to the first / second key." : "");
        }
    }

    internal void SelectTrainer(TrainerKind kind)
    {
        if (settings.Kind == kind) return;
        Suspend();
        var initialPattern = kind == TrainerKind.Reading ? TrainerPattern.ReadingMix : TrainerPattern.Standard;
        settings = settings with { Kind = kind, Pattern = initialPattern, OverallDifficulty = 5,
            Sliders = kind == TrainerKind.Reading ? TrainerSliderStyle.Mixed : TrainerSliderStyle.None,
            GuidedCues = kind == TrainerKind.Spinner || preferences.GuidedCues };
        sliderSelector.Current.Value = settings.Sliders; results.Clear(); feedback.Text = "";
        patternSelector.Items = TrainerPatterns.Choices(kind).Values;
        patternSelector.Current.Value = initialPattern;
        rebuildPresets();
        updateInstruction(); refreshHistory();
    }

    public void Suspend(string message = "Session stopped. Completed sessions are saved in your history.")
    {
        if (!running) return;
        running = false; track?.Stop(); refreshPracticeIntent();
        if (reactionStage is not null) reactionStage.Hide();
        setup.Show();
        controls.Show(); exerciseChoices.Show(); advancedToggle.Show(); advanced.Alpha = advancedOpen ? 1 : 0; stop.Hide(); timingControls.Alpha = settings.Kind != TrainerKind.Reaction && settings.Music != "song" ? 1 : 0; status.Text = message;
    }

    protected override void Update()
    {
        base.Update();
        updateLayout();
        updateWarmupGrid();
        if (!running) return;
        audioClock?.ProcessFrame();
        if (!host.IsActive.Value) { Suspend("Session stopped when AimMod lost focus. Start again when you are ready."); return; }
        double time = elapsed;
        if (isTiming)
        {
            if (time > lastAudioPosition) { lastAudioPosition = time; lastAudioAdvance = Time.Current; }
            else if (Time.Current - lastAudioAdvance > 2500)
            { Suspend("Audio stopped. Check your output device and start a new session."); return; }
        }
        reaction?.Advance(time);
        if (reaction is not null && settings.GuidedCues) updateReactionGuide(time);
        if (reaction is not null) reactionStatus.Text = $"Reaction practice · {Math.Max(0, Math.Ceiling((reaction.EndMs - time) / 1000)):0}s left · {reaction.Trials.Count(t => t.Outcome == ReactionOutcome.Hit)} correct";
        double end = tapping is {} active ? active.EndMs + Math.Max(200, -settings.OffsetMs + active.WindowMs) : reaction?.EndMs ?? pointer!.EndMs;
        if (time >= end)
        {
            var result = tapping?.Result(DateTimeOffset.Now) ?? reaction?.Result(DateTimeOffset.Now) ?? pointer!.Result(DateTimeOffset.Now);
            Suspend("Session complete.");
            showResult(result);
            showingResults = true; setup.Hide(); results.FadeInFromZero(220);
            recordTraining?.Invoke(result); recordTraining = null;
            try { (activeHistory ?? history()).Add(result); refreshHistory(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { status.Text = "Session complete, but history could not be saved. Your result is shown below."; }
            return;
        }
        if (tapping is { } tap && time + settings.OffsetMs < tap.StartMs)
            showLive(status, $"Count-in  {Math.Clamp((int)((time + settings.OffsetMs - 500) / tap.BeatMs) + 1, 1, 4)} / 4");
        else showLive(status, $"{Math.Max(0, (end - time) / 1000):0}s remaining  |  {(tapping?.Hits.Count ?? reaction?.Trials.Count(t => t.Outcome == ReactionOutcome.Hit) ?? pointer!.Responses.Count)} hits  |  Escape to stop");
    }

    // Per-frame labels: replacing a text flow rebuilds its sprites, so only write changes.
    private readonly Dictionary<TextFlowContainer, string> liveText = [];
    private void showLive(TextFlowContainer target, string value)
    {
        if (liveText.TryGetValue(target, out string? shown) && shown == value) return;
        liveText[target] = value; target.Text = value;
    }

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (!running) return base.OnKeyDown(e);
        if (e.Key == Key.Escape) { Suspend(); return true; }
        Key[] keys = TrainerSettings.ParseKeys(settings.Keys);
        int index = Array.IndexOf(keys, e.Key);
        if (index < 0) return base.OnKeyDown(e);
        if (!e.Repeat) tap(index);
        return true;
    }

    private void tap(int key, Vector2? mouse = null)
    {
        if (!running) return;
        double time = elapsed;
        if (reaction is {} reactionSession)
        {
            reactionSession.Tap(time, key);
            feedback.Text = reactionSession.LastFeedback;
        }
        else if (tapping is { } session)
        {
            if (time + settings.OffsetMs < session.StartMs - session.WindowMs) return;
            var hit = session.Tap(time, key);
            feedback.Text = hit is null ? "Extra tap" : $"{Math.Abs(hit.OffsetMs):0} ms {(hit.OffsetMs < 0 ? "early" : "late")}";
            field.LastOffset = hit?.OffsetMs;
        }
        else if (pointer is { } drill)
        {
            Vector2 cursor = mouse ?? field.ToLocalSpace(GetContainingInputManager()!.CurrentState.Mouse.Position);
            var target = drill.Targets.FirstOrDefault(t => Vector2.Distance(cursor,
                new Vector2((float)t.X * field.DrawWidth, (float)t.Y * field.DrawHeight)) <= 24);
            bool hit = drill.Tap(time, target?.Number);
            feedback.Text = hit ? $"{drill.Responses[^1]:0} ms" : settings.Kind == TrainerKind.Reaction ? "Too soon - wait for mint" : "Land on the next target before tapping";
            field.Reset();
        }
    }

    private void showResult(TrainerResult r)
    {
        contentScroll.ScrollTo(0, false);
        var savedRuns = history().Load();
        var last = savedRuns.Where(p => p.Id != r.Id && p.CompletedAt < r.CompletedAt && p.Assisted == r.Assisted
            && p.Settings.Kind == r.Settings.Kind && p.UsesOsuJudgements == r.UsesOsuJudgements).OrderByDescending(p => p.CompletedAt).FirstOrDefault();
        addResultHeader(r);
        results.Add(resultKpis(r, last));
        if (!addGuidedActions(r)) addNextRun(r, savedRuns);
        if ((r.UsesOsuJudgements || r.Settings.Kind <= TrainerKind.Rhythm) && r.Settings.Kind != TrainerKind.Spinner && r.MeanMs is not null)
            results.Add(new TrainerTimingHistogram(r, last));
        if (r.Settings.Kind == TrainerKind.Spinner)
            results.Add(paragraph("Radius is in osu! pixels. A smaller circle only helps while rotation stays smooth. Guided and unguided runs are compared separately."));
        addSpecializedResults(r);
        if (r.Settings.Kind != TrainerKind.Spinner && TrainerProgressComparison.Build(r, savedRuns) is { } comparison && r.UsesOsuJudgements)
        {
            results.Add(new AimModSubsectionHeader("Same drill", "first 3 runs vs latest 3"));
            var changes = flow();
            changes.Add(comparisonMetric("ACCURACY", comparison.FirstAccuracy, comparison.LatestAccuracy, "%", AimModTrainerTrend.HigherIsBetter));
            changes.Add(comparisonMetric("MISSES", comparison.FirstMisses, comparison.LatestMisses, "", AimModTrainerTrend.LowerIsBetter));
            changes.Add(comparisonMetric("TIMING SPREAD", comparison.FirstSpread, comparison.LatestSpread, " ms", AimModTrainerTrend.LowerIsBetter));
            results.Add(changes);
        }
        else if (last is null) results.Add(paragraph("Complete another run to compare your results."));
    }

    private void addResultHeader(TrainerResult r)
    {
        results.Add(text(r.WarmupRun is not null ? "Warmup drill complete" : r.Assisted ? "Assisted session complete" : r.Settings.GuidedCues ? "Guided practice complete" : "Session complete", 22, AimModPalette.Text));
        var chips = flow(); chips.Spacing = new(6);
        chips.Add(new AimModTrainerChip(DisplayName(r.Settings.Kind), tone: AimModTrainerChipTone.Accent));
        if (TrainerPresets.For(r.Settings.Kind).FirstOrDefault(p => p.Matches(r.Settings)) is { } preset) chips.Add(new AimModTrainerChip(preset.Title));
        if (r.Settings.Kind == TrainerKind.Reaction)
        {
            chips.Add(new AimModTrainerChip(ReactionSession.Name(r.Settings.ReactionMode), FontAwesome.Solid.Bolt));
            chips.Add(new AimModTrainerChip($"{r.Settings.ReactionWindowMs} ms window", FontAwesome.Solid.Stopwatch));
        }
        else chips.Add(new AimModTrainerChip(r.Settings.TempoDescription, FontAwesome.Solid.Music));
        chips.Add(new AimModTrainerChip($"{r.PlayedSeconds ?? r.Settings.Seconds:0.#} s", FontAwesome.Regular.Clock));
        if (r.Settings.Kind == TrainerKind.Reading)
        {
            chips.Add(new AimModTrainerChip($"{r.Settings.ReadingGroupSize}-note phrases"));
            chips.Add(new AimModTrainerChip($"AR {r.Settings.ApproachRate}"));
            if (r.Settings.ReadingHidden) chips.Add(new AimModTrainerChip("Hidden", FontAwesome.Solid.EyeSlash));
        }
        if (r.Settings.Music == "song") chips.Add(new AimModTrainerChip($"{r.Settings.SongTitle} · +{r.Settings.SongStartSeconds}s", FontAwesome.Solid.Headphones));
        if (r.Settings.MeasuredStars is { } stars) chips.Add(new AimModTrainerChip($"{stars:0.00}", FontAwesome.Solid.Star, tooltip: TrainerStarTarget.Describe(r.Settings)));
        results.Add(chips);
    }

    private Drawable resultKpis(TrainerResult r, TrainerResult? last)
    {
        var metrics = flow();
        string versus(double? now, double? before, string unit, string pattern = "0.0") => now is { } a && before is { } b
            ? $"{(a - b).ToString("+" + pattern + ";-" + pattern + ";0")}{unit} vs last" : last is null ? "first run" : "no previous value";
        double? change(double? now, double? before) => now is { } a && before is { } b ? a - b : null;
        if (r.Reaction is {} reactionResult)
        {
            var previous = last?.Reaction;
            metrics.Add(new AimModTrainerKpi("MEDIAN RESPONSE", ms(reactionResult.MedianMs), versus(reactionResult.MedianMs, previous?.MedianMs, " ms", "0"),
                change(reactionResult.MedianMs, previous?.MedianMs), AimModTrainerTrend.LowerIsBetter));
            metrics.Add(new AimModTrainerKpi("SLOWEST 10%", ms(reactionResult.Slow90Ms), "90th percentile", tooltip: "Your slower responses. Keep mistakes low before shortening the window."));
            metrics.Add(new AimModTrainerKpi("CORRECT", $"{reactionResult.Correct}", $"{reactionResult.Missed} missed cues"));
            metrics.Add(new AimModTrainerKpi("EARLY TAPS", $"{reactionResult.Early}", versus(reactionResult.Early, previous?.Early, "", "0"), change(reactionResult.Early, previous?.Early), AimModTrainerTrend.LowerIsBetter));
            if (r.Settings.ReactionMode is ReactionMode.Choice or ReactionMode.ChoiceGoNoGo) metrics.Add(new AimModTrainerKpi("WRONG KEYS", $"{reactionResult.WrongKey}"));
            if (r.Settings.ReactionMode is ReactionMode.GoNoGo or ReactionMode.ChoiceGoNoGo)
            {
                metrics.Add(new AimModTrainerKpi("CORRECT HOLDS", $"{reactionResult.Withheld}", "stayed still on STOP"));
                metrics.Add(new AimModTrainerKpi("STOP ERRORS", $"{reactionResult.FalseAlarms}", "tapped on STOP"));
            }
        }
        else if (r.Settings.Kind == TrainerKind.Spinner && r.SpinnerPractice is {} spin)
        {
            var previous = last?.SpinnerPractice;
            metrics.Add(new AimModTrainerKpi("AVERAGE RPM", $"{spin.MeanRpm:0}", versus(spin.MeanRpm, previous?.MeanRpm, "", "0"), change(spin.MeanRpm, previous?.MeanRpm), AimModTrainerTrend.HigherIsBetter, tooltip: "Average rotations per minute while a key is held."));
            metrics.Add(new AimModTrainerKpi("SPEED VARIATION", spin.SpeedVariationPercent is {} variation ? $"{variation:0}%" : "--", versus(spin.SpeedVariationPercent, previous?.SpeedVariationPercent, "%", "0"),
                change(spin.SpeedVariationPercent, previous?.SpeedVariationPercent), AimModTrainerTrend.LowerIsBetter, tooltip: "How much your speed changes during a spin. Lower is smoother."));
            metrics.Add(new AimModTrainerKpi("KEY HELD", $"{spin.HeldPercent:0}%", versus(spin.HeldPercent, previous?.HeldPercent, "%", "0"), change(spin.HeldPercent, previous?.HeldPercent), AimModTrainerTrend.HigherIsBetter));
            metrics.Add(new AimModTrainerKpi("DIRECTION CHANGES", $"{spin.DirectionChanges}"));
            metrics.Add(new AimModTrainerKpi("SPINNERS", $"{spin.Attempts}"));
            metrics.Add(new AimModTrainerKpi("CIRCLE RADIUS", spin.MeanRadius is {} radius ? $"{radius:0} px" : "--"));
        }
        else if (r.UsesOsuJudgements || r.Settings.Kind <= TrainerKind.Rhythm)
        {
            if (r.UsesOsuJudgements)
                metrics.Add(new AimModTrainerKpi("HIT ACCURACY", $"{r.Accuracy:0.00}%", versus(r.Accuracy, last?.Accuracy, "%"), change(r.Accuracy, last?.Accuracy), AimModTrainerTrend.HigherIsBetter,
                    tooltip: "osu! accuracy from 300s, 100s, 50s and misses."));
            metrics.Add(new AimModTrainerKpi("TIMING SPREAD", ms(r.SpreadMs), versus(r.SpreadMs, last?.SpreadMs, " ms"), change(r.SpreadMs, last?.SpreadMs), AimModTrainerTrend.LowerIsBetter,
                tooltip: "How much your tap timing varies (standard deviation). Lower is steadier."));
            metrics.Add(new AimModTrainerKpi("TAPS WITHIN ±25 MS", $"{r.OnTimePercent:0.0}%", versus(r.OnTimePercent, last?.OnTimePercent, "%"), change(r.OnTimePercent, last?.OnTimePercent), AimModTrainerTrend.HigherIsBetter,
                tooltip: "Share of taps that landed within 25 ms of the note."));
            metrics.Add(new AimModTrainerKpi("MISSES", $"{r.Misses}", last is null ? r.UsesOsuJudgements ? $"{r.Hits} of {r.Notes} hit" : $"{r.Extras} extra · {r.RepeatedKeys} repeated" : versus(r.Misses, last.Misses, "", "0"),
                change(r.Misses, last?.Misses), AimModTrainerTrend.LowerIsBetter, tooltip: $"{r.Hits} of {r.Notes} notes hit."));
            // Offset and drift are best near zero, so compare their distance from zero.
            string towardZero(double? now, double? before) => now is { } a && before is { } b
                ? Math.Abs(Math.Abs(a) - Math.Abs(b)) < .05 ? "same distance from 0" : $"{Math.Abs(Math.Abs(a) - Math.Abs(b)):0.0} ms {(Math.Abs(a) < Math.Abs(b) ? "closer to" : "further from")} 0"
                : now is { } n ? Math.Abs(n) < 1 ? "centred" : n < 0 ? "early on average" : "late on average" : "--";
            double? distance(double? now, double? before) => now is { } a && before is { } b ? Math.Abs(a) - Math.Abs(b) : null;
            metrics.Add(new AimModTrainerKpi("AVERAGE OFFSET", r.MeanMs is { } mean ? $"{mean:+0.0;-0.0;0} ms" : "--", towardZero(r.MeanMs, last?.MeanMs),
                distance(r.MeanMs, last?.MeanMs), AimModTrainerTrend.LowerIsBetter,
                tooltip: "Negative is early, positive is late; best near 0. Same sign as osu! audio offset."));
            if (r.DriftMs is { } drift)
                metrics.Add(new AimModTrainerKpi("DRIFT", $"{drift:+0.0;-0.0;0} ms", last?.DriftMs is null ? "end vs start" : towardZero(drift, last.DriftMs),
                    distance(drift, last?.DriftMs), AimModTrainerTrend.LowerIsBetter,
                    tooltip: "How your average timing moved between the first and last third of the run; best near 0."));
        }
        else
        {
            metrics.Add(new AimModTrainerKpi(r.Settings.Kind == TrainerKind.Reaction ? "MEDIAN REACTION" : "MEDIAN TARGET TIME", ms(r.ResponseMs)));
            metrics.Add(new AimModTrainerKpi("TARGETS HIT", $"{r.Hits}"));
            metrics.Add(new AimModTrainerKpi(r.Settings.Kind == TrainerKind.Reaction ? "FALSE STARTS" : "OFF-TARGET TAPS", $"{r.Extras}"));
        }
        return metrics;
    }

    private void addNextRun(TrainerResult r, IReadOnlyList<TrainerResult> savedRuns)
    {
        var next = column(); next.Spacing = new(8);
        next.Add(new AimModSubsectionHeader("Next run", preferences.AdaptiveDifficulty && !r.Assisted ? "adapted from this result" : null));
        next.Add(paragraph(TrainerSession.NextStep(r)));
        if (preferences.AdaptiveDifficulty && !r.Assisted)
        {
            var planned = adaptiveSettings(r.Settings, savedRuns.Append(r));
            var plan = flow();
            foreach (var gauge in gauges(planned, usual(r.Settings.Kind))) { gauge.Width = 112; plan.Add(gauge); }
            next.Add(plan);
        }
        results.Add(next);
        var nextActions = flow();
        nextActions.Add(new AimModButton(preferences.AdaptiveDifficulty ? "Start next run" : "Repeat exercise", () => repeat(r.Settings, 0), true));
        nextActions.Add(new AimModButton("Practice settings", returnToPracticeSettings));
        if (r.Settings.Kind != TrainerKind.Reaction) nextActions.Add(new AimModButton("Try on a beatmap", openCoaching));
        if (r.Settings.Kind != TrainerKind.Reaction && r.Settings.Music == "cues" && r.Settings.Bpm > 60)
            nextActions.Add(new AimModButton("Try 10 BPM slower", () => repeat(r.Settings, -10)));
        results.Add(nextActions);
    }

    private void returnToPracticeSettings()
    {
        showingResults = false;
        results.Clear();
        results.Hide();
        setup.Show();
        refreshPracticeIntent();
        // Start sticks to the bottom edge, so the top of the choices is the useful place to return to.
        contentScroll.ScrollTo(0, false);
    }

    private void repeat(TrainerSettings selected, int tempoChange)
    {
        // A generated run must not overwrite the user's manual controls on the results path.
        if ((preferences.AdaptiveDifficulty || selected.MinimumStars is not null) && selected.Kind == settings.Kind && tempoChange == 0)
        { Start(); return; }
        SelectTrainer(selected.Kind);
        restorePatternControls(selected);
        aimStyleSelector.Current.Value = selected.AimStyle; aimSpacingSelector.Current.Value = selected.AimSpacing; circleSizeSelector.Current.Value = selected.CircleSize;
        if (!freshAimLayout) settings = settings with { PatternSeed = selected.PatternSeed };
        musicSelector.Current.Value = selected.Music;
        songStartSelector.Current.Value = selected.SongStartSeconds;
        if (selected.Music == "song" && selected.SongIdentity != settings.SongIdentity)
        { status.Text = $"Choose {selected.SongTitle} in the song picker to repeat this session."; return; }
        cueSelector.Current.Value = selected.Cue;
        durationSelector.Current.Value = selected.Seconds;
        tempoSelector.Current.Value = Math.Clamp(selected.Bpm + tempoChange, 60, 240);
        Start();
    }

    private void refreshHistory()
    {
        refreshPresetSummary();
        refreshSkillSummary();
        refreshPracticeIntent();
        if (recent is null) return;
        renderProgress();
    }

    protected override void Dispose(bool isDisposing)
    { songSearchCancellation?.Cancel(); songSearchCancellation?.Dispose(); track?.Stop(); track?.Dispose(); tracks?.Dispose(); base.Dispose(isDisposing); }

    private static string ms(double? value) => value is { } n ? $"{n:0.0} ms" : "--";
    private static Drawable comparisonMetric(string title, double? first, double? latest, string unit, AimModTrainerTrend trend)
    {
        string format(double? value) => value is { } n ? $"{n:0.0}{unit}" : "--";
        return new AimModTrainerKpi(title, format(latest), $"from {format(first)}", first is { } a && latest is { } b ? b - a : null, trend);
    }
    private static FillFlowContainer<Drawable> flow() => new() { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Spacing = new(8), Direction = FillDirection.Full };
    private static FillFlowContainer<Drawable> column() => new() { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Spacing = new(8), Direction = FillDirection.Vertical };
    private static OsuSpriteText text(string value, float size, Colour4 colour) => new() { Text = value, Font = new FontUsage(size: size), Colour = colour };
    private static OsuTextFlowContainer paragraph(string value) => new(t => { t.Font = new FontUsage(size: 14); t.Colour = AimModPalette.Muted; }) { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = value };
    private const float selectorLabelSpacing = 20;

    private Drawable selector<T>(string label, IEnumerable<KeyValuePair<string, T>> options, T selected, Action<T> changed, float width, Action<AimModDropdown<T>>? capture = null)
    {
        var labels = options.ToArray();
        var dropdown = new TrainerDropdown<T>(v => labels.FirstOrDefault(p => EqualityComparer<T>.Default.Equals(p.Value, v)).Key ?? v?.ToString() ?? "") { RelativeSizeAxes = Axes.X, Y = selectorLabelSpacing, Items = labels.Select(p => p.Value) };
        cardMenus.Add(dropdown);
        capture?.Invoke(dropdown);
        dropdown.Current.Value = selected; dropdown.Current.BindValueChanged(e => changed(e.NewValue));
        string? hint = label switch
        {
            "PATTERN" => "How notes and rests are arranged.",
            "NOTES PER BEAT" => "More notes at the same music tempo.",
            "SLIDERS" => "Mix holds with taps, or focus on holds.",
            "SLIDER LENGTH" => "How long each slider lasts, in beats.",
            "MOVEMENT" => "The path followed by tapping patterns.",
            "APPROACH RATE" => "Higher AR gives less time to read each note.",
            "AIM TYPE" => "The kind of cursor movement to practise.",
            "JUMP DISTANCE" => "100% is normal spacing for this drill.",
            "CIRCLE SIZE" => "Higher CS means smaller targets.",
            "LAYOUT" => "Keep a layout to compare repeat runs.",
            "AUDIO OFFSET" => "Keep your osu! offset for fair comparisons.",
            "CUE DELAY" => "How long to wait before the visual cue.",
            "REACTION DRILL" => "React, choose a key, or hold back on STOP.",
            "RESPONSE WINDOW" => "How long each cue accepts a response.",
            "SEQUENCE LENGTH" => "Notes per phrase, followed by a short reading break.",
            "READING CHALLENGE" => "Harder patterns, with a limit on visible notes.",
            "NOTE VISIBILITY" => "Hidden makes notes fade before their hit time.",
            _ => null,
        };
        var field = new Container { Width = width, Height = hint is null ? 56 : 104,
            Children = [text(label, 11, AimModPalette.Muted), dropdown] };
        if (hint is not null) field.Add(new OsuTextFlowContainer(t => { t.Font = new(size:12); t.Colour = AimModPalette.Muted; })
            { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Y = 62, Text = hint });
        return field;
    }

    private interface ITrainerMenu { bool MenuOpen { get; } }

    private partial class TrainerDropdown<T>(Func<T, string> label) : AimModDropdown<T>, ITrainerMenu
    {
        public Func<T,string> Label { get; set; } = label;
        public bool MenuOpen => Menu.State == osu.Framework.Graphics.UserInterface.MenuState.Open;
        protected override LocalisableString GenerateItemText(T item) => Label(item);
    }

    private partial class TrainerField : Container
    {
        private readonly NativeTrainersWorkspace owner;
        private readonly Container marks;
        private readonly Box background;
        private readonly OsuSpriteText cue;
        private readonly Box line;
        private readonly Box errorMarker;
        private readonly List<(int Index, CircularContainer Dot)> notes = [];
        private double nextRebuild;
        private Vector2 targetSize;
        private static readonly FontUsage reactionFont = new(size: 30, weight: "SemiBold");
        private double drawnReactionCue = -1;
        private bool drawnReactionReady;
        public double? LastOffset { get; set; }
        public TrainerField(NativeTrainersWorkspace owner)
        {
            this.owner = owner; Masking = true; CornerRadius = 8;
            Children = [background = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                line = new Box { RelativePositionAxes = Axes.X, X = .25f, Y = 38, Height = 132, Width = 2, Colour = AimModPalette.Accent },
                marks = new Container { RelativeSizeAxes = Axes.Both },
                cue = text("", 19, AimModPalette.Text),
                errorMarker = new Box { Y = 204, Width = 3, Height = 12, Colour = AimModPalette.Accent }];
            cue.Anchor = cue.Origin = Anchor.Centre;
        }
        public void Reset() { marks.Clear(); notes.Clear(); nextRebuild = 0; LastOffset = null; }
        protected override bool OnMouseDown(MouseDownEvent e)
        { if (!owner.running || !owner.mouseButtons || e.Button is not (MouseButton.Left or MouseButton.Right)) return false; owner.tap(e.Button == MouseButton.Left ? 0 : 1, e.MousePosition); return true; }
        protected override void Update()
        {
            base.Update();
            line.Alpha = owner.isTiming ? 1 : 0; errorMarker.Alpha = LastOffset is null ? 0 : 1;
            errorMarker.X = DrawWidth * .5f + (float)(LastOffset ?? 0) * DrawWidth / 250;
            background.Colour = AimModPalette.Panel;
            if (!owner.running)
            { marks.Clear(); notes.Clear(); cue.Text = "Ready when you are"; return; }
            double time = owner.elapsed + (owner.isTiming ? owner.settings.OffsetMs : 0);
            if (owner.tapping is { } tapping)
            {
                cue.Text = time < tapping.StartMs ? "Listen to the count-in" : "";
                cue.Y = 80;
                if (time >= nextRebuild)
                {
                    marks.Clear(); notes.Clear(); nextRebuild = time + 150;
                    for (int i = 0; i < tapping.Notes.Count; i++)
                    {
                        double delta = tapping.Notes[i].TimeMs - time;
                        if (delta is < -350 or > 1700) continue;
                        var dot = circle("", i % 2 == 0 ? AimModPalette.Accent : AimModPalette.Text, 16);
                        marks.Add(dot); notes.Add((i, dot));
                    }
                }
                foreach (var (index, dot) in notes)
                {
                    dot.Position = new(DrawWidth * .25f + (float)((tapping.Notes[index].TimeMs - time) / 1700) * DrawWidth * .75f, 105);
                    dot.Alpha = tapping.WasHit(index) ? .15f : tapping.Notes[index].TimeMs < time - tapping.WindowMs ? .3f : 1;
                }
            }
            else if (owner.reaction is {} reaction)
            {
                bool ready = time >= reaction.CueTime;
                bool stopCue = ready && reaction.NoGo;
                background.Colour = ready && !stopCue ? AimModPalette.Accent : AimModPalette.Panel;
                cue.Colour = ready && !stopCue ? AimModPalette.Canvas : AimModPalette.Text;
                cue.Y = 0;
                bool choice = owner.settings.ReactionMode is ReactionMode.Choice or ReactionMode.ChoiceGoNoGo;
                // Assigning a font re-lays out the text. Only change it when it differs.
                if (!cue.Font.Equals(reactionFont)) cue.Font = reactionFont;
                cue.Y = choice ? -95 : 0;
                if (reaction.CueTime != drawnReactionCue || ready != drawnReactionReady || targetSize != DrawSize)
                {
                    marks.Clear(); drawnReactionCue = reaction.CueTime; drawnReactionReady = ready; targetSize = DrawSize;
                    if (choice)
                        for (int i = 0; i < 2; i++)
                        {
                            var cap = circle(owner.settings.Keys.Split(" / ")[i], AimModPalette.Text, 88);
                            cap.Position = new(DrawWidth * (i == 0 ? .35f : .65f), 175);
                            cap.Alpha = ready && !stopCue && i == reaction.RequiredKey ? 1 : .3f;
                            marks.Add(cap);
                        }
                }
                cue.Text = ready ? stopCue ? "STOP · do not tap" : choice ? $"GO · {owner.settings.Keys.Split(" / ")[reaction.RequiredKey]}" : "GO · tap now"
                    : time < reaction.FeedbackUntil ? reaction.LastFeedback : "Wait for the cue";
            }
            else if (owner.pointer is { } pointer)
            {
                cue.Y = 0;
                if (owner.settings.Kind == TrainerKind.Reaction)
                {
                    bool ready = time >= pointer.CueTime;
                    background.Colour = ready ? AimModPalette.Accent : AimModPalette.Panel;
                    cue.Colour = ready ? AimModPalette.Canvas : AimModPalette.Text;
                    cue.Text = ready ? "Tap now" : "Wait for mint";
                }
                else
                {
                    cue.Text = "";
                    if (marks.Count == 0 || targetSize != DrawSize)
                    {
                        marks.Clear(); targetSize = DrawSize;
                        foreach (var t in pointer.Targets)
                        {
                            var dot = circle(owner.settings.Kind == TrainerKind.Reading ? t.Number.ToString() : "", AimModPalette.Accent, 48);
                            dot.Position = new((float)t.X * DrawWidth, (float)t.Y * DrawHeight); marks.Add(dot);
                        }
                    }
                }
            }
        }
        private static CircularContainer circle(string label, Colour4 colour, float size) => new()
        { Size = new(size), Origin = Anchor.Centre, Masking = true, BorderThickness = 2, BorderColour = colour,
            Children = [new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.AccentMuted },
                new OsuSpriteText { Anchor = Anchor.Centre, Origin = Anchor.Centre, Text = label, Font = new FontUsage(size: 18), Colour = AimModPalette.Text }] };
    }
}
