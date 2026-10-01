using System.Text;

namespace AimMod.InGame.Multiplayer;

// Diagnostic for match scenarios whose map doesn't load: variants of the base that each add one
// part of what the generator changes, to load one by one from KovaaK's menu and see which part
// makes KovaaK's keep the previous map.
static partial class MatchScenario
{
    public const string ProbePrefix = "AimMod Probe ";
    public sealed record Variant(string Name, string Label, string Text);

    public static IReadOnlyList<Variant> Bisect(string baseText, string generated)
    {
        var (bh, bs, bmap, nl) = Parse(baseText);
        var (gh, gs, _, _) = Parse(generated);
        static string Id(Section x) => x.Title + "|" + x.Get("Name");
        static IEnumerable<string> Body(Section x) => x.Lines.Where(l => l.Trim().Length > 0);
        static Section Copy(Section x) => new() { Title = x.Title, Lines = x.Lines.ToList() };
        var baseIds = bs.Select(Id).ToHashSet(StringComparer.Ordinal);
        var added = gs.Where(x => !baseIds.Contains(Id(x))).ToList();
        var changed = gs.Where(x => baseIds.Contains(Id(x)) && !Body(bs.First(b => Id(b) == Id(x))).SequenceEqual(Body(x))).ToList();
        // The added bot: bot, aim and dodge profiles plus the characters its bots use.
        var botCharacters = added.Where(x => x.Title == "[Bot Profile]").Select(x => x.Get("CharacterProfile")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var bot = added.Where(x => x.Title is "[Bot Profile]" or "[Aim Profile]" or "[Dodge Profile]" || x.Title == "[Character Profile]" && botCharacters.Contains(x.Get("Name") ?? "")).ToList();
        // Bodies for other players: added characters nothing in the file uses.
        var used = botCharacters.Append(gh.Get("PlayerProfile") ?? "").ToHashSet(StringComparer.Ordinal);
        var avatars = added.Where(x => x.Title == "[Character Profile]" && !used.Contains(x.Get("Name") ?? "")).ToList();
        var rest = added.Except(bot).Except(avatars).ToList();

        var variants = new List<Variant>();
        var generatedName = gh.Get("Name") ?? "";
        string ProbeName(int n, string tail) => ProbePrefix + n.ToString("00") + " - " + tail;
        void Raw(int n, string label, string tail)
        {
            var name = ProbeName(n, tail);
            var text = ReplaceHeaderName(baseText, name);
            variants.Add(new(name, label, text));
        }
        void Built(int n, string label, Action<Section, List<Section>> change)
        {
            var name = ProbeName(n, label);
            var header = Copy(bh); var sections = bs.Select(Copy).ToList();
            change(header, sections);
            header.Set("Name", name);
            variants.Add(new(name, label, Write(header, sections, bmap, nl)));
        }
        void Replace(List<Section> sections, IEnumerable<Section> with)
        {
            foreach (var x in with) { var i = sections.FindIndex(s => Id(s) == Id(x)); if (i >= 0) sections[i] = Copy(x); else sections.Add(Copy(x)); }
        }
        void Header(Section header, bool bots)
        {
            foreach (var line in gh.Lines.Where(l => l.Contains('=')))
            {
                var key = line[..line.IndexOf('=')];
                if (key == "Name" || (!bots && key is "BotCharacters" or "AddedBots")) continue;
                if (gh.Get(key) is { } value && header.Get(key) != value) header.Set(key, value);
            }
        }
        Raw(0, "the base file, only Name changed", "source copy");
        Built(1, "base rewritten by AimMod", (_, _) => { });
        Raw(2, "the base file with a generated-length name", generatedName.StartsWith(Prefix, StringComparison.Ordinal) ? generatedName[Prefix.Length..] : generatedName);
        Built(3, "header changes", (h, _) => Header(h, bots: false));
        Built(4, "avatar character profiles", (_, s) => Replace(s, avatars));
        Built(5, "hidden bot", (h, s) => { Replace(s, bot); foreach (var key in new[] { "BotCharacters", "AddedBots" }) if (gh.Get(key) is { } v) h.Set(key, v); });
        Built(6, "weapons and changed profiles", (_, s) => { Replace(s, changed); Replace(s, rest); });
        Built(7, "everything but avatars", (h, s) => { Header(h, bots: true); Replace(s, changed); Replace(s, bot); Replace(s, rest); });
        Built(8, "everything", (h, s) => { Header(h, bots: true); Replace(s, changed); Replace(s, bot); Replace(s, rest); Replace(s, avatars); });
        return variants;
    }

    // Only the header's Name line, the rest of the file byte for byte.
    static string ReplaceHeaderName(string text, string name)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            var line = (end < 0 ? text[start..] : text[start..end]).TrimEnd('\r');
            if (line.StartsWith('[')) break;
            if (line.StartsWith("Name=", StringComparison.Ordinal)) return text[..start] + "Name=" + name + text[(start + line.Length)..];
            if (end < 0) break;
            start = end + 1;
        }
        return "Name=" + name + (text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n") + text;
    }

    // --bisect-arena <base .sce> <mode> <out folder>: the variants plus variants.tsv listing them.
    public static int BisectFile(string basePath, string mode, string outFolder)
    {
        if (!LobbyModes.All.Contains(mode)) { Console.Error.WriteLine("Unknown mode."); return 1; }
        var text = File.ReadAllText(basePath);
        var name = Path.GetFileNameWithoutExtension(basePath);
        var settings = LobbyRules.Normalize(new LobbySettings(Mode: mode, Scenario: new ScenarioChoice(name, ContentLibrary.TextHash(text), "", "", 60)), 2);
        var generated = Generate(new Inputs(text, settings));
        Directory.CreateDirectory(outFolder);
        var list = new StringBuilder("file\tchange\tmap section\n");
        foreach (var v in Bisect(text, generated))
        {
            File.WriteAllText(Path.Combine(outFolder, v.Name + ".sce"), v.Text, new UTF8Encoding(false));
            list.Append(v.Name).Append('\t').Append(v.Label).Append('\t').Append(MapSectionOf(v.Text) == MapSectionOf(text) ? "identical" : "DIFFERENT").Append('\n');
        }
        File.WriteAllText(Path.Combine(outFolder, "variants.tsv"), list.ToString());
        Console.Write(list.ToString());
        return 0;
    }

    // --install-probe-variants <folder> [--game <root>] [--remove]: copies the variants into KovaaK's
    // Scenarios folder (or removes every AimMod Probe file there) and asks AimModCore to refresh its list.
    public static int InstallProbes(string folder, string? gameRoot, bool remove, string output)
    {
        if (ContentLibrary.Locate(gameRoot) is not { } root) { Console.Error.WriteLine("KovaaK's wasn't found; pass --game <FPSAimTrainer folder>."); return 1; }
        var scenarios = Path.Combine(root, "Saved", "SaveGames", "Scenarios");
        if (!Directory.Exists(scenarios)) { Console.Error.WriteLine("The Scenarios folder wasn't found."); return 1; }
        var count = 0;
        if (remove)
            foreach (var file in Directory.EnumerateFiles(scenarios, ProbePrefix + "*.sce")) { File.Delete(file); count++; }
        else
            foreach (var file in Directory.EnumerateFiles(folder, ProbePrefix + "*.sce").Order(StringComparer.Ordinal))
            {
                File.Copy(file, Path.Combine(scenarios, Path.GetFileName(file)), overwrite: true); count++;
                Console.WriteLine(Path.GetFileNameWithoutExtension(file));
            }
        var refreshed = GameCommands.Capabilities(output) is { } caps && (caps.Contains("refresh") || caps.Contains("load"))
            && new GameCommands(output).Send(new("refresh-scenarios", null, null, null, null, null, null, null)).Sequence is not null;
        Console.WriteLine((remove ? "Removed " : "Installed ") + count + " probe scenarios. " + (refreshed ? "Asked KovaaK's to refresh its scenario list." : "Restart KovaaK's (or refresh its scenario list) to see the change."));
        if (!remove) Console.WriteLine("Load each from KovaaK's menu (search \"" + ProbePrefix.Trim() + "\") and note which ones show the map.");
        return 0;
    }
}
