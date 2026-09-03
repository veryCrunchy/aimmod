using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class NativePpTargetsWorkspaceTests
{
    [Test]
    public void ConstructsWithUnavailableOnlineServices()
    {
        var source = new InMemoryLocalLibrarySource(Array.Empty<LocalBeatmapSet>(), Array.Empty<LocalReplay>());

        Assert.DoesNotThrow(() => _ = new NativePpTargetsWorkspace(source, () => null, () => null));
    }
}
