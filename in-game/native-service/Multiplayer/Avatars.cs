using System.Text;

namespace AimMod.InGame.Multiplayer;

// How other players appear in your game. AimModSteam spawns each remote player
// as an inert KovaaK's bot and loads the character profile named by the lobby
// key aimmod.char.<SteamID>; these are the profiles AimMod offers. The model
// and skin values follow the game's assets (Meso_CharacterModel,
// Meso_CharacterSkin_McCree, Endo_CharacterModel); confirm them in the live test.
sealed record AvatarProfile(string Id, string Label, string Model, string Skin)
{
    public string ProfileName => "AimMod " + Model + (Skin == "Default" ? "" : " " + Skin);
}

static class AvatarProfiles
{
    public const string Default = "meso-mccree";
    public static readonly AvatarProfile[] All =
    [
        new("meso-mccree", "Meso · McCree", "Meso", "McCree"),
        new("meso-tracer", "Meso · Tracer", "Meso", "Tracer"),
        new("meso-genji", "Meso · Genji", "Meso", "Genji"),
        new("meso-pharah", "Meso · Pharah", "Meso", "Pharah"),
        new("meso", "Meso", "Meso", "Default"),
        new("endo", "Endo", "Endo", "Default"),
        // The other models of the game's free Default pack (their default skins).
        new("ecto", "Ecto", "Ecto", "Default"),
        new("diver", "Diver", "Diver", "Default"),
        new("medusa", "Medusa", "Medusa", "Default"),
        new("pill", "Pill", "Pill", "Default"),
        new("pigeon", "Pigeon", "Pigeon", "Default"),
        new("pumpkin", "Pumpkin", "Pumpkin", "Default"),
        new("jack-o-lantern", "Jack-o-lantern", "JackOLantern", "Default"),
    ];
    public static AvatarProfile? Find(string? id) => All.FirstOrDefault(a => a.Id == id);

    // A plain, invulnerable body that never scores: the bridge drives it from the
    // player's pose stream, so movement values only need to be sane.
    public static IReadOnlyList<string> Lines(AvatarProfile a) =>
    [
        "Name=" + a.ProfileName, "MaxHealth=100000.0", "WeaponProfileNames=;;;;;;;", "AbilityProfileNames=;;;",
        "MinRespawnDelay=1.0", "MaxRespawnDelay=1.0", "StepUpHeight=75.0", "CrouchHeightModifier=0.75",
        "MovementType=Base", "MaxSpeed=1100.0", "MaxCrouchSpeed=400.0", "Acceleration=6000.0", "AirAcceleration=6000.0",
        "Friction=8.0", "BrakingFrictionFactor=2.0", "JumpVelocity=800.0", "Gravity=3.0", "AirControl=0.25",
        "CanCrouch=true", "AirJumpCount=0", "StrafeSpeedMult=1.0", "BackSpeedMult=1.0",
        "MainBBType=Cylindrical", "MainBBHeight=230.0", "MainBBRadius=45.0", "MainBBHasHead=true", "MainBBHeadRadius=30.0", "MainBBHeadOffset=0.0", "MainBBHide=true",
        "ProjBBType=Cylindrical", "ProjBBHeight=230.0", "ProjBBRadius=45.0", "ProjBBHasHead=true", "ProjBBHeadRadius=30.0", "ProjBBHeadOffset=0.0", "ProjBBHide=true",
        "HideWeapon=true", "CharacterModel=" + a.Model, "CharacterSkin=" + a.Skin, "MeshHitDetection=false",
        "DisableCharacterCollision=true", "InvinciblePlayer=true", "InvincibleBots=true",
    ];
    public static string File(AvatarProfile a) => string.Join("\n", Lines(a)) + "\n";
}

// Writes the avatar profiles into Saved/SaveGames/Characters so the bridge can load
// them for any scenario. Only AimMod's own files (same reserved names, recorded
// content) are created or refreshed; a user's profile with the same name is left alone.
static class AvatarFiles
{
    public static int Install(string charactersFolder)
    {
        var written = 0;
        try
        {
            Directory.CreateDirectory(charactersFolder);
            foreach (var a in AvatarProfiles.All)
            {
                var path = Path.Combine(charactersFolder, a.ProfileName + ".chr");
                var text = AvatarProfiles.File(a);
                if (File.Exists(path))
                {
                    var existing = File.ReadAllText(path, Encoding.UTF8);
                    // Ours if it's an earlier AimMod avatar profile (same name, AimMod's marker lines).
                    var ours = existing.StartsWith("Name=" + a.ProfileName + "\n", StringComparison.Ordinal) && existing.Contains("\nCharacterModel=" + a.Model + "\n", StringComparison.Ordinal) && existing.Contains("\nMaxHealth=100000.0\n", StringComparison.Ordinal);
                    if (!ours || existing == text) continue;
                }
                AtomicFile.WriteText(path, text);
                written++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return written;
    }
}
