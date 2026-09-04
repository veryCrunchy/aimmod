namespace AimMod.Desktop.Practice;

public sealed record PracticeMapArtifact(PracticeMapExportResult Export, string ArchivePath);

public sealed class PracticeMapArtifactBuilder
{
    private readonly IPracticeAudioSlicer audioSlicer;

    public PracticeMapArtifactBuilder()
        : this(new WindowsFfmpegAudioSlicer())
    {
    }

    internal PracticeMapArtifactBuilder(IPracticeAudioSlicer audioSlicer)
    {
        this.audioSlicer = audioSlicer ?? throw new ArgumentNullException(nameof(audioSlicer));
    }

    public async Task<PracticeMapArtifact> BuildAsync(
        PracticeSourceBeatmap source,
        PracticeMapPlan plan,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        string root = Path.GetFullPath(destinationDirectory);
        string files = Path.Combine(root, "map");
        string archive = Path.Combine(root, "AimMod practice.osz");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(files);
            PracticeMapExportResult export = await new PracticeMapExporter().ExportAsync(
                source,
                plan,
                files,
                audioSlicer,
                cancellationToken).ConfigureAwait(false);
            await PracticeMapPackageService.CreateAsync(export, archive, cancellationToken).ConfigureAwait(false);
            return new PracticeMapArtifact(export, archive);
        }
        catch
        {
            TryDelete(root);
            throw;
        }
    }

    internal static void TryDelete(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;

        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
