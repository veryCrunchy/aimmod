using System.Reflection;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Osu.Runtime;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osuTK;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class NativePpTargetsForecastUiTests
{
    private const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly DateTimeOffset now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static (PpTargetCandidate Candidate, OfficialBeatmapSet Set) target()
    {
        var distribution = new PpOutcomeDistribution(1.5, 2.5, .975, .008, .95, .9, 20, 8, 20, false, false, .9, -.012);
        const int objects = 800;
        var scenarios = PpTargetOutcomeModel.ScenarioMisses(distribution, objects)
            .Select(m => new PpScenario(m, distribution.ScenarioAccuracy(m, objects), distribution.ScenarioCombo(m, objects, 1_100), 200 * Math.Pow(.93, m))).ToArray();
        var atoms = PpTargetOutcomeModel.Integrate(distribution, scenarios, objects, 3, 240, .97);
        var prediction = new PpPatternPrediction(.8, .97, .6, [], [], [new("Overall", .8, .97, .6, 9, .004, -9, 104, 18), new("Jumps", .7, .96, .5, 5)]);
        var estimate = new PpTargetEstimate(PpTargetOutcomeModel.Mean(atoms), 240, new(150, 210), 20, PpTargetConfidence.High, "Official osu! ruleset / synthetic",
            BeatmapId: 7, PatternPrediction: prediction, Outcome: new PpOutcomeEstimate(distribution, scenarios, atoms, 3, .97, 12, objects, 1_100));
        var pass = new PpTargetPassEstimate(.82, .7, .9, 24, 6, Confidence: PpTargetConfidence.High);
        var forecast = PpTargetForecastModel.Forecast(estimate, pass, null)!;
        var difficulty = new OfficialBeatmapDifficulty(7, "Insane", "osu", 5.2, 190, 200, 4, 9.3f, 8.8f, 6, 1_000, 400, 1_100);
        var set = new OfficialBeatmapSet(70, "Synthetic target", "", "Synthetic artist", "", "Synthetic mapper", "", "ranked",
            now, now, 1_000, 10, false, false, null, null, null, null, [difficulty]);
        var candidate = new PpTargetCandidate(70, 7, "Synthetic target", "Synthetic artist", "Synthetic mapper", "", "ranked", "Insane", 5.2, 190, 200, 1_100, null,
            .8, .8, 1, null, null, estimate, [], 1, 1, PpTargetConfidence.High, pass, 1.4, .4, .97, forecast);
        return (candidate, set);
    }

    private static NativePpTargetsWorkspace workspaceWith(PpTargetCandidate candidate, OfficialBeatmapSet set)
    {
        var workspace = new NativePpTargetsWorkspace(new InMemoryLocalLibrarySource([], []), () => null, () => null);
        typeof(NativePpTargetsWorkspace).GetField("setsById", flags)!.SetValue(workspace, new Dictionary<int, OfficialBeatmapSet> { [set.BeatmapSetId] = set });
        var ranked = new[] { new NativePpTargetsWorkspace.RankedTarget(candidate, NativePpTargetsWorkspace.RowKey(candidate, NativePpTargetsWorkspace.TargetSort.BestFit)) };
        typeof(NativePpTargetsWorkspace).GetMethod("renderCandidates", flags)!.Invoke(workspace, [ranked]);
        return workspace;
    }

    [Test]
    public void RowPopoverIsHostedByTheTooltipLayerAndStaysInsideTheWindow()
    {
        var (candidate, set) = target();
        using var workspace = workspaceWith(candidate, set);
        var row = (Drawable)((System.Collections.IDictionary)typeof(NativePpTargetsWorkspace).GetField("targetRows", flags)!.GetValue(workspace)!)[7]!;

        // The tooltip container draws above every workspace layer, so neither rows, the scroll mask nor the detail pane can cover it.
        Assert.That(row, Is.Not.InstanceOf<IHasTooltip>(), "A text tooltip would replace the visual popover.");
        var provider = (IHasCustomTooltip<PpTargetCandidate>)row;
        Assert.That(provider.TooltipContent, Is.SameAs(candidate));
        var popover = provider.GetCustomTooltip();
        Assert.That(popover, Is.InstanceOf<NativePpTargetsWorkspace.PpForecastTooltip>());
        Assert.That(((Drawable)popover).Width, Is.LessThanOrEqualTo(480).And.LessThanOrEqualTo(800 - 12));

        // Same content again, as the container refreshes each hover: no rebuild, so no flicker.
        popover.SetContent(candidate);
        var body = (Container)typeof(NativePpTargetsWorkspace.PpForecastTooltip).GetField("body", flags)!.GetValue(popover)!;
        Drawable card = body.Child;
        popover.SetContent(candidate);
        Assert.That(body.Child, Is.SameAs(card));
        Assert.That(card, Is.InstanceOf<NativePpTargetsWorkspace.PpForecastCard>());

        var size = new Vector2(NativePpTargetsWorkspace.PpForecastTooltip.TooltipWidth, 560);
        foreach (var window in new[] { new Vector2(800, 760), new Vector2(1100, 760), new Vector2(1600, 900) })
            foreach (var cursor in new[] { Vector2.Zero, new Vector2(window.X, 0), window, new Vector2(0, window.Y), window / 2 })
            {
                Vector2 placed = NativePpTargetsWorkspace.PpForecastTooltip.Clamp(cursor + new Vector2(12, 18), size, window);
                Assert.That(placed.X, Is.GreaterThanOrEqualTo(0), $"{window} {cursor}");
                Assert.That(placed.Y, Is.GreaterThanOrEqualTo(0), $"{window} {cursor}");
                Assert.That(placed.X + size.X, Is.LessThanOrEqualTo(window.X), $"{window} {cursor}");
                Assert.That(placed.Y + size.Y, Is.LessThanOrEqualTo(window.Y), $"{window} {cursor}");
            }
    }

    [Test]
    public void KeyboardSelectionShowsTheVisualBreakdownInTheDetailPane()
    {
        var (candidate, set) = target();
        using var workspace = workspaceWith(candidate, set);
        // Arrow-key navigation selects without opening, exactly like this call.
        typeof(NativePpTargetsWorkspace).GetMethod("selectTarget", flags)!.Invoke(workspace, [candidate, set, false, null]);
        object details = typeof(NativePpTargetsWorkspace).GetField("selectedDetails", flags)!.GetValue(workspace)!;
        Assert.That(details.GetType().GetField("forecastCard", flags)!.GetValue(details), Is.InstanceOf<NativePpTargetsWorkspace.PpForecastCard>());
        var viewport = (Drawable)typeof(NativePpTargetsWorkspace).GetField("detailViewport", flags)!.GetValue(workspace)!;
        var results = (Drawable)typeof(NativePpTargetsWorkspace).GetField("resultScroll", flags)!.GetValue(workspace)!;
        Assert.That(viewport.Depth, Is.LessThan(results.Depth), "The detail pane must draw ahead of the result list.");
    }

    [Test]
    public void RowFiguresSeparateChanceFromAccuracy()
    {
        var (candidate, _) = target();
        var breakdown = candidate.Forecast!.Breakdown!;
        string summary = NativePpTargetsWorkspace.ForecastText.PerformanceSummary(breakdown);
        Assert.That(summary, Does.Match(@"^≈\d+\.\d% acc  ·  (~\d+ miss(es)?|\d+–\d+ misses)  ·  \d+ ms early$"));
        Assert.That(summary, Does.Not.Contain("chance"));
        Assert.Multiple(() =>
        {
            Assert.That(NativePpTargetsWorkspace.ForecastText.Chance(.914), Is.EqualTo("91%"));
            Assert.That(NativePpTargetsWorkspace.ForecastText.Chance(.999), Is.EqualTo(">99%"));
            Assert.That(NativePpTargetsWorkspace.ForecastText.Chance(.001), Is.EqualTo("<1%"));
            Assert.That(NativePpTargetsWorkspace.ForecastText.Offset(-12.4), Is.EqualTo("12 ms early"));
            Assert.That(NativePpTargetsWorkspace.ForecastText.Offset(3.6), Is.EqualTo("4 ms late"));
            Assert.That(NativePpTargetsWorkspace.ForecastText.Offset(.2), Is.EqualTo("centred timing"));
            Assert.That(NativePpTargetsWorkspace.ForecastText.AccountGain(0), Is.EqualTo("No account gain expected"));
            Assert.That(NativePpTargetsWorkspace.ForecastText.AccountGain(1.44), Is.EqualTo("+1.4 account pp per try"));
            Assert.That(NativePpTargetsWorkspace.ForecastText.AccountGain(null), Is.EqualTo("Account gain unverified"));
            Assert.That(NativePpTargetsWorkspace.ForecastText.MissRange(breakdown.Misses with { Low = 1, High = 3 }), Is.EqualTo("1–3 misses"));
        });
    }
}
