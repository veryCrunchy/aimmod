#pragma once
// Native replay presentation at engine frame rate. The Lua renderer owns the
// replay scene (paused world, its own camera actor and inert target proxies);
// this presenter only moves those replay-owned actors, before every engine
// tick, to the pose at the exact playback instant (publication clock +
// elapsed wall time) from the service's protocol 6 motion window. It never
// touches gameplay actors and runs only while the game is paused and the
// camera it moves is the player's current view target.
#include "GameBindings.hpp"

#include <aimmod/PlaybackFrame.hpp>

#include <Unreal/FWeakObjectPtr.hpp>

#include <atomic>
#include <condition_variable>
#include <filesystem>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>

namespace aimmod
{
    class Output;
    namespace game
    {
        struct Bindings;
        struct Scene;
    } // namespace game

    class Presenter
    {
    public:
        Presenter(game::Bindings& bindings, game::Scene& scene, Output& output) : m_b(bindings), m_scene(scene), m_output(output) {}
        ~Presenter() { Stop(); }
        bool Bind();          // game thread, once
        void Start();         // reader thread
        void Stop();
        void Apply();         // game thread, before every engine tick
        bool ready() const { return m_ready; }

    private:
        void Read();
        void Resolve(const std::string& proxies);
        void Report(double now);

        game::Bindings& m_b;
        game::Scene& m_scene;
        Output& m_output;
        bool m_ready{};
        game::Getter m_setLocationRotation, m_setLocation, m_setFov, m_viewTarget;
        game::Field m_cameraComponent;
        game::UClass* m_cameraActorClass{};

        // Reader thread -> game thread.
        std::thread m_thread;
        std::mutex m_mutex;
        std::condition_variable m_wake;
        bool m_stop{};
        std::shared_ptr<const PlaybackFrame> m_frame;
        std::string m_proxiesText;

        // Game thread.
        std::string m_resolvedProxies;
        RC::Unreal::FWeakObjectPtr m_camera;
        std::unordered_map<std::uint32_t, RC::Unreal::FWeakObjectPtr> m_targets;
        std::atomic<bool> m_active{false};
        std::atomic<std::uint64_t> m_applies{0};
        std::uint64_t m_windowClamps{};
        double m_lastApply{-1}, m_lastPlayback{-1}, m_lastSpeed{1};
        double m_dtSum{}, m_dtMax{}, m_stepErrorMax{};
        std::uint64_t m_periodApplies{};
        double m_nextReport{}, m_nextBeat{};
        const char* m_idleReason{};
    };
} // namespace aimmod
