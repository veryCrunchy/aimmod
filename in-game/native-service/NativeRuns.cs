using System.Globalization;

namespace AimMod.InGame;
static class NativeRuns
{
    public static string Decode(string value) => System.Text.RegularExpressions.Regex.Replace(value, "%([0-9A-Fa-f]{2})", m => ((char)Convert.ToByte(m.Groups[1].Value,16)).ToString());
    public static Run? Parse(string line)
    {
        var c = line.Split('\t').Select(Decode).ToArray();
        if (c.Length != 9 || c[0] != "run" || string.IsNullOrWhiteSpace(c[1]) || string.IsNullOrWhiteSpace(c[2])) return null;
        double? Number(int i) => double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
        var score=Number(3); var duration=Number(5); var accuracy=Number(4);
        if (score is null || duration is null or <=0 || !DateTimeOffset.TryParse(c[8], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)) return null;
        if (accuracy is <0 or >100) accuracy=null;
        return new("native:"+c[1], c[2], score.Value, accuracy, duration.Value, Number(6)??0, Number(7)??0, c[8], null,null,null,null,false);
    }
    public static IReadOnlyList<Run> Read(string path)
    {
        if (!File.Exists(path)) return [];
        // Read sharing allows the mod to append a completed run. A partial final
        // line fails validation and is retried on the next file-change refresh.
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        using var reader=new StreamReader(stream);
        var runs=new Dictionary<string,Run>();
        while (reader.ReadLine() is string line)
        { var run=Parse(line); if(run is not null) runs[run.Id]=run; }
        return runs.Values.OrderByDescending(r=>r.Timestamp).Take(5000).ToArray();
    }
}
