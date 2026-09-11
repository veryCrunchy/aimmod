using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics.Textures;
using osu.Game.Beatmaps;
using osu.Game.Skinning;

namespace AimMod.Desktop.Trainers;

// Uses the original objects and difficulty. The ruleset's rate mod changes
// audio, approach time and hit windows together; no generated map copies.
internal sealed class DtWorkingBeatmap(IBeatmap map, byte[] audioBytes, string extension, AudioManager audio, string sourceDirectory,
    osu.Framework.Graphics.Rendering.IRenderer renderer) : WorkingBeatmap(TrainerAudioIdentity.Attach(map.BeatmapInfo, audioBytes), audio)
{
    private readonly TrainerBackground? background = TrainerBackground.Read(sourceDirectory, map.Metadata.BackgroundFile) is { } bytes ? new(bytes, renderer) : null;
    private readonly ITrackStore tracks = audio.GetTrackStore(new TrainerAudio { Wave = audioBytes });
    private readonly string audioName = "dt-practice" + extension;
    protected override IBeatmap GetBeatmap() => map;
    protected override Track GetBeatmapTrack() => tracks.Get(audioName);
    // This store is disposed after the session. Never lend its track to the
    // restored menu map or a subsequent attempt, even when they use the same song.
    public override bool TryTransferTrack(WorkingBeatmap target) => false;
    public override Texture GetBackground() => background?.Texture!;
    protected override ISkin GetSkin() => null!;
    public override Stream GetStream(string storagePath)
    {
        string path = Path.GetFullPath(Path.Combine(sourceDirectory, storagePath));
        return path.StartsWith(sourceDirectory, StringComparison.OrdinalIgnoreCase) && File.Exists(path) && new FileInfo(path).Length <= 32 * 1024 * 1024
            ? File.OpenRead(path) : Stream.Null;
    }
    public void ReleaseAudio() { tracks.Dispose(); background?.Dispose(); }
}
