namespace AimMod.InGame.Multiplayer;

// Bots in the score modes (score race, duel, free-for-all rounds, practice): a bot "plays" the
// round's scenario on the host, sending score frames like a player's game and finishing when the
// time is up. Its score follows a believable run: a pace from a reference score (the host's best on
// the scenario, else a typical one), scaled by difficulty and its own steady form, with streaks and
// slumps along the way. Deterministic per bot and round. Bots never post runs or reach the Hub.
static class BotScorer
{
    // Difficulty against the reference: easy well below it, hard around it.
    public static double Pace(string skill) => skill switch { BotSkills.Easy => 0.62, BotSkills.Hard => 1.02, _ => 0.84 };

    // The bot's own form: a few percent either way, fixed per bot.
    public static double Form(string bot) => 0.95 + BotStrategy.Hash(bot) % 1000 / 10000.0;

    // Where the bot's run stands `elapsed` seconds into a `limit`-second scenario.
    public static (double Score, int Shots, int Hits) At(string bot, string skill, string round, double reference, double limit, double elapsed)
    {
        var t = Math.Clamp(elapsed, 0, limit);
        var rate = reference / Math.Max(1, limit) * Pace(skill) * Form(bot);
        var phase = BotStrategy.Hash(bot + "#" + round) % 628 / 100.0;
        // Streaks and slumps: the pace wanders (1 + 0.15 sin) but never stops, so the score only climbs
        // (the integral of that pace).
        const double amplitude = 0.15, omega = 0.45;
        var score = Math.Max(0, Math.Round(rate * (t + amplitude / omega * (Math.Cos(phase) - Math.Cos(omega * t + phase))), 1));
        var shots = (int)(t * (skill == BotSkills.Hard ? 2.8 : skill == BotSkills.Easy ? 2.0 : 2.4));
        var accuracy = skill switch { BotSkills.Easy => 0.58, BotSkills.Hard => 0.86, _ => 0.72 };
        return (score, shots, (int)(shots * accuracy));
    }

    // A reference score for a scenario: the best of the given runs, else about 1,000 a minute.
    public static double Reference(IEnumerable<double> scores, double limit)
    {
        var best = scores.Where(s => double.IsFinite(s) && s > 0).DefaultIfEmpty(0).Max();
        return best > 0 ? best : 1000 * limit / 60;
    }
}
