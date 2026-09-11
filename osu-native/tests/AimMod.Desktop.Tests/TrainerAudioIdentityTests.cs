using AimMod.Desktop.Trainers;
using NUnit.Framework;
using osu.Game.Beatmaps;

namespace AimMod.Desktop.Tests;

public sealed class TrainerAudioIdentityTests
{
    private static BeatmapInfo map() => new() { BeatmapSet = new BeatmapSetInfo(), Metadata = new BeatmapMetadata { AudioFile = "audio.mp3" } };

    [Test]
    public void MissingManifestsMustNotMatchPracticeAudio()
    {
        var startup = map();
        var first = map();
        // The upstream comparison treats two missing entries as the same audio.
        Assert.That(startup.AudioEquals(first), Is.True);
        TrainerAudioIdentity.Attach(first, [1, 2, 3]);
        Assert.That(startup.AudioEquals(first), Is.False);
        Assert.That(first.AudioEquals(startup), Is.False);
    }

    [Test]
    public void RealDeviceTrackCannotLeakThroughTheMenuIntoANewSession()
    {
        var menu = new TransferableMap(map());
        var first = new TransferableMap(map());
        var next = new TransferableMap(map());
        menu.LoadTrack(); first.LoadTrack(); next.LoadTrack();
        // Reproduce the upstream path masked by a dummy-device integration host.
        Assert.That(first.TryTransferTrack(menu), Is.True);
        Assert.That(menu.TryTransferTrack(next), Is.True);
        Assert.That(next.Track, Is.SameAs(first.Track));

        var isolated = new TransferableMap(TrainerAudioIdentity.Attach(map(), [1, 2, 3]));
        isolated.LoadTrack();
        Assert.That(menu.TryTransferTrack(isolated), Is.False);
        Assert.That(isolated.TryTransferTrack(menu), Is.False);
        Assert.That(isolated.Track, Is.Not.SameAs(menu.Track));
    }

    private sealed class DeviceTrack() : osu.Framework.Audio.Track.Track("test device")
    {
        public override bool IsDummyDevice => false;
        public override double CurrentTime => 0;
        public override bool IsRunning => false;
        public override bool Seek(double value) => true;
        public override Task<bool> SeekAsync(double value) => Task.FromResult(true);
        public override void Start() { }
        public override Task StartAsync() => Task.CompletedTask;
        public override void Stop() { }
        public override Task StopAsync() => Task.CompletedTask;
    }

    private sealed class TransferableMap(BeatmapInfo info) : WorkingBeatmap(info, null!)
    {
        protected override IBeatmap GetBeatmap() => new Beatmap { BeatmapInfo = BeatmapInfo };
        protected override osu.Framework.Audio.Track.Track GetBeatmapTrack() => new DeviceTrack();
        public override osu.Framework.Graphics.Textures.Texture GetBackground() => null!;
        protected override osu.Game.Skinning.ISkin GetSkin() => null!;
        public override Stream GetStream(string path) => Stream.Null;
    }

    [Test]
    public void SameFilenameWithDifferentMusicHasDifferentIdentity()
    {
        var first = TrainerAudioIdentity.Attach(map(), [1, 2, 3]);
        var second = TrainerAudioIdentity.Attach(map(), [4, 5, 6]);
        Assert.That(first.AudioEquals(second), Is.False);
        TrainerAudioIdentity.Attach(second, [1, 2, 3]);
        Assert.That(first.AudioEquals(second), Is.True);
        Assert.That(second.BeatmapSet!.Files.Count, Is.EqualTo(1));
    }
}
