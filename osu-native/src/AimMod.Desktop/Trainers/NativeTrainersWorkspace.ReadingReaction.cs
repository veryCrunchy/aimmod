using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Trainers;

public partial class NativeTrainersWorkspace
{
    private ReactionSession? reaction;
    private Container reactionStage = null!;
    private OsuSpriteText reactionStatus = null!;
    private osu.Game.Graphics.Containers.OsuTextFlowContainer reactionInstructions = null!;
    private FillFlowContainer<Drawable> readingControls = null!;
    private AimModDropdown<int> readingLengthSelector = null!, readingComplexitySelector = null!, reactionWindowSelector = null!;
    private AimModDropdown<bool> readingHiddenSelector = null!;
    private AimModDropdown<ReactionMode> reactionModeSelector = null!;

    private void buildSpecializedControls(FillFlowContainer<Drawable> body)
    {
        readingControls = flow(); readingControls.Depth = -9.3f;
        readingControls.Add(selector("READING CHALLENGE", new Dictionary<string, int> { ["Clear phrases"] = 0, ["Layered patterns"] = 1, ["Complex patterns"] = 2 },
            settings.ReadingComplexity, n => { settings = settings with { ReadingComplexity = n }; refreshHistory(); }, 185, d => readingComplexitySelector = d));
        readingControls.Add(selector("SEQUENCE LENGTH", new[] { 4, 6, 8, 12 }.Select(n => new KeyValuePair<string, int>($"{n} notes per phrase", n)),
            settings.ReadingGroupSize, n => { settings = settings with { ReadingGroupSize = n }; refreshHistory(); }, 200, d => readingLengthSelector = d));
        readingControls.Add(selector("NOTE VISIBILITY", new Dictionary<string, bool> { ["Normal approach circles"] = false, ["Hidden · fading notes"] = true },
            settings.ReadingHidden, hidden => { settings = settings with { ReadingHidden = hidden }; refreshHistory(); }, 235, d => readingHiddenSelector = d));

        reactionControls.Add(selector("REACTION DRILL", new Dictionary<string, ReactionMode> { ["Simple reaction"] = ReactionMode.Simple,
            ["Choose the key"] = ReactionMode.Choice, ["GO / STOP"] = ReactionMode.GoNoGo, ["Key choice + GO / STOP"] = ReactionMode.ChoiceGoNoGo },
            settings.ReactionMode, mode => { settings = settings with { ReactionMode = mode }; refreshHistory(); }, 230, d => reactionModeSelector = d));
        reactionControls.Add(selector("RESPONSE WINDOW", new[] { 600, 1000, 1200, 1500, 2000 }.Select(n => new KeyValuePair<string, int>($"{n} ms", n)),
            settings.ReactionWindowMs, n => { settings = settings with { ReactionWindowMs = n }; refreshHistory(); }, 175, d => reactionWindowSelector = d));
    }

    private void buildReactionStage()
    {
        var body = column(); body.Spacing = new(16); body.Padding = new MarginPadding(20);
        body.Add(reactionStatus = text("", 20, AimModPalette.Text));
        body.Add(reactionInstructions = paragraph(""));
        body.Add(new TrainerField(this) { RelativeSizeAxes = Axes.X, Height = 300 });
        body.Add(new AimModButton("Stop practice", () => Suspend()));
        AddInternal(reactionStage = new Container { RelativeSizeAxes = Axes.Both, Depth = -30,
            Padding = new MarginPadding { Top = 76 }, Alpha = 0,
            Children = [new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas }, body] });
    }

    private string reactionHelp() => settings.ReactionMode switch
    {
        ReactionMode.Choice => $"Press the key shown in the cue: {settings.Keys}. A wrong key counts as an error.",
        ReactionMode.GoNoGo => $"Tap either {settings.Keys} on GO. Do not tap on STOP. Wait until the next cue.",
        ReactionMode.ChoiceGoNoGo => $"Press the displayed key on GO. Do not tap on STOP. Your keys: {settings.Keys}.",
        _ => $"Wait for GO, then press either {settings.Keys}. Let the cue appear before moving your finger.",
    };

    private void updateReactionGuide(double time)
    {
        if (reaction is null) return;
        // Feedback follows a resolved cue. Never predict the next cue or shorten its delay.
        var last = reaction.Trials.LastOrDefault();
        if (time > reaction.FeedbackUntil) { reactionInstructions.Text = reactionHelp(); return; }
        reactionInstructions.Text = last?.Outcome switch
        {
            ReactionOutcome.Early => "Let the cue appear before pressing. Keep your fingers relaxed during the wait.",
            ReactionOutcome.WrongKey => "Take a moment to read the key before pressing. Keep each finger ready over its own key.",
            ReactionOutcome.FalseAlarm => "On STOP, keep both keys up. Wait for a GO cue before pressing.",
            ReactionOutcome.Missed => "Stay ready through the wait. Read the cue as it appears; a longer response window can help.",
            ReactionOutcome.Hit => "Release the key and reset. Wait for the next cue instead of guessing its timing.",
            _ => reactionHelp()
        };
    }

    private void addSpecializedResults(TrainerResult result)
    {
        if (result.Reaction is {} r)
        {
            results.Add(text("Response consistency", 15, AimModPalette.Text));
            results.Add(paragraph($"First half: {ms(r.FirstHalfMs)} median · second half: {ms(r.LastHalfMs)} median. Only correct responses are included."));
            var responses = r.Trials.Where(t => t.Outcome == ReactionOutcome.Hit).Select(t => t.ResponseMs!.Value).ToArray();
            if (responses.Length >= 2) results.Add(new TrainerProgressChart(responses));
            results.Add(paragraph("The 90th percentile shows your slower responses. Keep mistakes low before shortening the response window. Visual reaction uses screen timing; audio offset is not applied."));
        }
        if (result.ReadingWindows is { Length: > 0 } windows)
        {
            results.Add(text("Where the sequence broke down", 15, AimModPalette.Text));
            foreach (var window in windows)
            {
                var row = flow();
                row.Add(text($"{window.StartSeconds:0}–{Math.Min(result.PlayedSeconds ?? result.Settings.Seconds, window.StartSeconds + 10):0}s", 13, AimModPalette.Muted));
                row.Add(text($"{window.HitPercent:0}% tap hits · {window.Misses} misses / {window.Circles} taps", 13, AimModPalette.Text));
                results.Add(row);
            }
            results.Add(paragraph("These are circle and slider-head results across the exercise. Misses can come from reading, aim or tapping; compare with an easier setup to narrow it down."));
        }
    }
}
