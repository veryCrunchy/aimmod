using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Coaching;

public partial class NativeCoachingWorkspace
{
    private sealed record SavedSection(PracticeSetProgress Set, PracticeDifficultyIdentity Difficulty);

    private void renderSectionNavigation(FillFlowContainer<Drawable> body, PracticeSetProgress current, string groupId, LocalReplay? run)
    {
        string key = CoachingMapKey(current.Map);
        var saved = practiceSets.Where(s => CoachingMapKey(s.Map) == key && !s.Map.PayloadRemoved && s.Map.RetiredAt is null)
            .OrderByDescending(s => s.Map.CreatedAt)
            .SelectMany(s => (s.Map.Tracking?.Difficulties ?? []).Where(d => d.BreakdownGroupId.Length > 0).Select(d => new SavedSection(s, d)))
            .DistinctBy(s => s.Difficulty.BreakdownGroupId).OrderBy(s => s.Difficulty.SourceStartMs).ToArray();
        var now = DateTimeOffset.UtcNow;
        var completion = saved.ToDictionary(s => s.Difficulty.BreakdownGroupId, s =>
            CoachingPracticeSessionPlanner.Build(s.Set, now, breakdownGroupId: s.Difficulty.BreakdownGroupId)
                .Stages.Where(stage => stage.Stage is CoachingPracticeStage.Isolate or CoachingPracticeStage.Combine).All(stage => stage.IsComplete));
        bool repeated(SavedSection s) => completion[s.Difficulty.BreakdownGroupId];
        void selectSection(SavedSection section)
        {
            viewedPracticeSets[key] = section.Set.Map.Id;
            viewedGroups[section.Set.Map.Id] = section.Difficulty.BreakdownGroupId;
            var nextStage = CoachingPracticeSessionPlanner.Build(section.Set, now, sessionFor(section.Set), section.Difficulty.BreakdownGroupId).NextStage;
            viewedStages[section.Set.Map.Id] = nextStage is null or CoachingPracticeStage.Baseline ? CoachingPracticeStage.Isolate : nextStage.Value;
            selectingTransferFor = null;
            practiceRunStatus = "";
            renderCoachingMap();
            coachingPages[4].ScrollTo(0, false);
        }
        body.Add(flow($"Sections in your saved exercises · {saved.Count(repeated)} / {saved.Length} have completed their practice runs", 13, coachingAccent));
        var choices = actionFlow();
        foreach (var (section, index) in saved.Select((s, i) => (s, i)))
        {
            string progress = repeated(section) ? "Runs complete" : "To practise";
            var button = new CoachingButton($"{index + 1}. {TimeSpan.FromMilliseconds(section.Difficulty.SourceStartMs):m\\:ss} - {TimeSpan.FromMilliseconds(section.Difficulty.SourceEndMs):m\\:ss} · {progress}",
                () => selectSection(section), compact: true);
            button.SetSelected(section.Difficulty.BreakdownGroupId == groupId); choices.Add(button);
        }
        body.Add(choices);
        var next = saved.FirstOrDefault(s => s.Difficulty.BreakdownGroupId != groupId && !repeated(s));
        var currentSection = saved.FirstOrDefault(s => s.Difficulty.BreakdownGroupId == groupId);
        bool ready = currentSection is not null && repeated(currentSection);
        if (next is not null)
            body.Add(new CoachingButton("Next section", () => selectSection(next), ready, true));
        else if (run is not null && practiceWorkspace is not null)
            body.Add(new CoachingButton("Choose next section", () =>
            {
                viewedPracticeSets.Remove(key);
                selectingTransferFor = null;
                practiceWorkspace.OpenNextBreakdown(new PracticeMapCandidate(run, [run.ScoreId], 1, run.MissCount, 0),
                    saved.Select(s => new PracticeSectionRange(s.Difficulty.SourceStartMs, s.Difficulty.SourceEndMs)).ToArray());
            }, ready, true));
        if (ready) body.Add(flow("You have completed the planned runs for this section. Move to another section, or repeat this one if it still needs work.", 13, AimModPalette.Muted));
    }
}
