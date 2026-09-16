namespace AimMod.Desktop.Trainers;

/// <summary>Selects performed attacks without moving them onto an unrelated practice grid.</summary>
public static class TrainerMusicalPhrases
{
    public static IReadOnlyList<TrainerNote> Select(TrainerSongArrangement music, TrainerSettings settings, double start, double end)
    {
        double beatMs = 60000.0 / music.Bpm;
        var limits = settings.SkillLimits ?? new();
        // Authored attacks are selected directly. The grid generator's extra subdivision
        // reserve would apply the density limit twice and quarter the available aim notes.
        double step = Math.Max(TrainerPatterns.Step(settings with { AdaptiveDifficulty = false, RandomizePatterns = false }), 1000 / limits.MaxNps / beatMs);
        var events = music.Events.Where(e => music.TimeAt(e.Beat) >= start && music.TimeAt(e.Beat) < Math.Min(end, music.TimeAt(music.EndBeat)))
            .OrderBy(e => e.Beat).ToArray();
        var chosen = new List<TrainerNote>();
        var beats = events.Select(e => e.Beat).ToArray();
        TrainerMusicEvent? near(double target)
        {
            int index = Array.BinarySearch(beats, target);
            if (index >= 0) return events[index];
            index = ~index;
            if (index < events.Length && Math.Abs(beats[index] - target) < .015) return events[index];
            return index > 0 && Math.Abs(beats[index - 1] - target) < .015 ? events[index - 1] : null;
        }
        TrainerNote note(TrainerMusicEvent e, int phrase) => new(music.TimeAt(e.Beat), phrase,
            TrainerPatterns.PatternAt(settings, Math.Max(0, (int)((music.TimeAt(e.Beat) - start) / (4 * beatMs)))));
        if (settings.Kind is TrainerKind.Bursts or TrainerKind.Alternating)
        {
            // Search for real evenly spaced runs. Never fill missing attacks with invented taps.
            double next = double.NegativeInfinity;
            int phrase = 0;
            foreach (var onset in events)
            {
                if (onset.Beat < next) continue;
                int maximum = settings.Kind == TrainerKind.Bursts ? limits.MaxBurst : limits.MaxChain;
                maximum = Math.Min(maximum, settings.Pattern switch {
                    TrainerPattern.BurstLadder => new[] {3,5,7,7,5,3}[phrase%6], TrainerPattern.FiveNotes => 5, TrainerPattern.SevenNotes => 7, TrainerPattern.NineNotes => 9,
                    TrainerPattern.PartialStreams => 8, TrainerPattern.LongStreams => 24,
                    TrainerPattern.Standard when settings.Kind == TrainerKind.Bursts => 3, _ => maximum,
                });
                maximum = Math.Min(maximum, music.SectionAt(onset.Beat)?.Quiet == true ? 3 : 24);
                TrainerMusicEvent[] best = [];
                foreach (double spacing in new[] { .25, .5, 1.0, 2.0 }.Where(s => s >= step - .001))
                {
                    var group = new List<TrainerMusicEvent> { onset };
                    for (int i = 1; i < maximum; i++)
                    {
                        double target = onset.Beat + i * spacing;
                        var hit = near(target);
                        if (hit is null || music.SectionAt(hit.Beat) != music.SectionAt(onset.Beat)) break;
                        group.Add(hit);
                    }
                    if (group.Count > best.Length) best = group.ToArray();
                }
                if (best.Length < 3) continue;
                chosen.AddRange(best.Select(e => note(e, phrase)));
                next = best[^1].Beat + Math.Max(1, step * 3);
                phrase++;
            }
        }
        else if (settings.Kind == TrainerKind.Steady)
        {
            // Keep a steady subdivision, but only when an instrument actually attacks there.
            double spacing = new[] { .25, .5, 1.0, 2.0, 4.0 }.First(s => s >= step - .001);
            foreach (var e in events)
            {
                double phase = settings.Pattern == TrainerPattern.Offbeat ? spacing / 2 : 0;
                if (Math.Abs((e.Beat - phase) / spacing - Math.Round((e.Beat - phase) / spacing)) > .015) continue;
                if (settings.Pattern == TrainerPattern.Gaps && e.Beat % 4 >= 2.5) continue;
                if (settings.Pattern == TrainerPattern.Doubles && (int)Math.Round(e.Beat / spacing) % 4 >= 2) continue;
                if (chosen.Count > 0 && music.TimeAt(e.Beat) - chosen[^1].TimeMs < step * beatMs - .001) continue;
                chosen.Add(note(e, (int)(e.Beat / 4)));
            }
        }
        else
        {
            // Resolve competing instruments by salience first, so a faint accompaniment
            // just before a melody note cannot consume the player's available tap spacing.
            foreach (var e in events.OrderByDescending(e => e.Strength).ThenBy(e => e.Beat))
            {
                if (settings.Pattern == TrainerPattern.Gaps && e.Beat % 4 >= 2.5) continue;
                if (e.Strength < (limits.Complexity == 0 ? .7 : .5)) continue;
                double localStep = step * (music.SectionAt(e.Beat)?.Quiet == true ? 1.5 : 1);
                if (chosen.Any(n => Math.Abs(n.TimeMs - music.TimeAt(e.Beat)) < localStep * beatMs - .001)) continue;
                chosen.Add(note(e, (int)(e.Beat / 4)));
            }
        }
        return chosen.OrderBy(n => n.TimeMs).ToArray();
    }
}
