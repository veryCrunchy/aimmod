using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using osu.Framework.Screens;
using osu.Framework.Threading;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Screens;
using osuTK.Input;

namespace AimMod.Desktop.Practice;

/// <summary>What the pause between attempts says, and how long it lasts.</summary>
public sealed record AdaptiveBreather(string Title, string Detail, bool Finished = false, double PauseMs = AdaptiveBreather.Long)
{
    public const double Long = 3_000, Short = 1_500;

    /// <summary>Names the drill in the player's HUD.</summary>
    public static string Label(AdaptiveDrill drill, int sections) => drill.Kind switch
    {
        AdaptiveDrillKind.FullRun => "Full run",
        AdaptiveDrillKind.Connect => $"Sections {drill.FirstSection + 1}–{drill.LastSection + 1}",
        AdaptiveDrillKind.Spot => $"Spot in section {drill.Focus + 1} of {sections}",
        _ => $"Section {drill.Focus + 1} of {sections}",
    };

    private static string title(AdaptiveDrill drill) => drill.Kind switch
    {
        AdaptiveDrillKind.FullRun => "Full run from the start",
        AdaptiveDrillKind.Connect => $"Connect sections {drill.FirstSection + 1} and {drill.LastSection + 1}",
        AdaptiveDrillKind.Spot => $"Spot in section {drill.Focus + 1} at {AdaptivePracticeCoach.Percent(drill.Speed)}",
        _ => $"Section {drill.Focus + 1} at {AdaptivePracticeCoach.Percent(drill.Speed)}",
    };

    public static AdaptiveBreather Opening(AdaptivePracticeProgress progress, AdaptiveDrill drill) => new(title(drill), progress.Attempts == 0
        ? "Short drills on the parts you need, each at one steady speed. Fail and it repeats a little slower; clear it cleanly and it speeds back up."
        : "Picking up where you left off.");

    /// <param name="missed">How the attempt went, when it survived but had too many misses to count as a clear.</param>
    public static AdaptiveBreather AfterFail(AdaptivePracticeProgress progress, int section, AdaptiveDrill next, AdaptiveResult? missed = null)
    {
        var state = progress.Sections[section];
        var reason = missed is null ? "" : AdaptivePracticeCoach.Describe(missed, progress.Leniency) + " ";
        string detail = next.Kind == AdaptiveDrillKind.Spot && next.Focus == section && state.ConsecutiveFails >= 2 && state.SpotClears == 0
            ? "Failed at the same place again. Drilling just that part."
            : state.ConsecutiveFails switch
            {
                1 when missed is not null => $"{reason}Too many to count as a clear. Same part, a little slower.",
                1 => "Take a breath. Same part, a little slower.",
                2 => "Two in a row. Slowing it down further so you can find the pattern.",
                _ when state.Speed <= AdaptivePracticeCoach.MinimumSpeed => "This is the slowest speed. Focus on reading the pattern, not on accuracy.",
                _ => $"{state.ConsecutiveFails} in a row. Slowing down more; clear it once and it speeds back up.",
            };
        return new(title(next), missed is not null && !detail.StartsWith(reason, StringComparison.Ordinal) ? reason + detail : detail);
    }

    public static AdaptiveBreather AfterSkip(AdaptiveDrill skipped, AdaptiveDrill next) => new(title(next),
        (skipped.Kind == AdaptiveDrillKind.Connect
            ? $"Skipped connecting sections {skipped.FirstSection + 1} and {skipped.LastSection + 1}."
            : $"Section {skipped.Focus + 1} marked as done.")
        + (next.Kind == AdaptiveDrillKind.FullRun ? " Every section is done. Now play the whole map." : ""), PauseMs: Short);

    public static AdaptiveBreather AfterClear(AdaptiveDrill previous, AdaptivePracticeProgress progress, AdaptiveDrill next, bool justCompleted,
        AdaptiveResult? result = null)
    {
        var breather = afterClear(previous, progress, next, justCompleted);
        // Section and spot drills say why they counted; connect and full runs are judged on getting through.
        return result is null || breather.Finished || previous.Kind is AdaptiveDrillKind.Connect or AdaptiveDrillKind.FullRun
            ? breather : breather with { Detail = AdaptivePracticeCoach.Describe(result, progress.Leniency) + " " + breather.Detail };
    }

    private static AdaptiveBreather afterClear(AdaptiveDrill previous, AdaptivePracticeProgress progress, AdaptiveDrill next, bool justCompleted)
    {
        if (justCompleted)
            return new("Full speed clear", $"You played the whole map at full speed without failing. {progress.Attempts} attempts, {progress.Fails} fails along the way.", true);
        if (next.Kind == AdaptiveDrillKind.FullRun)
            return new(title(next), previous.Kind == AdaptiveDrillKind.FullRun ? "Again from the start." : "Every section is solid at full speed. Now play the whole map.");
        if (next.Kind == AdaptiveDrillKind.Connect)
            return new(title(next), $"Section {next.LastSection + 1} is solid at full speed. Now play it joined to the section before.", PauseMs: Short);
        var state = progress.Sections[next.Focus];
        if (next.Focus != previous.Focus || previous.Kind == AdaptiveDrillKind.Connect)
            return new(title(next), previous.Kind is AdaptiveDrillKind.Connect
                ? $"Sections {previous.FirstSection + 1} and {previous.LastSection + 1} hold together. On to section {next.Focus + 1}."
                : $"Section {previous.Focus + 1} is done. On to section {next.Focus + 1}.", PauseMs: Short);
        if (next.Kind == AdaptiveDrillKind.Spot)
            return previous.Kind == AdaptiveDrillKind.Spot
                ? new(title(next), state.SpotClears > 0 ? "Clean. Once more to lock it in." : "Not clean yet. Same spot again.", PauseMs: state.SpotClears > 0 ? Short : Long)
                : new(title(next), "Cleared, but the misses were in one place. Drilling just that part.");
        if (previous.Kind == AdaptiveDrillKind.Spot)
            return new(title(next), "Spot learned. Now the whole section again.", PauseMs: Short);
        if (next.Speed > previous.Speed)
            return new(title(next), $"Speeding up to {AdaptivePracticeCoach.Percent(next.Speed)}.", PauseMs: Short);
        if (next.Speed < 1)
            return new(title(next), "Not clean yet. One more steady run speeds it up.", PauseMs: Short);
        return state.CleanStreak > 0
            ? new(title(next), $"Clean {state.CleanStreak} of {AdaptivePracticeCoach.RequiredStreak(state)} at full speed.", PauseMs: Short)
            : new(title(next), $"Cleared with misses. {AdaptivePracticeCoach.RequiredStreak(state)} clean runs at full speed move you on.");
    }
}

/// <summary>
/// Hosts one adaptive practice session: plays a drill, takes a short breath after it, and starts the drill
/// the coach picks next.
/// </summary>
public partial class AdaptivePracticeScreen : OsuScreen, IPracticeGameplaySession
{
    private readonly AdaptiveAttemptPlan plan;
    private readonly string mapTitle;
    private readonly Action<AdaptivePracticeProgress> save;
    private readonly Action<AdaptivePracticeProgress> finished;
    private readonly Action<Action> post;
    private readonly OsuSpriteText title, countdown, mapLabel;
    private readonly OsuTextFlowContainer detail;
    private readonly AdaptiveSectionStrip strip;
    private readonly FillFlowContainer card;
    private AdaptivePracticePlayer? player;
    private ScheduledDelegate? nextAttempt;
    private double? attemptAt;
    private bool done, finishedReported;
    /// <summary>Runs as each attempt's gameplay becomes ready.</summary>
    public Action? OnAttemptReady { get; init; }

    public override bool HideOverlaysOnEnter => true;

    /// <param name="post">Runs work on the update thread from a scheduler that is not suspended with this screen.
    /// A screen under the current attempt is not updated, so its own scheduler would hold attempt events until the attempt ends.</param>
    public AdaptivePracticeScreen(AdaptiveAttemptPlan plan, string mapTitle, Action<AdaptivePracticeProgress> save, Action<AdaptivePracticeProgress> finished,
        Action<Action> post)
    {
        this.post = post;
        this.plan = plan;
        this.mapTitle = mapTitle;
        this.save = save;
        this.finished = finished;
        card = new FillFlowContainer
        {
            Anchor = Anchor.Centre, Origin = Anchor.Centre, Width = 560, AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical, Spacing = new(0, 10),
            Children =
            [
                mapLabel = new OsuSpriteText { Font = new(size: 13, weight: "SemiBold"), Colour = AimModPalette.Accent },
                title = new OsuSpriteText { Font = new(size: 30, weight: "SemiBold"), Colour = AimModPalette.Text },
                detail = new OsuTextFlowContainer(t => { t.Font = new(size: 15); t.Colour = AimModPalette.Muted; }) { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y },
                strip = new AdaptiveSectionStrip(plan.Sections.Count) { RelativeSizeAxes = Axes.X, Height = 10, Margin = new MarginPadding { Top = 8 } },
                countdown = new OsuSpriteText { Font = new(size: 13), Colour = AimModPalette.Muted },
            ],
        };
        InternalChildren = [new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas.Opacity(.94f) }, card];
    }

    public override void OnEntering(ScreenTransitionEvent e)
    {
        base.OnEntering(e);
        breathe(AdaptiveBreather.Opening(plan.Progress, plan.Drill));
    }

    public override void OnResuming(ScreenTransitionEvent e)
    {
        base.OnResuming(e);
        player = null;
    }

    private void breathe(AdaptiveBreather message)
    {
        if (Beatmap.Value.TrackLoaded) Beatmap.Value.Track.Stop();
        mapLabel.Text = mapTitle;
        title.Text = message.Title;
        detail.Text = message.Detail;
        strip.Set(plan.Progress, plan.Drill.Focus);
        // Appear once the stopped attempt has faded out (osu!'s player takes 250 ms), not on top of it.
        card.FadeOut().Then().Delay(250).FadeIn(300, Easing.OutQuint);
        nextAttempt?.Cancel();
        if (message.Finished) { attemptAt = null; countdown.Text = "Enter or Esc to return to coaching."; done = true; return; }
        attemptAt = Time.Current + message.PauseMs;
        nextAttempt = Scheduler.AddDelayed(startAttempt, message.PauseMs);
    }

    protected override void Update()
    {
        base.Update();
        if (attemptAt is { } at)
            countdown.Text = $"Starting in {Math.Max(1, Math.Ceiling((at - Time.Current) / 1000)):0}  ·  Space to start now"
                + (AdaptivePracticeCoach.CanSkip(plan.Drill) ? "  ·  S to skip" : "") + "  ·  Esc to finish";
    }

    private void startAttempt()
    {
        nextAttempt?.Cancel();
        attemptAt = null;
        if (done || player is not null || !this.IsCurrentScreen()) return;
        this.Push(player = new AdaptivePracticePlayer(plan, e => post(() => handle(e))) { OnReady = OnAttemptReady });
    }

    private void handle(AdaptiveAttemptEvent outcome)
    {
        if (done) return;
        var now = DateTimeOffset.UtcNow;
        switch (outcome)
        {
            case AdaptiveAttemptEvent.Failed failed:
            {
                update(AdaptivePracticeCoach.Fail(plan.Progress, plan.Sections, failed.Section, failed.AtMs, now));
                var next = AdaptivePracticeCoach.Next(plan.Progress, plan.Sections);
                returnFromAttempt(() => { plan.Drill = next; breathe(AdaptiveBreather.AfterFail(plan.Progress, failed.Section, next)); });
                return;
            }
            case AdaptiveAttemptEvent.Completed completed:
            {
                var previous = plan.Drill;
                var before = plan.Progress;
                update(AdaptivePracticeCoach.Complete(plan.Progress, plan.Sections, previous, completed.Result, now));
                bool struggled = plan.Progress.Fails > before.Fails, justCompleted = !before.Completed && plan.Progress.Completed;
                var next = AdaptivePracticeCoach.Next(plan.Progress, plan.Sections);
                returnFromAttempt(() =>
                {
                    plan.Drill = next;
                    breathe(struggled ? AdaptiveBreather.AfterFail(plan.Progress, previous.Focus, next, missed: completed.Result)
                        : AdaptiveBreather.AfterClear(previous, plan.Progress, next, justCompleted, completed.Result));
                });
                return;
            }
            default:
                finish();
                return;
        }
    }

    private void update(AdaptivePracticeProgress progress)
    {
        plan.Progress = progress;
        save(progress);
    }

    private void returnFromAttempt(Action then)
    {
        // Exiting the attempt resumes this screen synchronously.
        if (!this.IsCurrentScreen()) this.MakeCurrent();
        then();
    }

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (!this.IsCurrentScreen()) return base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape:
                finish();
                return true;
            case Key.Enter or Key.KeypadEnter when done:
                finish();
                return true;
            case Key.S when attemptAt is not null && AdaptivePracticeCoach.CanSkip(plan.Drill):
                skip();
                return true;
            case Key.Space or Key.Enter or Key.KeypadEnter when attemptAt is not null:
                startAttempt();
                return true;
        }
        return base.OnKeyDown(e);
    }

    protected override bool OnClick(ClickEvent e)
    {
        if (attemptAt is null || !this.IsCurrentScreen()) return base.OnClick(e);
        startAttempt();
        return true;
    }

    public void StopSession() => finish();

    private void skip()
    {
        var skipped = plan.Drill;
        update(AdaptivePracticeCoach.Skip(plan.Progress, skipped, DateTimeOffset.UtcNow));
        plan.Drill = AdaptivePracticeCoach.Next(plan.Progress, plan.Sections);
        breathe(AdaptiveBreather.AfterSkip(skipped, plan.Drill));
    }

    private void finish()
    {
        if (finishedReported) return;
        finishedReported = done = true;
        nextAttempt?.Cancel();
        attemptAt = null;
        player?.Abandon();
        finished(plan.Progress);
    }
}
