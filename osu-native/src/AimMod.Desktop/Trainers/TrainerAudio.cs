using osu.Framework.IO.Stores;

namespace AimMod.Desktop.Trainers;

/// <summary>Render cues into a single PCM timeline to avoid frame-scheduled metronome jitter.</summary>
public sealed class TrainerAudio : IResourceStore<byte[]>
{
    public byte[] Wave { get; set; } = [];
    public byte[] Get(string name) => Wave;
    public Stream GetStream(string name) => new MemoryStream(Wave, false);

    public Task<byte[]> GetAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(Wave);
    public IEnumerable<string> GetAvailableResources() => [];
    public void Dispose() { Wave = []; }

    public static byte[] Asset(string name)
    {
        using var source = typeof(TrainerAudio).Assembly.GetManifestResourceStream($"AimMod.Resources.Audio.{name}")
            ?? throw new IOException("Training audio is unavailable.");
        using var buffer = new MemoryStream(); source.CopyTo(buffer); return buffer.ToArray();
    }

    public static short[] ReadCue(string name)
    {
        if (name is not ("pulse" or "glass" or "snap")) throw new ArgumentException("Unknown training cue.");
        using var reader = new BinaryReader(new MemoryStream(Asset($"trainer-cue-{name}.wav")));
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException();
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException();
        bool format = false;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            string id = new(reader.ReadChars(4)); int length = reader.ReadInt32();
            long end = reader.BaseStream.Position + length;
            if (length < 0 || end > reader.BaseStream.Length) throw new InvalidDataException();
            if (id == "fmt ")
            {
                if (length < 16 || reader.ReadInt16() != 1 || reader.ReadInt16() != 1 || reader.ReadInt32() != 44100) throw new InvalidDataException();
                reader.ReadInt32(); reader.ReadInt16();
                format = reader.ReadInt16() == 16;
            }
            if (id == "data" && format)
                return Enumerable.Range(0, length / 2).Select(_ => reader.ReadInt16()).ToArray();
            reader.BaseStream.Position = end + (length % 2);
        }
        throw new InvalidDataException("Training cue has no PCM audio.");
    }

    public static byte[] Render(TrainerSession session, IEnumerable<double>? targetTimes = null)
    {
        const int rate = 44100;
        var pcm = new short[(int)((session.EndMs + 600) * rate / 1000)];
        var cue = ReadCue(session.Settings.Cue);
        void click(double ms, double gain)
        {
            int start = (int)Math.Round(ms * rate / 1000);
            for (int i = 0; i < cue.Length && start + i < pcm.Length; i++)
                pcm[start + i] = (short)Math.Clamp(pcm[start + i] + cue[i] * gain, short.MinValue, short.MaxValue);
        }
        for (int i = 0; i < 4; i++) click(500 + session.BeatMs * i, .85);
        foreach (double time in targetTimes ?? session.Notes.Select(n => n.TimeMs)) click(time, 1);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + pcm.Length * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate);
        writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(pcm.Length * 2);
        foreach (short sample in pcm) writer.Write(sample);
        return stream.ToArray();
    }
}
