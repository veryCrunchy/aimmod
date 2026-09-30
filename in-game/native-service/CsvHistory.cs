using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;
namespace AimMod.InGame;
record CsvImportResult(int Imported,int Skipped,int Invalid,int Scanned);
sealed class CsvHistory
{
 readonly string file; readonly object gate=new(); volatile Run[] runs=[];
 const int MaxRuns=100000;
 public CsvHistory(string folder){file=Path.Combine(folder,"imported-history.json");try{if(File.Exists(file)&&new FileInfo(file).Length<=64*1024*1024)runs=(JsonSerializer.Deserialize<Run[]>(File.ReadAllText(file))??[]).Where(Stored).Take(MaxRuns).ToArray();}catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException){}}
 // The array is replaced, never mutated, so readers (the history refresh loop)
 // never wait for a long-running import that holds the write gate.
 public IReadOnlyList<Run> Runs=>runs;
 static bool Valid(Run r)=>r is not null&&!string.IsNullOrWhiteSpace(r.Scenario)&&r.Scenario.Length<=512&&double.IsFinite(r.Score)&&Math.Abs(r.Score)<1e12&&double.IsFinite(r.Duration)&&r.Duration>0&&r.Duration<=86400&&HubHistory.Date(r.Timestamp)!=DateTimeOffset.MinValue;
 // Persisted imports always carry a csv_ identity; anything else in the owned
 // file is damaged and would otherwise break identity-based history merging.
 static bool Stored(Run r)=>Valid(r)&&r.Id is {Length:>4 and <=128}&&r.Id.StartsWith("csv_",StringComparison.Ordinal);
 static string Key(Run r)=>r.Scenario+"\n"+r.Score.ToString("R",CultureInfo.InvariantCulture)+"\n"+HubHistory.Date(r.Timestamp).UtcTicks;
 // Local, absolute drive paths only. UNC and device paths (\\server\share,
 // \\?\, \\.\) are refused: enumerating a remote share would make Windows
 // authenticate to that host with the user's credentials.
 internal static bool AcceptableDirectory([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? directory)=>!string.IsNullOrWhiteSpace(directory)&&directory.Length<=1024&&Path.IsPathFullyQualified(directory)
  &&!directory.StartsWith(@"\\",StringComparison.Ordinal)&&!directory.StartsWith("//",StringComparison.Ordinal)&&!directory.Contains('\0')
  &&(!OperatingSystem.IsWindows()||directory.Length>=3&&char.IsAsciiLetter(directory[0])&&directory[1]==':'&&directory[2] is '\\' or '/');
 public CsvImportResult Import(string directory,IEnumerable<Run> existing)
 {
  lock(gate){
   var runs=this.runs;
   if(!AcceptableDirectory(directory)||!Directory.Exists(directory))throw new ArgumentException("Choose an existing absolute stats directory.");
   var root=new DirectoryInfo(directory);if((root.Attributes&FileAttributes.ReparsePoint)!=0)throw new ArgumentException("Choose the original stats directory.");
   var known=new HashSet<string>(existing.Where(Valid).Concat(runs).Select(Key),StringComparer.Ordinal);
   var additions=new List<Run>();int skipped=0,invalid=0,scanned=0;
   foreach(var path in Directory.EnumerateFiles(root.FullName,"*",System.IO.SearchOption.TopDirectoryOnly).Where(p=>Path.GetExtension(p).Equals(".csv",StringComparison.OrdinalIgnoreCase))){
    scanned++;
    try{
     var info=new FileInfo(path);if((info.Attributes&FileAttributes.ReparsePoint)!=0||info.Length>16*1024*1024||info.Length==0){invalid++;continue;}
     var run=Parse(Path.GetFileName(path),File.ReadAllText(path));
     if(!known.Add(Key(run))){skipped++;continue;}
     if(runs.Length+additions.Count>=MaxRuns){invalid++;continue;}
     additions.Add(run);
    }catch(Exception e)when(e is IOException or UnauthorizedAccessException or FormatException or MalformedLineException){invalid++;}
   }
   if(additions.Count>0){var merged=runs.Concat(additions).OrderByDescending(r=>HubHistory.Date(r.Timestamp)).ToArray();Directory.CreateDirectory(Path.GetDirectoryName(file)!);AtomicFile.WriteBytes(file,JsonSerializer.SerializeToUtf8Bytes(merged),durable:true);this.runs=merged;}
   return new(additions.Count,skipped,invalid,scanned);
  }
 }
 internal static Run Parse(string filename,string content)
 {
  var match=Regex.Match(filename,@"^(?<scenario>.+) - Challenge(?: Start| End)? - (?<stamp>\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2})(?: Stats)?\.csv$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
  if(!match.Success||!DateTime.TryParseExact(match.Groups["stamp"].Value,"yyyy.MM.dd-HH.mm.ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var end))throw new FormatException("Unrecognized stats filename.");
  var fields=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  using var parser=new TextFieldParser(new StringReader(content)){TextFieldType=FieldType.Delimited,HasFieldsEnclosedInQuotes=true,TrimWhiteSpace=true};parser.SetDelimiters(",");
  while(!parser.EndOfData){var row=parser.ReadFields();if(row is {Length:>=2}&&row[0].EndsWith(':')){var name=row[0].TrimEnd(':').Trim();if(fields.ContainsKey(name))throw new FormatException("Duplicate summary field.");fields[name]=row[1].Trim();}}
  double? Number(string key){return fields.TryGetValue(key,out var value)&&double.TryParse(value.TrimEnd('s'),NumberStyles.Float,CultureInfo.InvariantCulture,out var n)&&double.IsFinite(n)?n:null;}
  var score=Number("Score")??throw new FormatException("Missing score.");
  if(!fields.TryGetValue("Challenge Start",out var startText)||!TimeSpan.TryParseExact(startText,["hh\\:mm\\:ss","hh\\:mm\\:ss\\.FFFFFFF","hh\\.mm\\.ss"],CultureInfo.InvariantCulture,out var start)||start<TimeSpan.Zero||start>=TimeSpan.FromDays(1))throw new FormatException("Missing challenge start.");
  var duration=(end.TimeOfDay-start).TotalSeconds;if(duration<0)duration+=86400;
  var hit=Number("Hit Count");var miss=Number("Miss Count");double? accuracy=hit is >=0&&miss is >=0&&hit+miss>0?hit/(hit+miss)*100:null;
  var run=new Run("",match.Groups["scenario"].Value,score,accuracy,duration,Math.Max(0,Number("Kills")??0),Math.Max(0,Number("Damage Done")??0),match.Groups["stamp"].Value,null,null,null,null,false);
  if(!Valid(run))throw new FormatException("Invalid score or duration.");
  return run with{Id="csv_"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key(run))))[..32].ToLowerInvariant()};
 }
}

