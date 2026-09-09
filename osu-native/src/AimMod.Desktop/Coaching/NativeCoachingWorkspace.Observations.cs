using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Coaching;

public partial class NativeCoachingWorkspace
{
    private void renderReplayObservations(FillFlowContainer<Drawable> host, LocalReplay run)
    {
        var observations = CoachingReplayObservations.Build(run, allReplays, analyses);
        foreach (var observation in observations)
        {
            var body = pageFlow(); body.Spacing = new(6);
            body.Add(flow(observation.Label, 17, AimModPalette.Text));
            body.Add(flow(observation.Detail, 13, AimModPalette.Muted));
            body.Add(flow(observation.Cue, 14, AimModPalette.Text));
            var actions = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Full, Spacing = new(8) };
            if (run.HasReplayFile && openReplayMoment is not null)
                actions.Add(new CoachingButton($"Watch {TimeSpan.FromMilliseconds(observation.TimeMs):m\\:ss}",
                    () => openReplayMoment(run, observation.TimeMs), compact: true));
            if (practiceWorkspace is not null)
                actions.Add(new CoachingButton("Break down this section", () => practiceWorkspace.OpenBreakdown(
                    new PracticeMapCandidate(run, observation.ScoreIds, Math.Max(1, observation.Plays), run.MissCount, 0),
                    observation.FirstObjectIndex), compact: true));
            body.Add(actions);
            host.Add(new CoachingCard(body, 12));
        }
    }
}
