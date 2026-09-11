using AimMod.Desktop.Updates;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

public sealed class NativeReleaseNotesTests
{
    [Test]
    public void UpcomingVersionShowsItsOwnNotesBeforeBundledHistory()
    {
        var state = new NativeUpdateState(NativeUpdateStage.Available, NativeUpdateChannel.Stable,
            "Update ready", "", "9.1.0", ReleaseNotes: "# AimMod 9.1.0\n- A new exercise.");
        var notes = NativeReleaseNotes.ForState(state);
        Assert.That(notes[0].Version, Is.EqualTo("9.1.0"));
        Assert.That(notes[0].Markdown, Does.Contain("A new exercise."));
        Assert.That(notes.Single(note => note.Version == "0.2.11").Markdown, Does.Contain("Double Time practice"));
    }

    [Test]
    public void MissingNotesNeverBorrowAnotherVersionsChanges()
    {
        var notes = NativeReleaseNotes.ForState(new(NativeUpdateStage.Available, NativeUpdateChannel.Stable, "", "", "9.1.0"));
        Assert.That(notes[0], Is.EqualTo(new NativeReleaseNotes("9.1.0", "")));
    }

    [Test]
    public void OfflineAndCurrentBuildsKeepBundledHistory()
    {
        var notes = NativeReleaseNotes.ForState(new(NativeUpdateStage.Unavailable, NativeUpdateChannel.Stable, "", ""));
        Assert.That(notes.Any(note => note.Version == "0.2.11"), Is.True);
        Assert.That(notes.Any(note => note.Version == "unreleased"), Is.False);
        notes = NativeReleaseNotes.ForState(new(NativeUpdateStage.Current, NativeUpdateChannel.Stable, "", "", "0.2.11"));
        Assert.That(notes[0].Markdown, Does.Contain("Double Time practice"));
    }

    [Test]
    public void NotesAreBoundedAndControlCharactersRemoved()
    {
        Assert.That(NativeReleaseNotes.Normalise("one\r\n\0two\tthree"), Is.EqualTo("one\ntwo\tthree"));
        Assert.That(NativeReleaseNotes.Normalise(new string('x', 100000)).Length, Is.EqualTo(NativeReleaseNotes.MaximumLength));
    }

    [TestCase("bad version")]
    [TestCase("9999999999999999999999999.0.0")]
    public void InvalidVersionDoesNotBreakReleaseHistory(string version)
    {
        Assert.That(NativeReleaseNotes.ForState(new(NativeUpdateStage.Available, NativeUpdateChannel.Stable, "", "", version))
            .All(entry => entry.Version != version), Is.True);
    }

    [Test]
    public void ChangingChannelDropsThePreviousTargetNotes()
    {
        var preview = new NativeUpdateState(NativeUpdateStage.Available, NativeUpdateChannel.Preview, "", "", "9.1.0-preview.1", ReleaseNotes: "Preview-only change.");
        Assert.That(NativeReleaseNotes.ForState(preview)[0].Markdown, Is.EqualTo("Preview-only change."));
        Assert.That(NativeReleaseNotes.ForState(NativeUpdateState.Initial(NativeUpdateChannel.Stable))
            .Any(entry => entry.Version == preview.Version), Is.False);
    }
}
