#include "ServiceHost.hpp"

#include <aimmod/Supervisor.hpp>

#include <Windows.h>

#include <chrono>

namespace aimmod
{
    namespace
    {
        // Held by the service for its whole lifetime (Program.cs singleton).
        constexpr wchar_t ServiceMutex[] = L"Local\\AimMod.KovaaksNative.History";

        double Seconds()
        {
            return std::chrono::duration<double>(std::chrono::steady_clock::now().time_since_epoch()).count();
        }
    } // namespace

    void ServiceHost::Start(std::filesystem::path executable, std::filesystem::path logFile)
    {
        if (m_thread.joinable()) return;
        m_executable = std::move(executable);
        m_log = std::move(logFile);
        m_stop = false;
        m_thread = std::thread([this] { Run(); });
    }

    void ServiceHost::Stop()
    {
        if (!m_thread.joinable()) return;
        {
            std::lock_guard lock(m_mutex);
            m_stop = true;
        }
        m_wake.notify_all();
        m_thread.join();
        if (m_process) CloseHandle(m_process);
        m_process = nullptr;
    }

    std::string ServiceHost::Describe() const
    {
        switch (m_state.load())
        {
        case 1: return "missing";
        case 2: return "running (started " + std::to_string(m_launches.load()) + "x)";
        case 3: return "running (already started)";
        case 4: return "restarting";
        default: return "starting";
        }
    }

    bool ServiceHost::OtherInstanceRunning() const
    {
        HANDLE mutex = OpenMutexW(SYNCHRONIZE, FALSE, ServiceMutex);
        if (!mutex) return false;
        CloseHandle(mutex);
        return true;
    }

    bool ServiceHost::Launch()
    {
        SECURITY_ATTRIBUTES inherit{sizeof(inherit), nullptr, TRUE};
        HANDLE log = CreateFileW(m_log.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_DELETE, &inherit, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        STARTUPINFOEXW startup{};
        startup.StartupInfo.cb = sizeof(startup);
        // Inherit only the log handle, never other game handles.
        SIZE_T attributeSize = 0;
        InitializeProcThreadAttributeList(nullptr, 1, 0, &attributeSize);
        std::string attributes(attributeSize, '\0');
        auto* list = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attributes.data());
        bool haveList = false;
        if (log != INVALID_HANDLE_VALUE && InitializeProcThreadAttributeList(list, 1, 0, &attributeSize))
        {
            haveList = UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, &log, sizeof(log), nullptr, nullptr) != FALSE;
            if (!haveList) DeleteProcThreadAttributeList(list);
        }
        if (haveList)
        {
            startup.lpAttributeList = list;
            startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            startup.StartupInfo.hStdInput = nullptr;
            startup.StartupInfo.hStdOutput = log;
            startup.StartupInfo.hStdError = log;
        }
        std::wstring command = L"\"" + m_executable.wstring() + L"\" --exit-with-game";
        PROCESS_INFORMATION info{};
        const std::wstring directory = m_executable.parent_path().wstring();
        BOOL ok = CreateProcessW(m_executable.c_str(), command.data(), nullptr, nullptr, haveList,
                                 CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP | EXTENDED_STARTUPINFO_PRESENT, nullptr, directory.c_str(),
                                 &startup.StartupInfo, &info);
        if (haveList) DeleteProcThreadAttributeList(list);
        if (log != INVALID_HANDLE_VALUE) CloseHandle(log);
        if (!ok) return false;
        CloseHandle(info.hThread);
        if (m_process) CloseHandle(m_process);
        m_process = info.hProcess;
        ++m_launches;
        return true;
    }

    void ServiceHost::Run()
    {
        RestartBackoff backoff;
        double nextStart = 0;
        for (;;)
        {
            {
                std::unique_lock lock(m_mutex);
                if (m_wake.wait_for(lock, std::chrono::seconds(2), [this] { return m_stop; })) return;
            }
            const double now = Seconds();
            if (m_process && WaitForSingleObject(m_process, 0) == WAIT_TIMEOUT)
            {
                m_state = 2;
                continue;
            }
            if (m_process)
            {
                // Our service exited while the game is still running.
                CloseHandle(m_process);
                m_process = nullptr;
                nextStart = backoff.Exited(now);
                m_state = 4;
            }
            if (OtherInstanceRunning())
            {
                // Started by the user or a previous game session; not ours to manage.
                m_state = 3;
                continue;
            }
            if (now < nextStart) continue;
            std::error_code error;
            if (!std::filesystem::is_regular_file(m_executable, error))
            {
                m_state = 1;
                continue;
            }
            if (Launch())
            {
                backoff.Started(now);
                m_state = 2;
            }
            else nextStart = backoff.Exited(now);
        }
    }
} // namespace aimmod
