namespace AimMod.Desktop.LocalLibrary;

public sealed record LocalLibraryProgress(string State, int Completed = 0, int Total = 0);

public interface ILocalLibraryProgressSource
{
    LocalLibraryProgress? Progress { get; }
}
