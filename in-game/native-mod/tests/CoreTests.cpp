// Engine-independent checks for AimModCore. Synthetic data only.
// Usage: aimmod_core_tests [--write-samples <dir>]
#include <aimmod/Formats.hpp>
#include <aimmod/Lifecycle.hpp>
#include <aimmod/ReplayWriter.hpp>
#include <aimmod/Settings.hpp>
#include <aimmod/Supervisor.hpp>

#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

using namespace aimmod;

static int g_checks = 0;
static int g_failures = 0;
#define CHECK(cond, message)                                                                                           \
    do                                                                                                                 \
    {                                                                                                                  \
        ++g_checks;                                                                                                    \
        if (!(cond))                                                                                                   \
        {                                                                                                              \
            ++g_failures;                                                                                              \
            std::printf("FAIL %s:%d %s\n", __FILE__, __LINE__, message);                                               \
        }                                                                                                              \
    } while (0)

static void Formats()
{
    CHECK(EscapeField("a%\tb\r\nc") == "a%25%09b%0D%0Ac", "journal escaping matches NativeRuns.Decode");
    JournalRun run{"1790000000-42-1", "Synthetic\tTarget", 321.5, 80.0, 60.0, 12.0, 1500.0, 1767225660};
    CHECK(FormatJournalLine(run) == "run\t1790000000-42-1\tSynthetic%09Target\t321.5\t80\t60\t12\t1500\t2026-01-01T00:01:00Z\n",
          "journal line layout");
    JournalRun sparse{"id-2", "S", 10, std::nullopt, 30.0, std::nullopt, std::nullopt, 0};
    CHECK(FormatJournalLine(sparse) == "run\tid-2\tS\t10\t\t30\t\t\t1970-01-01T00:00:00Z\n", "unknown fields stay empty");
    // Scores are native floats: print what the game shows, not float noise.
    CHECK(FormatNumber(static_cast<double>(1234.56f), 0) == "1234.56", "float scores print shortest");
    CHECK(FormatNumber(0.1, 0) == "0.1", "doubles print shortest");
    CHECK(FormatNumber(12345.678, 7) == "12345.68", "replay precision is %.7g");

    std::string json;
    AppendJsonString(json, "a\"b\\c\x01\xc3\xa9");
    CHECK(json == "\"a\\\"b\\\\c\\u0001\xc3\xa9\"", "json escaping keeps UTF-8");

    LiveSnapshot live;
    CHECK(FormatLiveOverlay(live) == "{\"version\":1,\"active\":false,\"paused\":false}", "idle live overlay");
    live.active = true;
    live.id = "1790000000-42-1";
    live.scenario = "Synthetic";
    live.scoreStatus = "available";
    live.score = 100.5;
    live.seconds = 12.25;
    live.shots = 10;
    live.hits = 8;
    live.remainingSeconds = 47.75;
    live.lastTimeToKillSeconds = std::nan("");
    CHECK(FormatLiveOverlay(live) ==
              "{\"version\":1,\"active\":true,\"paused\":false,\"scoreStatus\":\"available\",\"id\":\"1790000000-42-1\",\"scenario\":\"Synthetic\","
              "\"score\":100.5,\"seconds\":12.25,\"shots\":10,\"hits\":8,\"remainingSeconds\":47.75}",
          "live overlay field order matches Telemetry.lua");
    live.transient = true;
    live.scoreStatus = "Bad Status";
    live.id = "../x";
    CHECK(FormatLiveOverlay(live).find("scoreStatus") == std::string::npos && FormatLiveOverlay(live).find("\"id\"") == std::string::npos &&
              FormatLiveOverlay(live).find("\"transient\":true") != std::string::npos,
          "unsafe tokens are omitted");

    CHECK(IsValidAttemptId("1790000000-42-1") && !IsValidAttemptId("") && !IsValidAttemptId("a/b") && !IsValidAttemptId(std::string(101, 'a')),
          "attempt id pattern");
    CHECK(FormatReplayStatus("recording", 3, 1, "") == "{\"state\":\"recording\",\"frames\":3,\"inputEvents\":1,\"reason\":\"\"}\n",
          "replay status");
}

static ReplayFrame Frame(double t)
{
    ReplayFrame f;
    f.t = t;
    double camera[7] = {1, 2, 3, 4, 5, 6, 90};
    std::memcpy(f.camera, camera, sizeof(camera));
    return f;
}

static void Replay()
{
    ReplayRecording r;
    ReplayHeader h{"1790000000-42-1", "Synthetic", 1767225600, "Map_A", 1.0, "native"};
    r.Begin(h);
    std::string text = r.TakePending();
    CHECK(text ==
              "{\"kind\":\"header\",\"version\":1,\"id\":\"1790000000-42-1\",\"scenario\":\"Synthetic\",\"recordedAt\":\"2026-01-01T00:00:00Z\","
              "\"coordinates\":\"unreal-centimeters\",\"nominalHz\":60,\"mapName\":\"Map_A\",\"mapScale\":1,\"startEvent\":\"native\"}\n",
          "replay header");
    std::string profile = "Bot Profile";
    ReplayFrame f = Frame(0.0166667);
    f.actors.push_back({1, 10, 20, 30, 40, 90, 0.5, &profile, 0, 90, 0});
    f.stats.score = 5;
    f.stats.hits = 1;
    f.stats.hitTarget = 1;
    f.stats.hitTime = 0.0166668; // rounds above t; must be clamped
    f.stats.hitDelta = 1;
    CHECK(r.AddFrame(f) == ReplayRecording::Result::Ok, "first frame");
    CHECK(r.AddFrame(Frame(0.016666701)) == ReplayRecording::Result::Skipped, "a frame that does not advance after rounding is skipped");
    ReplayFrame bad = Frame(1);
    bad.camera[6] = 0;
    CHECK(r.AddFrame(bad) == ReplayRecording::Result::Skipped, "invalid FOV skipped");
    CHECK(r.AddInput(0.02, "AxisTurn", 0) == ReplayRecording::Result::Ok, "first zero kept");
    CHECK(r.AddInput(0.03, "AxisTurn", 0) == ReplayRecording::Result::Skipped, "redundant zero dropped");
    CHECK(r.AddInput(0.04, "AxisTurn", 1.5) == ReplayRecording::Result::Ok && r.AddInput(0.05, "AxisTurn", 1.5) == ReplayRecording::Result::Ok,
          "repeated deltas kept");
    CHECK(r.AddInput(0.06, "Teleport", 1) == ReplayRecording::Result::Skipped, "unknown actions rejected");
    CHECK(r.AddFrame(Frame(0.0333333)) == ReplayRecording::Result::Ok, "second frame");
    text = r.TakePending();
    CHECK(text.find("{\"kind\":\"frame\",\"t\":0.0166667,\"camera\":[1,2,3,4,5,6,90],\"actors\":[[1,10,20,30,40,90]],"
                    "\"health\":[{\"id\":1,\"percent\":0.5}],\"appearance\":[{\"id\":1,\"profile\":\"Bot Profile\",\"rotation\":[0,90,0]}],"
                    "\"stats\":{\"score\":5,\"hits\":1,\"hitTarget\":1,\"hitTime\":0.0166667,\"hitDelta\":1}}\n") == 0,
          "frame layout matches ReplayCapture.lua");
    std::string end = r.Finish("completed", 42.5);
    CHECK(end == "{\"kind\":\"end\",\"reason\":\"completed\",\"frames\":2,\"inputEvents\":3,\"score\":42.5}\n", "end record counts");

    ReplayLimits small;
    small.maxBytes = 4096 + 600;
    ReplayRecording bounded(small);
    bounded.Begin(h);
    int ok = 0;
    for (int i = 1; i < 100; ++i)
        if (bounded.AddFrame(Frame(i / 60.0)) == ReplayRecording::Result::Ok) ++ok;
    CHECK(bounded.full() && ok > 0 && ok < 99 && bounded.bytes() <= small.maxBytes - small.endReserve, "byte budget bounds the file");
    ReplayLimits frames;
    frames.maxFrames = 2;
    ReplayRecording limited(frames);
    limited.Begin(h);
    limited.AddFrame(Frame(0.1));
    limited.AddFrame(Frame(0.2));
    CHECK(limited.AddFrame(Frame(0.3)) == ReplayRecording::Result::Full, "frame limit");
}

static void Settings()
{
    auto s = ParseNativeSettings("AIMMOD_SETTINGS_1\nreplayRecordingEnabled\t0\nhubHistoryEnabled\t1\n");
    CHECK(s && !s->replayRecordingEnabled && s->hubHistoryEnabled, "settings parse");
    CHECK(ParseNativeSettings("AIMMOD_SETTINGS_1\r\nreplayRecordingEnabled\t1\r\nhubHistoryEnabled\t0\r\n").has_value(), "CRLF accepted");
    CHECK(!ParseNativeSettings("damaged") && !ParseNativeSettings("AIMMOD_SETTINGS_1\nreplayRecordingEnabled\t2\nhubHistoryEnabled\t1\n"),
          "malformed settings rejected");
    CHECK(FormatCoreActive("0.1.0", 1790000000, "telemetry,replay") == "AIMMOD_CORE_1\t0.1.0\t1790000000\ttelemetry,replay\n", "handshake");
    CHECK(IsReplayPlaybackHeader("AIMMOD_REPLAY_1\t12\t1") && !IsReplayPlaybackHeader("AIMMOD_REPLAY_1\t12\t0") &&
              !IsReplayPlaybackHeader("AIMMOD_REPLAY_\t1\t1"),
          "playback header");
}

static void Backoff()
{
    RestartBackoff b;
    b.Started(0);
    CHECK(b.Exited(10) == 12, "first restart after 2 s");
    CHECK(b.Exited(12) == 16, "doubling");
    for (int i = 0; i < 10; ++i) b.Exited(20);
    CHECK(b.currentDelay() == 60, "capped");
    b.Started(100);
    CHECK(b.Exited(500) == 502, "reset after stable uptime");
}

// Timeline helper for the lifecycle machine.
struct Run
{
    Lifecycle machine{"test"};
    PollSample s{};
    std::vector<LifecycleEvent> events;
    Run()
    {
        s.available = true;
        s.scenario = "Synthetic";
        s.scenarioKey = 1;
        s.lastScore = 50;
        s.lastTimeRemaining = 0;
    }
    void Poll()
    {
        for (auto& e : machine.Poll(s)) events.push_back(e);
        s.now += 0.05;
    }
    void Idle(int polls)
    {
        s.running = false;
        for (int i = 0; i < polls; ++i) Poll();
    }
    void Play(double from, double to, double total = 60)
    {
        s.running = true;
        for (double t = from; t <= to + 1e-9; t += 0.05)
        {
            s.elapsed = t;
            s.remaining = total - t < 0 ? 0 : total - t;
            Poll();
        }
    }
    int Count(LifecycleEvent::Kind kind) const
    {
        int n = 0;
        for (auto& e : events) n += e.kind == kind;
        return n;
    }
    const LifecycleEvent* Last(LifecycleEvent::Kind kind) const
    {
        for (auto it = events.rbegin(); it != events.rend(); ++it)
            if (it->kind == kind) return &*it;
        return nullptr;
    }
};

static void LifecycleChecks()
{
    using K = LifecycleEvent::Kind;
    {
        Run r;
        r.Idle(3);
        r.Play(0, 59.95);
        CHECK(r.Count(K::Started) == 1 && r.machine.active(), "timer start");
        r.s.running = false;
        r.s.remaining = 0;
        r.Poll();
        r.Poll();
        r.s.lastScore = 812.5;
        r.s.lastTimeRemaining = 0;
        r.Poll();
        auto* done = r.Last(K::Completed);
        CHECK(done && done->score == 812.5 && done->scoreSource == "stats-last-score", "completion takes StatsManager last score");
        CHECK(done && std::fabs(done->duration - 60) < 0.06, "duration includes the expired timer");
        CHECK(done && done->id == r.Last(K::Started)->id && !r.machine.active(), "same attempt id");
        r.Idle(5);
        CHECK(r.Count(K::Started) == 1, "a finished run never reopens itself");
    }
    {
        Run r;
        r.Idle(1);
        r.Play(0, 20);
        r.Idle(80);
        CHECK(r.Count(K::Canceled) == 1 && r.Last(K::Canceled)->reason == "quit" && r.Count(K::Completed) == 0, "quit is not a completion");
    }
    {
        Run r;
        r.Idle(1);
        r.Play(0, 20);
        r.Play(0.1, 5);
        CHECK(r.Count(K::Started) == 2 && r.Count(K::Canceled) == 1 && r.Last(K::Canceled)->reason == "restart", "restart splits attempts");
        CHECK(r.events[0].id != r.Last(K::Started)->id, "restart gets a new id");
    }
    {
        Run r;
        r.Idle(1);
        r.Play(0, 20);
        r.s.elapsed = 0; // one stale read
        r.Poll();
        r.Play(20.05, 21);
        CHECK(r.Count(K::Started) == 1 && r.Count(K::Canceled) == 0, "a single stale timer read does not split a run");
    }
    {
        Run r; // same score twice: the timer ran out, LastScore unchanged
        r.Idle(1);
        r.Play(0, 59.95);
        r.s.running = false;
        for (int i = 0; i < 70; ++i) r.Poll();
        auto* done = r.Last(K::Completed);
        CHECK(done && done->score == 50, "timer expiry completes even when the score repeats");
    }
    {
        Run r; // kill-count challenge ends early; only the stored time changes
        r.Idle(1);
        r.Play(0, 30);
        r.s.running = false;
        r.s.lastTimeRemaining = 30;
        r.Poll();
        CHECK(r.Count(K::Completed) == 0, "wait briefly for the last score to settle");
        for (int i = 0; i < 12; ++i) r.Poll();
        CHECK(r.Count(K::Completed) == 1 && r.Last(K::Completed)->score == 50, "early completion with a repeated score");
        CHECK(std::fabs(r.Last(K::Completed)->duration - 30) < 0.06, "early completion duration");
    }
    {
        Run r; // mod loaded in the middle of a run
        r.Play(10, 20);
        CHECK(r.Count(K::Started) == 0, "no attempt without observing its start");
        r.Idle(2);
        r.Play(0, 1);
        CHECK(r.Count(K::Started) == 1, "next run starts normally");
    }
    {
        Run r;
        r.Idle(1);
        r.Play(0, 10);
        r.s.running = false;
        r.s.paused = true;
        for (int i = 0; i < 200; ++i) r.Poll();
        CHECK(r.machine.active() && r.Count(K::Canceled) == 0, "pause does not end a run");
        r.s.paused = false;
        r.Play(10.05, 11);
        CHECK(r.Count(K::Started) == 1 && r.machine.active(), "run continues after pause");
    }
    {
        Run r;
        r.Idle(1);
        r.Play(0, 10);
        r.s.available = false;
        for (int i = 0; i < 120; ++i) r.Poll();
        CHECK(r.Last(K::Canceled) && r.Last(K::Canceled)->reason == "world-changed", "world teardown cancels");
    }
    {
        Run r;
        r.Idle(1);
        r.Play(0, 10);
        r.s.scenarioKey = 2;
        r.s.scenario = "Other";
        r.Poll();
        CHECK(r.Last(K::Canceled) && r.Last(K::Canceled)->reason == "scenario-changed", "scenario change cancels");
    }
    {
        Run r;
        r.Idle(1);
        r.machine.OnSignal(Signal::Start, r.s.now);
        r.Play(0, 1);
        CHECK(r.Last(K::Started) && r.Last(K::Started)->startEvent == "broadcast-start", "start signal labels the attempt");
        r.machine.OnSignal(Signal::Complete, r.s.now, 77.0);
        r.s.running = false;
        r.s.lastScore.reset();
        r.Poll();
        for (int i = 0; i < 12; ++i) r.Poll();
        CHECK(r.Last(K::Completed) && r.Last(K::Completed)->score == 77.0 && r.Last(K::Completed)->scoreSource == "hook",
              "completion hook score is used without a stats score");
    }
    {
        Run r; // no score source at all
        r.Idle(1);
        r.s.lastScore.reset();
        r.Play(0, 59.95);
        r.s.running = false;
        for (int i = 0; i < 70; ++i) r.Poll();
        CHECK(r.Last(K::Completed) && !r.Last(K::Completed)->score, "completion without a score is reported unscored");
    }
    {
        Run r; // the indicator value is a fallback only
        r.Idle(1);
        r.s.lastScore.reset();
        r.s.indicatorScore = 640;
        r.Play(0, 59.95);
        r.s.running = false;
        r.s.indicatorScore.reset();
        for (int i = 0; i < 70; ++i) r.Poll();
        CHECK(r.Last(K::Completed) && r.Last(K::Completed)->score == 640 && r.Last(K::Completed)->scoreSource == "indicator",
              "last indicator score is the fallback");
    }
    {
        Run r; // quick restart from the results screen: completion then a new run
        r.Idle(1);
        r.Play(0, 59.95);
        r.s.running = false;
        r.Poll();
        r.s.lastScore = 900;
        r.Play(0, 0.5);
        CHECK(r.Count(K::Completed) == 1 && r.Count(K::Started) == 2, "completion observed before the next run starts");
    }
}

static void WriteSamples(const std::filesystem::path& dir)
{
    std::filesystem::create_directories(dir / "replays");
    JournalRun run{"1790000000-42-1", "Synthetic target test", 321.5, 80.0, 60.0, 12.0, 1500.0, 1767225660};
    std::ofstream(dir / "completed.tsv", std::ios::binary) << FormatJournalLine(run);
    LiveSnapshot live;
    live.active = true;
    live.id = run.id;
    live.scenario = run.scenario;
    live.scoreStatus = "available";
    live.score = 100.5;
    live.seconds = 12.25;
    live.shots = 10;
    live.hits = 8;
    live.kills = 3;
    live.remainingSeconds = 47.75;
    live.lastTimeToKillSeconds = 1.5;
    std::ofstream(dir / "live-overlay.json", std::ios::binary) << FormatLiveOverlay(live);
    ReplayRecording r;
    r.Begin({run.id, run.scenario, 1767225600, "Map_A", 1.0, "native"});
    std::string profile = "Bot";
    for (int i = 1; i <= 3; ++i)
    {
        ReplayFrame f = Frame(i / 60.0);
        f.actors.push_back({1, 10.0 * i, 20, 30, 40, 90, 0.5, &profile, 0, 90, 0});
        f.stats.score = i;
        f.stats.seconds = i / 60.0;
        r.AddFrame(f);
        r.AddInput(i / 60.0, "AxisTurn", 0.5);
    }
    std::ofstream out(dir / "replays" / (run.id + ".amreplay"), std::ios::binary);
    out << r.TakePending() << r.Finish("completed", 321.5);
}

int main(int argc, char** argv)
{
    if (argc == 3 && std::strcmp(argv[1], "--write-samples") == 0)
    {
        WriteSamples(argv[2]);
        return 0;
    }
    Formats();
    Replay();
    Settings();
    Backoff();
    LifecycleChecks();
    std::printf("%d AimModCore checks, %d failed.\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
