#pragma once
// Named pipe server for the AimMod native service: \\.\pipe\aimmod-steam-v1.
// One local client at a time, current user only (explicit DACL), remote
// clients rejected. Frames are a 4-byte little-endian length followed by that
// many bytes of UTF-8 JSON (1..MaxPipeFrame).

#include <Windows.h>

#include <atomic>
#include <functional>
#include <mutex>
#include <string>
#include <thread>

namespace bridge
{
    class PipeServer
    {
    public:
        using MessageFn = std::function<void(std::string&&)>;
        using StateFn = std::function<void(bool connected)>;
        using LogFn = std::function<void(const std::string&)>;

        PipeServer(std::wstring name, MessageFn onMessage, StateFn onState, LogFn log);
        ~PipeServer();
        PipeServer(const PipeServer&) = delete;
        PipeServer& operator=(const PipeServer&) = delete;

        bool Start();
        void Stop();
        // Thread-safe. Drops the message when no client is connected.
        bool Send(const std::string& json);
        bool Connected() const { return m_connected.load(); }

    private:
        void Run();
        bool ReadExact(void* buffer, DWORD size);
        void Disconnect();

        std::wstring m_name;
        MessageFn m_onMessage;
        StateFn m_onState;
        LogFn m_log;
        HANDLE m_pipe = INVALID_HANDLE_VALUE;
        HANDLE m_stop = nullptr;
        HANDLE m_readEvent = nullptr;
        std::thread m_thread;
        std::mutex m_writeMutex;
        std::atomic<bool> m_connected{false};
        // A write timed out or was cut short: the stream may hold a partial
        // frame, so nothing more is written and the client is dropped.
        std::atomic<bool> m_broken{false};
    };
} // namespace bridge
