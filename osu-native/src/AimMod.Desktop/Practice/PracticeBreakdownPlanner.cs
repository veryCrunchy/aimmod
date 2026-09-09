using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AimMod.Desktop.Practice;

/// <summary>Connected comparisons share a source section and preserve its rhythm, repeats and audio.</summary>
public static class PracticeBreakdownPlanner
{
    public static string Label(PracticeBreakdownVariant variant) => variant switch
    {
        PracticeBreakdownVariant.ReducedMovement => "Reduced movement",
        PracticeBreakdownVariant.AimFocus => "Aim focus (Relax)",
        PracticeBreakdownVariant.CombinedEasier => "Combined, easier",
        _ => "Original section",
    };

    public static IReadOnlyList<PracticeMapPlan> CreatePlans(PracticeSourceBeatmap source, PracticeSourceSection section, PracticeMapOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(section);
        if (section.HitObjects.Count == 0) throw new InvalidDataException("Choose a section with playable notes.");
        // Source content and section identity, never machine-specific paths, form the stable comparison key.
        string identity = source.Metadata.Title + "|" + source.Metadata.Artist + "|" + source.Metadata.Version + "|"
            + string.Join('\n', section.HitObjects.Select(o => string.Join(',', o.Fields)));
        string group = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var baseline = PracticeMapPlanner.CreateSectionPlan(source, section, options) with { BreakdownGroupId = group };
        // Circle size stays unchanged. Keep enough of the original spacing to read
        // each note, rather than packing ordinary patterns into overlapping circles.
        const double compactScale = .7;
        double sliderMultiplier = 1.4;
        string? sliderLine = source.Sections.GetValueOrDefault("Difficulty", []).FirstOrDefault(line => line.StartsWith("SliderMultiplier:", StringComparison.OrdinalIgnoreCase));
        if (sliderLine is not null && (!double.TryParse(sliderLine.Split(':', 2)[1], CultureInfo.InvariantCulture, out sliderMultiplier) || !double.IsFinite(sliderMultiplier) || sliderMultiplier <= 0))
            throw new InvalidDataException("The source map has an invalid slider speed.");
        var compact = baseline with
        {
            BreakdownVariant = PracticeBreakdownVariant.ReducedMovement,
            HitObjects = baseline.HitObjects.Select(item => compactObject(item, compactScale)).ToArray(),
            // Slider distance and velocity change together, retaining slider durations, ticks and repeats.
            DifficultyOverrides = new Dictionary<string, string> { ["SliderMultiplier"] = number(sliderMultiplier * compactScale) },
        };
        var aim = baseline with { BreakdownVariant = PracticeBreakdownVariant.AimFocus, RequiredMods = "RX" };
        var combined = PracticeMapPlanner.CreateSectionPlan(source, section, options with
        {
            PlaybackRate = Math.Max(.75, options.Normalised().PlaybackRate * .85),
        }) with { BreakdownVariant = PracticeBreakdownVariant.CombinedEasier, BreakdownGroupId = group };
        if (combined.AudioSlice.PlaybackRate >= baseline.AudioSlice.PlaybackRate)
            combined = combined with
            {
                HitObjects = combined.HitObjects.Select(item => compactObject(item, .75)).ToArray(),
                DifficultyOverrides = new Dictionary<string, string> { ["SliderMultiplier"] = number(sliderMultiplier * .75) },
            };
        return new[] { compact, aim, combined, baseline }.Select(plan => plan with
        {
            OutputVersion = Label(plan.BreakdownVariant),
            Attribution = plan.Attribution + " AimMod section breakdown: " + Label(plan.BreakdownVariant) + ".",
        }).ToArray();
    }

    private static PracticeHitObject compactObject(PracticeHitObject item, double scale)
    {
        if (item.IsSpinner) return item;
        int x = coordinate(item.X, 256, scale), y = coordinate(item.Y, 192, scale);
        var fields = item.Fields.ToArray(); fields[0] = number(x); fields[1] = number(y);
        if (item.IsSlider)
        {
            if (fields.Length < 8) throw new InvalidDataException("The source slider is incomplete.");
            string[] path = fields[5].Split('|');
            for (int i = 1; i < path.Length; i++)
            {
                string[] point = path[i].Split(':');
                // Modern paths can contain another segment type between their control points.
                if (point.Length == 1 && point[0] is "B" or "C" or "L" or "P") continue;
                if (point.Length != 2 || !double.TryParse(point[0], CultureInfo.InvariantCulture, out double px)
                    || !double.TryParse(point[1], CultureInfo.InvariantCulture, out double py)
                    || !double.IsFinite(px) || !double.IsFinite(py)) throw new InvalidDataException("The source slider path is invalid.");
                path[i] = $"{coordinate(px, 256, scale)}:{coordinate(py, 192, scale)}";
            }
            if (!double.TryParse(fields[7], CultureInfo.InvariantCulture, out double length) || !double.IsFinite(length) || length < 0)
                throw new InvalidDataException("The source slider length is invalid.");
            fields[5] = string.Join('|', path); fields[7] = number(length * scale);
        }
        return item with { X = x, Y = y, Fields = fields };
    }

    private static int coordinate(double value, double centre, double scale) => checked((int)Math.Round(centre + (value - centre) * scale));
    private static string number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
