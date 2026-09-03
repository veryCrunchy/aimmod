using System.Text.Json;
using System.Text.Json.Serialization;
using Realms;

const ulong supportedSchemaVersion = 52;

bool validArguments = args is ["beatmaps" or "skins" or "scores" or "replay-theme-skin", _]
    or ["beatmap-set-files", _, _];
if (!validArguments)
{
    Console.Error.WriteLine("usage: osu-realm-reader <beatmaps|skins|scores|replay-theme-skin> <osu-data-root>");
    Console.Error.WriteLine("       osu-realm-reader beatmap-set-files <osu-data-root> <beatmap-md5-or-sha256>");
    return 2;
}

string root = Path.GetFullPath(args[1]);
string realmPath = Path.Combine(root, "client.realm");
if (!File.Exists(realmPath))
{
    Console.Error.WriteLine("client.realm was not found");
    return 3;
}

var config = new RealmConfiguration(realmPath)
{
    IsDynamic = true,
    IsReadOnly = true,
    SchemaVersion = supportedSchemaVersion,
};

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

try
{
    using Realm realm = Realm.GetInstance(config);
    object payload = args[0] switch
    {
        "beatmaps" => ReadBeatmaps(realm, root),
        "skins" => ReadSkins(realm, root),
        "scores" => ReadScores(realm, root),
        "replay-theme-skin" => ReadReplayThemeSkin(realm, root),
        "beatmap-set-files" => ReadBeatmapSetFiles(realm, args[2]),
        _ => throw new InvalidOperationException(),
    };
    Console.Write(JsonSerializer.Serialize(payload, jsonOptions));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"read-only Realm open failed for supported schema {supportedSchemaVersion}: {error.Message}");
    return 4;
}

static BeatmapSetFilesDto ReadBeatmapSetFiles(Realm realm, string requestedHash)
{
    requestedHash = requestedHash.Trim().ToLowerInvariant();
    bool validHash = requestedHash.Length is 32 or 64
        && requestedHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    if (!validHash)
        throw new InvalidOperationException("The requested beatmap hash must be hexadecimal MD5 or SHA-256.");

    IRealmObject? selected = realm.DynamicApi.All("Beatmap").AsEnumerable().FirstOrDefault(beatmap =>
        string.Equals(Get<string>(beatmap, "MD5Hash"), requestedHash, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Get<string>(beatmap, "Hash"), requestedHash, StringComparison.OrdinalIgnoreCase));
    if (selected is null)
        throw new InvalidOperationException("No local beatmap matches the requested replay hash.");

    IRealmObjectBase? set = GetObject(selected, "BeatmapSet");
    if (set is null || Get<bool>(set, "DeletePending"))
        throw new InvalidOperationException("The requested beatmap set is unavailable or pending deletion.");

    var files = set.DynamicApi.GetList<IEmbeddedObject>("Files")
        .Select(usage => new NamedFileHashDto(
            Get<string>(usage, "Filename") ?? string.Empty,
            GetObject(usage, "File") is { } stored ? Get<string>(stored, "Hash") ?? string.Empty : string.Empty))
        .ToArray();

    return new BeatmapSetFilesDto(
        Get<Guid>(set, "ID").ToString("D"),
        Get<int>(set, "OnlineID"),
        Get<string>(set, "Hash") ?? string.Empty,
        Get<Guid>(selected, "ID").ToString("D"),
        Get<int>(selected, "OnlineID"),
        Get<string>(selected, "Hash") ?? string.Empty,
        Get<string>(selected, "MD5Hash") ?? string.Empty,
        files);
}

static IReadOnlyList<BeatmapDto> ReadBeatmaps(Realm realm, string root)
{
    var result = new List<BeatmapDto>();
    foreach (IRealmObject beatmap in realm.DynamicApi.All("Beatmap"))
    {
        IRealmObjectBase? set = GetObject(beatmap, "BeatmapSet");
        if (set is null || Get<bool>(set, "DeletePending"))
            continue;

        IRealmObjectBase? metadata = GetObject(beatmap, "Metadata");
        IRealmObjectBase? difficulty = GetObject(beatmap, "Difficulty");
        IRealmObjectBase? ruleset = GetObject(beatmap, "Ruleset");
        IRealmObjectBase? author = metadata is null ? null : GetObject(metadata, "Author");
        IRealmObjectBase? userSettings = GetObject(beatmap, "UserSettings");
        string backgroundName = metadata is null ? string.Empty : Get<string>(metadata, "BackgroundFile") ?? string.Empty;
        string? backgroundPath = ResolveNamedFile(set, backgroundName, root);
        string audioName = metadata is null ? string.Empty : Get<string>(metadata, "AudioFile") ?? string.Empty;
        string? audioPath = ResolveNamedFile(set, audioName, root);
        double stars = Get<double>(beatmap, "StarRating");
        int beatmapId = Get<int>(beatmap, "OnlineID");
        int setId = Get<int>(set, "OnlineID");

        result.Add(new BeatmapDto(
            setId > 0 ? setId.ToString() : Get<Guid>(set, "ID").ToString("D"),
            beatmapId > 0 ? beatmapId.ToString() : Get<Guid>(beatmap, "ID").ToString("D"),
            metadata is null ? string.Empty : Get<string>(metadata, "Artist") ?? string.Empty,
            metadata is null ? string.Empty : Get<string>(metadata, "Title") ?? string.Empty,
            author is null ? string.Empty : Get<string>(author, "Username") ?? string.Empty,
            Get<string>(beatmap, "DifficultyName") ?? string.Empty,
            ruleset is null ? "osu" : Get<string>(ruleset, "ShortName") ?? "osu",
            stars >= 0 ? stars : null,
            Positive(Get<double>(beatmap, "BPM")),
            ToSeconds(Get<double>(beatmap, "Length")),
            Get<int>(beatmap, "Status").ToString(),
            backgroundPath,
            audioPath,
            metadata is null ? -1 : Get<int>(metadata, "PreviewTime"),
            userSettings is null ? 0 : Get<double>(userSettings, "Offset"),
            difficulty is null ? null : Get<double>(difficulty, "CircleSize"),
            difficulty is null ? null : Get<double>(difficulty, "ApproachRate"),
            difficulty is null ? null : Get<double>(difficulty, "OverallDifficulty"),
            difficulty is null ? null : Get<double>(difficulty, "DrainRate"),
            Get<string>(beatmap, "Hash") ?? string.Empty,
            Get<string>(beatmap, "MD5Hash") ?? string.Empty,
            Get<DateTimeOffset?>(beatmap, "LastPlayed")?.ToUniversalTime().ToString("O"),
            Get<DateTimeOffset>(set, "DateAdded").ToUniversalTime().ToString("O")));
    }
    return result;
}

static IReadOnlyList<SkinDto> ReadSkins(Realm realm, string root)
{
    var result = new List<SkinDto>();
    foreach (IRealmObject skin in realm.DynamicApi.All("Skin"))
    {
        if (Get<bool>(skin, "DeletePending") || Get<bool>(skin, "Protected"))
            continue;
        var files = skin.DynamicApi.GetList<IEmbeddedObject>("Files");
        result.Add(new SkinDto(
            Get<Guid>(skin, "ID").ToString("D"),
            Get<string>(skin, "Name") ?? string.Empty,
            Get<string>(skin, "Creator") ?? string.Empty,
            Get<string>(skin, "Hash") ?? string.Empty,
            files.Count,
            files.Select(file => new NamedFileDto(
                Get<string>(file, "Filename") ?? string.Empty,
                ResolveFile(GetObject(file, "File"), root))).ToArray()));
    }
    return result;
}

static IReadOnlyList<ScoreDto> ReadScores(Realm realm, string root)
{
    var result = new List<ScoreDto>();
    foreach (IRealmObject score in realm.DynamicApi.All("Score"))
    {
        if (Get<bool>(score, "DeletePending"))
            continue;
        IRealmObjectBase? user = GetObject(score, "User");
        IRealmObjectBase? ruleset = GetObject(score, "Ruleset");
        IRealmObjectBase? beatmap = GetObject(score, "BeatmapInfo");
        string? replayPath = ResolveNamedFile(score, ".osr", root, suffixMatch: true);
        result.Add(new ScoreDto(
            Get<Guid>(score, "ID").ToString("D"),
            Get<string>(score, "BeatmapHash") ?? string.Empty,
            beatmap is null ? null : Get<int>(beatmap, "OnlineID"),
            ruleset is null ? string.Empty : Get<string>(ruleset, "ShortName") ?? string.Empty,
            user is null ? string.Empty : Get<string>(user, "Username") ?? string.Empty,
            user is null ? -1 : Get<int>(user, "OnlineID"),
            Get<long>(score, "TotalScore"),
            Get<long>(score, "TotalScoreWithoutMods"),
            Get<int>(score, "MaxCombo"),
            Get<double>(score, "Accuracy") * 100,
            Get<double?>(score, "PP"),
            Get<DateTimeOffset>(score, "Date").ToUniversalTime().ToString("O"),
            Get<long>(score, "OnlineID"),
            Get<long>(score, "LegacyOnlineID"),
            Get<string>(score, "ClientVersion") ?? string.Empty,
            Get<string>(score, "Hash") ?? string.Empty,
            Get<string>(score, "Mods") ?? string.Empty,
            Get<string>(score, "Statistics") ?? string.Empty,
            Get<string>(score, "MaximumStatistics") ?? string.Empty,
            GetIntList(score, "Pauses"),
            replayPath));
    }
    return result;
}

static ReplayThemeSkinDto ReadReplayThemeSkin(Realm realm, string root)
{
    const string argonSkin = "cffa69de-b3e3-4dee-8563-3c4f425c05d0";
    const string trianglesSkin = "2991cfd8-2140-469a-bcb9-2ec23fbce4ad";
    string configuredId = ReadAllowedIniValue(Path.Combine(root, "game.ini"), "Skin") ?? argonSkin;
    if (!Guid.TryParse(configuredId, out Guid selectedId))
        selectedId = Guid.Parse(trianglesSkin);

    IRealmObject? selected = realm.DynamicApi.All("Skin").AsEnumerable()
        .FirstOrDefault(skin => Get<Guid>(skin, "ID") == selectedId && !Get<bool>(skin, "DeletePending"));
    if (selected is null)
    {
        selectedId = Guid.Parse(trianglesSkin);
        selected = realm.DynamicApi.All("Skin").AsEnumerable()
            .FirstOrDefault(skin => Get<Guid>(skin, "ID") == selectedId && !Get<bool>(skin, "DeletePending"));
    }

    if (selected is null)
        return new ReplayThemeSkinDto(selectedId.ToString("D"), "osu! \"triangles\" (2017)", "team osu!", Array.Empty<NamedFileHashDto>());

    var files = selected.DynamicApi.GetList<IEmbeddedObject>("Files")
        .Select(file => new NamedFileHashDto(
            Get<string>(file, "Filename") ?? string.Empty,
            GetObject(file, "File") is { } stored ? Get<string>(stored, "Hash") ?? string.Empty : string.Empty))
        .Where(file => file.Hash.Length == 64)
        .ToArray();
    return new ReplayThemeSkinDto(
        Get<Guid>(selected, "ID").ToString("D"),
        Get<string>(selected, "Name") ?? string.Empty,
        Get<string>(selected, "Creator") ?? string.Empty,
        files);
}

static string? ReadAllowedIniValue(string path, string key)
{
    if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024)
        return null;
    foreach (string line in File.ReadLines(path))
    {
        string trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            continue;
        int separator = trimmed.IndexOf('=');
        if (separator <= 0 || !trimmed[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            continue;
        return trimmed[(separator + 1)..].Trim();
    }
    return null;
}

static T Get<T>(IRealmObjectBase value, string property) => value.DynamicApi.Get<T>(property);

static IReadOnlyList<int> GetIntList(IRealmObjectBase value, string property)
{
    try { return value.DynamicApi.GetList<int>(property).ToArray(); }
    catch { return Array.Empty<int>(); }
}

static IRealmObjectBase? GetObject(IRealmObjectBase value, string property)
{
    try { return value.DynamicApi.Get<IRealmObjectBase?>(property); }
    catch { return null; }
}

static string? ResolveNamedFile(IRealmObjectBase owner, string wanted, string root, bool suffixMatch = false)
{
    if (string.IsNullOrWhiteSpace(wanted))
        return null;
    foreach (IEmbeddedObject usage in owner.DynamicApi.GetList<IEmbeddedObject>("Files"))
    {
        string filename = Get<string>(usage, "Filename") ?? string.Empty;
        bool matches = suffixMatch
            ? filename.EndsWith(wanted, StringComparison.OrdinalIgnoreCase)
            : filename.Equals(wanted, StringComparison.OrdinalIgnoreCase);
        if (matches)
            return ResolveFile(GetObject(usage, "File"), root);
    }
    return null;
}

static string? ResolveFile(IRealmObjectBase? file, string root)
{
    string hash = file is null ? string.Empty : Get<string>(file, "Hash") ?? string.Empty;
    if (hash.Length < 2)
        return null;
    string path = Path.Combine(root, "files", hash[..1], hash[..2], hash);
    return File.Exists(path) ? path : null;
}

static double? Positive(double value) => double.IsFinite(value) && value > 0 ? value : null;
static uint? ToSeconds(double milliseconds) => milliseconds > 0 ? (uint)Math.Ceiling(milliseconds / 1000) : null;

record BeatmapDto(string BeatmapsetId, string BeatmapId, string Artist, string Title, string Creator,
    string DifficultyName, string Mode, double? StarRating, double? Bpm, uint? LengthSeconds, string Status,
    string? BackgroundPath, string? AudioPath, int PreviewTimeMs, double UserOffsetMs,
    double? CircleSize, double? ApproachRate, double? OverallDifficulty, double? HpDrain,
    string ContentHash, string Md5Hash, string? LastPlayed, string DateAdded);

record NamedFileDto(string Filename, string? Path);
record NamedFileHashDto(string Filename, string Hash);
record BeatmapSetFilesDto(string BeatmapSetId, int OnlineBeatmapSetId, string BeatmapSetHash,
    string SelectedBeatmapId, int OnlineBeatmapId, string SelectedContentHash, string SelectedMd5Hash,
    IReadOnlyList<NamedFileHashDto> Files);
record ReplayThemeSkinDto(string Id, string Name, string Creator, IReadOnlyList<NamedFileHashDto> Files);
record SkinDto(string Id, string Name, string Creator, string Hash, int FileCount, IReadOnlyList<NamedFileDto> Files);
record ScoreDto(string Id, string BeatmapHash, int? BeatmapId, string Mode, string PlayerName, int PlayerId,
    long TotalScore, long TotalScoreWithoutMods, int MaxCombo, double AccuracyPercent, double? Pp, string PlayedAt,
    long OnlineId, long LegacyOnlineId, string ClientVersion, string ScoreHash, string ModsJson,
    string StatisticsJson, string MaximumStatisticsJson, IReadOnlyList<int> Pauses, string? ReplayPath);
