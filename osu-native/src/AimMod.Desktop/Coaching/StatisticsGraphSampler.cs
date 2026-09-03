namespace AimMod.Desktop.Coaching;

internal static class StatisticsGraphSampler
{
    private const int minimum_time_samples = 32;

    /// <summary>
    /// Converts an irregular cumulative history into evenly spaced time samples.
    /// Empty periods keep the previous total, so horizontal distance represents
    /// elapsed time rather than the number of plays.
    /// </summary>
    public static CoachingChartPoint[] SampleCumulative(
        IReadOnlyList<CoachingChartPoint> points,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (limit < 2)
            throw new ArgumentOutOfRangeException(nameof(limit));
        if (points.Count <= 1)
            return points.ToArray();

        CoachingChartPoint[] ordered = points.OrderBy(point => point.PlayedAt).ToArray();
        DateTimeOffset start = ordered[0].PlayedAt;
        DateTimeOffset end = ordered[^1].PlayedAt;
        long durationTicks = end.UtcTicks - start.UtcTicks;
        if (durationTicks <= 0)
            return new[] { ordered[^1] };

        int sampleCount = Math.Min(limit, Math.Max(ordered.Length, minimum_time_samples));
        var result = new CoachingChartPoint[sampleCount];
        int sourceIndex = 0;
        for (int index = 0; index < sampleCount; index++)
        {
            double progress = index / (sampleCount - 1d);
            long targetTicks = start.UtcTicks + (long)Math.Round(durationTicks * progress);
            var target = new DateTimeOffset(targetTicks, TimeSpan.Zero);
            while (sourceIndex + 1 < ordered.Length
                   && ordered[sourceIndex + 1].PlayedAt.UtcTicks <= targetTicks)
            {
                sourceIndex++;
            }

            CoachingChartPoint current = ordered[sourceIndex];
            result[index] = current with { PlayedAt = target };
        }

        return result;
    }
}
