using AimMod.Desktop.Privacy;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

public sealed class AimModDataStatementTests
{
    [Test]
    public void DocumentationMatchesTheStatementShownInSettings()
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "docs", "what-aimmod-reads.md")))
            directory = Path.GetDirectoryName(directory);
        Assert.That(directory, Is.Not.Null, "docs/what-aimmod-reads.md was not found");
        string[] lines = File.ReadAllLines(Path.Combine(directory!, "docs", "what-aimmod-reads.md"));

        var expected = AimModDataStatement.Sections.SelectMany(s => new[] { "## " + s.Title }.Concat(s.Points.Select(p => "- " + p))).ToArray();
        var documented = lines.Where(l => l.StartsWith("## ", StringComparison.Ordinal) || l.StartsWith("- ", StringComparison.Ordinal)).ToArray();
        Assert.That(documented, Is.EqualTo(expected));
    }
}
