using AimMod.Osu.Worker;
using AimMod.Osu.Runtime.Contracts;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using NUnit.Framework;
namespace AimMod.Osu.Worker.Tests;

[TestFixture]
public class ModePpTests {
    [TestCase(1, true)] [TestCase(1, false)]
    [TestCase(2, true)] [TestCase(2, false)]
    [TestCase(3, true)] [TestCase(3, false)]
    public async Task CalculatesExactModeScoresAndMisses(int mode, bool lazer) {
        string directory = Directory.CreateTempSubdirectory("aimmod-mode-test-").FullName;
        try {
            string path = Path.Combine(directory,"map.osu");
            string text = $"osu file format v14\n[General]\nMode:{mode}\n[Metadata]\nTitle:Synthetic\nArtist:Example\nCreator:Example\nVersion:Test\n[Difficulty]\nCircleSize:4\nOverallDifficulty:8\nHPDrainRate:5\nSliderMultiplier:1.4\nSliderTickRate:1\n[TimingPoints]\n0,500,4,2,1,50,1,0\n[HitObjects]\n";
            text += string.Join("\n",Enumerable.Range(0,100).Select(i => mode==3 && i%3==0
                ? $"{64+i%4*128},192,{1000+i*500},128,0,{1250+i*500}:0:0:0:0:"
                : $"{64+i%4*128},192,{1000+i*500},1,0,0:0:0:0:"));
            File.WriteAllText(path,text);
            var ruleset = ModePerformance.CreateRuleset(mode);
            Mod[] mods = !lazer && ruleset.CreateMod<ModClassic>() is {} classic ? [classic] : [];
            var working = new FlatWorkingBeatmap(path);
            var playable = working.GetPlayableBeatmap(ruleset.RulesetInfo,mods);
            using var processor = ruleset.CreateScoreProcessor();
            processor.Mods.Value = mods; processor.ApplyBeatmap(playable);
            var hits = processor.MaximumStatistics;
            if (mode==3 && !lazer) hits[HitResult.Perfect] = playable.HitObjects.Count;
            var stats = new PpScoreStatistics(hits.GetValueOrDefault(HitResult.Great),0,0,0,0,0,
                Perfect:hits.GetValueOrDefault(HitResult.Perfect), LargeTickHit:hits.GetValueOrDefault(HitResult.LargeTickHit), SmallTickHit:hits.GetValueOrDefault(HitResult.SmallTickHit));
            var request = new PpWhatIfRequest(directory,path,[],1,MaxCombo:processor.MaximumCombo,Statistics:stats,LegacyScore:!lazer,RulesetId:mode);
            var calculator = new OfficialPpWhatIfCalculator();
            var perfect = await calculator.CalculateAsync(PpInputValidator.Validate(request),CancellationToken.None);
            var missed = await calculator.CalculateAsync(PpInputValidator.Validate(request with { Accuracy=.98, MaxCombo=1,
                Statistics=stats with { Perfect=mode==3 ? stats.Perfect-1 : 0, Great=mode==3 ? 0 : stats.Great-1, Miss=1 } }),CancellationToken.None);
            Assert.That(perfect.PerformancePoints,Is.GreaterThan(0));
            Assert.That(missed.PerformancePoints,Is.LessThan(perfect.PerformancePoints));
            Assert.That(perfect.ObjectCount,Is.EqualTo(mode==3 && lazer ? 134 : 100));
        } finally { Directory.Delete(directory,true); }
    }
}
