#include "Pipe.hpp"

#include "Codec.hpp"

#include <sddl.h>

#include <memory>
#include <vector>

#pragma comment(lib, "advapi32.lib")

namespace bridge
{
    namespace
    {
        // "D:P(A;;GA;;;<current user>)(A;;GA;;;SY)": the current user and SYSTEM only.
        bool CurrentUserOnly(SECURITY_ATTRIBUTES& sa, PSECURITY_DESCRIPTOR& descriptor)
        {
            HANDLE token = nullptr;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
            DWORD size = 0;
            GetTokenInformation(token, TokenUser, nullptr, 0, &size);
            std::vector<std::uint8_t> buffer(size);
            const bool ok = size && GetTokenInformation(token, TokenUser, buffer.data(), size, &size);
            CloseHandle(token);
            if (!ok) return false;
            LPWSTR sid = nullptr;
            if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid, &sid)) return false;
            const std::wstring sddl = std::wstring(L"D:P(A;;GA;;;") + sid + L")(A;;GA;;;SY)";
            LocalFree(sid);
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor, nullptr)) return false;
            sa.nLength = sizeof(sa);
            sa.lpSecurityDescriptor = descriptor;
            sa.bInheritHandle = FALSE;
            return true;
        }
    } // namespace

    PipeServer::PipeServer(std::wstring name, MessageFn onMessage, StateFn onState, LogFn log)
        : m_name(std::move(name)), m_onMessage(std::move(onMessage)), m_onState(std::move(onState)), m_log(std::move(log))
    {
    }

    PipeServer::~PipeServer() { Stop(); }

    bool PipeServer::Start()
    {
        SECURITY_ATTRIBUTES sa{};
        PSECURITY_DESCRIPTOR descriptor = nullptr;
        if (!CurrentUserOnly(sa, descriptor))
        {
            m_log("pipe: could not build the current-user security descriptor; IPC disabled");
            return false;
        }
        m_pipe = CreateNamedPipeW(m_name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
                                  PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 128 * 1024, 128 * 1024, 0, &sa);
        LocalFree(descriptor);
        if (m_pipe == INVALID_HANDLE_VALUE)
        {
            m_log("pipe: could not create the pipe (another AimModSteam running?); IPC disabled");
            return false;
        }
        m_stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        m_readEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        m_thread = std::thread([this] { Run(); });
        return true;
    }

    void PipeServer::Stop()
    {
        if (m_stop) SetEvent(m_stop);
        if (m_thread.joinable()) m_thread.join();
        if (m_pipe != INVALID_HANDLE_VALUE)
        {
            CancelIoEx(m_pipe, nullptr);
            CloseHandle(m_pipe);
            m_pipe = INVALID_HANDLE_VALUE;
        }
        if (m_stop) CloseHandle(m_stop);
        if (m_readEvent) CloseHandle(m_readEvent);
        m_stop = m_readEvent = nullptr;
    }

    bool PipeServer::ReadExact(void* buffer, DWORD size)
    {
        auto* p = static_cast<std::uint8_t*>(buffer);
        while (size > 0)
        {
            OVERLAPPED ov{};
            ov.hEvent = m_readEvent;
            ResetEvent(m_readEvent);
            DWORD got = 0;
            if (!ReadFile(m_pipe, p, size, &got, &ov))
            {
                if (GetLastError() != ERROR_IO_PENDING) return false;
                HANDLE waits[2] = {m_stop, m_readEvent};
                if (WaitForMultipleObjects(2, waits, FALSE, INFINITE) != WAIT_OBJECT_0 + 1)
                {
                    CancelIoEx(m_pipe, &ov);
                    GetOverlappedResult(m_pipe, &ov, &got, TRUE);
                    return false;
                }
                if (!GetOverlappedResult(m_pipe, &ov, &got, FALSE)) return false;
            }
            if (got == 0) return false;
            p += got;
            size -= got;
        }
        return true;
    }

    void PipeServer::Disconnect()
    {
        if (m_connected.exchange(false))
        {
            std::lock_guard lock(m_writeMutex);
            FlushFileBuffers(m_pipe);
            DisconnectNamedPipe(m_pipe);
            m_onState(false);
        }
        else DisconnectNamedPipe(m_pipe);
    }

    void PipeServer::Run()
    {
        while (WaitForSingleObject(m_stop, 0) != WAIT_OBJECT_0)
        {
            OVERLAPPED ov{};
            ov.hEvent = m_readEvent;
            ResetEvent(m_readEvent);
            bool connected = ConnectNamedPipe(m_pipe, &ov) != FALSE;
            if (!connected)
            {
                const DWORD error = GetLastError();
                if (error == ERROR_PIPE_CONNECTED) connected = true;
                else if (error == ERROR_IO_PENDING)
                {
                    HANDLE waits[2] = {m_stop, m_readEvent};
                    if (WaitForMultipleObjects(2, waits, FALSE, INFINITE) != WAIT_OBJECT_0 + 1)
                    {
                        CancelIoEx(m_pipe, &ov);
                        DWORD ignored = 0;
                        GetOverlappedResult(m_pipe, &ov, &ignored, TRUE);
                        return;
                    }
                    DWORD ignored = 0;
                    connected = GetOverlappedResult(m_pipe, &ov, &ignored, FALSE) != FALSE;
                }
                else
                {
                    DisconnectNamedPipe(m_pipe);
                    if (WaitForSingleObject(m_stop, 500) == WAIT_OBJECT_0) return;
                    continue;
                }
            }
            if (!connected) continue;
            m_connected = true;
            m_log("pipe: service connected");
            m_onState(true);

            while (true)
            {
                std::uint8_t header[4];
                if (!ReadExact(header, 4)) break;
                const std::uint32_t length = header[0] | (header[1] << 8) | (header[2] << 16) | (static_cast<std::uint32_t>(header[3]) << 24);
                if (length == 0 || length > MaxPipeFrame)
                {
                    m_log("pipe: frame length out of range; dropping the client");
                    break;
                }
                std::string body(length, '\0');
                if (!ReadExact(body.data(), length)) break;
                m_onMessage(std::move(body));
            }
            m_log("pipe: service disconnected");
            Disconnect();
        }
    }

    bool PipeServer::Send(const std::string& json)
    {
        if (!m_connected.load() || json.empty() || json.size() > MaxPipeFrame) return false;
        std::lock_guard lock(m_writeMutex);
        std::string frame(4, '\0');
        const auto n = static_cast<std::uint32_t>(json.size());
        frame[0] = static_cast<char>(n & 0xFF);
        frame[1] = static_cast<char>((n >> 8) & 0xFF);
        frame[2] = static_cast<char>((n >> 16) & 0xFF);
        frame[3] = static_cast<char>((n >> 24) & 0xFF);
        frame += json;
        HANDLE done = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        OVERLAPPED ov{};
        ov.hEvent = done;
        DWORD wrote = 0;
        bool ok = WriteFile(m_pipe, frame.data(), static_cast<DWORD>(frame.size()), &wrote, &ov) != FALSE;
        if (!ok && GetLastError() == ERROR_IO_PENDING)
        {
            // A client that stops reading for 2 s is treated as gone.
            if (WaitForSingleObject(done, 2000) == WAIT_OBJECT_0) ok = GetOverlappedResult(m_pipe, &ov, &wrote, FALSE) != FALSE;
            else
            {
                CancelIoEx(m_pipe, &ov);
                GetOverlappedResult(m_pipe, &ov, &wrote, TRUE);
                ok = false;
            }
        }
        CloseHandle(done);
        return ok && wrote == frame.size();
    }
} // namespace bridge
