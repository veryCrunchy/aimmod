using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AimMod.Osu.Runtime.Contracts;
namespace AimMod.Desktop.Practice;
public sealed record PracticeSetArtifact(string ArchivePath, IReadOnlyList<PracticeMapPlan> Plans, IReadOnlyList<PracticeDifficultyIdentity> Difficulties);
public sealed class PracticeSetArtifactBuilder
{
    private readonly IPracticeAudioSlicer slicer;
    public PracticeSetArtifactBuilder() : this(new WindowsFfmpegAudioSlicer()) { }
    internal PracticeSetArtifactBuilder(IPracticeAudioSlicer slicer) => this.slicer=slicer;
    public static IReadOnlyList<PracticeMapPlan> Plan(PracticeSourceBeatmap source, IEnumerable<ReplayAnalysisResult> analyses, PracticeMapOptions options, bool all)
    {
        var evidence=analyses.ToArray();
        if (!all) return PracticeMapPlanner.CreatePlans(source,evidence,options);
        var selection=options with { DrillType=PracticeDrillType.Mixed, FirstObjectIndex=null, AllowPatternPractice=true, IncludeOverlappingSections=true };
        return PracticeMapPlanner.FindSections(source,evidence,selection)
            .Select(section => PracticeMapPlanner.CreateSectionPlan(source,section,selection)).ToArray();
    }
    public async Task<PracticeSetArtifact> BuildAsync(PracticeSourceBeatmap source, IReadOnlyList<PracticeMapPlan> plans,
        string destination, IProgress<string>? progress=null, CancellationToken token=default)
    {
        if (plans.Count==0) throw new InvalidOperationException("Choose at least one practice section.");
        string root=Path.GetFullPath(destination), archive=Path.Combine(root,"AimMod practice.osz");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Practice destination is not empty.");
        var identities=new List<PracticeDifficultyIdentity>(); var exports=new List<PracticeMapExportResult>(); var outputPlans=new List<PracticeMapPlan>();
        string title=source.Metadata.Title+" - AimMod practice "+DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH-mm-ss-fff");
        try
        {
            for (int i=0;i<plans.Count;i++)
            {
                token.ThrowIfCancellationRequested(); var original=plans[i];
                string name=$"{i+1:00} {PracticeMapPlanner.Label(original.DrillType)} - {TimeSpan.FromMilliseconds(original.SourceSection.SourceStartTimeMs):mm\\:ss} - {original.AudioSlice.PlaybackRate:P0}";
                var plan=original with { OutputVersion=name, OutputSetTitle=title, AudioSlice=original.AudioSlice with { OutputFilename=$"practice-{i+1:00}.ogg" } };
                progress?.Report($"Creating difficulty {i+1} of {plans.Count}: {PracticeMapPlanner.Label(plan.DrillType)}");
                var export=await new PracticeMapExporter().ExportAsync(source,plan,Path.Combine(root,"map"),slicer,token).ConfigureAwait(false);
                byte[] bytes=await File.ReadAllBytesAsync(export.BeatmapPath,token).ConfigureAwait(false);
                identities.Add(new(name,plan.DrillType,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant(),
                    plan.SourceSection.SourceStartTimeMs,plan.SourceSection.SourceEndTimeMs,plan.SourceSection.WeaknessScore>0));
                exports.Add(export); outputPlans.Add(plan);
            }
            using (var stream=new FileStream(archive,FileMode.CreateNew,FileAccess.Write))
            using (var zip=new ZipArchive(stream,ZipArchiveMode.Create,false,Encoding.UTF8))
                foreach(var export in exports)
                    foreach(string path in new[]{export.BeatmapPath,export.AudioPath})
                    {
                        token.ThrowIfCancellationRequested(); var entry=zip.CreateEntry(Path.GetFileName(path),CompressionLevel.NoCompression);
                        entry.ExternalAttributes=(int)FileAttributes.Normal;
                        await using var input=File.OpenRead(path); await using var output=entry.Open();
                        await input.CopyToAsync(output,token).ConfigureAwait(false);
                    }
            using(var zip=ZipFile.OpenRead(archive))
                if(zip.Entries.Count!=plans.Count*2 || zip.Entries.Any(e=>e.Length==0)) throw new InvalidDataException("The practice set is incomplete.");
            return new(archive,outputPlans,identities);
        }
        catch { PracticeMapArtifactBuilder.TryDelete(root); throw; }
    }
}
