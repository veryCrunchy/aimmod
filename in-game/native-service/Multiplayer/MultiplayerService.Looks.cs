namespace AimMod.InGame.Multiplayer;

// The Look tab's model and skin choices (GET /multiplayer?part=looks). These are AimMod's own
// avatar profiles (Avatars.cs), grouped by model: the free KovaaK's characters only, never a
// paid or locked skin. The choice is the "avatar" action, saved as prefs.avatar; it travels to
// the lobby like before and the character preview renders it (MultiplayerService.CosmeticPreview.cs).
// Read-only: catalog items, equipping and the preview request stay where they are.
sealed partial class MultiplayerService
{
    public object LooksView()
    {
        lock (gate)
        {
            var selected = AvatarProfiles.Find(myAvatar) ?? AvatarProfiles.Find(AvatarProfiles.Default)!;
            return new
            {
                selected = selected.Id,
                model = selected.Model,
                @default = AvatarProfiles.Default,
                models = AvatarProfiles.All.GroupBy(a => a.Model).Select(g => new
                {
                    id = g.Key,
                    label = g.Key,
                    skins = g.Select(a => new { id = a.Id, skin = a.Skin, label = a.Skin }),
                }),
            };
        }
    }
}
