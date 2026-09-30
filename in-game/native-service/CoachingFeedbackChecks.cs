using System.Text;
using System.Text.Json;
namespace AimMod.InGame;
static class CoachingFeedbackChecks
{
    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "aimmod-advice-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); int count = 0;
        void Check(bool ok, string name) { count++; if (!ok) throw new Exception(name); }
        try
        {
            var store = new CoachingFeedback(folder);
            AdviceState Apply(object input) => store.ApplyJson(JsonSerializer.SerializeToUtf8Bytes(input));
            var card = new { id = "global-stable", title = "Synthetic advice", body = "Measured evidence", tip = "One clear focus" };
            var observed = new { action = "observe", scope = "all", cards = new[] { card } };
            Check(Apply(observed).History.Single().Title == card.title, "Store preserves only supplied engine advice");
            var bytes = File.ReadAllBytes(Path.Combine(folder, "coaching-feedback.json"));
            Apply(observed);
            Check(store.Current.History.Length == 1 && bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "coaching-feedback.json"))), "Repeated redraw never duplicates advice or rewrites history");
            Apply(new { action = "feedback", scope = "all", id = card.id, feedback = "helpful" });
            Check(new CoachingFeedback(folder).Current.Feedback.Single().Feedback == "helpful", "Useful feedback survives restart with desktop vocabulary");
            Apply(new { action = "observe", scope = "scenario:synthetic", cards = new[] { card } });
            Apply(new { action = "feedback", scope = "scenario:synthetic", id = card.id, feedback = "not_for_me" });
            Check(store.Current.Feedback.Length == 2 && store.Current.Feedback.Single(x => x.Scope == "all").Feedback == "helpful", "Scenario hide never changes all-practice feedback");
            Apply(new { action = "feedback", scope = "scenario:synthetic", id = card.id, feedback = "none" });
            Check(store.Current.Feedback.Length == 1 && store.Current.History.Length == 2, "Restore clears hide without deleting prior advice");
            Apply(new { action = "observe", scope = "all", cards = new[] { card with { body = "New measured evidence" } } });
            Check(store.Current.History.Length == 3, "Changed recommendation evidence retains earlier advice");
            foreach (var invalid in new[] { "{}", "null", "{\"action\":\"feedback\",\"scope\":\"all\",\"id\":\"unseen\",\"feedback\":\"helpful\"}", "{\"action\":\"feedback\",\"scope\":\"all\",\"id\":\"global-stable\",\"feedback\":\"made_up\"}", "{\"action\":\"observe\",\"scope\":\"all\",\"scope\":\"all\",\"cards\":[]}" })
            {
                var prior = store.Current; bool rejected = false;
                try { store.ApplyJson(Encoding.UTF8.GetBytes(invalid)); } catch (JsonException) { rejected = true; }
                Check(rejected && store.Current == prior, "Invalid feedback cannot mutate private history");
            }
            for (int i = 0; i < 205; i++) Apply(new { action = "observe", scope = "all", cards = new[] { card with { body = "Synthetic evidence " + i } } });
            Check(store.Current.History.Length == 200 && new CoachingFeedback(folder).Current.History.Length == 200, "Advice history remains bounded and restarts intact");
            Check(!Directory.EnumerateFiles(folder, "*.tmp").Any(), "Atomic private writes leave no temporary files");
            Console.WriteLine($"PASS {count} coaching feedback checks");
        }
        finally { Directory.Delete(folder, true); }
    }
}
