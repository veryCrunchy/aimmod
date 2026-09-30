namespace AimMod.InGame;
static class ReplayKeyboardChecks
{
    internal static void Run()
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        var edges = new ReplayKeyboardEdges(); var keys = new HashSet<int>();
        var commands = new List<(string Action, double? Value)>(); int reads = 0;
        void Poll(long session = 1, bool eligible = true) => edges.Poll(session, eligible, key => { reads++; return keys.Contains(key); }, (a,v) => commands.Add((a,v)));
        Poll(0); Poll(1,false); Check(reads == 0, "Never reads keyboard outside focused active replay");
        keys.Add(0x20); Poll(); Poll(); Check(commands.Count == 0,"Held entry key ignored");
        keys.Clear(); Poll(); keys.Add(0x20); Poll(); Poll(); Check(commands.SequenceEqual(new[]{("toggle",(double?)null)}),"Space dispatches only once per press");
        keys.Clear(); Poll(); keys.Add(0x27); Poll(); Check(commands[^1] == ("seek-relative",5),"Right seeks five seconds");
        keys.Clear(); Poll(); keys.Add(0x25); keys.Add(0x10); Poll(); Check(commands[^1] == ("seek-relative",-1),"Shift Left seeks one second");
        var before=commands.Count; Poll(1,false); Poll(); Check(commands.Count==before,"Held key after focus regain ignored");
        keys.Clear(); Poll(); keys.Add(0x20); Poll(2); Check(commands.Count==before,"New replay load baselines held key");
        keys.Add(0x11); Poll(2); keys.Clear(); Poll(2); keys.Add(0x12); keys.Add(0x20); Poll(2); Check(commands.Count==before,"Ctrl/Alt combinations suppressed");
        keys.Clear(); Poll(2); keys.Add(0x25); keys.Add(0x27); Poll(2); Check(commands.Count==before,"Opposite arrows do not issue conflicting seeks");
        keys.Clear(); Poll(2); keys.Add(0x20); keys.Add(0x1B); Poll(2); Check(commands.Count==before+1 && commands[^1].Action=="close","Escape wins over simultaneous transport commands");
        Poll(2); Check(commands.Count==before+1,"Held Escape cannot repeat");
        var now=DateTime.UtcNow;
        string Ack(bool active=true,string id="synthetic",long revision=4)=>$"{{\"state\":\"ready\",\"mode\":\"main\",\"active\":{active.ToString().ToLowerInvariant()},\"replayId\":\"{id}\",\"revision\":{revision}}}";
        Check(ReplayKeyboard.IsAcknowledged(Ack(),now,now,"synthetic",4),"Active current renderer accepted");
        Check(!ReplayKeyboard.IsAcknowledged(Ack(false),now,now,"synthetic",4),"Preflight alone does not enable keys");
        Check(!ReplayKeyboard.IsAcknowledged(Ack(),now,now,"other",4),"Different replay rejected");
        Check(!ReplayKeyboard.IsAcknowledged(Ack(),now,now,"synthetic",5),"Previous load of same replay rejected");
        Check(!ReplayKeyboard.IsAcknowledged(Ack(),now.AddSeconds(-4),now,"synthetic",4),"Stale renderer rejected");
        Check(!ReplayKeyboard.IsAcknowledged("{}",now,now,"synthetic",4),"Missing acknowledgment fields fail closed");
        Check(ReplayKeyboard.IsGameExecutable("X:/Synthetic/FPSAimTrainer/Binaries/Win64/FPSAimTrainer-Win64-Shipping.exe"),"Exact game binary layout accepted");
        Check(!ReplayKeyboard.IsGameExecutable("X:/Synthetic/Other/FPSAimTrainer-Win64-Shipping.exe") && !ReplayKeyboard.IsGameExecutable("X:/Synthetic/FPSAimTrainer/Binaries/Win64/other.exe"),"Unrelated executables rejected");
        Console.WriteLine($"Replay keyboard checks passed ({checks}).");
    }
}
