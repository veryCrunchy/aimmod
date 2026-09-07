using System.Text.Json;
namespace AimMod.Desktop.LocalLibrary;

public sealed record ScoreModChoice(string Key, string Label);

public static class ScoreMods {
    public const string Any = "*";
    public static string[] Acronyms(LocalReplay run) => run.Mods.Concat(read(run).Select(m => m.Acronym))
        .Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0 && s != "NM").Distinct().Order().ToArray();

    private sealed record Mod(string Acronym, string Settings);
    private static Mod[] read(LocalReplay run) => read(run.ModsJson);
    private static Mod[] read(string? json) {
        try {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 16384) return [];
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return doc.RootElement.EnumerateArray().Select(m => m.ValueKind == JsonValueKind.String
                ? new Mod(m.GetString()!.ToUpperInvariant(), "")
                : new Mod(m.GetProperty("acronym").GetString()!.ToUpperInvariant(),
                    m.TryGetProperty("settings",out var settings) && settings.ValueKind == JsonValueKind.Object && settings.EnumerateObject().Any()
                    ? canonical(settings) : "")).ToArray();
        } catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or NullReferenceException) { return []; }
    }
    private static string canonical(JsonElement element) => element.ValueKind switch {
        JsonValueKind.Object => "{" + string.Join(",",element.EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal).Select(p=>JsonSerializer.Serialize(p.Name)+":"+canonical(p.Value))) + "}",
        JsonValueKind.Array => "["+string.Join(",",element.EnumerateArray().Select(canonical))+"]",
        _ => element.GetRawText()
    };
    public static string Configuration(LocalReplay run) => Configuration(Acronyms(run), run.ModsJson);
    public static string Configuration(IEnumerable<string> acronyms, string? json, Func<string, string>? normalise = null) {
        var configured = read(json).ToLookup(m=>normalise?.Invoke(m.Acronym) ?? m.Acronym);
        return string.Join("+",acronyms.Order().Select(a=>a+string.Join("",configured[a].Select(m=>m.Settings).Distinct().Order())));
    }
    public static string SetupKey(LocalReplay run) =>
        $"{run.RulesetShortName}|{(run.LegacyScore || run.Origin == LocalLibraryOrigin.Stable ? "stable" : "lazer")}|{(run.BeatmapHash.Length>0 ? run.BeatmapHash.ToLowerInvariant() : run.BeatmapId != Guid.Empty ? run.BeatmapId.ToString("N") : run.ScoreId.ToString("N"))}|{Configuration(run)}";
    public static string Display(LocalReplay run) => display(Acronyms(run), read(run));
    public static string Display(IReadOnlyList<string> acronyms, string? json) => display(acronyms.ToArray(), read(json));
    private static string display(string[] acronyms, Mod[] configured) {
        var mods = configured.ToLookup(m=>m.Acronym);
        var names = acronyms.Select(a => {
            var settings = mods[a].FirstOrDefault()?.Settings;
            if (string.IsNullOrEmpty(settings)) return a;
            using var doc = JsonDocument.Parse(settings);
            return a+" ("+string.Join(", ",doc.RootElement.EnumerateObject().Select(p => p.Name == "speed_change" ? p.Value.GetRawText()+"×" : p.Name.Replace('_',' ')+": "+p.Value.ToString()))+")";
        }).ToArray();
        return names.Length == 0 ? "No mods" : string.Join(" + ",names);
    }
    public static IReadOnlyList<ScoreModChoice> Choices(IEnumerable<LocalReplay> runs) {
        var all=runs.ToArray();
        var choices=new List<ScoreModChoice> { new(Any,"All mods"),new("NM","No mods") };
        choices.AddRange(all.SelectMany(Acronyms).Distinct().Order().Select(a=>new ScoreModChoice("mod:"+a,a)));
        choices.AddRange(all.Where(r=>Acronyms(r).Length>1 || read(r).Any(m=>m.Settings.Length>0))
            .GroupBy(Configuration).OrderBy(g=>g.Key).Select(g=>new ScoreModChoice("setup:"+g.Key,"Exact: "+Display(g.First()))));
        return choices;
    }
    public static bool Matches(LocalReplay run,string selection) => selection switch {
        Any or "" => true, "NM" => Acronyms(run).Length==0,
        _ when selection.StartsWith("mod:") => Acronyms(run).Contains(selection[4..]),
        _ when selection.StartsWith("setup:") => Configuration(run)==selection[6..], _ => false
    };
    public static bool IsManualPlay(LocalReplay run) => !Acronyms(run).Intersect(new[]{"AT","CN","RX","AP"}).Any();
}
