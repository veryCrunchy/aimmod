using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// Cosmetics: this player's equipped catalog items and the viewer setting
// (cosmetics-settings.json), the cosmetic.look lobby message, and
// cosmetic-looks.txt for AimModCore, written and deleted with the session marker.
sealed partial class MultiplayerService
{
    public static readonly string[] CosmeticViews = ["all", "friends", "off"];
    CosmeticsCatalog? cosmetics;
    List<CosmeticRef>? equipped;
    string cosmeticView = "all";
    string? announcedLook, looksWritten;

    string? CosmeticsPath => outputFolder is null ? null : Path.Combine(outputFolder, "cosmetics-settings.json");
    string? LooksPath => outputFolder is null ? null : Path.Combine(outputFolder, CosmeticLooks.FileName);

    // The shipped catalog: next to the service (AimModCore\service\cosmetics) unless a host gave one.
    public string? CosmeticsFolder { get; set; } = Path.Combine(AppContext.BaseDirectory, "cosmetics");
    CosmeticsCatalog CosmeticCatalog => cosmetics ??= CosmeticsCatalog.Load(CosmeticsFolder, library.Root is { } root ? Path.Combine(root, "Content", "Paks", "~AimMod") : null);

    void LoadCosmetics()
    {
        if (equipped is not null) return;
        equipped = [];
        try
        {
            if (CosmeticsPath is not { } path || !File.Exists(path) || new FileInfo(path).Length > 8192) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            equipped = ReadRefs(doc.RootElement.TryGetProperty("equipped", out var e) ? e : default).ToList();
            if (doc.RootElement.TryGetProperty("show", out var s) && CosmeticViews.Contains(s.GetString())) cosmeticView = s.GetString()!;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { }
    }
    void SaveCosmetics()
    {
        if (CosmeticsPath is not { } path) return;
        try { AtomicFile.WriteText(path, JsonSerializer.Serialize(new { equipped, show = cosmeticView }, Protocol.Json)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Structure only: ids, whole versions, at most MaxEquipped. Each viewer resolves them against its own catalog.
    static IEnumerable<CosmeticRef> ReadRefs(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array) yield break;
        var seen = new HashSet<string>();
        foreach (var x in list.EnumerateArray().Take(CosmeticsCatalog.MaxEquipped))
            if (x.ValueKind == JsonValueKind.Object && x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && CosmeticsCatalog.ValidId(id.GetString())
                && x.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var version) && version is >= 1 and <= 100000 && seen.Add(id.GetString()!))
                yield return new CosmeticRef(id.GetString()!, version);
    }

    // My own items, as this machine's catalog knows them (unknown or unavailable ones are dropped).
    IReadOnlyList<CosmeticRef> OwnLook() { LoadCosmetics(); return CosmeticCatalog.Filter(equipped); }

    LobbyResult CosmeticAction(string action, JsonElement args)
    {
        LoadCosmetics();
        string? Text(string key) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        switch (action)
        {
            case "cosmetic-equip" or "cosmetic-remove":
                var item = CosmeticCatalog.Pickable.FirstOrDefault(i => i.Id == Text("id"));
                if (item is null) return LobbyResult.Fail("invalid", "That item isn’t in your AimMod catalog.");
                // One item per kind: equipping replaces what that slot had.
                var kinds = CosmeticCatalog.Items.ToDictionary(i => i.Id, i => i.Kind);
                equipped!.RemoveAll(r => r.Id == item.Id || (action == "cosmetic-equip" && kinds.GetValueOrDefault(r.Id) == item.Kind));
                if (action == "cosmetic-equip") { if (equipped.Count >= CosmeticsCatalog.MaxEquipped) return LobbyResult.Fail("full", "Remove an item first."); equipped.Add(new CosmeticRef(item.Id, item.Version)); }
                SaveCosmetics(); AnnounceLook(force: true); WriteLooks(force: true);
                return LobbyResult.Success;
            case "cosmetic-view":
                if (!CosmeticViews.Contains(Text("show"))) return LobbyResult.Fail("invalid", "Pick all, friends or off.");
                cosmeticView = Text("show")!; SaveCosmetics(); WriteLooks(force: true);
                return LobbyResult.Success;
            default:
                return LobbyResult.Fail("invalid", "Unknown cosmetics action.");
        }
    }

    // Tell the lobby what I wear: the host stores it, everyone else sends cosmetic.look to the host.
    void AnnounceLook(bool force = false)
    {
        if (Current is null) { announcedLook = null; return; }
        var look = OwnLook();
        var key = (Current.Id) + "|" + string.Join(',', look.Select(r => r.Id + "@" + r.Version));
        if (!force && key == announcedLook) return;
        if (core is not null) core.SetCosmetics(SelfId, look);
        else if (hostPeer is not null && mirror is not null) Send(hostPeer, "cosmetic.look", new { items = look.Select(r => new { id = r.Id, version = r.Version }) });
        else return;
        announcedLook = key;
    }

    // The host's side of cosmetic.look: structure is checked here; meaning is up to each viewer.
    void TakeLook(string peer, JsonElement body)
    {
        if (core is null || !body.TryGetProperty("items", out var items)) return;
        core.SetCosmetics(peer, ReadRefs(items).ToArray());
    }

    // cosmetic-looks.txt, filtered by the viewer setting. Only while the session marker is there.
    void WriteLooks(bool force = false)
    {
        if (LooksPath is not { } path) return;
        if (markerKey is null || Current is not { } lobby) { DeleteLooks(); return; }
        var friends = cosmeticView == "friends" ? Friends().Select(f => f.Id).ToHashSet() : null;
        var peers = cosmeticView == "off" ? [] : lobby.Members.Where(m => m.Id != SelfId && (friends is null || friends.Contains(m.Id)))
            .Select(m => (m.Id, CosmeticCatalog.Filter(m.Cosmetics))).ToArray();
        var text = CosmeticLooks.Format(OwnLook(), peers);
        if (!force && text == looksWritten) return;
        try { AtomicFile.WriteText(path, text); looksWritten = text; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    void DeleteLooks()
    {
        looksWritten = null;
        if (LooksPath is not { } path) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public object CosmeticsView()
    {
        lock (gate)
        {
            LoadCosmetics();
            var catalog = CosmeticCatalog;
            var mine = OwnLook().Select(r => r.Id).ToHashSet();
            return new
            {
                available = catalog.Available, problem = catalog.Problem, version = catalog.Version, show = cosmeticView,
                items = catalog.Pickable.Select(i => new { i.Id, i.Version, i.Kind, i.Name, i.Models, i.Color, i.Swatch, i.Shine, equipped = mine.Contains(i.Id) }),
                unavailable = catalog.Items.Count(i => !i.Draft) - catalog.Pickable.Count,
            };
        }
    }
}
