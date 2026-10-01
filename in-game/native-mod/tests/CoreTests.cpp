// Engine-independent checks for AimModCore. Synthetic data only.
// Usage: aimmod_core_tests [--write-samples <dir>]
#include <aimmod/CosmeticsPreview.hpp>
#include <aimmod/Formats.hpp>
#include <aimmod/GameCommand.hpp>
#include <aimmod/GameStats.hpp>
#include <aimmod/PlaybackFrame.hpp>
#include <aimmod/Lifecycle.hpp>
#include <aimmod/MatchPlay.hpp>
#include <aimmod/ReplayV2.hpp>
#include <aimmod/ReplayWriter.hpp>
#include <aimmod/Settings.hpp>
#include <aimmod/Supervisor.hpp>
#include <aimmod/Water.hpp>

#include <algorithm>
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

// Synthetic attempt: 400 fps engine frames, mouse counts x 0.07, a view that
// follows the counts through 0.114586 deg/unit, one target with a gap.
static replay2::Capture SyntheticCapture(std::uint32_t compressionSeed = 0)
{
    replay2::Capture c;
    c.header = {"1790000000-42-2", "Synthetic target test", 1767225600, "Map_A", 1.0, "native"};
    c.reason = "completed";
    c.score = 321.5;
    c.profiles[1] = "Bot";
    double yaw = 10, pitch = 0;
    for (std::uint32_t f = 0; f < 1200; ++f)
    {
        c.frameTimes.push_back(0.001 + f / 400.0);
        const int dx = static_cast<int>((f * 7 + compressionSeed) % 11) - 5, dy = static_cast<int>(f % 5) - 2;
        if (dx) c.inputs.push_back({f, replay2::AxisTurn, static_cast<float>(dx * 0.07)});
        if (dy) c.inputs.push_back({f, replay2::AxisLookUp, static_cast<float>(dy * 0.07)});
        yaw += static_cast<double>(static_cast<float>(dx * 0.07)) * 0.114586;
        pitch += static_cast<double>(static_cast<float>(dy * 0.07)) * -0.114586;
        if (f == 600) c.inputs.push_back({f, 4, 1.0f}); // FirePressed
        if (f % 7 == 0)
        {
            c.camera.push_back({f, 0, 0, 0, pitch, yaw, 0, 103});
            if (f < 500 || f > 700)
                c.actors.push_back({f, 1, 1000.0 + f, -50, 30, 32, 90, 0.5, true, 0, 90, 0});
            c.stats.push_back({f, f * 0.5, std::floor(f / 10.0), std::floor(f / 20.0), 0.0, 0.0, f / 400.0});
        }
    }
    return c;
}

static void ReplayFormat2()
{
    using namespace replay2;
    Capture c = SyntheticCapture();
    EncodeReport report;
    EncodeOptions options;
    auto bytes = Encode(c, options, &report);
    CHECK(!bytes.empty() && report.fileBytes == bytes.size(), "format 2 encodes");
    CHECK(std::fabs(report.quantum - 0.07) < 1e-6, "mouse count quantum detected");
    CHECK(std::fabs(report.yawPerUnit - 0.114586) < 1e-4 && std::fabs(report.pitchPerUnit + 0.114586) < 1e-4, "rotation per unit fitted");
    CHECK(report.keyframeErrorMax < options.rotationTolerance + 1e-9, "drift stays within tolerance");
    auto d = Decode(bytes.data(), bytes.size());
    CHECK(d.has_value(), "format 2 decodes");
    if (!d) return;
    double worst = 0;
    for (const CameraSample& s : c.camera)
    {
        double r[3];
        d->Rotation(s.frame, r);
        worst = std::max({worst, std::fabs(r[0] - s.pitch), std::fabs(std::fmod(r[1] - s.yaw + 540.0, 360.0) - 180.0)});
    }
    CHECK(worst <= options.rotationTolerance + 1e-3, "decoded rotation matches every sample");
    CHECK(d->frameTimes.size() == c.frameTimes.size() && std::fabs(d->frameTimes[123] - c.frameTimes[123]) < 5e-5, "frame clock preserved");
    CHECK(d->inputs.size() == c.inputs.size(), "every input kept");
    CHECK(d->actors.size() == 1 && d->actors[0].profile == "Bot", "target and profile kept");
    int starts = 0;
    for (const auto& p : d->actors[0].points) starts += p.segmentStart;
    CHECK(starts == 2 && d->actors[0].points.size() < 10, "linear target compresses to its segments (gap kept)");
    CHECK(d->headerJson.find("\"version\":2") != std::string::npos && d->headerJson.find("\"reason\":\"completed\"") != std::string::npos &&
              d->headerJson.find("\"score\":321.5") != std::string::npos,
          "header carries the summary");
    std::vector<std::uint8_t> damaged = bytes;
    damaged.resize(damaged.size() - 3);
    CHECK(!Decode(damaged.data(), damaged.size()), "truncated file rejected");
    // Without inputs the encoder falls back to dense keyframes (still exact).
    Capture noInputs = c;
    noInputs.inputs.clear();
    auto dense = Encode(noInputs, options, &report);
    auto dd = Decode(dense.data(), dense.size());
    CHECK(dd && dd->keyframes.size() > c.camera.size() / 2, "missing input stream degrades to keyframes");
}

static void GameStatsChecks()
{
    const char* csv =
        "Kill #,Timestamp,Bot,Weapon,TTK,Shots,Hits,Accuracy,Damage Done,Damage Possible,Efficiency,Cheated,OverShots\r\n\r\n"
        "Weapon,Shots,Hits,Damage Done,Damage Possible,,Sens Scale\r\nTrack Master 100,5967,3511,3511.0,5967.0,\r\n\r\n"
        "Kills:,0\r\nDeaths:,0\r\nFight Time:,0.0\r\nDamage Done:,3511.0\r\nHit Count:,3511\r\nMiss Count:,2456\r\n"
        "Score:,10533.0\r\nScenario:,Synthetic, Track + Slowed\r\nChallenge Start:,01:11:40.178\r\n";
    auto s = ParseGameStats(csv);
    CHECK(s && s->score == 10533 && s->scenario == "Synthetic, Track + Slowed" && s->hits == 3511 && s->shots() == 5967 && s->kills == 0 &&
              s->damage == 3511,
          "stats CSV parsed");
    CHECK(s && s->challengeStartSeconds && std::fabs(*s->challengeStartSeconds - (3600 + 11 * 60 + 40.178)) < 1e-6, "challenge start parsed");
    CHECK(!ParseGameStats("Scenario:,x\n") && !ParseGameStats("Score:,nan\nScenario:,x\n"), "incomplete stats rejected");
    CHECK(IsChallengeStatsFile("Synthetic - Challenge - 2026.10.01-01.12.40 Stats.csv") && !IsChallengeStatsFile("notes.csv"), "stats file name");
    // Scenario names outside the ANSI code page (stars, CJK) in the stats folder.
    CHECK(IsChallengeStatsFile(std::wstring_view(L"Synthetic ★ 練習 - Challenge - 2026.10.01-01.12.40 Stats.csv")) &&
              !IsChallengeStatsFile(std::wstring_view(L"★ notes.csv")),
          "wide stats file names");
}

static void PlaybackChecks()
{
    const char* text = "AIMMOD_REPLAY_6\t7\t1\nmeta\tid\tS\tM\t1\ntime\t5\t60\t1\t1\ncamera\t0\t0\t0\t0\t0\t0\t90\n"
                       "actor\t1\t100\t0\t0\t10\t20\nclock\t1000000\t5\nmotion\t5\t0\t0\t0\t0\t170\t0\t90\n"
                       "motion\t5.1\t0\t0\t0\t1\t-170\t0\t90\nvelocity\t1\t100\t0\t0\n";
    auto f = ParsePlaybackFrame(text);
    CHECK(f && f->revision == 7 && f->playing && f->clockUnixMs == 1000000 && f->motion.size() == 2 && f->targets.size() == 1 && f->targets[0].moving,
          "protocol 6 frame parsed");
    if (!f) return;
    CHECK(std::fabs(PlaybackTime(*f, 1000050) - 5.05) < 1e-9, "playback time from the publication clock");
    CHECK(PlaybackTime(*f, 1009999) == 5.1, "clamped to the motion window");
    double pose[7];
    CHECK(CameraAt(*f, 5.05, pose) && std::fabs(pose[3] - 0.5) < 1e-9 && std::fabs(std::fabs(pose[4]) - 180) < 1e-9, "yaw interpolates across the wrap");
    CHECK(!ParsePlaybackFrame("AIMMOD_REPLAY_5\t1\t1\n") && !ParsePlaybackFrame("AIMMOD_REPLAY_6\t1\t1\nvelocity\t9\t0\t0\t0\n"),
          "other protocols and orphan rows rejected");
}

static void CommandChecks()
{
    auto parse = [](const char* text) { return ParseGameCommand(text); };
    auto load = parse("AIMMOD_CORE_COMMAND_1\nseq\t7\naction\tload-scenario\nscenario\tVT PGT Novice S5\n");
    CHECK(std::holds_alternative<GameCommand>(load) && std::get<GameCommand>(load).scenario == "VT PGT Novice S5" &&
              std::get<GameCommand>(load).action == GameCommand::Action::LoadScenario,
          "load command parsed");
    auto start = parse("AIMMOD_CORE_COMMAND_1\r\nseq\t8\r\naction\tstart-scenario\r\nscenario\tX\r\nmode\tfreeplay\r\ntimeScale\t0.5\r\ntargetSize\t1.5\r\ntargetSpeed\t2\r\nweapon\tTrack Master 100\r\n");
    CHECK(std::holds_alternative<GameCommand>(start) && std::get<GameCommand>(start).HasOverrides() && *std::get<GameCommand>(start).timeScale == 0.5,
          "freeplay start with overrides parsed");
    auto code = [&](const char* text) {
        auto r = parse(text);
        return std::holds_alternative<CommandError>(r) ? std::get<CommandError>(r).code : std::string("accepted");
    };
    CHECK(code("AIMMOD_CORE_COMMAND_1\nseq\t9\naction\tstart-scenario\nscenario\tX\nmode\tchallenge\ntargetSize\t2\n") == "overrides-freeplay-only",
          "challenge runs cannot be modified");
    CHECK(code("AIMMOD_CORE_COMMAND_1\nseq\t9\naction\tstart-scenario\nscenario\tX\nmode\tchallenge\n") == "accepted", "plain challenge start allowed");
    CHECK(code("AIMMOD_CORE_COMMAND_1\nseq\t9\naction\tstart-scenario\nscenario\tX\ntimeScale\t9\n") == "invalid-override", "out-of-range override rejected");
    CHECK(code("AIMMOD_CORE_COMMAND_1\nseq\t9\naction\tload-scenario\nscenario\tX\ntimeScale\t1\n") == "invalid-command", "overrides only on start");
    CHECK(code("AIMMOD_CORE_COMMAND_1\nseq\t9\naction\tdelete-scores\n") == "invalid-command", "unknown action rejected");
    CHECK(code("AIMMOD_CORE_COMMAND_1\nseq\t9\naction\tload-scenario\nscenario\tA\x01B\n") == "invalid-scenario", "control characters rejected");
    CHECK(code("AIMMOD_CORE_COMMAND_1\naction\treset-overrides\n") == "invalid-command", "sequence required");
    CHECK(code("AIMMOD_CORE_COMMAND_1\nseq\t9\naction\treset-overrides\nextra\t1\n") == "invalid-command", "unknown fields rejected");
    CHECK(code("AIMMOD_CORE_COMMAND_1\nseq\t9\naction\treset-overrides\n") == "accepted", "reset accepted");
    CHECK(code("AIMMOD_CORE_COMMAND_1\nseq\t9\naction\trefresh-scenarios\n") == "accepted", "refresh accepted");
    auto seeded = parse("AIMMOD_CORE_COMMAND_1\nseq\t11\naction\tstart-scenario\nscenario\tX\nmode\tfreeplay\nseed\t4294967295\n");
    CHECK(std::holds_alternative<GameCommand>(seeded) && std::get<GameCommand>(seeded).seed == 4294967295u, "freeplay seed parsed");
    CHECK(code("AIMMOD_CORE_COMMAND_1\nseq\t11\naction\tstart-scenario\nscenario\tX\nmode\tchallenge\nseed\t1\n") == "seed-not-allowed", "no seeds in ranked challenges");
    CHECK(code("AIMMOD_CORE_COMMAND_1\nseq\t11\naction\tstart-scenario\nscenario\tAimMod Match - Cata - ab93\nmode\tchallenge\nseed\t1\n") == "accepted",
          "generated match scenarios may be seeded as challenges");
    CHECK(code("AIMMOD_CORE_COMMAND_1\nseq\t11\naction\tstart-scenario\nscenario\tX\nseed\t4294967296\n") == "invalid-seed" &&
              code("AIMMOD_CORE_COMMAND_1\nseq\t11\naction\tload-scenario\nscenario\tX\nseed\t5\n") == "invalid-seed",
          "seed range and action checked");
    CHECK(SeedFor(7, 0) == SeedFor(7, 0) && SeedFor(7, 0) != SeedFor(7, 1) && SeedFor(7, 1) != SeedFor(8, 1), "per-event seeds reproducible and distinct");
    // A freeplay seed must stop when a challenge starts in the same scenario.
    CHECK(SeedAllowed("Synthetic Clicking", false) && !SeedAllowed("Synthetic Clicking", true) && SeedAllowed("AimMod Match - Cata - ab93", true),
          "freeplay seeds never drive a ranked challenge");
    const char* thumb = "AIMMOD_CORE_COMMAND_1\nseq\t10\naction\tcapture-thumbnail\nscenario\tAimMod - Dust2 (CSGO) - CS Movement\nwidth\t1920\nheight\t1080\n"
                        "out\tdust2 thumb.png\nview1\t100,-20.5,300,-10,45,90\nview2\t0,0,0,0,180,70\n";
    auto t = parse(thumb);
    CHECK(std::holds_alternative<GameCommand>(t) && std::get<GameCommand>(t).views.size() == 2 && std::get<GameCommand>(t).views[0].y == -20.5 &&
              std::get<GameCommand>(t).width == 1920 && ThumbnailFileName("dust2 thumb.png", 1, 2) == "dust2 thumb-2.png",
          "thumbnail capture parsed");
    auto thumbCode = [&](const char* replace, const char* with) {
        std::string text = thumb;
        text.replace(text.find(replace), std::strlen(replace), with);
        return code(text.c_str());
    };
    CHECK(thumbCode("out\tdust2 thumb.png", "out\t..\\x.png") == "invalid-thumbnail" && thumbCode("dust2 thumb.png", "thumb.jpg") == "invalid-thumbnail",
          "thumbnail output must be a plain .png name");
    CHECK(thumbCode("width\t1920", "width\t7680") == "invalid-thumbnail" && thumbCode("height\t1080", "height\t10.5") == "invalid-thumbnail",
          "thumbnail resolution bounded");
    CHECK(thumbCode("view2\t0,0,0,0,180,70", "view3\t0,0,0,0,180,70") == "invalid-thumbnail" && thumbCode("-10,45,90", "-10,45") == "invalid-thumbnail" &&
              thumbCode("-10,45,90", "-95,45,90") == "invalid-thumbnail",
          "views validated");
    CHECK(code("AIMMOD_CORE_COMMAND_1\nseq\t9\naction\tload-scenario\nscenario\tX\nout\tx.png\n") == "invalid-command", "thumbnail fields only for captures");
    CHECK(code("AIMMOD_CORE_COMMAND_1\nseq\t9\naction\trefresh-scenarios\nscenario\tX\n") == "accepted", "refresh ignores a scenario");
    CHECK(IsScenarioFileName("AimMod Match - Cata IC Long Strafes - Timed - ab93b242") && IsScenarioFileName("Track + Slowed (2)"),
          "match scenario names are file names");
    CHECK(!IsScenarioFileName("..\\..\\Win64\\x") && !IsScenarioFileName("a/b") && !IsScenarioFileName("C:x") && !IsScenarioFileName("x.") &&
              !IsScenarioFileName("..") && !IsScenarioFileName("a\"b"),
          "paths and reserved names never reach the file system");
    CHECK(FormatCommandResult(7, "error", "challenge-active", "Finish\tit") == "AIMMOD_CORE_RESULT_1\t7\terror\tchallenge-active\tFinish%09it\n", "result line");
}

static void ClipChecks()
{
    using namespace replay2;
    Capture c = SyntheticCapture();
    c.marks = {800};
    Capture clip = Slice(c, c.frameTimes[800] - 0.5, c.frameTimes[800] + 0.25, "1790000000-42-2-clip1");
    CHECK(clip.frameTimes.size() > 250 && clip.frameTimes.front() == 0 && clip.clipOf == "1790000000-42-2" &&
              std::fabs(clip.clipStart - (c.frameTimes[800] - 0.5)) < 0.003 && !clip.score,
          "clip slices and re-bases the run");
    EncodeReport full, small;
    auto fullBytes = Encode(c, {}, &full);
    auto clipBytes = Encode(clip, {}, &small);
    auto decoded = Decode(clipBytes.data(), clipBytes.size());
    CHECK(!clipBytes.empty() && clipBytes.size() < fullBytes.size() && decoded && decoded->headerJson.find("\"clipOf\":\"1790000000-42-2\"") != std::string::npos,
          "clip encodes on its own");
    if (decoded)
    {
        double r[3];
        decoded->Rotation(0, r);
        const CameraSample* source = nullptr;
        for (const CameraSample& s : c.camera)
            if (s.frame >= 600) { source = &s; break; }
        CHECK(source != nullptr, "source sample");
    }
    auto marked = Decode(fullBytes.data(), fullBytes.size());
    CHECK(marked && marked->headerJson.find("\"marks\":[") != std::string::npos, "marks in the replay header");
    auto settings = ParseClipSettings("AIMMOD_CLIPS_1\nkey\tF9\nbefore\t5\nafter\t1\n");
    CHECK(settings && settings->virtualKey == 0x78 && settings->before == 5 && settings->after == 1, "clip settings parsed");
    CHECK(!ParseClipSettings("AIMMOD_CLIPS_1\nkey\tW\n") && !ParseClipSettings("AIMMOD_CLIPS_1\nbefore\t99\n") && VirtualKeyFromName("F8") == 0x77,
          "gameplay keys and long clips rejected");
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
        Run r; // live test: quit after 2 s while the stats manager resets to 0
        r.s.lastScore = 3773;
        r.Idle(1);
        r.Play(0, 2.1);
        r.s.running = false;
        r.s.lastScore = 0;
        r.s.lastTimeRemaining = 0;
        for (int i = 0; i < 120; ++i) r.Poll();
        CHECK(r.Count(K::Completed) == 0 && r.Last(K::Canceled) && r.Last(K::Canceled)->reason == "quit", "a reset stats score is not a completion");
    }
    {
        Run r; // live test: GetLastScore reads 0 at completion, the live score is 10533
        r.Idle(1);
        r.s.indicatorScore = 10533;
        r.Play(0, 59.95);
        r.s.running = false;
        r.s.lastScore = 0;
        for (int i = 0; i < 120; ++i) r.Poll();
        CHECK(r.Last(K::Completed) && r.Last(K::Completed)->score == 10533 && r.Last(K::Completed)->scoreSource == "indicator",
              "a zero never replaces the live score");
    }
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
        for (int i = 0; i < 110; ++i) r.Poll();
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
        r.Idle(120);
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
        for (int i = 0; i < 110; ++i) r.Poll();
        auto* done = r.Last(K::Completed);
        CHECK(done && done->score == 50, "timer expiry completes even when the score repeats");
    }
    {
        Run r; // kill-count challenge ends early: only the game's stats record proves it
        r.Idle(1);
        r.Play(0, 30);
        r.s.running = false;
        r.s.lastTimeRemaining = 30;
        for (int i = 0; i < 20; ++i) r.Poll();
        CHECK(r.Count(K::Completed) == 0, "stored values alone are not completion evidence");
        r.machine.OnSignal(Signal::Complete, r.s.now, 4321.0);
        r.Poll();
        CHECK(r.Count(K::Completed) == 1 && r.Last(K::Completed)->score == 4321.0 && r.Last(K::Completed)->scoreSource == "game-stats",
              "early completion takes the game's record");
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
        CHECK(r.Last(K::Completed) && r.Last(K::Completed)->score == 77.0 && r.Last(K::Completed)->scoreSource == "game-stats",
              "the completion record's score wins");
    }
    {
        Run r; // no score source at all
        r.Idle(1);
        r.s.lastScore.reset();
        r.Play(0, 59.95);
        r.s.running = false;
        for (int i = 0; i < 110; ++i) r.Poll();
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
        for (int i = 0; i < 110; ++i) r.Poll();
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
    auto compact = replay2::Encode(SyntheticCapture(), {});
    std::ofstream(dir / "replays" / "1790000000-42-2.amreplay", std::ios::binary).write(reinterpret_cast<const char*>(compact.data()), static_cast<std::streamsize>(compact.size()));
}

#include "CosmeticsTests.inl"

static void EndRunChecks()
{
    auto parse = [](const std::string& body) { return ParseGameCommand("AIMMOD_CORE_COMMAND_1\nseq\t9\n" + body); };
    auto stop = parse("action\tend-run\nscenario\tAimMod Match - Synthetic - 0a1b2c3d\n");
    CHECK(std::holds_alternative<GameCommand>(stop) && std::get<GameCommand>(stop).action == GameCommand::Action::EndRun &&
              !std::get<GameCommand>(stop).reset && std::string(ActionName(GameCommand::Action::EndRun)) == "end-run",
          "end-run stops by default");
    auto reset = parse("action\tend-run\nscenario\tAimMod Match - Synthetic - 0a1b2c3d\nthen\treset\n");
    CHECK(std::holds_alternative<GameCommand>(reset) && std::get<GameCommand>(reset).reset, "end-run reset");
    auto other = parse("action\tend-run\nscenario\tVT Pasu\n");
    CHECK(std::holds_alternative<CommandError>(other) && std::get<CommandError>(other).code == "not-a-match", "end-run only for match scenarios");
    CHECK(std::holds_alternative<CommandError>(parse("action\tend-run\nscenario\tAimMod Match - X - 0a1b2c3d\nthen\tquit\n")) &&
              std::holds_alternative<CommandError>(parse("action\tend-run\nscenario\tAimMod Match - X - 0a1b2c3d\nmode\tchallenge\n")) &&
              std::holds_alternative<CommandError>(parse("action\tload-scenario\nscenario\tX\nthen\treset\n")) &&
              std::holds_alternative<CommandError>(parse("action\tend-run\n")),
          "end-run fields validated");
    auto ensure = parse("action\tensure-map\nscenario\tAimMod Match - Synthetic - 0a1b2c3d\n");
    CHECK(std::holds_alternative<GameCommand>(ensure) && std::get<GameCommand>(ensure).action == GameCommand::Action::EnsureMap &&
              std::string(ActionName(GameCommand::Action::EnsureMap)) == "ensure-map",
          "ensure-map parses for a match scenario");
    CHECK(std::holds_alternative<GameCommand>(parse("action\tensure-map\nscenario\tAimMod - aim_synthetic (CSS) - CS Movement\n")) &&
              std::holds_alternative<GameCommand>(parse("action\tensure-map\nscenario\tAimMod Probe 03 - Synthetic\n")),
          "ensure-map parses for AimMod's map ports and probes");
    {
        auto other = parse("action\tensure-map\nscenario\tVT Pasu\n");
        CHECK(std::holds_alternative<CommandError>(other) && std::get<CommandError>(other).code == "not-a-match", "ensure-map refuses other scenarios");
    }
    CHECK(std::holds_alternative<CommandError>(parse("action\tensure-map\n")) &&
              std::holds_alternative<CommandError>(parse("action\tensure-map\nscenario\tAimMod Match - X\nmode\tchallenge\n")) &&
              std::holds_alternative<CommandError>(parse("action\tensure-map\nscenario\tAimMod Match - X\nmapScale\t2\n")) &&
              std::holds_alternative<CommandError>(parse("action\tensure-map\nscenario\tAimMod Match - ..\\x\n")),
          "ensure-map refuses a missing name, modes, overrides and path characters");
    CHECK(MapFixAllowed("AimMod Match - A - 0a1b2c3d") && !MapFixAllowed("VT Pasu") && !MapFixAllowed("aimmod match - a") && !MapFixAllowed("AimMod Match - a/b"),
          "only AimMod's own scenario names allow a map rebuild");
    CHECK(SameMapLoaded("AIMMOD_aim_map_css.json", 4.0, "aimmod_aim_map_css.json", 4.0) && !SameMapLoaded("defaultscenario.map", 4.0, "aimmod_aim_map_css.json", 4.0) &&
              !SameMapLoaded("aimmod_aim_map_css.json", 1.0, "aimmod_aim_map_css.json", 4.0) && !SameMapLoaded("", 1.0, "", 1.0) &&
              SameMapLoaded("x.map", 3.80001, "x.map", 3.8),
          "the same-map test ignores case and needs the same scale");
    auto quit = parse("action\tquit-run\n");
    CHECK(std::holds_alternative<GameCommand>(quit) && std::get<GameCommand>(quit).action == GameCommand::Action::QuitRun &&
              std::string(ActionName(GameCommand::Action::QuitRun)) == "quit-run",
          "quit-run parses");
    CHECK(std::holds_alternative<CommandError>(parse("action\tquit-run\nscenario\tX\n")) &&
              std::holds_alternative<CommandError>(parse("action\tquit-run\nmode\tfreeplay\n")) &&
              std::holds_alternative<CommandError>(parse("action\tquit-run\nthen\treset\n")),
          "quit-run takes no fields");

    // Every override reset must undo a weapon change (the scenario's own loadout comes back).
    auto weapon = parse("action\tstart-scenario\nscenario\tX\nmode\tfreeplay\nweapon\tSynthetic Rifle\n");
    CHECK(std::holds_alternative<GameCommand>(weapon) && RestoreFor(std::get<GameCommand>(weapon)).weapon &&
              !RestoreFor(std::get<GameCommand>(weapon)).timeDilation,
          "a weapon override is restored on reset");
    auto scaled = parse("action\tstart-scenario\nscenario\tX\ntimeScale\t0.5\ntargetSize\t2\nmapScale\t1.5\n");
    CHECK(std::holds_alternative<GameCommand>(scaled) && RestoreFor(std::get<GameCommand>(scaled)).timeDilation &&
              RestoreFor(std::get<GameCommand>(scaled)).adaptive && RestoreFor(std::get<GameCommand>(scaled)).mapScale &&
              !RestoreFor(std::get<GameCommand>(scaled)).weapon,
          "each requested override has a restore");
    auto plain = parse("action\tstart-scenario\nscenario\tX\n");
    CHECK(std::holds_alternative<GameCommand>(plain) && !RestoreFor(std::get<GameCommand>(plain)).Any(), "no overrides, nothing to restore");
}

static void MatchPlayChecks()
{
    const std::string good = "AIMMOD_PLAYSTATE_1\t7\nmatch\tAimMod Match - Synthetic - 1\nhealth\t62.5\t100\nalive\t1\nrespawnAt\t0\nprotected\t0\n"
                             "hit\t3\tmember-2\t37.5\t1\t0\t-1\t0\n";
    auto s = ParsePlayState(good);
    CHECK(s && s->sequence == 7 && s->scenario == "AimMod Match - Synthetic - 1" && s->health == 62.5 && s->maxHealth == 100 && s->alive &&
              !s->spawnProtected && s->lastHit && s->lastHit->sequence == 3 && s->lastHit->attacker == "member-2" && s->lastHit->damage == 37.5 &&
              s->lastHit->headshot && s->lastHit->direction[1] == -1,
          "play state parses");
    auto dead = ParsePlayState("AIMMOD_PLAYSTATE_1\t8\r\nmatch\tAimMod Match - X\r\nhealth\t0\t100\r\nalive\t0\r\nrespawnAt\t1790000003000\r\nprotected\t1\r\n");
    CHECK(dead && !dead->alive && dead->respawnAtMs == 1790000003000 && dead->spawnProtected && !dead->lastHit, "dead state with CRLF, no hit");
    CHECK(!ParsePlayState(""), "empty play state");
    CHECK(!ParsePlayState("AIMMOD_PLAYSTATE_2\t1\nmatch\tA\nhealth\t1\t1\n"), "unknown version");
    CHECK(!ParsePlayState("AIMMOD_PLAYSTATE_1\t1\nhealth\t1\t1\n"), "match name required");
    CHECK(!ParsePlayState("AIMMOD_PLAYSTATE_1\t1\nmatch\tA\n"), "health required");
    CHECK(!ParsePlayState("AIMMOD_PLAYSTATE_1\t1\nmatch\tA\nhealth\t120\t100\n"), "health above max");
    CHECK(!ParsePlayState("AIMMOD_PLAYSTATE_1\t1\nmatch\tA\nhealth\tnan\t100\n"), "non-numeric health");
    CHECK(!ParsePlayState("AIMMOD_PLAYSTATE_1\t1\nmatch\tA\nhealth\t1\t1\nalive\tyes\n"), "flags are 0/1");
    CHECK(!ParsePlayState("AIMMOD_PLAYSTATE_1\t1\nmatch\tA\nhealth\t1\t1\nhit\t1\tbad id!\t5\t0\t0\t0\t1\n"), "member ids are plain");
    CHECK(!ParsePlayState("AIMMOD_PLAYSTATE_1\t1\nmatch\tA\nhealth\t1\t1\nteleport\t0\t0\t0\n"), "unknown rows reject the file");

    // Capsule at the origin, radius 30, half height 90 (segment +-60).
    const double c[3] = {0, 0, 0};
    const double o[3] = {-500, 0, 0}, d[3] = {1, 0, 0};
    auto t = RayCapsule(o, d, c, 30, 90);
    CHECK(t && std::fabs(*t - 470) < 1e-9, "ray meets the cylinder");
    const double high[3] = {-500, 0, 80};
    auto top = RayCapsule(high, d, c, 30, 90);
    const double hitPoint[3] = {-500 + *top, 0, 80};
    CHECK(top && *top > 470 && IsHeadHit(hitPoint, c, 90), "ray meets the top sphere as a head hit");
    const double miss[3] = {-500, 0, 100};
    CHECK(!RayCapsule(miss, d, c, 30, 90), "ray above the capsule misses");
    const double back[3] = {-1, 0, 0};
    CHECK(!RayCapsule(o, back, c, 30, 90), "ray pointing away misses");
    const double down[3] = {0, 0, -1}, above[3] = {0, 0, 500};
    auto vertical = RayCapsule(above, down, c, 30, 90);
    CHECK(vertical && std::fabs(*vertical - 410) < 1e-9, "vertical ray meets the cap");
    const double body[3] = {0, 0, 0};
    CHECK(!IsHeadHit(body, c, 90), "centre is a body hit");

    auto round = ParseRoundState("AIMMOD_ROUND_1\t12\nmatch\tAimMod Match - Arena - 0a1b2c3d\nspawn\tr3-t\t100.5\t-20\t30\t90\n"
                                 "phase\tfreeze\t1\t1\t1790000005000\nloadout\tAK-47 Rifle\t-\t100\t1\t0\n");
    CHECK(round && round->sequence == 12 && round->spawn && round->spawn->id == "r3-t" && round->spawn->x == 100.5 && round->spawn->yaw == 90 &&
              round->phase && round->phase->name == "freeze" && round->phase->frozen && round->phase->buy && round->phase->endsMs == 1790000005000 &&
              round->loadout && round->loadout->primary == "AK-47 Rifle" && round->loadout->pistol == "-" && round->loadout->helmet && !round->loadout->kit,
          "round state parses");
    auto dmRound = ParseRoundState("AIMMOD_ROUND_1\t1\r\nmatch\tAimMod Match - X - 0a1b2c3d\r\nspawn\t7\t0\t0\t0\t0\r\n");
    CHECK(dmRound && dmRound->spawn && !dmRound->phase && !dmRound->loadout, "deathmatch round state: spawn only");
    CHECK(!ParseRoundState("AIMMOD_ROUND_1\t1\nspawn\t7\t0\t0\t0\t0\n") && !ParseRoundState("AIMMOD_ROUND_2\t1\nmatch\tA\n") &&
              !ParseRoundState("AIMMOD_ROUND_1\t1\nmatch\tA\nphase\twarmup\t0\t0\t0\n") &&
              !ParseRoundState("AIMMOD_ROUND_1\t1\nmatch\tA\nspawn\tbad id\t0\t0\t0\t0\n") &&
              !ParseRoundState("AIMMOD_ROUND_1\t1\nmatch\tA\nloadout\t\t-\t0\t0\t0\n") &&
              !ParseRoundState("AIMMOD_ROUND_1\t1\nmatch\tA\nspawn\t1\t0\t0\t0\t0\nspawn\t2\t0\t0\t0\t0\n") &&
              !ParseRoundState("AIMMOD_ROUND_1\t1\nmatch\tA\nteleport\t0\n"),
          "malformed round states are rejected whole");

    ShotRecord shot{1790000000123, 4, {1, 2, 3}, {1, 0, 0}, 1, 9, true, false};
    CHECK(FormatShot(shot) == "shot\t1790000000123\t4\t1\t2\t3\t1\t0\t0\t1\t9\t1\t0\n", "shot row layout");
}

// Cosmetics page preview frames: a synthetic capture pair. The "character" is
// a dark, off-centre figure low in a black frame, like a badly lit capture.
static PreviewPixels Solid(int w, int h, std::uint8_t r, std::uint8_t g, std::uint8_t b)
{
    PreviewPixels p;
    p.width = w;
    p.height = h;
    p.rgba.resize(static_cast<std::size_t>(w) * h * 4);
    for (std::size_t i = 0; i < p.rgba.size(); i += 4) p.rgba[i] = r, p.rgba[i + 1] = g, p.rgba[i + 2] = b, p.rgba[i + 3] = 255;
    return p;
}
static void Paint(PreviewPixels& p, int x0, int y0, int x1, int y1, std::uint8_t r, std::uint8_t g, std::uint8_t b)
{
    for (int y = y0; y < y1; ++y)
        for (int x = x0; x < x1; ++x)
        {
            std::uint8_t* o = &p.rgba[(static_cast<std::size_t>(y) * p.width + x) * 4];
            o[0] = r, o[1] = g, o[2] = b;
        }
}
static const std::uint8_t* PixelAt(const PreviewPixels& p, int x, int y) { return &p.rgba[(static_cast<std::size_t>(y) * p.width + x) * 4]; }

static void PreviewComposeChecks()
{
    const int capture = PreviewSize * PreviewSupersample;
    // Background normals decode the cleared G-buffer to one constant value.
    PreviewPixels color = Solid(capture, capture, 0, 0, 0), normals = Solid(capture, capture, 54, 54, 54);
    // Body 200x480 at the lower right, head above it; the colour is dark (a badly lit capture).
    Paint(normals, 420, 260, 620, 740, 128, 128, 250);
    Paint(normals, 470, 190, 570, 260, 200, 128, 200);
    Paint(color, 420, 260, 620, 740, 40, 60, 50);
    Paint(color, 470, 190, 570, 260, 60, 80, 70);
    // Bloom/fog-like haze on the background colour must not matter: the mask comes from the normals.
    Paint(color, 0, 0, 200, 200, 9, 9, 9);
    const PreviewComposition c = ComposePreview(color, normals, PreviewMask::Background, PreviewSize);
    CHECK(c.image.Valid() && c.image.width == PreviewSize && c.image.height == PreviewSize, "preview frame size");
    CHECK(!c.empty && c.left == 420 && c.right == 619 && c.top == 190 && c.bottom == 739, "character bounds from the normal capture");
    CHECK(c.gain == 2.0, "a dark capture is brightened, but only mildly (gain at most 2)");
    // The character is centred: find its columns/rows in the output (pixels far from the backdrop).
    int minX = PreviewSize, maxX = -1, minY = PreviewSize, maxY = -1;
    for (int y = 0; y < PreviewSize; ++y)
        for (int x = 0; x < PreviewSize; ++x)
        {
            const std::uint8_t* p = PixelAt(c.image, x, y);
            if (p[1] > 0x40) minX = std::min(minX, x), maxX = std::max(maxX, x), minY = std::min(minY, y), maxY = std::max(maxY, y);
        }
    CHECK(maxX > minX && std::abs((minX + maxX) - (PreviewSize - 1)) <= 4 && std::abs((minY + maxY) - (PreviewSize - 1)) <= 4, "character centred in the frame");
    CHECK(maxY - minY > PreviewSize * 0.8 && maxY - minY < PreviewSize * 0.9, "character fills the frame height with a margin");
    // Backdrop: the page's dark green-grey, never black, lighter towards the middle.
    const std::uint8_t* corner = PixelAt(c.image, 0, 0);
    CHECK(corner[0] >= 0x10 && corner[1] > corner[0] && corner[1] > corner[2] && corner[1] < 0x30, "backdrop is dark green-grey, not black");
    CHECK(PixelAt(c.image, 40, PreviewSize / 2)[1] > corner[1], "backdrop is lighter towards the middle");
    CHECK(PixelAt(c.image, 0, 0)[3] == 255, "frame is opaque");
    // Turning the character (narrower silhouette) does not zoom: the height decides the scale.
    PreviewPixels narrow = Solid(capture, capture, 54, 54, 54), narrowColor = Solid(capture, capture, 0, 0, 0);
    Paint(narrow, 480, 190, 560, 740, 128, 128, 250);
    Paint(narrowColor, 480, 190, 560, 740, 40, 60, 50);
    const PreviewComposition turned = ComposePreview(narrowColor, narrow, PreviewMask::Background, PreviewSize);
    int turnedMinY = PreviewSize, turnedMaxY = -1;
    for (int y = 0; y < PreviewSize; ++y)
        if (PixelAt(turned.image, PreviewSize / 2, y)[1] > 0x40) turnedMinY = std::min(turnedMinY, y), turnedMaxY = std::max(turnedMaxY, y);
    CHECK(std::abs((turnedMaxY - turnedMinY) - (maxY - minY)) <= 2, "turning the character keeps its size");
    // Nothing rendered: backdrop only, no shadow, no gain.
    const PreviewComposition none = ComposePreview(Solid(capture, capture, 0, 0, 0), Solid(capture, capture, 54, 54, 54), PreviewMask::Background, PreviewSize);
    CHECK(none.empty && none.gain == 1 && none.image.Valid() && PixelAt(none.image, 0, 0)[1] == corner[1], "an empty capture shows the backdrop");
    CHECK(ComposePreview(PreviewPixels{}, normals, PreviewMask::Background, PreviewSize).empty, "missing capture is empty, not a crash");
    // Scene-colour alpha as the mask: inverse opacity, 255 where nothing rendered, 0 on the character.
    PreviewPixels alpha = Solid(capture, capture, 0, 0, 0);
    for (std::size_t i = 3; i < alpha.rgba.size(); i += 4) alpha.rgba[i] = 255;
    for (int y = 190; y < 740; ++y)
        for (int x = (y < 260 ? 470 : 420); x < (y < 260 ? 570 : 620); ++x) alpha.rgba[(static_cast<std::size_t>(y) * capture + x) * 4 + 3] = 0;
    const PreviewComposition a = ComposePreview(color, alpha, PreviewMask::InverseAlpha, PreviewSize);
    CHECK(!a.empty && a.left == 420 && a.right == 619 && a.top == 190 && a.bottom == 739 && a.image.rgba == c.image.rgba, "the alpha mask cuts out the same character");
    CHECK(ComposePreview(color, Solid(capture, capture, 0, 0, 0), PreviewMask::InverseAlpha, PreviewSize).empty, "an alpha that marks everything is no mask");
    CHECK(PreviewCameraDistance(95, 40, 30) > 450 && PreviewCameraDistance(95, 40, 30) < 560, "camera distance fits a standing character");
}

// Cosmetics page preview: request validation and where it may run.
static void PreviewChecks()
{
    const std::int64_t now = 1790000000;
    const std::string good = "v=1\r\nexpires=1790000005\nseq=7\nmodel=Meso\nskin=McCree\nyaw=-35.5\n"
                             "vector=PrimaryColor:0.85,0.22,0.05,1\nscalar=Roughness:0.6\nfuture=ignored\n";
    auto r = ParsePreviewRequest(good, now);
    CHECK(r && r->seq == 7 && r->model == "Meso" && r->skin == "McCree" && r->yaw == -35.5, "preview request parses");
    CHECK(r && r->vectors.size() == 1 && r->vectors[0].name == "PrimaryColor" && r->vectors[0].value[1] == 0.22 && r->scalars.size() == 1, "preview parameters parse");
    auto same = ParsePreviewRequest("v=1\nexpires=1790000005\nseq=8\nmodel=Meso\nskin=McCree\nyaw=90\nvector=PrimaryColor:0.85,0.22,0.05,1\nscalar=Roughness:0.6\n", now);
    CHECK(r && same && r->LookKey() == same->LookKey(), "rotation alone does not change the look");
    auto other = ParsePreviewRequest("v=1\nexpires=1790000005\nseq=9\nmodel=Endo\n", now);
    CHECK(other && r && other->LookKey() != r->LookKey() && other->skin.empty() && other->yaw == 0, "model change changes the look; skin and yaw optional");

    const char* bad[] = {
        "v=2\nexpires=1790000005\nseq=1\nmodel=Meso\n",                         // version
        "v=1\nexpires=1790000000\nseq=1\nmodel=Meso\n",                         // expired
        "v=1\nexpires=1790000016\nseq=1\nmodel=Meso\n",                         // too far ahead
        "v=1\nexpires=1790000005\nmodel=Meso\n",                                // no seq
        "v=1\nexpires=1790000005\nseq=1\n",                                     // no model
        "v=1\nexpires=1790000005\nseq=1\nmodel=../Meso\n",                      // path-like model
        "v=1\nexpires=1790000005\nseq=1\nmodel=C:/skins/me.png\n",              // file, not a model
        "v=1\nexpires=1790000005\nseq=1\nmodel=Meso\nyaw=200\n",                // yaw range
        "v=1\nexpires=1790000005\nseq=1\nmodel=Meso\nyaw=nan\n",                // not finite
        "v=1\nexpires=1790000005\nseq=1\nmodel=Meso\nvector=Tint:2,0,0,1\n",    // colour range
        "v=1\nexpires=1790000005\nseq=1\nmodel=Meso\nvector=Tint:1,0,0\n",      // three channels
        "v=1\nexpires=1790000005\nseq=1\nmodel=Meso\nscalar=Rough:11\n",        // scalar range
        "v=1\nexpires=1790000005\nseq=1\nmodel=Meso\nmodel=Endo\n",             // duplicate key
        "v=1\nexpires=1790000005\nseq=-1\nmodel=Meso\n",                        // negative seq
        "v=1\nexpires=1790000005\nseq=1\nmodel=Meso\nnoequals\n",               // malformed line
        "",                                                                    // empty
    };
    for (const char* text : bad) CHECK(!ParsePreviewRequest(text, now), text);
    std::string many = "v=1\nexpires=1790000005\nseq=1\nmodel=Meso\n";
    for (int i = 0; i < 9; ++i) many += "scalar=S" + std::to_string(i) + ":1\n";
    CHECK(!ParsePreviewRequest(many, now), "at most 8 scalars");
    CHECK(!ParsePreviewRequest(std::string(MaxPreviewRequestBytes + 1, 'x'), now), "oversized request");
    auto worn = ParsePreviewRequest("v=1\nexpires=1790000005\nseq=1\nmodel=Meso\naccessory=accessory-halo\naccessory=accessory-collar\n", now);
    auto weapon = ParsePreviewRequest("v=1\nexpires=1790000005\nseq=1\nmodel=Meso\nfinish=finish-gold\nview=weapon\n", now);
    CHECK(weapon && weapon->weaponView && weapon->finish == "finish-gold" && other && weapon->LookKey() != other->LookKey() && !other->weaponView, "weapon view and finish parse");
    CHECK(!ParsePreviewRequest("v=1\nexpires=1790000005\nseq=1\nmodel=Meso\nview=arms\n", now) &&
              !ParsePreviewRequest("v=1\nexpires=1790000005\nseq=1\nmodel=Meso\nfinish=a\nfinish=b\n", now),
          "unknown views and two finishes are rejected");
    CHECK(worn && worn->accessories.size() == 2 && worn->accessories[1] == "accessory-collar" && worn->LookKey() != other->LookKey(), "accessories parse and change the look");
    for (const char* text : {"v=1\nexpires=1790000005\nseq=1\nmodel=Meso\naccessory=../halo\n", "v=1\nexpires=1790000005\nseq=1\nmodel=Meso\naccessory=Halo\n",
                             "v=1\nexpires=1790000005\nseq=1\nmodel=Meso\naccessory=a\naccessory=b\naccessory=c\naccessory=d\n"})
        CHECK(!ParsePreviewRequest(text, now), text);

    // Never in challenges (ranked), benchmarks, the editor or while loading; unknown counts as no.
    const PreviewGameState menu{false, false, false, false};
    CHECK(DecidePreview(r, menu).run, "preview runs in freeplay or menus with the page open");
    CHECK(!DecidePreview(std::nullopt, menu).run, "no request: no preview");
    CHECK(!DecidePreview(r, {true, false, false, false}).run, "never in a challenge");
    CHECK(!DecidePreview(r, {false, true, false, false}).run, "never in a benchmark");
    CHECK(!DecidePreview(r, {false, false, true, false}).run, "never in the scenario editor");
    CHECK(!DecidePreview(r, {false, false, false, true}).run, "never while loading");
    CHECK(!DecidePreview(r, {std::nullopt, false, false, false}).run, "unknown challenge state counts as a challenge");
    CHECK(!DecidePreview(r, {false, std::nullopt, false, false}).run, "unknown benchmark state counts as a benchmark");
    CHECK(!PreviewParkDue(100, 95) && !PreviewParkDue(100, 90) && PreviewParkDue(100.5, 90) && PreviewParkDue(100, -1e9),
          "the stage parks only 10 s after the last request (a flapping heartbeat never parks it)");
    CHECK(FormatPreviewFrame(3, "preview-1.png", 384, 384) == "v=1\nseq=3\nfile=preview-1.png\nwidth=384\nheight=384\n", "frame record format");
    PreviewComposeChecks();
}

static void WaterChecks()
{
    using namespace aimmod::water;
    // Gate: AimMod's own scenarios in freeplay only.
    CHECK(Allowed("AimMod - fy_pool_day (CSS) - CS Movement", false, false, false, false), "water in an AimMod port");
    CHECK(Allowed("AimMod Match - fy_pool_day - 0a1b2c3d", false, false, false, false), "water in a generated match arena");
    CHECK(!Allowed("AimMod - fy_pool_day (CSS) - CS Movement", true, false, false, false), "never in a challenge");
    CHECK(!Allowed("AimMod - fy_pool_day (CSS) - CS Movement", false, true, false, false), "never in a benchmark");
    CHECK(!Allowed("AimMod - fy_pool_day (CSS) - CS Movement", false, false, true, false), "never in the scenario editor");
    CHECK(!Allowed("AimMod - fy_pool_day (CSS) - CS Movement", false, false, false, true), "not while loading");
    CHECK(!Allowed("1wall6targets TE", false, false, false, false), "never in other scenarios");
    CHECK(!Allowed("My AimMod - copy", false, false, false, false), "prefix only");
    CHECK(!Allowed("", false, false, false, false), "no scenario, no water");

    // CS: 250 u/s run at MapScale 4 -> 200 u/s swim, friction 4/s (engine 0.5 x 8), sink 48 u/s.
    const Tuning cs = TuningFor(Style::CounterStrike, 1000.0, 4.0);
    CHECK(std::fabs(cs.maxSwimSpeed - 800.0) < 1e-9, "CS swims at 0.8 x run speed");
    CHECK(std::fabs(0.5 * cs.fluidFriction - 4.0) < 1e-9, "CS water friction is sv_friction");
    CHECK(std::fabs(cs.sinkSpeed - 192.0) < 1e-9, "CS sinks at 48 u/s");
    CHECK(std::fabs(cs.outOfWaterZ - 1024.0) < 1e-9, "CS water jump is 256 u/s");
    CHECK(cs.buoyancy == 1.0, "no gravity under water");
    CHECK(cs.terminalVelocity > cs.maxSwimSpeed, "a dive keeps some speed");
    CHECK(cs.jumpOutOfWaterPitch <= -90.0, "climbing out needs no view pitch");
    // Quake 3: 320 u/s run -> 160 u/s swim, friction 1 x waterlevel 3, sink 60 u/s.
    const Tuning q3 = TuningFor(Style::Quake, 1280.0, 4.0);
    CHECK(std::fabs(q3.maxSwimSpeed - 640.0) < 1e-9, "Quake swims at half the run speed");
    CHECK(std::fabs(0.5 * q3.fluidFriction - 3.0) < 1e-9, "Quake water friction");
    CHECK(std::fabs(q3.sinkSpeed - 240.0) < 1e-9, "Quake sinks at 60 u/s");
    CHECK(std::fabs(q3.outOfWaterZ - 1400.0) < 1e-9, "Quake water jump is 350 u/s");
    CHECK(std::string(StyleName(Style::Quake)) == "quake" && std::string(StyleName(Style::CounterStrike)) == "cs", "style names");
    // Scale follows MapScale; bad inputs fall back.
    const Tuning half = TuningFor(Style::CounterStrike, 500.0, 2.0);
    CHECK(std::fabs(half.sinkSpeed - 96.0) < 1e-9 && std::fabs(half.maxSwimSpeed - 400.0) < 1e-9, "tuning follows MapScale");
    const Tuning bad = TuningFor(Style::CounterStrike, std::nan(""), -1.0);
    CHECK(std::isfinite(bad.maxSwimSpeed) && bad.maxSwimSpeed > 0 && std::isfinite(bad.sinkSpeed), "bad inputs give sane tuning");

    // Swim input: jump up at full speed, idle sinks at sinkSpeed, moving holds depth.
    CHECK(VerticalInput(cs, true, 0.0) == 1.0 && VerticalInput(cs, true, 1.0) == 1.0, "jump swims up");
    CHECK(std::fabs(VerticalInput(cs, false, 0.0) + 0.24) < 1e-9, "idle sinks (60/250 of max, like CS)");
    CHECK(VerticalInput(cs, false, 1.0) == 0.0, "moving holds the depth");
    CHECK(VerticalInput(Tuning{}, false, 0.0) == 0.0, "no tuning, no input");

    // Jump keys from Input.ini.
    const auto keys = JumpKeys("[/Script/Engine.InputSettings]\r\n"
                               "ActionMappings=(ActionName=\"Fire\",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=LeftMouseButton)\r\n"
                               "ActionMappings=(ActionName=\"Jump\",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=SpaceBar)\r\n"
                               "+ActionMappings=(ActionName=\"Jump\",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=MouseScrollDown)\r\n"
                               "ActionMappings=(ActionName=\"Jump\",Key=SpaceBar)\r\n"
                               "ActionMappings=(ActionName=\"Jump\",Key=Bad Key)\r\n");
    CHECK(keys.size() == 2 && keys[0] == "SpaceBar" && keys[1] == "MouseScrollDown", "jump keys, deduplicated, odd names skipped");
    CHECK(JumpKeys("") == std::vector<std::string>{"SpaceBar"}, "default jump key");
    CHECK(JumpKeys("ActionMappings=(ActionName=\"JumpPad\",Key=J)") == std::vector<std::string>{"SpaceBar"}, "other actions ignored");
    const Tint tint = UnderwaterTint();
    CHECK(tint.b > tint.r && tint.r > 0 && tint.b <= 1, "underwater tint is a light blue");
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
    ReplayFormat2();
    GameStatsChecks();
    PlaybackChecks();
    CommandChecks();
    ClipChecks();
    Settings();
    Backoff();
    LifecycleChecks();
    MatchPlayChecks();
    EndRunChecks();
    cosmetics_checks::Run();
    PreviewChecks();
    WaterChecks();
    std::printf("%d AimModCore checks, %d failed.\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
