using AimMod.Desktop.PpTargets;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;
using System.Runtime.Versioning;

namespace AimMod.Desktop.Tests;

public sealed partial class OffscreenVisualCaptureTests
{
    [TestCase(1100, 760, false)]
    [TestCase(800, 760, false)]
    [TestCase(1100, 760, true)]
    [Explicit("Renders synthetic PP learning forecasts on a private desktop.")]
    [SupportedOSPlatform("windows")]
    public async Task CaptureLearningForecast(int width, int height, bool details)
    {
        string output = Path.Combine(TestContext.CurrentContext.WorkDirectory, "visual-captures", $"pp-learning-{width}-{details}.png");
        var cache = await createPpTargetCaptureCache(output);
        var snapshot = cache.Load()!;
        var now = snapshot.CachedAt;
        var shape = new PpPatternFeatures { PointCount = 600, JumpFraction = .8, StreamFraction = .05, BurstFraction = .1, SharpTurnFraction = .2, NotesPerSecond = 5, JumpDistance = 200 };
        var attempts = new List<PpTargetPassSample>();
        var evidence = new List<PpPatternEvidence>();
        string setup = ScoreMods.Configuration([], "", PpTargetMods.NormaliseOne);
        Dictionary<string, PpPatternOutcome> outcomes(double accuracy) => new[] { "Overall", "Jumps", "Streams" }
            .ToDictionary(p => p, p => new PpPatternOutcome(100, p == "Streams" ? accuracy : .98, .005, new Dictionary<ReplayMissReason, int>()));
        for (int map = 1; map <= 4; map++) for (int session = 0; session < 2; session++) for (int i = 0; i < 5; i++)
        {
            Guid id = Guid.NewGuid(); var time = now.AddDays(-5 + session).AddHours(map).AddMinutes(i * 3);
            attempts.Add(new(map, time, 5.2, 190, 200, "", true, Accuracy: .972, LocalScoreId: id, Pp: 80 + i * 20, LocalAttempt: true));
            evidence.Add(new(id, $"synthetic-map-{map}", "", time, shape, 1, outcomes(.98), setup));
        }
        for (int i = 0; i < 6; i++) evidence.Add(new(Guid.NewGuid(), $"synthetic-map-{i % 3 + 1}", "",
            now.AddMinutes(-30 + i * 5), shape, 1, outcomes(.94), setup));
        var profile = snapshot.Profile with { Opportunities = new(now, snapshot.Profile.Opportunities!.BestPlays, attempts),
            PatternProfile = snapshot.Profile.PatternProfile! with { Evidence = evidence,
                SessionForm = PpTargetSessionFormModel.Build(evidence, now) with
                { Patterns = PpTargetSessionFormModel.Build(evidence, now).Patterns.Select(p => p with { UsesSimilarMaps = true }).ToArray() } } };
        Assert.That(profile.PatternProfile!.SessionForm!.Patterns, Is.Not.Empty);
        var estimates = snapshot.ExactEstimates.ToDictionary(p => p.Key, p => p.Value with { Features = shape });
        await cache.SaveAsync(snapshot with { Profile = profile, ExactEstimates = estimates });
        var target = snapshot.Catalog[0].Difficulties[0];
        Assert.That(PpTargetLearningModel.Predict(profile.Opportunities, profile.PatternProfile, estimates[target.BeatmapId], target.BeatmapId,
            target.StarRating, target.Bpm, target.TotalLengthSeconds, []), Is.Not.Null);
        await WindowsPrivateDesktopCapture.CaptureAsync((host, success, fail) => new CapturePpTargetsGame(host, cache, output, width, height, success, fail, details), TimeSpan.FromSeconds(60));
    }
}
