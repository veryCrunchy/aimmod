#include "Output.hpp"

#include "Log.hpp"

#include <aimmod/Formats.hpp>
#include <aimmod/Settings.hpp>

#include <Windows.h>
#include <ShlObj.h>

#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>

namespace aimmod
{
    namespace
    {
        constexpr wchar_t SharedLiveName[] = L"Local\\AimMod.KovaaksNative.Live";
        constexpr std::size_t SharedLiveSize = 8192;
        constexpr std::size_t SharedHeader = 32;

        std::uint64_t NowMs()
        {
            return static_cast<std::uint64_t>(
                std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
        }

        std::int64_t UnixMs()
        {
            return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
        }

        bool ReadSmall(const std::filesystem::path& path, std::string& out, std::size_t limit)
        {
            HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                                      FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file == INVALID_HANDLE_VALUE) return false;
            out.assign(limit, '\0');
            DWORD read = 0;
            BOOL ok = ReadFile(file, out.data(), static_cast<DWORD>(limit), &read, nullptr);
            CloseHandle(file);
            if (!ok) return false;
            out.resize(read);
            return true;
        }

        bool WriteAll(HANDLE file, const std::string& data)
        {
            std::size_t done = 0;
            while (done < data.size())
            {
                DWORD written = 0;
                DWORD chunk = static_cast<DWORD>(std::min<std::size_t>(data.size() - done, 1u << 20));
                if (!WriteFile(file, data.data() + done, chunk, &written, nullptr) || written == 0) return false;
                done += written;
            }
            return true;
        }
    } // namespace

    std::filesystem::path Output::DefaultRoot()
    {
        PWSTR local = nullptr;
        std::filesystem::path root;
        if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local)) && local) root = local;
        if (local) CoTaskMemFree(local);
        if (root.empty()) return {};
        return root / L"AimMod" / L"KovaaksNative";
    }

    bool Output::Start(const std::filesystem::path& root, std::string version)
    {
        if (m_thread.joinable() || root.empty()) return false;
        m_root = root;
        m_version = std::move(version);
        {
            // <game>\FPSAimTrainer\Binaries\Win64\<exe> -> <game>\FPSAimTrainer\stats
            wchar_t exe[MAX_PATH * 4]{};
            const DWORD length = GetModuleFileNameW(nullptr, exe, static_cast<DWORD>(std::size(exe)));
            if (length > 0 && length < std::size(exe)) m_stats = std::filesystem::path(exe).parent_path().parent_path().parent_path() / L"stats";
        }
        std::error_code error;
        std::filesystem::create_directories(m_root / L"replays", error);
        if (!std::filesystem::is_directory(m_root, error)) return false;
        // Unfinished recordings from earlier sessions (either recorder) are
        // never published; remove ones not touched for ten minutes.
        const auto cutoff = std::filesystem::file_time_type::clock::now() - std::chrono::minutes(10);
        for (const auto& entry : std::filesystem::directory_iterator(m_root / L"replays", error))
        {
            std::error_code itemError;
            if (entry.path().extension() != L".partial" || !entry.is_regular_file(itemError)) continue;
            if (entry.last_write_time(itemError) < cutoff && !itemError) std::filesystem::remove(entry.path(), itemError);
        }
        m_mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, static_cast<DWORD>(SharedLiveSize), SharedLiveName);
        if (m_mapping) m_view = MapViewOfFile(m_mapping, FILE_MAP_WRITE, 0, 0, SharedLiveSize);
        if (m_view)
        {
            auto* bytes = static_cast<std::uint8_t*>(m_view);
            const std::uint32_t magic = 0x564C4D41u, layout = 1u; // "AMLV"
            std::memcpy(bytes, &magic, 4);
            std::memcpy(bytes + 4, &layout, 4);
        }
        m_stop = false;
        Periodic(true);
        m_thread = std::thread([this] { Run(); });
        return true;
    }

    void Output::Stop()
    {
        if (!m_thread.joinable()) return;
        {
            std::lock_guard lock(m_mutex);
            m_stop = true;
        }
        m_wake.notify_all();
        m_thread.join();
        // Retract the handshake so the Lua mod resumes immediately.
        DeleteFileW((m_root / L"core-active.tsv").c_str());
        DeleteFileW((m_root / L"core-scene.json").c_str());
        if (m_view) UnmapViewOfFile(m_view);
        if (m_mapping) CloseHandle(m_mapping);
        m_view = m_mapping = nullptr;
    }

    void Output::AppendJournal(std::string line)
    {
        {
            std::lock_guard lock(m_mutex);
            m_jobs.push_back({Job::Kind::Journal, std::move(line)});
        }
        m_wake.notify_one();
    }

    void Output::PublishLive(std::string body)
    {
        std::lock_guard lock(m_mutex);
        if (body == m_live) return;
        m_live = std::move(body);
        m_liveDirty = true;
    }

    std::optional<std::variant<GameCommand, CommandError>> Output::TakeCommand()
    {
        std::lock_guard lock(m_mutex);
        if (m_commands.empty()) return std::nullopt;
        auto command = std::move(m_commands.front());
        m_commands.pop_front();
        return command;
    }

    void Output::PublishCommandResult(std::string body)
    {
        {
            std::lock_guard lock(m_mutex);
            m_results.push_back(std::move(body));
        }
        m_wake.notify_one();
    }

    void Output::ReadCommand(std::uint64_t now)
    {
        if (now - m_lastCommandCheck < 100) return;
        m_lastCommandCheck = now;
        std::string text;
        if (!ReadSmall(m_root / L"core-command.tsv", text, 4097) || text == m_commandText) return;
        m_commandText = text;
        auto parsed = ParseGameCommand(text);
        const std::uint64_t sequence = std::holds_alternative<GameCommand>(parsed) ? std::get<GameCommand>(parsed).sequence : std::get<CommandError>(parsed).sequence;
        // A request left from an earlier session is never replayed.
        if (!m_commandPrimed)
        {
            m_commandPrimed = true;
            m_commandSequence = sequence;
            return;
        }
        if (sequence != 0 && sequence <= m_commandSequence) return;
        if (sequence != 0) m_commandSequence = sequence;
        std::lock_guard lock(m_mutex);
        if (m_commands.size() < 8) m_commands.push_back(std::move(parsed));
    }

    void Output::PublishSelfPose(std::string body)
    {
        std::lock_guard lock(m_mutex);
        m_selfPose = std::move(body);
        m_selfPoseDirty = true;
    }

    void Output::PublishPointer(std::string body)
    {
        {
            std::lock_guard lock(m_mutex);
            m_pointer = std::move(body);
            m_pointerDirty = true;
        }
        m_wake.notify_one();
    }

    void Output::PublishSelfShots(std::string body)
    {
        {
            std::lock_guard lock(m_mutex);
            m_selfShots = std::move(body);
            m_selfShotsDirty = true;
        }
        m_wake.notify_one();
    }

    void Output::AuditQuitStats(std::string scenario)
    {
        std::lock_guard lock(m_mutex);
        m_quitStats = QuitStats{std::move(scenario), std::filesystem::file_time_type::clock::now() - std::chrono::seconds(2), NowMs() + 15000, false};
    }

    void Output::CheckQuitStats(std::uint64_t now)
    {
        std::optional<QuitStats> audit;
        {
            std::lock_guard lock(m_mutex);
            audit = m_quitStats;
        }
        if (!audit || m_stats.empty()) return;
        std::error_code error;
        for (const auto& entry : std::filesystem::directory_iterator(m_stats, error))
        {
            std::error_code itemError;
            if (!entry.is_regular_file(itemError) || entry.last_write_time(itemError) < audit->since || itemError) continue;
            // Wide names only: path::string() throws outside the ANSI code page.
            const std::wstring name = entry.path().filename().wstring();
            if (IsChallengeStatsFile(std::wstring_view(name)) && name.rfind(Widen(audit->scenario) + L" - Challenge - ", 0) == 0) audit->found = true;
        }
        if (!audit->found && now < audit->until)
        {
            std::lock_guard lock(m_mutex);
            if (m_quitStats) m_quitStats->found = false;
            return;
        }
        Log(audit->found ? "quit-run audit: the game wrote a challenge stats CSV after the quit - UNEXPECTED, please report"
                         : "quit-run audit: no challenge stats CSV written (as expected)");
        std::lock_guard lock(m_mutex);
        m_quitStats.reset();
    }

    Output::RoundStateSnapshot Output::roundState() const
    {
        std::lock_guard lock(const_cast<std::mutex&>(m_mutex));
        return {m_roundState, m_roundStateVersion};
    }

    void Output::ReadRoundState(std::uint64_t now)
    {
        WIN32_FILE_ATTRIBUTE_DATA data{};
        const auto path = m_root / L"round-state.tsv";
        std::uint64_t stamp = 0;
        if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &data))
            stamp = (static_cast<std::uint64_t>(data.ftLastWriteTime.dwHighDateTime) << 32) | data.ftLastWriteTime.dwLowDateTime;
        FILETIME nowFile{};
        GetSystemTimeAsFileTime(&nowFile);
        const std::uint64_t nowStamp = (static_cast<std::uint64_t>(nowFile.dwHighDateTime) << 32) | nowFile.dwLowDateTime;
        if (stamp == 0 || nowStamp - stamp >= 5ull * 10000000ull)
        {
            std::lock_guard lock(m_mutex);
            if (m_roundState)
            {
                m_roundState.reset();
                ++m_roundStateVersion;
            }
            m_roundStateStamp = stamp;
            return;
        }
        m_playStateSeenAt = now; // keeps the writer on its fast cadence
        if (stamp == m_roundStateStamp) return;
        std::string text;
        if (!ReadSmall(path, text, 4097)) return;
        auto parsed = ParseRoundState(text);
        std::lock_guard lock(m_mutex);
        m_roundStateStamp = stamp;
        if (parsed) m_roundState = std::make_shared<const RoundState>(std::move(*parsed));
        else m_roundState.reset();
        ++m_roundStateVersion;
    }

    Output::PlayStateSnapshot Output::playState() const
    {
        std::lock_guard lock(const_cast<std::mutex&>(m_mutex));
        return {m_playState, m_playStateVersion};
    }

    // Polled every writer pass (<= 20 ms while match files are in use): a
    // changed file is parsed; a file not rewritten for 5 s counts as gone.
    void Output::ReadPlayState(std::uint64_t now)
    {
        WIN32_FILE_ATTRIBUTE_DATA data{};
        const auto path = m_root / L"play-state.tsv";
        std::uint64_t stamp = 0;
        if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &data))
            stamp = (static_cast<std::uint64_t>(data.ftLastWriteTime.dwHighDateTime) << 32) | data.ftLastWriteTime.dwLowDateTime;
        FILETIME nowFile{};
        GetSystemTimeAsFileTime(&nowFile);
        const std::uint64_t nowStamp = (static_cast<std::uint64_t>(nowFile.dwHighDateTime) << 32) | nowFile.dwLowDateTime;
        const bool fresh = stamp != 0 && nowStamp - stamp < 5ull * 10000000ull;
        if (!fresh)
        {
            std::lock_guard lock(m_mutex);
            if (m_playState)
            {
                m_playState.reset();
                ++m_playStateVersion;
            }
            m_playStateStamp = stamp;
            return;
        }
        m_playStateSeenAt = now;
        if (stamp == m_playStateStamp) return;
        std::string text;
        if (!ReadSmall(path, text, 4097)) return; // locked mid-replace: retry next pass
        auto parsed = ParsePlayState(text);
        std::lock_guard lock(m_mutex);
        m_playStateStamp = stamp;
        if (parsed) m_playState = std::make_shared<const PlayState>(std::move(*parsed));
        else m_playState.reset();
        ++m_playStateVersion;
    }

    Output::OverlayInputs Output::overlay() const
    {
        std::lock_guard lock(const_cast<std::mutex&>(m_mutex));
        return m_overlay;
    }

    // Every writer pass: the notice file when its write time changes (it changes only
    // when the service has something new); the URL, the panel state and the switch
    // twice a second.
    void Output::ReadOverlayInputs(std::uint64_t now)
    {
        WIN32_FILE_ATTRIBUTE_DATA data{};
        const auto path = m_root / L"multiplayer-notify.json";
        std::uint64_t stamp = 0;
        if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &data))
            stamp = (static_cast<std::uint64_t>(data.ftLastWriteTime.dwHighDateTime) << 32) | data.ftLastWriteTime.dwLowDateTime;
        bool changed = false;
        std::optional<overlay::Notice> notice;
        bool noticeRead = false;
        if (stamp != m_notifyStamp)
        {
            std::string text;
            if (stamp == 0 || ReadSmall(path, text, overlay::MaxNoticeBytes + 1))
            {
                m_notifyStamp = stamp;
                if (text != m_notifyText)
                {
                    m_notifyText = std::move(text);
                    notice = overlay::ParseNotice(m_notifyText);
                    noticeRead = true;
                }
            }
        }
        std::string url;
        bool panel = false, native = true, lua = false, slowRead = false, gameEnabled = false, hudNative = true;
        std::string hudUrl;
        if (now - m_lastOverlayCheck >= 500)
        {
            m_lastOverlayCheck = now;
            slowRead = true;
            std::string text;
            if (ReadSmall(m_root / L"live-overlay-url.txt", text, 513))
            {
                if (auto u = overlay::NoticeUrl(text)) url = *u;
                if (auto h = overlay::HudUrl(text)) hudUrl = *h;
            }
            text.clear();
            if (ReadSmall(m_root / L"overlay-settings.json", text, (64u << 10) + 1)) gameEnabled = overlay::GameEnabled(text);
            text.clear();
            if (ReadSmall(m_root / L"aimmod-panel.tsv", text, 128)) panel = overlay::PanelOpen(text, static_cast<std::int64_t>(std::time(nullptr)));
            text.clear();
            if (ReadSmall(m_root / L"ui-host.tsv", text, 512))
            {
                native = overlay::ParseUiHost(text) == overlay::Host::Native;
                hudNative = overlay::ParseUiHost(text, "hud") == overlay::Host::Native;
            }
            text.clear();
            if (ReadSmall(m_root / L"lua-notice.tsv", text, 128)) lua = overlay::LuaLayerActive(text, static_cast<std::int64_t>(std::time(nullptr)));
        }
        std::lock_guard lock(m_mutex);
        if (noticeRead)
        {
            const bool differs = notice.has_value() != m_overlay.notice.has_value() ||
                                 (notice && (notice->content != m_overlay.notice->content || notice->full != m_overlay.notice->full ||
                                             notice->interactive != m_overlay.notice->interactive || notice->cursor != m_overlay.notice->cursor ||
                                             notice->swallowMenu != m_overlay.notice->swallowMenu || notice->boardHeld != m_overlay.notice->boardHeld));
            if (differs)
            {
                m_overlay.notice = notice;
                changed = true;
            }
        }
        if (slowRead && (url != m_overlay.url || panel != m_overlay.panelOpen || native != m_overlay.native || lua != m_overlay.luaLayer ||
                         hudUrl != m_overlay.hudUrl || gameEnabled != m_overlay.gameEnabled || hudNative != m_overlay.hudNative))
        {
            m_overlay.hudUrl = std::move(hudUrl);
            m_overlay.gameEnabled = gameEnabled;
            m_overlay.hudNative = hudNative;
            m_overlay.luaLayer = lua;
            m_overlay.url = std::move(url);
            m_overlay.panelOpen = panel;
            m_overlay.native = native;
            changed = true;
        }
        if (changed) ++m_overlay.version;
    }

    void Output::SetCosmeticsSources(std::filesystem::path catalogDir, std::filesystem::path paksDir)
    {
        m_catalogDir = std::move(catalogDir);
        m_paksDir = std::move(paksDir);
    }

    Output::CosmeticsInputs Output::cosmetics() const
    {
        std::lock_guard lock(const_cast<std::mutex&>(m_mutex));
        return m_cosmetics;
    }

    // Writer thread, once: catalog.json counts only when it matches the
    // manifest; pak items only when their pak does.
    void Output::LoadCosmeticsLibrary()
    {
        m_libraryLoaded = true;
        auto library = std::make_shared<CosmeticsLibrary>();
        std::string manifestText, catalogText;
        if (m_catalogDir.empty() || !ReadSmall(m_catalogDir / L"catalog-manifest.json", manifestText, (1u << 20) + 1) ||
            !ReadSmall(m_catalogDir / L"catalog.json", catalogText, (4u << 20) + 1))
        {
            library->status = "not installed";
            Log("cosmetics: catalog not installed");
        }
        else if (std::string error; auto manifest = cosmetics::ParseManifest(manifestText, &error))
        {
            auto check = cosmetics::VerifyManifest(*manifest, m_catalogDir, m_paksDir);
            for (const std::string& problem : check.problems) Log("cosmetics: " + problem);
            if (!check.catalogVerified) library->status = "catalog does not match its manifest";
            else if (auto catalog = cosmetics::ParseCatalog(catalogText, &error))
            {
                std::vector<std::string> problems = catalog->errors;
                cosmetics::ApplyManifestItems(*manifest, *catalog, problems);
                library->index = cosmetics::BuildIndex(catalog->items, problems);
                library->verifiedPaks = std::move(check.verifiedPaks);
                // Runtime meshes: read once, hashed and parsed from the same bytes.
                for (const cosmetics::ManifestFile& f : manifest->files)
                {
                    if (!mesh::IsMeshName(f.name)) continue;
                    std::string bytes, why;
                    if (!ReadSmall(m_catalogDir / std::filesystem::path(f.name), bytes, mesh::MaxFileBytes + 1)) why = "missing";
                    else if (bytes.size() != f.size) why = "wrong size";
                    else if (cosmetics::Sha256Hex(bytes) != f.sha256) why = "wrong hash";
                    else if (auto parsed = mesh::Parse(bytes, &why))
                    {
                        library->meshes[f.name] = std::make_shared<const mesh::Mesh>(std::move(*parsed));
                        library->verifiedMeshes.insert(f.name);
                        continue;
                    }
                    problems.push_back("mesh " + f.name + ": " + why);
                }
                for (const std::string& problem : problems) Log("cosmetics: catalog: " + problem);
                library->status = "catalog " + std::to_string(static_cast<long long>(catalog->version)) + ", " + std::to_string(library->index.size()) +
                                  " item(s), " + std::to_string(cosmetics::Pickable(library->index).size()) + " released, " +
                                  std::to_string(library->verifiedPaks.size()) + " verified pak(s), " + std::to_string(library->verifiedMeshes.size()) + " verified mesh(es)";
            }
            else library->status = error;
        }
        else library->status = error;
        Log("cosmetics: " + library->status);
        std::lock_guard lock(m_mutex);
        m_cosmetics.library = std::move(library);
    }

    void Output::ReadCosmeticsInputs()
    {
        std::string marker, looks, dev;
        if (!ReadSmall(m_root / L"aimmod-session.txt", marker, 1025)) marker.clear();
        if (!ReadSmall(m_root / L"cosmetic-looks.txt", looks, 65537)) looks.clear();
        const bool drafts = ReadSmall(m_root / L"cosmetics-dev.txt", dev, 256) && dev.find("allow_drafts=1") != std::string::npos;
        std::optional<cosmetics::Marker> parsedMarker;
        std::shared_ptr<const cosmetics::Looks> parsedLooks;
        const bool markerChanged = marker != m_markerText, looksChanged = looks != m_looksText;
        if (!markerChanged && !looksChanged)
        {
            std::lock_guard lock(m_mutex);
            m_cosmetics.allowDrafts = drafts;
            return;
        }
        std::string reason;
        if (!marker.empty()) parsedMarker = cosmetics::ParseMarker(marker, &reason);
        if (markerChanged && !marker.empty() && !parsedMarker) Log("cosmetics: session marker ignored (" + reason + ")");
        if (!looks.empty())
        {
            if (auto parsed = cosmetics::ParseLooks(looks, &reason)) parsedLooks = std::make_shared<const cosmetics::Looks>(std::move(*parsed));
            else if (looksChanged) Log("cosmetics: looks ignored (" + reason + ")");
        }
        m_markerText = std::move(marker);
        m_looksText = std::move(looks);
        std::lock_guard lock(m_mutex);
        m_cosmetics.marker = std::move(parsedMarker);
        m_cosmetics.looks = std::move(parsedLooks);
        m_cosmetics.allowDrafts = drafts;
    }

    std::shared_ptr<const std::unordered_map<std::string, std::string>> Output::avatars() const
    {
        std::lock_guard lock(const_cast<std::mutex&>(m_mutex));
        return m_avatars;
    }

    ClipSettings Output::clipSettings() const
    {
        std::lock_guard lock(const_cast<std::mutex&>(m_mutex));
        return m_clips;
    }

    void Output::PublishScene(std::string body)
    {
        std::lock_guard lock(m_mutex);
        if (body == m_sceneBody) return;
        m_sceneBody = std::move(body);
        m_sceneDirty = true;
    }

    void Output::PublishReplayStatus(std::string body)
    {
        {
            std::lock_guard lock(m_mutex);
            if (body == m_status) return;
            m_status = std::move(body);
            m_statusDirty = true;
        }
        m_wake.notify_one();
    }

    void Output::SetCapabilities(std::string capabilities)
    {
        std::lock_guard lock(m_mutex);
        if (capabilities == m_capabilities) return;
        m_capabilities = std::move(capabilities);
        m_capsDirty = true;
    }

    void Output::WatchGameStats(std::string scenario, std::int64_t sinceUnixMs, std::optional<double> localStartSeconds)
    {
        std::lock_guard lock(m_mutex);
        m_found.reset();
        m_watch = StatsWatch{std::move(scenario), sinceUnixMs, localStartSeconds, NowMs() + 15000};
    }

    void Output::StopGameStats()
    {
        std::lock_guard lock(m_mutex);
        m_watch.reset();
        m_found.reset();
    }

    std::optional<GameStats> Output::TakeGameStats()
    {
        std::lock_guard lock(m_mutex);
        std::optional<GameStats> out;
        out.swap(m_found);
        return out;
    }

    void Output::ScanGameStats(std::uint64_t now)
    {
        std::optional<StatsWatch> watch;
        {
            std::lock_guard lock(m_mutex);
            if (!m_watch || m_found) return;
            if (now >= m_watch->expires)
            {
                m_watch.reset();
                return;
            }
            watch = m_watch;
        }
        if (m_stats.empty() || now - m_lastStatsScan < 150) return;
        m_lastStatsScan = now;
        std::error_code error;
        const auto since = std::chrono::system_clock::time_point(std::chrono::milliseconds(watch->since - 2000));
        for (const auto& entry : std::filesystem::directory_iterator(m_stats, error))
        {
            std::error_code itemError;
            const std::wstring name = entry.path().filename().wstring();
            if (m_consumed.contains(name) || !entry.is_regular_file(itemError)) continue;
            const auto written = std::chrono::clock_cast<std::chrono::system_clock>(entry.last_write_time(itemError));
            if (itemError || written < since) continue;
            // Never path::string(): it throws for names outside the ANSI code page.
            if (!IsChallengeStatsFile(std::wstring_view(name))) continue;
            std::string text;
            if (!ReadSmall(entry.path(), text, 256 * 1024)) continue;
            auto stats = ParseGameStats(text);
            if (!stats) continue; // possibly still being written: retried next scan
            m_consumed.insert(name);
            if (stats->scenario != watch->scenario) continue;
            if (watch->localStart && stats->challengeStartSeconds)
            {
                double delta = std::fabs(*stats->challengeStartSeconds - *watch->localStart);
                delta = std::min(delta, 86400.0 - delta);
                if (delta > 5.0) continue;
            }
            std::lock_guard lock(m_mutex);
            if (m_watch && m_watch->scenario == watch->scenario && m_watch->since == watch->since)
            {
                m_found = std::move(stats);
                m_watch.reset();
            }
            return;
        }
    }

    void Output::ReplayWrite(std::unique_ptr<replay2::Capture> capture)
    {
        if (!capture) return;
        {
            std::lock_guard lock(m_mutex);
            Job job{Job::Kind::Replay};
            job.capture = std::move(capture);
            m_jobs.push_back(std::move(job));
        }
        m_wake.notify_one();
    }

    bool Output::WriteAtomic(const std::filesystem::path& path, const std::string& body)
    {
        std::filesystem::path next = path;
        next += L".next";
        HANDLE file = CreateFileW(next.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return false;
        bool ok = WriteAll(file, body);
        CloseHandle(file);
        if (ok && MoveFileExW(next.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING)) return true;
        DeleteFileW(next.c_str());
        return false;
    }

    void Output::WriteShared(const std::string& body)
    {
        if (!m_view || body.size() > SharedLiveSize - SharedHeader) return;
        auto* bytes = static_cast<std::uint8_t*>(m_view);
        auto* sequence = reinterpret_cast<volatile LONG64*>(bytes + 8);
        InterlockedIncrement64(sequence); // odd: write in progress
        const std::int64_t stamp = UnixMs();
        const std::int32_t length = static_cast<std::int32_t>(body.size());
        std::memcpy(bytes + 16, &stamp, 8);
        std::memcpy(bytes + 24, &length, 4);
        std::memcpy(bytes + SharedHeader, body.data(), body.size());
        InterlockedIncrement64(sequence); // even: stable
    }

    bool Output::Execute(Job& job)
    {
        const std::filesystem::path replays = m_root / L"replays";
        switch (job.kind)
        {
        case Job::Kind::Journal:
        {
            HANDLE file = CreateFileW((m_root / L"completed.tsv").c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                      nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file == INVALID_HANDLE_VALUE) return false;
            bool ok = WriteAll(file, job.data);
            CloseHandle(file);
            return ok;
        }
        case Job::Kind::Replay:
        {
            const replay2::Capture& c = *job.capture;
            replay2::EncodeReport report;
            const std::vector<std::uint8_t> bytes = replay2::Encode(c, {}, &report);
            if (bytes.empty())
            {
                Warn("replay not saved: capture unusable (id=" + c.header.id + ")");
                return true;
            }
            const auto partial = replays / std::filesystem::path(c.header.id + ".partial");
            const auto final = replays / std::filesystem::path(c.header.id + ".amreplay");
            HANDLE file = CreateFileW(partial.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file == INVALID_HANDLE_VALUE) return false; // retried
            const bool ok = WriteAll(file, std::string(bytes.begin(), bytes.end()));
            CloseHandle(file);
            if (!ok || !MoveFileExW(partial.c_str(), final.c_str(), 0))
            {
                DeleteFileW(partial.c_str());
                Warn("replay not saved: write failed (id=" + c.header.id + ")");
                return true;
            }
            // Clips the player marked during the run: short slices, own files.
            const ClipSettings clips = clipSettings();
            int index = 0;
            double lastMark = -1e9;
            for (std::uint32_t mark : c.marks)
            {
                if (mark >= c.frameTimes.size()) continue;
                const double at = c.frameTimes[mark];
                if (at - lastMark < 1.0) continue; // one clip per press
                lastMark = at;
                const std::string clipId = c.header.id + "-clip" + std::to_string(++index);
                replay2::Capture clip = replay2::Slice(c, at - clips.before, at + clips.after, clipId);
                clip.reason = "completed";
                replay2::EncodeReport clipReport;
                const auto clipBytes = replay2::Encode(clip, {}, &clipReport);
                if (clipBytes.empty()) continue;
                const auto clipPath = replays / std::filesystem::path(clipId + ".amreplay");
                const auto clipPartial = replays / std::filesystem::path(clipId + ".partial");
                HANDLE clipFile = CreateFileW(clipPartial.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
                if (clipFile == INVALID_HANDLE_VALUE) continue;
                const bool written = WriteAll(clipFile, std::string(clipBytes.begin(), clipBytes.end()));
                CloseHandle(clipFile);
                if (!written || !MoveFileExW(clipPartial.c_str(), clipPath.c_str(), 0)) DeleteFileW(clipPartial.c_str());
                else Log("clip saved id=" + clipId + " bytes=" + std::to_string(clipBytes.size()) + " from " + FormatNumber(clip.clipStart, 4) + " s");
            }
            char line[512];
            std::snprintf(line, sizeof(line),
                          "replay saved id=%s bytes=%zu (%.1f KB/min, body %zu) frames=%u inputs=%u samples=%u keyframes=%u targets %u->%u points "
                          "yaw/unit=%.9g pitch/unit=%.9g quantum=%.9g drift max=%.4f rms=%.5f deg",
                          c.header.id.c_str(), report.fileBytes, report.duration > 0 ? report.fileBytes / 1024.0 / (report.duration / 60.0) : 0.0,
                          report.bodyBytes, report.engineFrames, report.inputEvents, report.cameraSamples, report.keyframes, report.actorSamples,
                          report.actorPoints, report.yawPerUnit, report.pitchPerUnit, report.quantum, report.keyframeErrorMax, report.keyframeErrorRms);
            Log(line);
            return true;
        }
        }
        return true;
    }

    void Output::Periodic(bool force)
    {
        const std::uint64_t now = NowMs();
        std::string live, status, caps, scene;
        bool liveDirty = false, statusDirty = false;
        {
            std::lock_guard lock(m_mutex);
            if (m_liveDirty)
            {
                live = m_live;
                liveDirty = true;
                m_liveDirty = false;
            }
            else if (!m_live.empty() && now - m_lastLiveWrite >= 1000) live = m_live;
            if (m_statusDirty)
            {
                status = m_status;
                statusDirty = true;
                m_statusDirty = false;
            }
            caps = m_capabilities;
            m_capsDirty = false;
            if (m_sceneDirty || (!m_sceneBody.empty() && now - m_lastSceneWrite >= 1000))
            {
                scene = m_sceneBody;
                m_sceneDirty = false;
            }
        }
        // Rewritten at least once a second: readers treat it as stale after 3 s.
        if (!scene.empty() && WriteAtomic(m_root / L"core-scene.json", scene)) m_lastSceneWrite = now;
        if (!live.empty())
        {
            // Readers treat live-overlay.json older than 2 s as stale: rewrite
            // unchanged content once per second, changed content immediately.
            if (WriteAtomic(m_root / L"live-overlay.json", live)) m_lastLiveWrite = now;
            else if (liveDirty)
            {
                std::lock_guard lock(m_mutex);
                if (!m_liveDirty) m_liveDirty = true;
            }
            if (liveDirty) WriteShared(live);
        }
        if (statusDirty && !WriteAtomic(m_root / L"replay-status.json", status))
        {
            std::lock_guard lock(m_mutex);
            if (!m_statusDirty) m_statusDirty = true;
        }
        if (force || now - m_lastHeartbeat >= 1000)
        {
            if (WriteAtomic(m_root / L"core-active.tsv", FormatCoreActive(m_version, std::time(nullptr), caps))) m_lastHeartbeat = now;
        }
        if (force || now - m_lastSettings >= 1000)
        {
            m_lastSettings = now;
            std::string text;
            // A missing or locked file keeps the previous state; a readable
            // malformed file fails closed (matches the service store).
            if (ReadSmall(m_root / L"native-settings.tsv", text, 1025))
            {
                auto parsed = ParseNativeSettings(text);
                m_recording.store(parsed && parsed->replayRecordingEnabled, std::memory_order_relaxed);
            }
        }
        ScanGameStats(now);
        ReadOverlayInputs(now);
        if (force || now - m_lastPoseCheck >= 500)
        {
            m_lastPoseCheck = now;
            std::error_code error;
            const auto request = m_root / L"self-pose.request";
            bool requested = false;
            if (std::filesystem::exists(request, error))
            {
                const auto written = std::chrono::clock_cast<std::chrono::system_clock>(std::filesystem::last_write_time(request, error));
                requested = !error && std::chrono::system_clock::now() - written < std::chrono::seconds(5);
            }
            if (m_poseRequested.exchange(requested) && !requested) DeleteFileW((m_root / L"self-pose.tsv").c_str());
            const auto shotsRequest = m_root / L"self-shots.request";
            bool shots = false;
            if (std::filesystem::exists(shotsRequest, error))
            {
                const auto written = std::chrono::clock_cast<std::chrono::system_clock>(std::filesystem::last_write_time(shotsRequest, error));
                shots = !error && std::chrono::system_clock::now() - written < std::chrono::seconds(5);
            }
            if (m_shotsRequested.exchange(shots) && !shots) DeleteFileW((m_root / L"self-shots.tsv").c_str());
            // AIMMOD_AVATARS_1 / <actor name>\t<stream id>: which drawn hull is which player.
            std::string text;
            if ((requested || shots) && ReadSmall(m_root / L"avatars.tsv", text, 16384) && text != m_avatarText)
            {
                m_avatarText = text;
                auto map = std::make_shared<std::unordered_map<std::string, std::string>>();
                std::size_t at = 0;
                bool header = true;
                while (at < text.size())
                {
                    auto end = text.find('\n', at);
                    std::string line = text.substr(at, end == std::string::npos ? std::string::npos : end - at);
                    at = end == std::string::npos ? text.size() : end + 1;
                    if (!line.empty() && line.back() == '\r') line.pop_back();
                    if (header)
                    {
                        header = false;
                        if (line != "AIMMOD_AVATARS_1") break;
                        continue;
                    }
                    auto tab = line.find('\t');
                    if (tab == std::string::npos || tab == 0 || map->size() >= 64) continue;
                    std::string stream = line.substr(tab + 1);
                    bool ok = !stream.empty() && stream.size() <= 64;
                    for (char ch : stream) ok &= std::isalnum(static_cast<unsigned char>(ch)) || ch == '-' || ch == '_';
                    if (ok) (*map)[line.substr(0, tab)] = stream;
                }
                std::lock_guard lock(m_mutex);
                m_avatars = std::move(map);
            }
        }
        {
            std::string pose;
            {
                std::lock_guard lock(m_mutex);
                if (m_selfPoseDirty) pose.swap(m_selfPose);
                m_selfPoseDirty = false;
            }
            if (!pose.empty() && m_poseRequested.load()) WriteAtomic(m_root / L"self-pose.tsv", pose);
        }
        {
            std::string pointer;
            {
                std::lock_guard lock(m_mutex);
                if (m_pointerDirty) pointer.swap(m_pointer);
                m_pointerDirty = false;
            }
            if (!pointer.empty()) WriteAtomic(m_root / L"overlay-pointer.tsv", pointer);
        }
        {
            std::string shots;
            {
                std::lock_guard lock(m_mutex);
                if (m_selfShotsDirty) shots.swap(m_selfShots);
                m_selfShotsDirty = false;
            }
            if (!shots.empty() && m_shotsRequested.load()) WriteAtomic(m_root / L"self-shots.tsv", shots);
        }
        if (!force && !m_libraryLoaded) LoadCosmeticsLibrary(); // first writer pass, off the game thread
        if (force || now - m_lastCosmeticsCheck >= 1000)
        {
            m_lastCosmeticsCheck = now;
            ReadCosmeticsInputs();
            CheckQuitStats(now);
        }
        if (force || now - m_lastPlayStateCheck >= 15)
        {
            m_lastPlayStateCheck = now;
            ReadPlayState(now);
            ReadRoundState(now);
        }
        if (force || now - m_lastClipCheck >= 2000)
        {
            m_lastClipCheck = now;
            std::string text;
            ClipSettings clips;
            if (ReadSmall(m_root / L"clip-settings.tsv", text, 1024))
                if (auto parsed = ParseClipSettings(text)) clips = *parsed;
            std::lock_guard lock(m_mutex);
            m_clips = clips;
        }
        if (std::error_code missing; !m_commandPrimed && now - m_lastCommandCheck >= 100 && !std::filesystem::exists(m_root / L"core-command.tsv", missing))
            m_commandPrimed = true;
        ReadCommand(now);
        {
            std::deque<std::string> results;
            {
                std::lock_guard lock(m_mutex);
                results.swap(m_results);
            }
            // The file keeps the last 8 results, oldest first, so two answers in
            // one pass (accepted + done, or two commands) are never lost.
            if (!results.empty())
            {
                for (std::string& r : results)
                {
                    if (!r.empty() && r.back() != '\n') r += '\n';
                    m_resultHistory.push_back(std::move(r));
                    while (m_resultHistory.size() > 8) m_resultHistory.pop_front();
                }
                std::string body;
                for (const std::string& r : m_resultHistory) body += r;
                WriteAtomic(m_root / L"core-command-result.tsv", body);
            }
        }
        if (force || now - m_lastPlaybackCheck >= 500)
        {
            m_lastPlaybackCheck = now;
            std::string head;
            bool active = false;
            if (ReadSmall(m_root / L"replay-frame.tsv", head, 64))
            {
                auto newline = head.find('\n');
                active = IsReplayPlaybackHeader(std::string_view(head).substr(0, newline));
            }
            m_playback.store(active, std::memory_order_relaxed);
        }
    }

    void Output::Run()
    {
        std::string lastError;
        for (;;)
        {
            std::deque<Job> jobs;
            bool stop;
            {
                std::unique_lock lock(m_mutex);
                // Match files (shots out, play state in) need low latency.
                const bool fast = m_shotsRequested.load(std::memory_order_relaxed) || NowMs() - m_playStateSeenAt < 10000;
                m_wake.wait_for(lock, std::chrono::milliseconds(fast ? 15 : 100),
                                [this] { return m_stop || !m_jobs.empty() || m_statusDirty || !m_results.empty() || m_selfShotsDirty; });
                jobs.swap(m_jobs);
                stop = m_stop;
            }
            std::deque<Job> retry;
            for (Job& job : jobs)
            {
                // An exception must never leave this thread: it would terminate the game.
                bool done = false;
                try
                {
                    done = Execute(job);
                }
                catch (const std::exception& e)
                {
                    Warn(std::string("writer: job failed: ") + e.what());
                    done = true;
                }
                catch (...)
                {
                    done = true;
                }
                if (!done && ++job.attempts < 50) retry.push_back(std::move(job));
            }
            if (!retry.empty())
            {
                std::lock_guard lock(m_mutex);
                for (auto it = retry.rbegin(); it != retry.rend(); ++it) m_jobs.push_front(std::move(*it));
            }
            try
            {
                Periodic(false);
            }
            catch (const std::exception& e)
            {
                // Logged once per distinct error: the pass repeats every few ms.
                if (lastError != e.what()) Warn(std::string("writer: periodic pass failed: ") + e.what());
                lastError = e.what();
            }
            catch (...)
            {
            }
            if (stop)
            {
                std::lock_guard lock(m_mutex);
                if (m_jobs.empty() || retry.size() == m_jobs.size()) break;
            }
            if (!retry.empty()) std::this_thread::sleep_for(std::chrono::milliseconds(200));
        }
    }
} // namespace aimmod
