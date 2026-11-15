#pragma once
#include <condition_variable>
#include <cstddef>
#include <deque>
#include <functional>
#include <mutex>
#include <string>
#include <thread>
#include <utility>

namespace aimmod {
// Log lines for a sink that may block (the UE4SS log file and console window): a worker
// writes them, so the game thread never waits on the sink. Not started, or stopped: the
// caller writes directly. A full queue drops lines and reports how many.
class AsyncLines {
public:
    using Sink = std::function<void(int level, const std::string& line)>;
    explicit AsyncLines(Sink sink, std::size_t limit = 2048) : m_sink(std::move(sink)), m_limit(limit) {}
    ~AsyncLines() { Stop(); }

    void Start()
    {
        std::lock_guard lock(m_mutex);
        if (m_running) return;
        m_running = true;
        m_worker = std::thread([this] { Run(); });
    }

    // Writes what is queued, then joins. Never call it under the loader lock.
    void Stop()
    {
        {
            std::lock_guard lock(m_mutex);
            if (!m_running) return;
            m_running = false;
        }
        m_wake.notify_all();
        if (m_worker.joinable()) m_worker.join();
    }

    void Post(int level, std::string line)
    {
        {
            std::lock_guard lock(m_mutex);
            if (m_running)
            {
                if (m_lines.size() >= m_limit) ++m_dropped;
                else m_lines.emplace_back(level, std::move(line));
                m_wake.notify_one();
                return;
            }
        }
        m_sink(level, line);
    }

private:
    void Run()
    {
        std::unique_lock lock(m_mutex);
        for (;;)
        {
            m_wake.wait(lock, [this] { return !m_running || !m_lines.empty() || m_dropped; });
            std::deque<std::pair<int, std::string>> lines;
            lines.swap(m_lines);
            const std::size_t dropped = std::exchange(m_dropped, 0);
            const bool last = !m_running;
            lock.unlock();
            for (const auto& [level, line] : lines) m_sink(level, line);
            if (dropped) m_sink(1, std::to_string(dropped) + " log lines dropped (the log could not keep up)");
            if (last) return;
            lock.lock();
        }
    }

    Sink m_sink;
    std::size_t m_limit;
    std::mutex m_mutex;
    std::condition_variable m_wake;
    std::deque<std::pair<int, std::string>> m_lines;
    std::size_t m_dropped{};
    bool m_running{};
    std::thread m_worker;
};
} // namespace aimmod
