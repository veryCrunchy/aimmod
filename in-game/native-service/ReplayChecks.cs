using System.Text.Json;

namespace AimMod.InGame;

static class ReplayChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "aimmod-replay-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new ReplayCatalog(root);
            string Header(string id = "synthetic") => JsonSerializer.Serialize(new {
                kind = "header", version = 1, id, scenario = "Synthetic target test", recordedAt = "2026-01-01T00:00:00Z", coordinates = "unreal-centimeters"
            });
            var first = "{\"kind\":\"frame\",\"t\":0,\"camera\":[0,0,0,0,179,0,90],\"actors\":[[1,100,0,0,10,10]]}";
            var second = "{\"kind\":\"frame\",\"t\":0.1,\"camera\":[0,0,0,0,-179,0,90],\"actors\":[[1,100,1,0,10,10]]}";
            var end = "{\"kind\":\"end\",\"reason\":\"completed\",\"frames\":2,\"inputEvents\":0}";
            var good = string.Join('\n', Header(), first, second, end) + "\n";
            void Save(string content) => File.WriteAllText(Path.Combine(root, "replays", "synthetic.amreplay"), content);
            void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException("Replay check: " + message); }
            Save(good);
            Check(catalog.List().Count == 1 && catalog.Read("synthetic")?.Frames.Count == 2, "valid replay can be listed and loaded");
            Check(catalog.Read("../synthetic") is null && catalog.Read("synthetic.amreplay") is null, "path traversal rejected");
            Save(good.TrimEnd('\n')); Check(catalog.Read("synthetic") is null && catalog.List().Count == 0, "unterminated file rejected");
            Save(good.Replace(end, "")); Check(catalog.Read("synthetic") is null, "missing completion rejected");
            Save(good.Replace("\"frames\":2", "\"frames\":3")); Check(catalog.Read("synthetic") is null, "frame count mismatch rejected");
            Save(good.Replace(second, first)); Check(catalog.Read("synthetic") is null, "duplicate frame times rejected");
            Save(good.Replace(Header(), Header("other"))); Check(catalog.Read("synthetic") is null, "header and file identity must match");
            Save(good.Replace("0.1", "1e999")); Check(catalog.Read("synthetic") is null, "nonfinite time rejected");
            Save(good.Replace("[1,100,1,0,10,10]", "[1,100,1,0,10,10],[1,101,1,0,10,10]")); Check(catalog.Read("synthetic") is null, "duplicate actors rejected");
            Save(good + second + "\n"); Check(catalog.Read("synthetic") is null, "data after completion rejected");
            Save(good.Replace("\"version\":1", "\"version\":2")); Check(catalog.Read("synthetic") is null, "unknown version rejected");
            Save(good.Replace(first, new string('x', 140000))); Check(catalog.Read("synthetic") is null, "oversize line rejected");
            Save(good.Replace("\"kind\":\"frame\",\"t\":0,", "\"kind\":\"frame\",\"t\":0,\"stats\":{\"score\":12,\"shots\":3,\"hits\":2},"));
            Check(catalog.Read("synthetic")?.Frames[0].Stats is { Score:12, Hits:2, Seconds:null }, "recorded metrics preserve unknown fields");
            Save(good); Check(catalog.Read("synthetic")?.Frames[0].Stats is null, "old replay has no fabricated metrics");
            Save(good.Replace("\"kind\":\"frame\",\"t\":0,", "\"kind\":\"frame\",\"t\":0,\"stats\":{\"hits\":-1},"));
            Check(catalog.Read("synthetic") is null, "negative hit count rejected");
            Save(good.Replace("completed", "interrupted"));
            Check(catalog.List().Count == 0 && catalog.Read("synthetic") is not null, "interrupted attempts stay out of library without deleting existing files");
            var health = good.Replace("\"kind\":\"frame\",\"t\":0,", "\"kind\":\"frame\",\"t\":0,\"health\":[{\"id\":1,\"percent\":0.4}],");
            Save(health); Check(catalog.Read("synthetic")?.Frames[0].Health?[0].Percent == .4, "recorded target health retained");
            Save(health.Replace("\"percent\":0.4", "\"percent\":1.4")); Check(catalog.Read("synthetic") is null, "health outside unit range rejected");
            Save(health.Replace("\"id\":1,\"percent\"", "\"id\":2,\"percent\"")); Check(catalog.Read("synthetic") is null, "health must belong to frame target");
            Save(good.Replace("\"reason\":\"completed\"", "\"reason\":\"completed\",\"score\":250"));
            Check(catalog.Read("synthetic") is { } completed && completed.Frames[0].Stats is null && completed.Frames[^1].Stats?.Score == 250, "completion score applies only at the recorded finish");
            Save(good);
            File.WriteAllText(Path.Combine(root,"completed.tsv"), "run\tsynthetic\tSynthetic target test\t321\t80\t60\t1\t1\t2026-01-01T00:01:00Z\n");
            Check(catalog.Read("synthetic")?.Frames[^1].Stats?.Score == 321, "exact native attempt restores final score for older recording");
            File.WriteAllText(Path.Combine(root,"completed.tsv"), "run\tother\tSynthetic target test\t321\t80\t60\t1\t1\t2026-01-01T00:01:00Z\n");
            Check(catalog.Read("synthetic")?.Frames[^1].Stats is null, "similar scenario cannot supply another attempt score");
            var appearance = good.Replace("\"kind\":\"frame\",\"t\":0,", "\"kind\":\"frame\",\"t\":0,\"appearance\":[{\"id\":1,\"profile\":\"Bot A\",\"rotation\":[0,45,0]}],");
            Save(appearance); Check(catalog.Read("synthetic")?.Frames[0].Appearance?[0].Profile == "Bot A", "profile and rotation retained");
            Save(appearance.Replace("\"id\":1,\"profile\"", "\"id\":2,\"profile\"")); Check(catalog.Read("synthetic") is null, "appearance must belong to recorded actor");
            Console.WriteLine("Replay catalog checks passed (24).");
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(full).StartsWith("aimmod-replay-check-", StringComparison.Ordinal) && Directory.Exists(full))
                Directory.Delete(full, true);
        }
    }
}
