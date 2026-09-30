using System.Text.Json;
namespace AimMod.Desktop.Onboarding;

public sealed record UserSetupPreferences(bool Completed = false, bool StartupSound = true);
public sealed class UserSetupStore(string path)
{
    public UserSetupPreferences Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<UserSetupPreferences>(File.ReadAllText(path)) ?? new() : new(); }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException){return new();}
    }
    public void Save(UserSetupPreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path+".tmp",JsonSerializer.Serialize(preferences));File.Move(path+".tmp",path,true);
    }
}
