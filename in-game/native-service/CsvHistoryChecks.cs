namespace AimMod.InGame;
static class CsvHistoryChecks
{
 public static void Run(){var count=0;void Check(bool value,string label){if(!value)throw new Exception(label);count++;}
 const string name="Synthetic - Challenge - 2026.01.02-00.00.30 Stats.csv";
 const string content="Score:,12.5\nHit Count:,3\nMiss Count:,1\nChallenge Start:,23:59:30.500\n";
 var run=CsvHistory.Parse(name,content);Check(run.Duration==59.5&&run.Score==12.5&&run.Accuracy==75,"CSV actual midnight duration and accuracy");
 Check(CsvHistory.Parse(name,"\"Score:\",\"-5\"\n\"Challenge Start:\",\"23:59:30.500\"").Score==-5,"Quoted summary and penalty score");
 Check(CsvHistory.Parse(name.Replace(" - Challenge - "," - Challenge Start - "),content).Scenario=="Synthetic","Challenge filename variants");
 foreach(var bad in new[]{content.Replace("12.5","NaN"),content.Replace("12.5","Infinity"),content.Replace("Score:","Other:"),content.Replace("23:59:30.500","26:00:00"),content+"Score:,100\n"}){try{CsvHistory.Parse(name,bad);throw new Exception("Accepted malformed summary");}catch(FormatException){count++;}}
 try{CsvHistory.Parse("Synthetic.csv",content);throw new Exception("Accepted unknown timestamp");}catch(FormatException){count++;}
 var folder=Path.Combine(Path.GetTempPath(),"aimmod-csv-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);var input=Path.Combine(folder,"input");Directory.CreateDirectory(input);
 try{
  File.WriteAllText(Path.Combine(input,name),content);File.WriteAllText(Path.Combine(input,"invalid.csv"),content);
  var store=new CsvHistory(folder);var first=store.Import(input,[]);Check(first is{Imported:1,Invalid:1,Scanned:2},"Only recognized CSV persisted");
  Check(store.Import(input,[]) is{Imported:0,Skipped:1},"Repeated import deduplicates");Check(new CsvHistory(folder).Runs.Single().Id==run.Id,"Imported history survives restart");
  var second=new CsvHistory(Path.Combine(folder,"second"));Check(second.Import(input,[run with{Id="desktop-existing"}]) is{Imported:0,Skipped:1},"Matches desktop history independent id");
  var third=new CsvHistory(Path.Combine(folder,"third"));Check(third.Import(input,[run with{Score=13}]).Imported==1,"Different actual score remains distinct");
  Check(!File.ReadAllText(Path.Combine(folder,"imported-history.json")).Contains(input,StringComparison.Ordinal),"No source paths persisted");
 }finally{Directory.Delete(folder,true);}
 Console.WriteLine(count+" CSV history checks passed.");
 }
}
