using AimMod.Desktop.PpTargets;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;

namespace AimMod.Desktop;

public partial class NativePpTargetsWorkspace
{
    internal static IReadOnlyList<string> TargetDetails(PpTargetCandidate target, OfficialBeatmapSet set, PpPatternProfile? profile)
    {
        var difficulty = set.Difficulties.FirstOrDefault(d => d.BeatmapId == target.BeatmapId);
        var lines = new List<string>
        {
            target.Title,
            $"{target.Artist} / {target.Creator}",
            $"[{target.Difficulty}]",
            $"{target.StarRating:0.00} stars / {target.Bpm:0} BPM / {TimeSpan.FromSeconds(target.TotalLengthSeconds):m\\:ss}",
            $"{set.Status} / {(target.SuggestedMods.Count == 0 ? "NM" : string.Join(" + ", target.SuggestedMods))}",
            $"AR {difficulty?.ApproachRate:0.#} / OD {difficulty?.OverallDifficulty:0.#} / CS {difficulty?.CircleSize:0.#} / HP {difficulty?.DrainRate:0.#}",
            target.MaximumCombo is { } combo ? $"Maximum combo: {combo:N0}x" : "Maximum combo unavailable",
            "PP prediction",
            target.ReadinessLabel,
        };
        if (target.Estimate is { } pp)
        {
            string setup = ScoreMods.Configuration(target.SuggestedMods, pp.ModsJson ?? "", PpTargetMods.NormaliseForSkill);
            var sessionPatterns = profile?.SessionForm is { } form && form.ExpiresAt > DateTimeOffset.UtcNow
                ? form.Patterns.Where(p => p.Setup == setup && p.LegacyScore == pp.LegacyScore).ToArray() : [];
            if (sessionPatterns.Length > 0)
            {
                lines.Add("Current session");
                foreach (var pattern in sessionPatterns.Where(p => p.Pattern != "Overall" || sessionPatterns.Length == 1))
                    lines.Add($"{pattern.Pattern}: {pattern.Observation}. Compared {pattern.Plays} recent plays across {pattern.Maps} maps with "
                        + (pattern.UsesSimilarMaps ? "similar patterns in your earlier plays." : "your earlier results on those maps."));
                lines.Add("Low-confidence session estimate. Relevant patterns make a small adjustment to projected accuracy and misses. Improving results may reflect warming up; the cause is uncertain. Adjustments expire after an hour without a comparable play.");
            }
            else lines.Add(profile?.SessionForm?.StatusFor(setup, pp.LegacyScore, DateTimeOffset.UtcNow)
                ?? "Session form: recent replay results are not available yet. Your longer-term skill profile is used.");
            if (target.Learning is { } learning)
            {
                lines.Add($"{(learning.PreviousTries == 0 ? "First try this session" : "Next try")}: {learning.FirstTryPp:0.0} expected PP; typical range {learning.FirstTryRange.Minimum:0}-{learning.FirstTryRange.Maximum:0}. Failed attempts count as zero.");
                lines.Add($"Best realistic target: {learning.TargetPp:0} PP in about {learning.LikelyTries} {(learning.PreviousTries > 0 ? "more " : "")}tries. Estimated chance by then: {learning.ReachProbability:P0}.");
                lines.Add($"Based on {learning.Sessions} recorded practice sessions across {learning.Maps} similar-pattern maps with matching mods. Low confidence; retry habits and unrecorded attempts can change this estimate.");
                lines.Add($"Target is the median best score within {learning.SupportedTries} observed tries, not a guaranteed score or lifetime limit. PP is scaled from your comparable score progression; the count is not extrapolated beyond recorded sessions.");
            }
            else lines.Add("Learning target unavailable: more recorded retry sessions with comparable patterns and PP results are needed.");
            lines.Add(target.ExpectedEarnedPp is { } earned
                ? $"Expected per attempt: {earned:0.0} PP. Projected score weighted by estimated pass chance; failed attempts earn no PP."
                : "A per-attempt PP estimate needs more comparable completed and failed plays.");
            lines.Add($"PP if the projected score is completed: {pp.ExpectedPp:0.0} ({pp.ExpectedPpRange.Minimum:0.0}-{pp.ExpectedPpRange.Maximum:0.0}). This is conditional, not a pass prediction.");
            lines.Add($"100% FC ceiling: {pp.RealisticMaximumPp:0.0} PP");
            lines.Add(pp.PatternPrediction?.ExpectedAccuracy is { } accuracy
                ? $"Projected head accuracy: {accuracy:P1}" : target.PassEstimate?.ConditionalAccuracy is { } observed
                    ? $"Accuracy from comparable completed plays: {observed:P1}" : "General accuracy fallback; comparable score evidence is missing");
        }
        else lines.Add("PP calculation pending");
        lines.Add("Skill evidence (30 days)");
        if (profile is null) lines.Add("Skill evidence loading");
        else
        {
            var support = PpTargetPatternModel.ScoreFit(profile, target.StarRating, target.SuggestedMods);
            lines.Add($"{support.Maps} comparable score-supported maps");
            lines.Add($"{profile.Evidence.Select(e => e.MapKey).Distinct().Count()} exact-replay maps in profile");
        }
        lines.Add("Pattern demand and fit");
        if (target.Estimate?.PatternPrediction is { } patterns)
        {
            foreach (var pattern in patterns.PatternFits)
                lines.Add(pattern.Fit is { } fit
                    ? $"{pattern.Pattern}: {fit:P0} fit / {pattern.DistinctMaps} comparable maps"
                    : $"{pattern.Pattern}: unmeasured / {pattern.DistinctMaps} comparable maps");
            lines.AddRange(patterns.Strengths);
            lines.AddRange(patterns.Risks);
            lines.AddRange(patterns.CoverageNotes ?? []);
        }
        else lines.Add("Pattern fit unmeasured");
        lines.Add("Pass and account gain");
        lines.Add(target.PassEstimate is { } pass
            ? $"Estimated pass: {pass.Probability:P0} ({pass.Lower:P0}-{pass.Upper:P0}); {pass.Attempts} attempts {(pass.SameMap ? "on this difficulty" : $"across {pass.Maps} maps")}. {pass.Confidence} confidence; {(pass.DurationAdjusted ? "adjusted from shorter maps; stamina is unverified" : pass.BroaderComparison ? "broader comparisons" : "matching mod setup")}."
            : "Pass chance unknown");
        lines.Add(target.EstimatedAccountGainPp is { } gain ? $"Expected account gain per attempt: +{gain:0.0} PP" : "Account gain unverified");
        if (target.AccountGainPerMinute is { } perMinute) lines.Add($"Account gain per minute: +{perMinute:0.0} PP");
        return lines;
    }

    private partial class PpTargetDetails : CompositeDrawable
    {
        private readonly AimModScrollContainer scroll;
        private readonly SpriteText actionStatus;
        private readonly ClickableContainer back;
        private readonly ClickableContainer saveButton;
        private readonly Container contentViewport;
        private bool busy;
        public bool IsBusy => busy;
        public float ScrollPosition => (float)scroll.Current;
        public void SetBackVisible(bool visible)
        {
            back.Alpha = visible ? 1 : 0;
            contentViewport.Padding = new MarginPadding { Top = visible ? 38 : 0, Bottom = 80 };
        }

        public PpTargetDetails(PpTargetCandidate target, OfficialBeatmapSet set, PpPatternProfile? profile,
            Func<OfficialBeatmapSet, Task<OnlineBeatmapImportResult>> save,
            Func<int, CancellationToken, Task>? open, Action close, float scrollPosition, bool installed = false)
        {
            RelativeSizeAxes = Axes.Both;
            var body = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical,
                Spacing = new(0, 10), Padding = new MarginPadding(12),
            };
            if (target.CoverUrl is not null)
                body.Add(new Container { RelativeSizeAxes = Axes.X, Height = 110, Masking = true,
                    Child = new AimModOnlineArtworkHost(target.CoverUrl) });
            foreach (string line in TargetDetails(target, set, profile))
            {
                bool heading = line is "PP prediction" or "Skill evidence (30 days)" or "Pattern demand and fit" or "Pass and account gain";
                body.Add(new TextFlowContainer(sprite =>
                {
                    sprite.Font = new FontUsage(size: line == target.Title ? 18 : heading ? 14 : 12, weight: heading ? "Bold" : "Regular");
                    sprite.Colour = heading ? AimModPalette.Cyan : AimModPalette.Text;
                }) { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = line, Margin = new MarginPadding { Top = heading ? 12 : 0 } });
            }
            InternalChildren =
            [
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                contentViewport = new Container
                {
                    RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Top = 38, Bottom = 80 },
                    Child = scroll = new AimModScrollContainer { RelativeSizeAxes = Axes.Both, Child = body },
                },
                back = button("Back to targets", FontAwesome.Solid.ArrowLeft, close, 12, 4, 160),
                new Container
                {
                    Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, RelativeSizeAxes = Axes.X, Height = 80,
                    Children =
                    [
                        button("Open osu!", FontAwesome.Solid.Play,
                            open is null ? null : () => _ = perform(async () => { await open(target.BeatmapId, CancellationToken.None); return "Opened"; }), 12, 6, 132),
                        saveButton = button(installed ? "Installed" : set.DownloadDisabled ? "Unavailable" : "Install", installed ? FontAwesome.Solid.Check : FontAwesome.Solid.Download,
                            installed || set.DownloadDisabled ? null : () => _ = perform(async () =>
                            {
                                var result = await save(set);
                                return result.Status == OnlineBeatmapImportStatus.Success ? "Added to osu!"
                                    : result.Status == OnlineBeatmapImportStatus.OsuInstallFailed ? "Saved locally; osu! import failed. Retry Save." : "Save failed. Try again.";
                            }), 156, 6, 132),
                        actionStatus = text(string.Empty, 11, AimModPalette.Muted).With(d => d.Position = new(12, 48)),
                    ],
                },
            ];
            Scheduler.AddDelayed(() => scroll.ScrollTo(scrollPosition, false), 50);
        }

        public void SetInstalled()
        {
            saveButton.Action = null;
            saveButton.Children.OfType<SpriteText>().Single().Text = "Installed";
            saveButton.Children.OfType<SpriteIcon>().Single().Icon = FontAwesome.Solid.Check;
        }

        private async Task perform(Func<Task<string>> action)
        {
            if (busy) return;
            busy = true;
            actionStatus.Text = "Working...";
            string message;
            try { message = await action().ConfigureAwait(false); }
            catch (Exception) { message = "Action failed. Try again."; }
            if (!IsDisposed) Schedule(() => { busy = false; actionStatus.Text = message; });
        }

        private static ClickableContainer button(string label, IconUsage icon, Action? action, float x, float y, float width) => new()
        {
            Position = new(x, y), Size = new(width, 32), Action = action,
            Children =
            [
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                new SpriteIcon { Position = new(10, 9), Size = new(14), Icon = icon, Colour = action is null ? AimModPalette.Muted : AimModPalette.Cyan },
                text(label, 11, action is null ? AimModPalette.Muted : AimModPalette.Text).With(d => d.Position = new(32, 9)),
            ],
        };
    }
}
