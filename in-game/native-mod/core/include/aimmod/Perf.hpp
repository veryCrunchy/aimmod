#pragma once
#include <array>
#include <algorithm>
#include <chrono>
#include <cstdint>
#include <string>

namespace aimmod::perf {
enum Part { Lifecycle, Control, Match, Gear, Grenades, Cosmetics, Water, Preview, Overlay, Hud, Tags, Replay, Ipc, Reflection, Weapons, Count };
inline constexpr const char* Names[] = {"lifecycle", "control", "match", "cs-gear", "grenades", "cosmetics", "water", "preview", "overlay", "hud", "world-tags", "replay", "ipc", "reflection", "weapon-restore"};
using Clock = std::chrono::steady_clock;
inline double Ms(Clock::duration d) { return std::chrono::duration<double, std::milli>(d).count(); }
struct Budget {
    unsigned strikes{};
    double until{};
    bool Observe(double ms, double now, bool optional) {
        strikes = ms > 2 ? strikes + 1 : 0;
        if (optional && strikes >= 3) { until = now + 1; strikes = 0; return true; }
        return false;
    }
    bool Allowed(double now) const { return now >= until; }
};
class Frame {
    std::array<double, Count> current{}, sum{}, peak{};
    std::array<Budget, Count> budgets{};
    Clock::time_point start{};
    double last{}, nextLog{}, nextSummary{};
    std::uint64_t frames{};
    bool throttleNotice{};
public:
    bool protect{};
    double delta{}, outside{};
    void Begin(double now) { current.fill(0); outside=0; start = Clock::now(); delta = last ? (now-last)*1000 : 0; last=now; }
    void Add(Part p, double ms) { current[p] += ms; }
    bool Allowed(Part p, double now) const { return !protect || budgets[p].Allowed(now); }
    std::string End(double now) {
        const double total = Ms(Clock::now()-start)+outside;
        ++frames;
        Part worst = Lifecycle;
        bool throttled = false;
        for (int i=0;i<Count;++i) {
            sum[i]+=current[i]; peak[i]=std::max(peak[i],current[i]);
            if(current[i]>current[worst]) worst=static_cast<Part>(i);
            // Only preview rendering may be skipped. Gameplay and release always run.
            throttled |= budgets[i].Observe(current[i],now,protect && i==Preview);
        }
        throttleNotice |= throttled;
        if (now >= nextLog && (total > 2 || (delta > 50 && total > 0.1) || throttleNotice)) {
            nextLog=now+5;
            const bool reportThrottle=throttleNotice; throttleNotice=false;
            return std::string("perf: frame_ms=")+std::to_string(delta)+" own_ms="+std::to_string(total)+" subsystem="+Names[worst]+" subsystem_ms="+std::to_string(current[worst])+(reportThrottle?" preview throttled for 1s":"");
        }
        return {};
    }
    std::string Summary(double now) {
        if(now<nextSummary || !frames) return {};
        nextSummary=now+5;
        std::string s="AIMMOD_PERF_1\t"+std::to_string(frames)+"\t"+std::to_string(delta)+"\n";
        for(int i=0;i<Count;++i) s+=std::string(Names[i])+"\t"+std::to_string(sum[i]/frames)+"\t"+std::to_string(peak[i])+"\n";
        frames=0;sum.fill(0);peak.fill(0);return s;
    }
};
inline thread_local Frame* Active{};
struct Scope {
    Part part; Frame* frame; Clock::time_point start;
    explicit Scope(Part p):part(p),frame(Active),start(Clock::now()){}
    ~Scope(){if(frame) frame->Add(part,Ms(Clock::now()-start));}
};
}
