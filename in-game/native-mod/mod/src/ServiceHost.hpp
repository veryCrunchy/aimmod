#pragma once
// Starts the AimMod native service next to the mod and restarts it with
// back-off if it exits while the game runs. Never stops or kills it: the
// service exits by itself (--exit-with-game) after the game closes.
#include <atomic>
#include <condition_variable>
#include <filesystem>
#include <mutex>
#include <string>
#include <thread>

namespace aimmod
{
    class ServiceHost
    {
    public:
        ~ServiceHost() { Stop(); }
        void Start(std::filesystem::path executable, std::filesystem::path logFile);
        void Stop();
        // Short status for the compatibility summary.
        std::string Describe() const;

    private:
        void Run();
        bool OtherInstanceRunning() const;
        bool Launch();

        std::filesystem::path m_executable, m_log;
        std::thread m_thread;
        std::mutex m_mutex;
        std::condition_variable m_wake;
        bool m_stop{};
        void* m_process{};
        std::atomic<int> m_launches{0};
        std::atomic<int> m_state{0}; // 0 idle, 1 missing, 2 running (ours), 3 running (external), 4 waiting to restart
    };
} // namespace aimmod
