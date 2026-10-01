#include <aimmod/ReplayV2.hpp>
#include <aimmod/ReplayWriter.hpp>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <map>

#ifdef _WIN32
#include <Windows.h>
#include <compressapi.h>
#pragma comment(lib, "Cabinet.lib")
#endif

namespace aimmod::replay2
{
    namespace
    {
        constexpr std::array<const char*, 20> Actions = {
            "AxisTurn",        "AxisLookUp",   "AxisMoveForward", "AxisMoveRight",  "FirePressed",    "FireReleased",   "AltFirePressed",
            "AltFireReleased", "JumpPressed",  "JumpReleased",    "CrouchPressed",  "CrouchReleased", "ReloadPressed",  "ReloadReleased",
            "ADSPressed",      "ADSReleased",  "AbilityPressed",  "AbilityReleased", "WeaponPressed", "WeaponReleased"};
        constexpr double TimeUnits = 20000.0; // 50 us
        constexpr double PositionScale = 100.0; // 0.01 cm
        constexpr double KeyAngleScale = 10000.0; // 0.0001 deg
        constexpr double FovScale = 10000.0;
        constexpr double AngleScale = 100.0; // 0.01 deg (target appearance)
        constexpr double HealthScale = 10000.0;
        constexpr double StatScales[6] = {1000.0, 1.0, 1.0, 1.0, 1e6, 1000.0}; // score shots hits kills damage seconds
        constexpr std::uint32_t FormatVersion = 2;

        struct Writer
        {
            std::vector<std::uint8_t> out;
            void U8(std::uint8_t v) { out.push_back(v); }
            void Var(std::uint64_t v)
            {
                while (v >= 0x80)
                {
                    out.push_back(static_cast<std::uint8_t>(v | 0x80));
                    v >>= 7;
                }
                out.push_back(static_cast<std::uint8_t>(v));
            }
            void SVar(std::int64_t v) { Var((static_cast<std::uint64_t>(v) << 1) ^ static_cast<std::uint64_t>(v >> 63)); }
            void F64(double v)
            {
                std::uint8_t b[8];
                std::memcpy(b, &v, 8);
                out.insert(out.end(), b, b + 8);
            }
            void F32(float v)
            {
                std::uint8_t b[4];
                std::memcpy(b, &v, 4);
                out.insert(out.end(), b, b + 4);
            }
            void Str(const std::string& s)
            {
                Var(s.size());
                out.insert(out.end(), s.begin(), s.end());
            }
        };

        struct Reader
        {
            const std::uint8_t* p;
            const std::uint8_t* end;
            bool ok = true;
            std::uint8_t U8()
            {
                if (p >= end) return ok = false, 0;
                return *p++;
            }
            std::uint64_t Var()
            {
                std::uint64_t v = 0;
                for (int shift = 0; shift < 64; shift += 7)
                {
                    if (p >= end) return ok = false, 0;
                    std::uint8_t b = *p++;
                    v |= static_cast<std::uint64_t>(b & 0x7f) << shift;
                    if (!(b & 0x80)) return v;
                }
                ok = false;
                return 0;
            }
            std::int64_t SVar()
            {
                std::uint64_t v = Var();
                return static_cast<std::int64_t>(v >> 1) ^ -static_cast<std::int64_t>(v & 1);
            }
            double F64()
            {
                if (end - p < 8) return ok = false, 0;
                double v;
                std::memcpy(&v, p, 8);
                p += 8;
                return v;
            }
            float F32()
            {
                if (end - p < 4) return ok = false, 0.f;
                float v;
                std::memcpy(&v, p, 4);
                p += 4;
                return v;
            }
            std::string Str()
            {
                std::uint64_t n = Var();
                if (!ok || n > 4096 || static_cast<std::uint64_t>(end - p) < n) return ok = false, std::string();
                std::string s(reinterpret_cast<const char*>(p), static_cast<std::size_t>(n));
                p += n;
                return s;
            }
            // Bounded element counts guard allocations from damaged files.
            std::size_t Count(std::size_t limit)
            {
                std::uint64_t n = Var();
                if (n > limit) ok = false;
                return ok ? static_cast<std::size_t>(n) : 0;
            }
        };

        std::int64_t Q(double v, double scale) { return static_cast<std::int64_t>(std::llround(v * scale)); }
        double Wrap(double degrees)
        {
            degrees = std::fmod(degrees + 180.0, 360.0);
            if (degrees < 0) degrees += 360.0;
            return degrees - 180.0;
        }
        double ClampPitch(double p) { return p > 90.0 ? 90.0 : p < -90.0 ? -90.0 : p; }

        // Greedy piecewise-linear simplification: keeps the fewest points such
        // that linear interpolation reproduces every sample within tolerance.
        template <std::size_t D>
        std::vector<std::size_t> Simplify(const std::vector<double>& t, const std::vector<std::array<double, D>>& v, const std::array<double, D>& tol,
                                          double maxSpan)
        {
            std::vector<std::size_t> keep;
            const std::size_t n = t.size();
            if (n == 0) return keep;
            keep.push_back(0);
            std::size_t a = 0;
            for (std::size_t b = a + 2; b < n; ++b)
            {
                bool fits = t[b] - t[a] <= maxSpan;
                for (std::size_t i = a + 1; fits && i < b; ++i)
                {
                    const double u = (t[i] - t[a]) / (t[b] - t[a]);
                    for (std::size_t d = 0; d < D; ++d)
                        if (std::fabs(v[a][d] + (v[b][d] - v[a][d]) * u - v[i][d]) > tol[d]) fits = false;
                }
                if (!fits)
                {
                    a = b - 1;
                    keep.push_back(a);
                }
            }
            if (n > 1 && keep.back() != n - 1) keep.push_back(n - 1);
            return keep;
        }

        void Append(std::string& json, const char* key, double value, int digits = 0)
        {
            json += ",\"";
            json += key;
            json += "\":";
            AppendNumber(json, value, digits);
        }
    } // namespace

    int ActionIndex(std::string_view action)
    {
        for (std::size_t i = 0; i < Actions.size(); ++i)
            if (action == Actions[i]) return static_cast<int>(i);
        return -1;
    }

    const char* ActionName(int index) { return index >= 0 && index < static_cast<int>(Actions.size()) ? Actions[static_cast<std::size_t>(index)] : ""; }

    bool Compress(std::uint32_t algorithm, const std::vector<std::uint8_t>& in, std::vector<std::uint8_t>& out)
    {
        if (algorithm == 0)
        {
            out = in;
            return true;
        }
#ifdef _WIN32
        COMPRESSOR_HANDLE handle = nullptr;
        if (!CreateCompressor(algorithm, nullptr, &handle)) return false;
        SIZE_T needed = 0;
        ::Compress(handle, in.data(), in.size(), nullptr, 0, &needed);
        out.resize(needed ? needed : in.size() + 1024);
        SIZE_T written = 0;
        const BOOL ok = ::Compress(handle, in.data(), in.size(), out.data(), out.size(), &written);
        CloseCompressor(handle);
        if (!ok) return false;
        out.resize(written);
        return true;
#else
        return false;
#endif
    }

    bool Decompress(std::uint32_t algorithm, const std::uint8_t* in, std::size_t size, std::vector<std::uint8_t>& out)
    {
        if (algorithm == 0)
        {
            out.assign(in, in + size);
            return true;
        }
#ifdef _WIN32
        DECOMPRESSOR_HANDLE handle = nullptr;
        if (!CreateDecompressor(algorithm, nullptr, &handle)) return false;
        SIZE_T needed = 0;
        ::Decompress(handle, in, size, nullptr, 0, &needed);
        bool ok = needed > 0 && needed <= 256u * 1024u * 1024u;
        if (ok)
        {
            out.resize(needed);
            SIZE_T written = 0;
            ok = ::Decompress(handle, in, size, out.data(), out.size(), &written) != FALSE;
            out.resize(ok ? written : 0);
        }
        CloseDecompressor(handle);
        return ok;
#else
        return false;
#endif
    }

    std::vector<std::uint8_t> Encode(const Capture& c, const EncodeOptions& o, EncodeReport* reportOut)
    {
        EncodeReport report;
        const std::size_t frames = c.frameTimes.size();
        if (frames < 2 || c.camera.size() < 2 || !IsValidAttemptId(c.header.id)) return {};
        for (std::size_t i = 1; i < frames; ++i)
            if (!(c.frameTimes[i] > c.frameTimes[i - 1])) return {};
        for (const CameraSample& s : c.camera)
            if (s.frame >= frames) return {};

        // Axis quantum: raw mouse counts times a constant on current builds.
        double quantum = 0;
        for (const InputEvent& e : c.inputs)
            if (IsAxis(e.action) && e.value != 0 && (quantum == 0 || std::fabs(e.value) < quantum)) quantum = std::fabs(e.value);
        if (quantum > 0)
            for (const InputEvent& e : c.inputs)
            {
                if (!IsAxis(e.action)) continue;
                const double n = e.value / quantum;
                if (std::fabs(n - std::round(n)) > 1e-3 || std::fabs(n) > 1e9)
                {
                    quantum = 0; // not quantized: store raw floats
                    break;
                }
            }

        // Per-frame prefix sums of the look axes (inputs of frame f are applied
        // before the state sampled after frame f).
        std::vector<double> turn(frames + 1, 0.0), look(frames + 1, 0.0);
        for (const InputEvent& e : c.inputs)
        {
            if (e.frame >= frames) continue;
            const double v = quantum > 0 ? std::round(e.value / quantum) * quantum : e.value;
            if (e.action == AxisTurn) turn[e.frame + 1] += v;
            else if (e.action == AxisLookUp) look[e.frame + 1] += v;
        }
        for (std::size_t i = 1; i <= frames; ++i)
        {
            turn[i] += turn[i - 1];
            look[i] += look[i - 1];
        }
        auto sumTo = [](const std::vector<double>& prefix, std::uint32_t frame) { return prefix[frame + 1]; };

        // Rotation per axis unit, least squares over sample intervals, refit
        // without outliers (teleports, clamps).
        auto fit = [&](bool yaw) {
            double k = 0;
            for (int pass = 0; pass < 2; ++pass)
            {
                double num = 0, den = 0;
                for (std::size_t i = 1; i < c.camera.size(); ++i)
                {
                    const auto& a = c.camera[i - 1];
                    const auto& b = c.camera[i];
                    const double s = yaw ? sumTo(turn, b.frame) - sumTo(turn, a.frame) : sumTo(look, b.frame) - sumTo(look, a.frame);
                    const double d = yaw ? Wrap(b.yaw - a.yaw) : b.pitch - a.pitch;
                    if (s == 0) continue;
                    if (pass == 1 && std::fabs(d - k * s) > 1.0) continue;
                    num += s * d;
                    den += s * s;
                }
                k = den > 0 ? num / den : 0;
            }
            return k;
        };
        const double yawPerUnit = fit(true);
        const double pitchPerUnit = fit(false);

        // Events that deserve an exact orientation: button edges, kills, hits.
        std::vector<std::uint32_t> events;
        for (const InputEvent& e : c.inputs)
            if (!IsAxis(e.action)) events.push_back(e.frame);
        for (const HitEvent& h : c.hits) events.push_back(h.frame);
        {
            std::optional<double> kills;
            for (const StatsSample& s : c.stats)
            {
                if (s.kills && kills && *s.kills != *kills) events.push_back(s.frame);
                if (s.kills) kills = s.kills;
            }
        }
        std::sort(events.begin(), events.end());

        // Keyframes: simulate the decoder and snap when it drifts.
        std::vector<Keyframe> keys;
        double errSq = 0;
        std::size_t errCount = 0;
        std::size_t nextEvent = 0;
        for (std::size_t i = 0; i < c.camera.size(); ++i)
        {
            const CameraSample& s = c.camera[i];
            bool key = keys.empty();
            double err = 0;
            if (!key)
            {
                const Keyframe& k = keys.back();
                const double yaw = k.yaw + yawPerUnit * (sumTo(turn, s.frame) - sumTo(turn, k.frame));
                const double pitch = ClampPitch(k.pitch + pitchPerUnit * (sumTo(look, s.frame) - sumTo(look, k.frame)));
                err = std::max({std::fabs(Wrap(yaw - s.yaw)), std::fabs(pitch - s.pitch), std::fabs(Wrap(k.roll - s.roll))});
                while (nextEvent < events.size() && events[nextEvent] <= k.frame) ++nextEvent;
                const bool event = nextEvent < events.size() && events[nextEvent] <= s.frame;
                key = err > o.rotationTolerance || c.frameTimes[s.frame] - c.frameTimes[k.frame] >= o.maxKeyframeInterval || event;
            }
            if (key)
            {
                if (!keys.empty())
                {
                    report.keyframeErrorMax = std::max(report.keyframeErrorMax, err);
                    errSq += err * err;
                    ++errCount;
                }
                keys.push_back({s.frame, s.pitch, s.yaw, s.roll});
            }
            else report.sampleErrorMax = std::max(report.sampleErrorMax, err);
        }
        report.keyframeErrorRms = errCount ? std::sqrt(errSq / static_cast<double>(errCount)) : 0;

        Writer w;
        // Frames.
        w.Var(frames);
        std::int64_t previousUnits = -1;
        for (double t : c.frameTimes)
        {
            std::int64_t units = std::max<std::int64_t>(Q(t, TimeUnits), previousUnits + 1);
            w.Var(static_cast<std::uint64_t>(units - (previousUnits < 0 ? 0 : previousUnits)));
            previousUnits = units;
        }
        // Inputs (columnar).
        std::vector<const InputEvent*> inputs;
        for (const InputEvent& e : c.inputs)
            if (e.frame < frames && e.action < Actions.size()) inputs.push_back(&e);
        std::stable_sort(inputs.begin(), inputs.end(), [](auto* a, auto* b) { return a->frame < b->frame; });
        w.F64(quantum);
        w.Var(inputs.size());
        std::uint32_t frame = 0;
        for (auto* e : inputs)
        {
            w.Var(e->frame - frame);
            frame = e->frame;
        }
        for (auto* e : inputs) w.U8(e->action);
        for (auto* e : inputs)
        {
            if (!IsAxis(e->action)) continue; // buttons carry no value
            if (quantum > 0) w.SVar(static_cast<std::int64_t>(std::llround(e->value / quantum)));
            else w.F32(e->value);
        }
        w.F64(yawPerUnit);
        w.F64(pitchPerUnit);
        // Rotation keyframes (absolute).
        w.Var(keys.size());
        frame = 0;
        for (const Keyframe& k : keys)
        {
            w.Var(k.frame - frame);
            frame = k.frame;
            w.SVar(Q(k.pitch, KeyAngleScale));
            w.SVar(Q(k.yaw, KeyAngleScale));
            w.SVar(Q(k.roll, KeyAngleScale));
        }
        // Camera location and FOV.
        {
            std::vector<double> t;
            std::vector<std::array<double, 4>> v;
            for (const CameraSample& s : c.camera)
            {
                t.push_back(c.frameTimes[s.frame]);
                v.push_back({s.x, s.y, s.z, s.fov});
            }
            const double ct = o.cameraTolerance;
            auto keep = Simplify<4>(t, v, {ct, ct, ct, 0.01}, o.maxSegmentSeconds);
            w.Var(keep.size());
            frame = 0;
            std::int64_t last[4]{};
            for (std::size_t idx : keep)
            {
                const CameraSample& s = c.camera[idx];
                w.Var(static_cast<std::uint64_t>(s.frame - frame) << 1);
                frame = s.frame;
                const std::int64_t q[4] = {Q(s.x, PositionScale), Q(s.y, PositionScale), Q(s.z, PositionScale), Q(s.fov, FovScale)};
                for (int d = 0; d < 4; ++d)
                {
                    w.SVar(q[d] - last[d]);
                    last[d] = q[d];
                }
            }
        }
        // Targets.
        {
            std::map<std::uint32_t, std::size_t> ordinal; // camera sample frame -> index
            for (std::size_t i = 0; i < c.camera.size(); ++i) ordinal.emplace(c.camera[i].frame, i);
            std::map<std::uint32_t, std::vector<const ActorSample*>> byId;
            for (const ActorSample& a : c.actors)
                if (a.id != 0 && a.frame < frames && ordinal.contains(a.frame)) byId[a.id].push_back(&a);
            w.Var(byId.size());
            for (auto& [id, samples] : byId)
            {
                std::stable_sort(samples.begin(), samples.end(), [](auto* a, auto* b) { return a->frame < b->frame; });
                report.actorSamples += static_cast<std::uint32_t>(samples.size());
                w.Var(id);
                auto profile = c.profiles.find(id);
                w.Str(profile != c.profiles.end() ? profile->second : std::string());
                // Segments: consecutive camera samples.
                std::vector<std::pair<std::size_t, std::size_t>> segments;
                for (std::size_t i = 0; i < samples.size(); ++i)
                {
                    if (i == 0 || ordinal[samples[i]->frame] != ordinal[samples[i - 1]->frame] + 1) segments.push_back({i, i});
                    segments.back().second = i;
                }
                std::vector<std::pair<std::size_t, bool>> points, rotations;
                for (auto [first, last] : segments)
                {
                    std::vector<double> t;
                    std::vector<std::array<double, 5>> v;
                    std::vector<std::array<double, 3>> r;
                    std::vector<std::size_t> rotIndex;
                    std::vector<double> rt;
                    double unwrap = 0, previousYaw = 0;
                    for (std::size_t i = first; i <= last; ++i)
                    {
                        const ActorSample* a = samples[i];
                        t.push_back(c.frameTimes[a->frame]);
                        v.push_back({a->x, a->y, a->z, a->radius, a->halfHeight});
                        if (a->hasRotation)
                        {
                            if (!rotIndex.empty()) unwrap += Wrap(a->yaw - previousYaw) - (a->yaw - previousYaw);
                            previousYaw = a->yaw;
                            r.push_back({a->pitch, a->yaw + unwrap, a->roll});
                            rt.push_back(c.frameTimes[a->frame]);
                            rotIndex.push_back(i);
                        }
                    }
                    const double pt = o.positionTolerance;
                    auto keep = Simplify<5>(t, v, {pt, pt, pt, 0.01, 0.01}, o.maxSegmentSeconds);
                    for (std::size_t j = 0; j < keep.size(); ++j) points.push_back({first + keep[j], j == 0});
                    const double at = o.angleTolerance;
                    auto rkeep = Simplify<3>(rt, r, {at, at, at}, o.maxSegmentSeconds);
                    for (std::size_t j = 0; j < rkeep.size(); ++j) rotations.push_back({rotIndex[rkeep[j]], j == 0});
                }
                report.actorPoints += static_cast<std::uint32_t>(points.size());
                w.Var(points.size());
                frame = 0;
                std::int64_t last[5]{};
                for (auto [idx, start] : points)
                {
                    const ActorSample* a = samples[idx];
                    w.Var((static_cast<std::uint64_t>(a->frame - frame) << 1) | (start ? 1u : 0u));
                    frame = a->frame;
                    const std::int64_t q[5] = {Q(a->x, PositionScale), Q(a->y, PositionScale), Q(a->z, PositionScale), Q(a->radius, PositionScale),
                                               Q(a->halfHeight, PositionScale)};
                    for (int d = 0; d < 5; ++d)
                    {
                        w.SVar(q[d] - last[d]);
                        last[d] = q[d];
                    }
                }
                // Health: change records (0 = unknown).
                std::vector<std::pair<std::uint32_t, std::uint64_t>> health;
                std::uint64_t current = ~0ull;
                for (const ActorSample* a : samples)
                {
                    const std::uint64_t h = a->health ? static_cast<std::uint64_t>(std::llround(std::clamp(*a->health, 0.0, 1.0) * HealthScale)) + 1 : 0;
                    if (h != current) health.push_back({a->frame, h});
                    current = h;
                }
                w.Var(health.size());
                frame = 0;
                for (auto [f, h] : health)
                {
                    w.Var(f - frame);
                    frame = f;
                    w.Var(h);
                }
                w.Var(rotations.size());
                frame = 0;
                std::int64_t lastRotation[3]{};
                for (auto [idx, start] : rotations)
                {
                    const ActorSample* a = samples[idx];
                    w.Var((static_cast<std::uint64_t>(a->frame - frame) << 1) | (start ? 1u : 0u));
                    frame = a->frame;
                    const std::int64_t q[3] = {Q(a->pitch, AngleScale), Q(a->yaw, AngleScale), Q(a->roll, AngleScale)};
                    for (int d = 0; d < 3; ++d)
                    {
                        w.SVar(q[d] - lastRotation[d]);
                        lastRotation[d] = q[d];
                    }
                }
            }
        }
        // Stats: change records; seconds only when the clock drifts.
        {
            std::vector<StatsRecord> records;
            std::int64_t current[6]{};
            bool known[6]{};
            double secondsAt = 0, secondsValue = 0;
            bool secondsKnown = false;
            for (const StatsSample& s : c.stats)
            {
                if (s.frame >= frames) continue;
                const std::optional<double>* fields[6] = {&s.score, &s.shots, &s.hits, &s.kills, &s.damage, &s.seconds};
                StatsRecord r;
                r.frame = s.frame;
                const double t = c.frameTimes[s.frame];
                for (int f = 0; f < 6; ++f)
                {
                    if (!*fields[f] || !IsUsableNumber(**fields[f])) continue;
                    const double value = **fields[f];
                    if (f == 5)
                    {
                        const double predicted = secondsValue + (t - secondsAt);
                        if (secondsKnown && std::fabs(predicted - value) <= 0.002) continue;
                        secondsKnown = true;
                        secondsAt = t;
                        secondsValue = value;
                    }
                    const std::int64_t q = Q(value, StatScales[f]);
                    if (f != 5 && known[f] && q == current[f]) continue;
                    r.mask |= static_cast<std::uint8_t>(1u << f);
                    r.values[f] = static_cast<double>(q - (known[f] ? current[f] : 0));
                    current[f] = q;
                    known[f] = true;
                }
                if (r.mask) records.push_back(r);
            }
            w.Var(records.size());
            frame = 0;
            for (const StatsRecord& r : records)
            {
                w.Var(r.frame - frame);
                frame = r.frame;
                w.U8(r.mask);
                for (int f = 0; f < 6; ++f)
                    if (r.mask & (1u << f)) w.SVar(static_cast<std::int64_t>(r.values[f]));
            }
        }
        // Registered hits (target markers).
        {
            std::vector<HitEvent> hits(c.hits.begin(), c.hits.end());
            std::stable_sort(hits.begin(), hits.end(), [](auto& a, auto& b) { return a.frame < b.frame; });
            w.Var(hits.size());
            frame = 0;
            for (const HitEvent& h : hits)
            {
                w.Var(h.frame - frame);
                frame = h.frame;
                w.Var(h.target);
            }
        }

        std::vector<std::uint8_t> body;
        std::uint32_t algorithm = o.compression;
        if (!Compress(algorithm, w.out, body))
        {
            algorithm = 0;
            body = w.out;
        }

        report.bodyBytes = w.out.size();
        report.engineFrames = static_cast<std::uint32_t>(frames);
        report.inputEvents = static_cast<std::uint32_t>(inputs.size());
        report.cameraSamples = static_cast<std::uint32_t>(c.camera.size());
        report.keyframes = static_cast<std::uint32_t>(keys.size());
        report.quantum = quantum;
        report.yawPerUnit = yawPerUnit;
        report.pitchPerUnit = pitchPerUnit;
        report.duration = c.frameTimes.back();

        std::string header = FormatReplayHeader(c.header);
        header.pop_back(); // reopen the object
        header.replace(header.find("\"version\":1"), 11, "\"version\":2");
        header += ",\"reason\":";
        AppendJsonString(header, c.reason);
        header += ",\"frames\":" + std::to_string(c.camera.size()) + ",\"inputEvents\":" + std::to_string(inputs.size());
        if (c.reason == "completed" && c.score && IsUsableNumber(*c.score)) Append(header, "score", *c.score);
        Append(header, "duration", report.duration, 9);
        if (!c.clipOf.empty() && IsValidAttemptId(c.clipOf))
        {
            header += ",\"clipOf\":";
            AppendJsonString(header, c.clipOf);
            Append(header, "clipStart", c.clipStart, 9);
        }
        if (!c.marks.empty())
        {
            header += ",\"marks\":[";
            for (std::size_t i = 0; i < c.marks.size(); ++i)
            {
                if (i) header += ',';
                AppendNumber(header, c.marks[i] < frames ? c.frameTimes[c.marks[i]] : 0.0, 6);
            }
            header += ']';
        }
        header += ",\"encoding\":{\"keyframes\":" + std::to_string(keys.size());
        Append(header, "quantum", quantum);
        Append(header, "yawPerUnit", yawPerUnit, 9);
        Append(header, "pitchPerUnit", pitchPerUnit, 9);
        Append(header, "keyframeErrorMax", report.keyframeErrorMax, 4);
        Append(header, "keyframeErrorRms", report.keyframeErrorRms, 4);
        header += "}}";

        std::vector<std::uint8_t> file(Magic, Magic + 8);
        auto u32 = [&](std::uint32_t v) {
            for (int i = 0; i < 4; ++i) file.push_back(static_cast<std::uint8_t>(v >> (8 * i)));
        };
        u32(FormatVersion);
        u32(static_cast<std::uint32_t>(header.size()));
        file.insert(file.end(), header.begin(), header.end());
        u32(algorithm);
        u32(static_cast<std::uint32_t>(w.out.size()));
        file.insert(file.end(), body.begin(), body.end());
        report.fileBytes = file.size();
        if (reportOut) *reportOut = report;
        return file;
    }

    Capture Slice(const Capture& s, double from, double to, const std::string& id)
    {
        Capture c;
        c.header = s.header;
        c.header.id = id;
        c.reason = s.reason;
        c.clipOf = s.header.id;
        c.profiles = s.profiles;
        const std::size_t n = s.frameTimes.size();
        std::uint32_t first = 0, last = 0;
        bool any = false;
        for (std::uint32_t f = 0; f < n; ++f)
        {
            const double t = s.frameTimes[f];
            if (t < from || t > to) continue;
            if (!any) first = f;
            last = f;
            any = true;
        }
        if (!any) return c;
        c.clipStart = s.frameTimes[first];
        for (std::uint32_t f = first; f <= last; ++f) c.frameTimes.push_back(s.frameTimes[f] - c.clipStart);
        auto inside = [&](std::uint32_t f) { return f >= first && f <= last; };
        for (const InputEvent& e : s.inputs)
            if (inside(e.frame)) c.inputs.push_back({e.frame - first, e.action, e.value});
        for (const CameraSample& e : s.camera)
            if (inside(e.frame))
            {
                CameraSample x = e;
                x.frame -= first;
                c.camera.push_back(x);
            }
        for (const ActorSample& e : s.actors)
            if (inside(e.frame))
            {
                ActorSample x = e;
                x.frame -= first;
                c.actors.push_back(x);
            }
        for (const StatsSample& e : s.stats)
            if (inside(e.frame))
            {
                StatsSample x = e;
                x.frame -= first;
                c.stats.push_back(x);
            }
        for (const HitEvent& e : s.hits)
            if (inside(e.frame)) c.hits.push_back({e.frame - first, e.target});
        // The clip shows the run's final score only when it reaches the end.
        if (last + 1 == n) c.score = s.score;
        return c;
    }

    std::optional<Decoded> Decode(const std::uint8_t* data, std::size_t size)
    {
        auto u32 = [&](std::size_t at) {
            std::uint32_t v = 0;
            for (int i = 0; i < 4; ++i) v |= static_cast<std::uint32_t>(data[at + i]) << (8 * i);
            return v;
        };
        if (size < 24 || std::memcmp(data, Magic, 8) != 0 || u32(8) != FormatVersion) return std::nullopt;
        const std::uint32_t headerLength = u32(12);
        if (headerLength > 65536 || 16 + headerLength + 8 > size) return std::nullopt;
        Decoded d;
        d.headerJson.assign(reinterpret_cast<const char*>(data + 16), headerLength);
        const std::size_t at = 16 + headerLength;
        const std::uint32_t algorithm = u32(at);
        const std::uint32_t rawSize = u32(at + 4);
        std::vector<std::uint8_t> body;
        if (!Decompress(algorithm, data + at + 8, size - at - 8, body) || body.size() != rawSize) return std::nullopt;
        Reader r{body.data(), body.data() + body.size()};

        const std::size_t frames = r.Count(20'000'000);
        d.frameTimes.reserve(frames);
        std::uint64_t units = 0;
        for (std::size_t i = 0; i < frames && r.ok; ++i)
        {
            units += r.Var();
            d.frameTimes.push_back(static_cast<double>(units) / TimeUnits);
        }
        d.quantum = r.F64();
        const std::size_t inputs = r.Count(20'000'000);
        d.inputs.resize(inputs);
        std::uint32_t frame = 0;
        for (auto& e : d.inputs)
        {
            frame += static_cast<std::uint32_t>(r.Var());
            e.frame = frame;
        }
        for (auto& e : d.inputs) e.action = r.U8();
        for (auto& e : d.inputs)
        {
            if (!IsAxis(e.action))
            {
                e.value = 1.0f;
                continue;
            }
            e.value = d.quantum > 0 ? static_cast<float>(static_cast<double>(r.SVar()) * d.quantum) : r.F32();
        }
        d.yawPerUnit = r.F64();
        d.pitchPerUnit = r.F64();
        const std::size_t keys = r.Count(10'000'000);
        frame = 0;
        for (std::size_t i = 0; i < keys && r.ok; ++i)
        {
            frame += static_cast<std::uint32_t>(r.Var());
            Keyframe k{frame};
            k.pitch = static_cast<double>(r.SVar()) / KeyAngleScale;
            k.yaw = static_cast<double>(r.SVar()) / KeyAngleScale;
            k.roll = static_cast<double>(r.SVar()) / KeyAngleScale;
            d.keyframes.push_back(k);
        }
        auto points = [&](std::vector<Point>& out, int dims, const double* scales) {
            const std::size_t n = r.Count(10'000'000);
            std::uint32_t f = 0;
            std::int64_t last[5]{};
            for (std::size_t i = 0; i < n && r.ok; ++i)
            {
                const std::uint64_t head = r.Var();
                f += static_cast<std::uint32_t>(head >> 1);
                Point p{f, (head & 1) != 0};
                for (int k = 0; k < dims; ++k)
                {
                    last[k] += r.SVar();
                    p.v[k] = static_cast<double>(last[k]) / scales[k];
                }
                out.push_back(p);
            }
        };
        const double cameraScales[4] = {PositionScale, PositionScale, PositionScale, FovScale};
        points(d.camera, 4, cameraScales);
        const std::size_t actors = r.Count(4096);
        for (std::size_t a = 0; a < actors && r.ok; ++a)
        {
            Track t;
            t.id = static_cast<std::uint32_t>(r.Var());
            t.profile = r.Str();
            const double scales[5] = {PositionScale, PositionScale, PositionScale, PositionScale, PositionScale};
            points(t.points, 5, scales);
            const std::size_t health = r.Count(10'000'000);
            std::uint32_t f = 0;
            for (std::size_t i = 0; i < health && r.ok; ++i)
            {
                f += static_cast<std::uint32_t>(r.Var());
                const std::uint64_t h = r.Var();
                t.health.push_back({f, h == 0 ? -1.0 : static_cast<double>(h - 1) / HealthScale});
            }
            const double angles[3] = {AngleScale, AngleScale, AngleScale};
            points(t.rotation, 3, angles);
            d.actors.push_back(std::move(t));
        }
        const std::size_t stats = r.Count(10'000'000);
        frame = 0;
        std::int64_t current[6]{};
        for (std::size_t i = 0; i < stats && r.ok; ++i)
        {
            frame += static_cast<std::uint32_t>(r.Var());
            StatsRecord s{frame, r.U8()};
            for (int f = 0; f < 6; ++f)
                if (s.mask & (1u << f))
                {
                    current[f] += r.SVar();
                    s.values[f] = static_cast<double>(current[f]) / StatScales[f];
                }
            d.stats.push_back(s);
        }
        const std::size_t hits = r.Count(10'000'000);
        frame = 0;
        for (std::size_t i = 0; i < hits && r.ok; ++i)
        {
            frame += static_cast<std::uint32_t>(r.Var());
            d.hits.push_back({frame, static_cast<std::uint32_t>(r.Var())});
        }
        if (!r.ok || r.p != r.end) return std::nullopt;
        for (const auto& e : d.inputs)
            if (e.frame >= d.frameTimes.size() || e.action >= Actions.size()) return std::nullopt;
        return d;
    }

    void Decoded::Rotation(std::uint32_t frame, double out[3]) const
    {
        out[0] = out[1] = out[2] = 0;
        if (keyframes.empty()) return;
        auto it = std::upper_bound(keyframes.begin(), keyframes.end(), frame, [](std::uint32_t f, const Keyframe& k) { return f < k.frame; });
        const Keyframe& k = it == keyframes.begin() ? keyframes.front() : *(it - 1);
        double turn = 0, look = 0;
        if (frame > k.frame)
        {
            // Inputs are frame-sorted; sum those in (k.frame, frame].
            auto lo = std::upper_bound(inputs.begin(), inputs.end(), k.frame, [](std::uint32_t f, const InputEvent& e) { return f < e.frame; });
            for (auto e = lo; e != inputs.end() && e->frame <= frame; ++e)
            {
                if (e->action == AxisTurn) turn += e->value;
                else if (e->action == AxisLookUp) look += e->value;
            }
        }
        out[0] = ClampPitch(k.pitch + pitchPerUnit * look);
        out[1] = Wrap(k.yaw + yawPerUnit * turn);
        out[2] = k.roll;
    }
} // namespace aimmod::replay2
