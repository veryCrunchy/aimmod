using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class DiskCacheBudgetTests
{
    [Test]
    public void EvictsOldestByBytesAndKeepsUnrelatedFiles()
    {
        string root = Directory.CreateTempSubdirectory("aimmod-cache-budget-").FullName;
        try
        {
            string old = Path.Combine(root, new string('a', 64) + ".json");
            string fresh = Path.Combine(root, new string('b', 64) + ".json");
            string preserved = Path.Combine(root, new string('c', 64) + ".json");
            string unrelated = Path.Combine(root, "settings.json");
            foreach (string path in new[] { old, fresh, preserved, unrelated }) File.WriteAllBytes(path, new byte[32]);
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));
            DiskCacheBudget.Trim(root, ".json", 10, 64, TimeSpan.FromDays(7), preserved);
            Assert.That(File.Exists(old), Is.False);
            Assert.That(File.Exists(fresh), Is.True);
            Assert.That(File.Exists(preserved), Is.True);
            Assert.That(File.Exists(unrelated), Is.True);
            File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddDays(-8));
            DiskCacheBudget.Trim(root, ".json", 10, 64, TimeSpan.FromDays(7), preserved);
            Assert.That(File.Exists(fresh), Is.False);
        }
        finally { Directory.Delete(root, true); }
    }
}
