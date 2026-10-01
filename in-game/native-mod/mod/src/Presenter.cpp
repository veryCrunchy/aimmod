#include "Presenter.hpp"

#include "Log.hpp"
#include "Output.hpp"
#include "World.hpp"

#include <Unreal/CoreUObject/UObject/UnrealType.hpp>
#include <Unreal/UClass.hpp>
#include <Unreal/UObject.hpp>
#include <Unreal/UObjectGlobals.hpp>

#include <Windows.h>

#include <chrono>
#include <cstring>
#include <sstream>

namespace aimmod
{
    using namespace game;

    namespace
    {
        std::int64_t UnixMs()
        {
            return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
        }
        double Seconds() { return std::chrono::duration<double>(std::chrono::steady_clock::now().time_since_epoch()).count(); }

        bool ReadFileText(const std::filesystem::path& path, std::string& out, std::size_t limit)
        {
            HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                                      FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file == INVALID_HANDLE_VALUE) return false;
            out.assign(limit, '\0');
            DWORD read = 0;
            const BOOL ok = ReadFile(file, out.data(), static_cast<DWORD>(limit), &read, nullptr);
            CloseHandle(file);
            if (!ok || read >= limit) return false;
            out.resize(read);
            return true;
        }

        void WriteVector(std::uint8_t* value, const Param& p, const double v[3])
        {
            if (p.size == 12)
            {
                const float f[3] = {static_cast<float>(v[0]), static_cast<float>(v[1]), static_cast<float>(v[2])};
                std::memcpy(value, f, sizeof(f));
            }
            else if (p.size == 24) std::memcpy(value, v, 24);
        }

        // Fills K2_SetActorLocation(AndRotation) / SetFieldOfView inputs by name.
        auto Filler(const double* location, const double* rotation, double fov)
        {
            return [=](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Vector && location) WriteVector(value, p, location);
                else if (p.kind == Kind::Rotator && rotation) WriteVector(value, p, rotation); // Pitch, Yaw, Roll
                else if (p.kind == Kind::Float && p.size == 4)
                {
                    const float f = static_cast<float>(fov);
                    std::memcpy(value, &f, 4);
                }
                else if (p.kind == Kind::Bool && p.boolProperty)
                    p.boolProperty->SetPropertyValue(value, p.name == "bTeleport"); // no sweep, teleport
            };
        }
    } // namespace

    bool Presenter::Bind()
    {
        m_setLocationRotation.BindPath(STR("/Script/Engine.Actor:K2_SetActorLocationAndRotation"), Shape::Command);
        m_setLocation.BindPath(STR("/Script/Engine.Actor:K2_SetActorLocation"), Shape::Command);
        m_setFov.BindPath(STR("/Script/Engine.CameraComponent:SetFieldOfView"), Shape::Command);
        // AController::GetViewTarget (APlayerController does not redeclare it as a UFUNCTION).
        m_viewTarget.BindPath(STR("/Script/Engine.Controller:GetViewTarget"), Shape::Object);
        m_cameraActorClass = FindClass(STR("/Script/Engine.CameraActor"));
        m_cameraComponent.Bind(m_cameraActorClass, STR("CameraComponent"));
        m_ready = m_setLocationRotation.ok() && m_setLocation.ok() && m_setFov.ok() && m_viewTarget.ok() && m_cameraActorClass && m_cameraComponent.ok() &&
                  m_b.gamePaused.ok() && m_b.statics;
        Log(std::string("replay presenter ") + (m_ready ? "ready" : "unavailable") + ": setLocationAndRotation=" + (m_setLocationRotation.ok() ? "ok" : m_setLocationRotation.error()) +
            " setFov=" + (m_setFov.ok() ? "ok" : m_setFov.error()) + " viewTarget=" + (m_viewTarget.ok() ? "ok" : m_viewTarget.error()));
        return m_ready;
    }

    void Presenter::Start()
    {
        if (!m_ready || m_thread.joinable()) return;
        m_stop = false;
        m_thread = std::thread([this] { Read(); });
    }

    void Presenter::Stop()
    {
        if (!m_thread.joinable()) return;
        {
            std::lock_guard lock(m_mutex);
            m_stop = true;
        }
        m_wake.notify_all();
        m_thread.join();
    }

    void Presenter::Read()
    {
        const auto root = m_output.root();
        std::string text, proxies, lastText;
        std::uint64_t lastBeat = ~0ull;
        auto nextProxies = std::chrono::steady_clock::now();
        for (;;)
        {
            const bool playback = m_output.playbackActive();
            {
                std::unique_lock lock(m_mutex);
                if (m_wake.wait_for(lock, std::chrono::milliseconds(playback ? 16 : 250), [this] { return m_stop; })) break;
            }
            if (!playback)
            {
                std::lock_guard lock(m_mutex);
                m_frame.reset();
                continue;
            }
            if (ReadFileText(root / L"replay-frame.tsv", text, 524288) && text != lastText)
            {
                lastText = text;
                if (auto frame = ParsePlaybackFrame(text))
                {
                    std::lock_guard lock(m_mutex);
                    m_frame = std::make_shared<const PlaybackFrame>(std::move(*frame));
                }
            }
            if (std::chrono::steady_clock::now() >= nextProxies)
            {
                nextProxies = std::chrono::steady_clock::now() + std::chrono::milliseconds(250);
                if (ReadFileText(root / L"replay-proxies.tsv", proxies, 65536))
                {
                    std::lock_guard lock(m_mutex);
                    m_proxiesText = proxies;
                }
            }
            // Heartbeat for the Lua renderer: while this count advances it
            // leaves the view and target locations to the presenter.
            const std::uint64_t applies = m_active.load() ? m_applies.load() : 0;
            if (applies != lastBeat)
            {
                lastBeat = applies;
                std::shared_ptr<const PlaybackFrame> frame;
                {
                    std::lock_guard lock(m_mutex);
                    frame = m_frame;
                }
                const std::string body = "AIMMOD_PRESENTER_1\t" + std::to_string(frame ? frame->revision : 0) + "\t" + std::to_string(applies) + "\n";
                const auto path = root / L"replay-presenter.tsv";
                auto next = path;
                next += L".next";
                HANDLE file = CreateFileW(next.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
                if (file != INVALID_HANDLE_VALUE)
                {
                    DWORD written = 0;
                    WriteFile(file, body.data(), static_cast<DWORD>(body.size()), &written, nullptr);
                    CloseHandle(file);
                    if (!MoveFileExW(next.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING)) DeleteFileW(next.c_str());
                }
            }
        }
    }

    void Presenter::Resolve(const std::string& text)
    {
        m_resolvedProxies = text;
        m_camera.Reset();
        m_ghost.Reset();
        m_targets.clear();
        std::istringstream in(text);
        std::string line;
        if (!std::getline(in, line) || line.rfind("AIMMOD_PROXIES_1", 0) != 0) return;
        while (std::getline(in, line))
        {
            if (!line.empty() && line.back() == '\r') line.pop_back();
            const auto tab = line.find('\t');
            if (tab == std::string::npos) continue;
            const std::string kind = line.substr(0, tab);
            std::string rest = line.substr(tab + 1);
            std::uint32_t id = 0;
            if (kind == "actor")
            {
                const auto second = rest.find('\t');
                if (second == std::string::npos) continue;
                id = static_cast<std::uint32_t>(std::strtoul(rest.substr(0, second).c_str(), nullptr, 10));
                rest = rest.substr(second + 1);
                if (id == 0) continue;
            }
            else if (kind != "camera" && kind != "ghost") continue;
            UObject* object = RC::Unreal::UObjectGlobals::StaticFindObject<UObject*>(nullptr, nullptr, Widen(rest));
            if (!object || !IsLiveInstance(object)) continue;
            if (kind == "camera")
            {
                if (object->IsA(m_cameraActorClass)) m_camera = object;
            }
            else if (kind == "ghost") m_ghost = object;
            else m_targets[id] = object;
        }
    }

    void Presenter::Report(double now)
    {
        if (now < m_nextReport) return;
        const double span = 5.0 - (m_nextReport - now > 0 ? m_nextReport - now : 0);
        (void)span;
        if (m_periodApplies > 0)
        {
            char line[256];
            std::snprintf(line, sizeof(line), "replay presenter: %.0f applies/s, frame dt avg %.2f ms max %.2f ms, playback step error max %.3f ms, window clamps %llu, targets %zu",
                          m_periodApplies / 5.0, m_dtSum / static_cast<double>(m_periodApplies) * 1000.0, m_dtMax * 1000.0, m_stepErrorMax * 1000.0,
                          static_cast<unsigned long long>(m_windowClamps), m_targets.size());
            Log(line);
        }
        m_nextReport = now + 5.0;
        m_periodApplies = 0;
        m_dtSum = m_dtMax = m_stepErrorMax = 0;
        m_windowClamps = 0;
    }

    void Presenter::Apply()
    {
        if (!m_ready) return;
        std::shared_ptr<const PlaybackFrame> frame;
        std::string proxies;
        {
            std::lock_guard lock(m_mutex);
            frame = m_frame;
            if (m_proxiesText != m_resolvedProxies) proxies = m_proxiesText;
        }
        if (!proxies.empty()) Resolve(proxies);
        const double now = Seconds();
        auto idle = [&](const char* reason) {
            if (m_active) Report(now + 1e9); // flush the period when presentation ends
            m_active = false;
            m_lastApply = -1;
            // Say once why a playing replay is not presented natively.
            if (frame && frame->visible && frame->playing && reason != m_idleReason)
            {
                m_idleReason = reason;
                Log(std::string("replay presenter idle: ") + reason);
            }
        };
        if (!frame || !frame->visible || !frame->playing || frame->motion.size() < 2 || !frame->clockUnixMs) return idle("no motion window");
        UObject* camera = m_camera.Get();
        UObject* player = m_scene.Player();
        if (!camera || !player) return idle(camera ? "player unavailable" : "replay camera not bound (replay-proxies.tsv)");
        // Only the replay's own camera while the player looks through it, in
        // a paused world.
        if (m_viewTarget.Object(player) != camera) return idle("replay camera is not the view target");
        if (!m_b.gamePaused.Bool(m_b.statics, player).value_or(false)) return idle("game not paused");
        UObject* component = m_cameraComponent.Object(camera);
        if (!component) return idle("camera component unavailable");

        const double t = PlaybackTime(*frame, UnixMs());
        double pose[7];
        if (!CameraAt(*frame, t, pose)) return idle("no pose");
        const double location[3] = {pose[0], pose[1], pose[2]};
        const double rotation[3] = {pose[3], pose[4], pose[5]};
        if (!m_setLocationRotation.Call(camera, Filler(location, rotation, 0)) || !m_setFov.Call(component, Filler(nullptr, nullptr, pose[6]))) return idle("camera update failed");
        // Comparison run: its aim point on the same timeline.
        if (UObject* ghost = m_ghost.Get(); ghost && !frame->ghostMotion.empty())
        {
            double g[7], aim[3];
            const double gt = std::clamp(t, frame->ghostMotion.front().t, frame->ghostMotion.back().t);
            if (CameraAt(frame->ghostMotion, gt, g))
            {
                AimPoint(g, 800.0, aim);
                m_setLocation.Call(ghost, Filler(aim, nullptr, 0));
            }
        }
        const double ahead = std::clamp(t - frame->time, 0.0, 0.1);
        for (const auto& target : frame->targets)
        {
            if (!target.moving) continue;
            auto it = m_targets.find(target.id);
            UObject* proxy = it == m_targets.end() ? nullptr : it->second.Get();
            if (!proxy) continue;
            const double p[3] = {target.position[0] + target.velocity[0] * ahead, target.position[1] + target.velocity[1] * ahead,
                                 target.position[2] + target.velocity[2] * ahead};
            m_setLocation.Call(proxy, Filler(p, nullptr, 0));
        }

        // Measurements: apply cadence and playback clock smoothness.
        if (m_active && m_lastApply >= 0)
        {
            const double dt = now - m_lastApply;
            m_dtSum += dt;
            m_dtMax = std::max(m_dtMax, dt);
            const double expected = dt * frame->speed;
            if (t < frame->motion.back().t) m_stepErrorMax = std::max(m_stepErrorMax, std::fabs((t - m_lastPlayback) - expected));
            else ++m_windowClamps;
        }
        else if (!m_active)
        {
            m_nextReport = now + 5.0;
            Log("replay presenter: presenting (targets bound " + std::to_string(m_targets.size()) + ")");
        }
        m_active = true;
        m_idleReason = nullptr;
        m_lastApply = now;
        m_lastPlayback = t;
        ++m_applies;
        ++m_periodApplies;
        Report(now);
    }
} // namespace aimmod
