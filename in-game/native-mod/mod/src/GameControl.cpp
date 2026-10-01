#include "GameControl.hpp"

#include "Log.hpp"
#include "Output.hpp"
#include "World.hpp"

#include <Unreal/CoreUObject/UObject/UnrealType.hpp>
#include <Unreal/UObject.hpp>
#include <Unreal/UObjectGlobals.hpp>

#include <Windows.h>

#include <algorithm>
#include <cstdlib>
#include <cstring>
#include <vector>

namespace aimmod
{
    using namespace game;

    namespace
    {
        constexpr std::uint8_t PlayTypeChallenge = 0, PlayTypeFreePlay = 1; // EScenarioPlayType
        constexpr std::uint8_t StartTypeStart = 0;                          // EScenarioStartType

        void WriteFloat(std::uint8_t* value, double v)
        {
            const float f = static_cast<float>(v);
            std::memcpy(value, &f, 4);
        }
        void WriteBool(std::uint8_t* value, const Param& p, bool v)
        {
            if (p.boolProperty) p.boolProperty->SetPropertyValue(value, v);
        }
        bool ReturnBool(const std::uint8_t* buffer, const std::vector<Param>& params)
        {
            for (const Param& p : params)
                if (p.ret && p.kind == Kind::Bool && p.boolProperty) return p.boolProperty->GetPropertyValue(const_cast<std::uint8_t*>(buffer) + p.offset);
            return false;
        }
        std::string Describe(const GameCommand& c)
        {
            std::string s = std::string(ActionName(c.action));
            if (!c.scenario.empty()) s += " scenario=\"" + c.scenario + "\"";
            if (c.action == GameCommand::Action::StartScenario) s += c.mode == GameCommand::Mode::Challenge ? " mode=challenge" : " mode=freeplay";
            auto add = [&](const char* k, const std::optional<double>& v) {
                if (v) s += std::string(" ") + k + "=" + FormatNumber(*v, 0);
            };
            add("timeScale", c.timeScale);
            add("targetSize", c.targetSize);
            add("targetSpeed", c.targetSpeed);
            add("mapScale", c.mapScale);
            if (!c.weapon.empty()) s += " weapon=\"" + c.weapon + "\"";
            return s;
        }
    } // namespace

    void GameControl::Bind()
    {
        m_start.BindPath(STR("/Script/GameSkillsTrainer.Start_Scenario:Start_Scenario"), Shape::Command);
        m_activate.BindPath(STR("/Script/Engine.BlueprintAsyncActionBase:Activate"), Shape::Command);
        m_startDefault = RC::Unreal::UObjectGlobals::StaticFindObject<UObject*>(nullptr, nullptr, STR("/Script/GameSkillsTrainer.Default__Start_Scenario"));
        m_persistentPlayType.BindPath(STR("/Script/GameSkillsTrainer.ScenarioManager:SetPersistentPlayType"), Shape::Command);
        m_playCurrent.BindPath(STR("/Script/GameSkillsTrainer.ScenarioManager:PlayCurrentScenario"), Shape::Command);
        m_localHash.BindPath(STR("/Script/GameSkillsTrainer.ScenarioManager:GetLocalScenarioHash"), Shape::Command);
        m_onlineHash.BindPath(STR("/Script/GameSkillsTrainer.ScenarioManager:GetOnlineScenarioHash"), Shape::Command);
        m_cancel.BindPath(STR("/Script/GameSkillsTrainer.ScenarioManager:CancelChallenge"), Shape::Command);
        m_timeDilation.BindPath(STR("/Script/Engine.GameplayStatics:SetGlobalTimeDilation"), Shape::Command);
        m_mapScale.BindPath(STR("/Script/GameSkillsTrainer.MetaGameState:SetMapScale"), Shape::Command);
        m_adaptiveOverride.BindPath(STR("/Script/GameSkillsTrainer.AdaptiveDifficultySystem:Import_OverrideProfile"), Shape::Command);
        m_adaptiveReset.BindPath(STR("/Script/GameSkillsTrainer.AdaptiveDifficultySystem:Reset_Profile"), Shape::Command);
        m_weapon.BindPath(STR("/Script/GameSkillsTrainer.WeaponHandler:SetWeaponProfileByString"), Shape::Command);
        // The game indexes local scenarios at startup; these rescan them (the
        // second is what the pause menu's Reload Profiles button reloads).
        m_refreshLocal.BindPath(STR("/Script/GameSkillsTrainer.ScenarioManager:RefreshLocalScenarios"), Shape::Command);
        m_reloadProfiles.BindPath(STR("/Script/GameSkillsTrainer.MetaGameState:ReloadAllProfiles"), Shape::Command);
        m_resetFreeplay.BindPath(STR("/Script/GameSkillsTrainer.ScenarioManager:Reset_FreeplaySession"), Shape::Command);
        {
            wchar_t exe[MAX_PATH * 4]{};
            const DWORD length = GetModuleFileNameW(nullptr, exe, static_cast<DWORD>(std::size(exe)));
            if (length > 0 && length < std::size(exe))
                m_scenarioFolder = std::filesystem::path(exe).parent_path().parent_path().parent_path() / L"Saved" / L"SaveGames" / L"Scenarios";
        }
        m_exec.BindPath(STR("/Script/Engine.KismetSystemLibrary:ExecuteConsoleCommand"), Shape::Command);
        m_kismet = RC::Unreal::UObjectGlobals::StaticFindObject<UObject*>(nullptr, nullptr, STR("/Script/Engine.Default__KismetSystemLibrary"));
        m_spawnBegin.BindPath(STR("/Script/Engine.GameplayStatics:BeginDeferredActorSpawnFromClass"), Shape::Command);
        m_spawnFinish.BindPath(STR("/Script/Engine.GameplayStatics:FinishSpawningActor"), Shape::Command);
        m_setViewTarget.BindPath(STR("/Script/Engine.PlayerController:SetViewTargetWithBlend"), Shape::Command);
        m_getViewTarget.BindPath(STR("/Script/Engine.Controller:GetViewTarget"), Shape::Object);
        m_destroy.BindPath(STR("/Script/Engine.Actor:K2_DestroyActor"), Shape::Command);
        m_hide.BindPath(STR("/Script/Engine.Actor:SetActorHiddenInGame"), Shape::Command);
        m_place.BindPath(STR("/Script/Engine.Actor:K2_SetActorLocationAndRotation"), Shape::Command);
        m_fov.BindPath(STR("/Script/Engine.CameraComponent:SetFieldOfView"), Shape::Command);
        m_cameraClass = FindClass(STR("/Script/Engine.CameraActor"));
        m_cameraComponent.Bind(m_cameraClass, STR("CameraComponent"));
        m_fullyLoaded.Bind(FindClass(STR("/Script/GameSkillsTrainer.MetaGameState")), STR("bFullyLoaded"));
        m_mapLoading.Bind(FindClass(STR("/Script/GameSkillsTrainer.MetaGameState")), STR("bMapLoading"));
        m_canLoad = m_start.ok() && m_activate.ok() && m_startDefault && m_localHash.ok() && m_b.lifecycleReady();
        m_canStart = m_canLoad && m_persistentPlayType.ok() && m_cancel.ok();
        m_canCapture = m_canStart && m_exec.ok() && m_kismet && m_spawnBegin.ok() && m_spawnFinish.ok() && m_setViewTarget.ok() && m_getViewTarget.ok() &&
                       m_destroy.ok() && m_place.ok() && m_fov.ok() && m_cameraClass && m_cameraComponent.ok();
        {
            wchar_t exe[MAX_PATH * 4]{};
            const DWORD length = GetModuleFileNameW(nullptr, exe, static_cast<DWORD>(std::size(exe)));
            if (length > 0 && length < std::size(exe))
                m_screenshots = std::filesystem::path(exe).parent_path().parent_path().parent_path() / L"Saved" / L"Screenshots" / L"WindowsNoEditor";
            m_thumbnails = m_output.root() / L"thumbnails";
        }
        auto st = [](const Getter& g) { return g.ok() ? std::string("ok") : g.error(); };
        Log("game control: load=" + std::string(m_canLoad ? "ok" : "unavailable") + " start=" + (m_canStart ? "ok" : "unavailable") +
            " Start_Scenario=" + st(m_start) + " Activate=" + st(m_activate) + " SetPersistentPlayType=" + st(m_persistentPlayType) +
            " GetLocalScenarioHash=" + st(m_localHash) + " CancelChallenge=" + st(m_cancel) + " timeScale=" + st(m_timeDilation) +
            " mapScale=" + st(m_mapScale) + " adaptive=" + st(m_adaptiveOverride) + " weapon=" + st(m_weapon) + " refresh=" + st(m_refreshLocal) +
            " reloadProfiles=" + st(m_reloadProfiles) + " capture=" + (m_canCapture ? "ok" : "unavailable") + " console=" + st(m_exec) +
            " spawn=" + st(m_spawnBegin) + " viewTarget=" + st(m_setViewTarget));
    }

    void GameControl::Answer(std::uint64_t sequence, const char* state, const std::string& code, const std::string& message)
    {
        m_output.PublishCommandResult(FormatCommandResult(sequence, state, code, message));
        Log("game command " + std::to_string(sequence) + ": " + state + " " + code + (message.empty() ? "" : " (" + message + ")"));
    }

    bool GameControl::ScenarioKnown(UObject* manager, const std::string& name) const
    {
        bool known = false;
        auto fill = [&](std::uint8_t* value, const Param& p) {
            if (p.kind == Kind::String) WriteString(value, name);
        };
        auto read = [&](const std::uint8_t* buffer, const std::vector<Param>& params) { known = ReturnBool(buffer, params); };
        m_localHash.Call(manager, fill, read);
        if (!known && m_onlineHash.ok()) m_onlineHash.Call(manager, fill, read);
        return known;
    }

    bool GameControl::SetPlayType(UObject* manager, GameCommand::Mode mode)
    {
        const std::uint8_t type = mode == GameCommand::Mode::Challenge ? PlayTypeChallenge : PlayTypeFreePlay;
        return m_persistentPlayType.Call(manager, [&](std::uint8_t* value, const Param& p) {
            if (p.kind == Kind::UInt8) *value = type;
        });
    }

    // The game's own scenario browser path: Start_Scenario (async action) + Activate.
    bool GameControl::StartScenario(const std::string& name, bool playOnLoad)
    {
        UObject* player = m_scene.Player();
        if (!player) return false;
        UObject* action = nullptr;
        const bool called = m_start.Call(
            m_startDefault,
            [&](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Object) std::memcpy(value, &player, sizeof(player));
                else if (p.kind == Kind::String) WriteString(value, name);
                else if (p.kind == Kind::Bool) WriteBool(value, p, p.name == "InPlayOnLoad" ? playOnLoad : false);
            },
            [&](const std::uint8_t* buffer, const std::vector<Param>& params) {
                for (const Param& p : params)
                    if (p.ret) action = ReadObject(buffer, p);
            });
        if (!called || !action) return false;
        m_pending->action = action;
        return m_activate.Call(action, [](std::uint8_t*, const Param&) {});
    }

    bool GameControl::ApplyOverrides(const GameCommand& c)
    {
        UObject* player = m_scene.Player();
        bool ok = true;
        if (c.timeScale)
        {
            const bool set = m_timeDilation.Call(m_b.statics, [&](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Object) std::memcpy(value, &player, sizeof(player));
                else if (p.kind == Kind::Float) WriteFloat(value, *c.timeScale);
            });
            m_overrides.timeDilation |= set;
            ok &= set;
        }
        if (c.mapScale)
        {
            UObject* state = m_scene.GameState();
            auto before = state ? m_b.mapScale.Number(state) : std::nullopt;
            const bool set = state && m_mapScale.Call(state, [&](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Float) WriteFloat(value, *c.mapScale);
            });
            if (set && !m_overrides.mapScaleBefore) m_overrides.mapScaleBefore = before;
            ok &= set;
        }
        if (c.targetSize || c.targetSpeed)
        {
            std::vector<UObject*> systems;
            RC::Unreal::UObjectGlobals::FindAllOf(STR("AdaptiveDifficultySystem"), systems);
            UObject* system = nullptr;
            for (UObject* s : systems)
                if (IsLiveInstance(s)) system = s;
            // Fixed multipliers through the game's own adaptive-difficulty
            // override: min = max = base, no adjustment.
            const double size = c.targetSize.value_or(1.0), speed = c.targetSpeed.value_or(1.0);
            const bool set = system && m_adaptiveOverride.Call(system, [&](std::uint8_t* value, const Param& p) {
                if (!p.structType) return;
                SetStructField(value, p.structType, "IsTargetSizeActive", c.targetSize ? 1 : 0);
                SetStructField(value, p.structType, "IsTimeDilationActive", 0);
                SetStructField(value, p.structType, "MinTargetSizeMultiplier", size);
                SetStructField(value, p.structType, "MaxTargetSizeMultiplier", size);
                SetStructField(value, p.structType, "TargetSizeBaseMultiplier", size);
                SetStructField(value, p.structType, "MinTargetSpeedMultiplier", speed);
                SetStructField(value, p.structType, "MaxTargetSpeedMultiplier", speed);
                SetStructField(value, p.structType, "TimeDilationBaseMultiplier", 1.0);
                SetStructField(value, p.structType, "AdjustmentRate", 0.0);
                SetStructField(value, p.structType, "AdjustmentInterval", 1.0);
            });
            m_overrides.adaptive |= set;
            ok &= set;
        }
        if (!c.weapon.empty())
        {
            UObject* character = player ? m_b.myCharacter.Object(player) : nullptr;
            UObject* handler = character ? game::Describe(character).weaponHandler.Object(character) : nullptr;
            std::int32_t result = -1;
            const bool set = handler && m_weapon.Call(
                                            handler,
                                            [&](std::uint8_t* value, const Param& p) {
                                                if (p.kind == Kind::String) WriteString(value, c.weapon);
                                                // Int32 Slot stays 0 (primary).
                                            },
                                            [&](const std::uint8_t* buffer, const std::vector<Param>& params) {
                                                for (const Param& p : params)
                                                    if (p.ret && p.kind == Kind::Int32) std::memcpy(&result, buffer + p.offset, 4);
                                            });
            Log("game control: weapon profile \"" + c.weapon + "\" -> " + std::to_string(result));
            ok &= set;
        }
        m_overrides.active = true;
        m_overrides.scenario = c.scenario;
        return ok;
    }

    void GameControl::ResetOverrides(const char* why)
    {
        if (!m_overrides.active) return;
        UObject* player = m_scene.Player();
        if (m_overrides.timeDilation)
            m_timeDilation.Call(m_b.statics, [&](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Object) std::memcpy(value, &player, sizeof(player));
                else if (p.kind == Kind::Float) WriteFloat(value, 1.0);
            });
        if (m_overrides.adaptive)
        {
            std::vector<UObject*> systems;
            RC::Unreal::UObjectGlobals::FindAllOf(STR("AdaptiveDifficultySystem"), systems);
            for (UObject* s : systems)
                if (IsLiveInstance(s)) m_adaptiveReset.Call(s, [](std::uint8_t*, const Param&) {});
        }
        if (m_overrides.mapScaleBefore)
            if (UObject* state = m_scene.GameState())
                m_mapScale.Call(state, [&](std::uint8_t* value, const Param& p) {
                    if (p.kind == Kind::Float) WriteFloat(value, *m_overrides.mapScaleBefore);
                });
        Log(std::string("game control: overrides reset (") + why + ")");
        m_overrides = {};
    }

    std::vector<std::wstring> GameControl::Screenshots() const
    {
        std::vector<std::wstring> names;
        std::error_code error;
        for (const auto& entry : std::filesystem::directory_iterator(m_screenshots, error))
            if (entry.path().extension() == L".png") names.push_back(entry.path().filename().wstring());
        return names;
    }

    void GameControl::BeginCapture(const GameCommand& c, double now, const std::string& current, UObject* manager)
    {
        ResetOverrides("thumbnail capture");
        m_capture = Capture{c};
        m_capture->deadline = now + 60.0 + 15.0 * static_cast<double>(c.views.size());
        if (current != c.scenario)
        {
            // Freeplay: nothing is recorded or submitted.
            if (!SetPlayType(manager, GameCommand::Mode::FreePlay) || !(m_pending = Pending{c, m_capture->deadline}, StartScenario(c.scenario, true)))
            {
                m_pending.reset();
                m_capture.reset();
                return Answer(c.sequence, "error", "start-failed", "The game did not start loading \"" + c.scenario + "\".");
            }
            m_pending.reset(); // the capture owns the wait
        }
        Answer(c.sequence, "accepted", "capturing", "");
    }

    void GameControl::EndCapture(const char* state, const std::string& code, const std::string& message)
    {
        if (!m_capture) return;
        Capture& k = *m_capture;
        UObject* player = m_scene.Player();
        if (UObject* previous = k.previousTarget.Get(); previous && player)
            m_setViewTarget.Call(player, [&](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Object) std::memcpy(value, &previous, sizeof(previous));
            });
        if (UObject* pawn = k.pawn.Get())
            m_hide.Call(pawn, [](std::uint8_t* value, const Param& p) { WriteBool(value, p, false); });
        if (UObject* camera = k.camera.Get()) m_destroy.Call(camera, [](std::uint8_t*, const Param&) {});
        std::string body = message;
        if (std::string(state) == "done")
        {
            body = "{\"files\":[";
            for (std::size_t i = 0; i < k.files.size(); ++i)
            {
                if (i) body += ',';
                AppendJsonString(body, k.files[i]);
            }
            body += "]}";
        }
        const auto seq = k.command.sequence;
        m_capture.reset();
        Answer(seq, state, code, body);
    }

    void GameControl::TickCapture(double now, const std::string& current, bool inChallenge, bool loading)
    {
        Capture& k = *m_capture;
        if (inChallenge) return EndCapture("error", "challenge-active", "A challenge started; the capture stopped.");
        if (now > k.deadline) return EndCapture("error", "timeout", "The thumbnail capture did not finish.");
        UObject* player = m_scene.Player();
        if (!player) return;
        switch (k.phase)
        {
        case Capture::Phase::Loading:
        {
            UObject* state = m_scene.GameState();
            const bool ready = current == k.command.scenario && !loading && state && m_fullyLoaded.Bool(state).value_or(true) &&
                               !m_mapLoading.Bool(state).value_or(false);
            if (!ready)
            {
                k.loadedAt = -1;
                return;
            }
            if (k.loadedAt < 0) k.loadedAt = now;
            if (now - k.loadedAt < 2.0) return; // let streaming settle
            // An inert camera of our own; the pawn (and its first-person weapon) hidden.
            UClass* cls = m_cameraClass;
            UObject* camera = nullptr;
            alignas(16) std::uint8_t identity[48]{};
            const float transform[12] = {0, 0, 0, 1, 0, 0, 0, 0, 1, 1, 1, 0};
            std::memcpy(identity, transform, sizeof(transform));
            auto fillTransform = [&](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Other && p.size == 48) std::memcpy(value, identity, 48);
            };
            m_spawnBegin.Call(
                m_b.statics,
                [&](std::uint8_t* value, const Param& p) {
                    if (p.kind == Kind::Object && p.worldContext) std::memcpy(value, &player, sizeof(player));
                    else if (p.kind == Kind::Object && p.name == "ActorClass") std::memcpy(value, &cls, sizeof(cls));
                    else if (p.kind == Kind::UInt8) *value = 1; // AlwaysSpawn
                    else fillTransform(value, p);
                },
                [&](const std::uint8_t* buffer, const std::vector<Param>& params) {
                    for (const Param& p : params)
                        if (p.ret) camera = ReadObject(buffer, p);
                });
            if (!camera) return EndCapture("error", "capture-failed", "Could not create the capture camera.");
            m_spawnFinish.Call(m_b.statics, [&](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Object && p.name == "Actor") std::memcpy(value, &camera, sizeof(camera));
                else fillTransform(value, p);
            });
            k.camera = camera;
            k.previousTarget = m_getViewTarget.Object(player);
            if (UObject* pawn = m_b.myCharacter.Object(player))
            {
                k.pawn = pawn;
                m_hide.Call(pawn, [](std::uint8_t* value, const Param& p) { WriteBool(value, p, true); });
            }
            m_setViewTarget.Call(player, [&](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Object) std::memcpy(value, &camera, sizeof(camera));
            });
            k.phase = Capture::Phase::Place;
            Log("thumbnail capture: world ready, " + std::to_string(k.command.views.size()) + " view(s)");
            return;
        }
        case Capture::Phase::Place:
        {
            UObject* camera = k.camera.Get();
            UObject* component = camera ? m_cameraComponent.Object(camera) : nullptr;
            if (!component) return EndCapture("error", "capture-failed", "The capture camera disappeared.");
            const auto& v = k.command.views[k.view];
            const double location[3] = {v.x, v.y, v.z}, rotation[3] = {v.pitch, v.yaw, 0};
            m_place.Call(camera, [&](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Vector && p.size == 12)
                {
                    const float f[3] = {static_cast<float>(location[0]), static_cast<float>(location[1]), static_cast<float>(location[2])};
                    std::memcpy(value, f, 12);
                }
                else if (p.kind == Kind::Rotator && p.size == 12)
                {
                    const float f[3] = {static_cast<float>(rotation[0]), static_cast<float>(rotation[1]), static_cast<float>(rotation[2])};
                    std::memcpy(value, f, 12);
                }
                else if (p.kind == Kind::Bool) WriteBool(value, p, p.name == "bTeleport");
            });
            m_fov.Call(component, [&](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Float) WriteFloat(value, v.fov);
            });
            k.until = now + 0.75; // textures and LODs stream in for the new view
            k.phase = Capture::Phase::Shoot;
            return;
        }
        case Capture::Phase::Shoot:
        {
            if (now < k.until) return;
            k.before = Screenshots();
            const std::string command = k.fallbackShot ? std::string("shot")
                                                       : "HighResShot " + std::to_string(k.command.width) + "x" + std::to_string(k.command.height);
            const bool sent = m_exec.Call(m_kismet, [&](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Object && p.worldContext) std::memcpy(value, &player, sizeof(player));
                else if (p.kind == Kind::Object) std::memcpy(value, &player, sizeof(player)); // SpecificPlayer
                else if (p.kind == Kind::String) WriteString(value, command);
            });
            if (!sent) return EndCapture("error", "capture-failed", "The screenshot command was not accepted.");
            Log("thumbnail capture: view " + std::to_string(k.view + 1) + " -> " + command);
            k.until = now + (k.fallbackShot ? 6.0 : 4.0);
            k.candidate.clear();
            k.nextCheck = now + 0.25;
            k.phase = Capture::Phase::Wait;
            return;
        }
        case Capture::Phase::Wait:
        {
            if (now < k.nextCheck) return;
            k.nextCheck = now + 0.25;
            std::error_code error;
            for (const std::wstring& name : Screenshots())
            {
                if (std::find(k.before.begin(), k.before.end(), name) != k.before.end()) continue;
                const auto size = std::filesystem::file_size(m_screenshots / name, error);
                if (error || size == 0) continue;
                if (k.candidate != name || k.candidateSize != size)
                {
                    k.candidate = name; // wait one more check for the writer to finish
                    k.candidateSize = size;
                    return;
                }
                std::filesystem::create_directories(m_thumbnails, error);
                const std::string file = ThumbnailFileName(k.command.out, k.view, k.command.views.size());
                const auto target = m_thumbnails / std::filesystem::path(Widen(file));
                if (!MoveFileExW((m_screenshots / name).c_str(), target.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_COPY_ALLOWED))
                    return EndCapture("error", "capture-failed", "Could not move the screenshot.");
                k.files.push_back(file);
                Log("thumbnail capture: saved " + file + (k.fallbackShot ? " (window resolution)" : ""));
                if (++k.view >= k.command.views.size()) return EndCapture("done", "captured", "");
                k.phase = Capture::Phase::Place;
                return;
            }
            if (now >= k.until)
            {
                // HighResShot unavailable in this build: fall back to a window-size shot once.
                if (!k.fallbackShot && !k.triedFallback)
                {
                    k.fallbackShot = k.triedFallback = true;
                    Warn("thumbnail capture: HighResShot produced no file; trying a window-size screenshot");
                    k.phase = Capture::Phase::Shoot;
                    k.until = now;
                    return;
                }
                return EndCapture("error", "screenshot-unavailable", "The game did not write a screenshot.");
            }
            return;
        }
        }
    }

    void GameControl::Reseed(std::uint32_t index, const char* why)
    {
        const std::uint32_t value = SeedFor(m_seeding->seed, index);
        std::srand(value);
        if (index < 4 || index % 25 == 0) Log("match seed: event " + std::to_string(index) + " (" + why + ")");
    }

    void GameControl::OnAttemptStarted(const std::string& scenario)
    {
        if (!m_seeding || !m_seeding->active || scenario != m_seeding->scenario) return;
        m_seeding->events = 0;
        Reseed(0, "attempt start");
    }

    void GameControl::OnSpawnEvent()
    {
        if (!m_seeding || !m_seeding->active || m_lastScenario != m_seeding->scenario) return;
        // Kill credit and character death can both fire for one death: one event per frame.
        if (m_seeding->lastEventTick == m_tick) return;
        m_seeding->lastEventTick = m_tick;
        Reseed(++m_seeding->events, "spawn event");
    }

    // Leaving a run without completing it. In a challenge this is
    // ScenarioManager:CancelChallenge, the game's cancel path (it ends in the
    // ChallengeCanceled broadcast; ChallengeComplete, the stats CSV and
    // leaderboard uploads belong to completion). AimModCore already uses it
    // to cancel a challenge started with overrides. Outside a challenge only
    // the freeplay session is reset.
    void GameControl::BeginQuit(const GameCommand& c, double now, bool inChallenge, const std::string& current)
    {
        UObject* manager = m_scene.Manager();
        ResetOverrides("run quit");
        if (m_seeding) Log("match seed: off (run quit)");
        m_seeding.reset();
        if (!inChallenge)
        {
            const bool reset = m_resetFreeplay.ok() && m_resetFreeplay.Call(manager, [](std::uint8_t*, const Param&) {});
            Log(std::string("game control: quit-run: no challenge running; freeplay session ") + (reset ? "reset" : "left as is"));
            return Answer(c.sequence, "done", "quit", reset ? "" : "No run to quit.");
        }
        if (!m_cancel.ok()) return Answer(c.sequence, "error", "unsupported", "Quitting a run is unavailable in this game version.");
        if (!m_cancel.Call(manager, [](std::uint8_t*, const Param&) {}))
            return Answer(c.sequence, "error", "quit-failed", "The game did not accept the cancel. Press Esc and leave the run.");
        Log("game control: quit-run: CancelChallenge in \"" + current + "\" (abandoned, no score submitted)");
        m_quitting = Quitting{c.sequence, now + 6.0, now + 2.5, false, current};
        Answer(c.sequence, "accepted", "quitting", "");
    }

    void GameControl::TickQuit(double now, bool inChallenge)
    {
        Quitting& q = *m_quitting;
        if (!inChallenge)
        {
            const auto seq = q.sequence;
            Log("game control: quit-run: challenge left" + std::string(q.retried ? " (after a second CancelChallenge)" : ""));
            m_quitDone = q.scenario;
            m_quitting.reset();
            return Answer(seq, "done", "quit", "");
        }
        if (!q.retried && now >= q.retryAt)
        {
            if (UObject* manager = m_scene.Manager()) m_cancel.Call(manager, [](std::uint8_t*, const Param&) {});
            q.retried = true;
            Log("game control: quit-run: still in the challenge; CancelChallenge again");
        }
        if (now > q.deadline)
        {
            const auto seq = q.sequence;
            m_quitting.reset();
            Answer(seq, "error", "quit-failed", "The game did not leave the challenge. Press Esc and leave the run.");
        }
    }

    bool GameControl::Refresh(const char* why)
    {
        bool any = false;
        if (UObject* manager = m_scene.Manager(); manager && m_refreshLocal.ok())
            any |= m_refreshLocal.Call(manager, [](std::uint8_t*, const Param&) {});
        Log(std::string("game control: local scenarios refreshed (") + why + ")" + (any ? "" : " - unavailable"));
        return any;
    }

    void GameControl::Proceed(const GameCommand& c, double now, UObject* manager)
    {
        if (c.action == GameCommand::Action::CaptureThumbnail) return BeginCapture(c, now, m_lastScenario, manager);
        const bool load = c.action == GameCommand::Action::LoadScenario;
        ResetOverrides("scenario-change");
        if (m_seeding) Log("match seed: off (new scenario request)");
        m_seeding.reset();
        m_pending = Pending{c, now + (load ? 30.0 : 45.0)};
        // Replays view the scenario without starting it; starts use the
        // requested play type (freeplay for any override).
        if (!load && !SetPlayType(manager, c.mode))
        {
            m_pending.reset();
            return Answer(c.sequence, "error", "start-failed", "The game did not accept the play type.");
        }
        if (!StartScenario(c.scenario, !load))
        {
            m_pending.reset();
            return Answer(c.sequence, "error", "start-failed", "The game did not start loading \"" + c.scenario + "\".");
        }
        m_pending->started = true;
        Answer(c.sequence, "accepted", load ? "loading" : "starting", "");
    }

    void GameControl::Begin(const GameCommand& c, double now, const std::string& current, bool inChallenge, bool loading)
    {
        Log("game command " + std::to_string(c.sequence) + ": " + Describe(c));
        UObject* manager = m_scene.Manager();
        if (c.action == GameCommand::Action::ResetOverrides)
        {
            ResetOverrides("requested");
            return Answer(c.sequence, "done", "reset", "");
        }
        if (c.action == GameCommand::Action::RefreshScenarios)
        {
            if (inChallenge) return Answer(c.sequence, "error", "challenge-active", "A challenge is running. Finish or quit it first.");
            if (!m_refreshLocal.ok()) return Answer(c.sequence, "error", "unsupported", "Rescanning scenarios is unavailable in this game version.");
            const bool ok = Refresh("requested");
            return Answer(c.sequence, ok ? "done" : "error", ok ? "refreshed" : "refresh-failed", "");
        }
        if (!manager || !m_scene.Player()) return Answer(c.sequence, "error", "game-unavailable", "KovaaK's is not ready yet.");
        if (c.action == GameCommand::Action::QuitRun)
        {
            if (m_pending || m_capture || m_refreshing || m_quitting) return Answer(c.sequence, "error", "busy", "Another game command is running. Try again in a moment.");
            return BeginQuit(c, now, inChallenge, current);
        }
        // Never interrupt a challenge: leaving it would cancel a ranked attempt.
        if (inChallenge) return Answer(c.sequence, "error", "challenge-active", "A challenge is running. Finish or quit it first.");
        if (loading || m_pending) return Answer(c.sequence, "error", "busy", "A scenario is loading. Try again in a moment.");
        const bool load = c.action == GameCommand::Action::LoadScenario;
        if (c.action != GameCommand::Action::CaptureThumbnail && (load ? !m_canLoad : !m_canStart))
            return Answer(c.sequence, "error", "unsupported", "Load \"" + c.scenario + "\" in KovaaK's; automatic loading is unavailable in this game version.");
        if (c.action == GameCommand::Action::CaptureThumbnail && !m_canCapture)
            return Answer(c.sequence, "error", "unsupported", "Thumbnail capture is unavailable in this game version.");
        if (m_capture) return Answer(c.sequence, "error", "busy", "A thumbnail capture is running.");
        if (c.action == GameCommand::Action::EndRun)
        {
            // Lobby time limit: end the freeplay match run cleanly by reloading
            // the scenario (stop: without playing; reset: playing again).
            if (!m_canLoad) return Answer(c.sequence, "error", "unsupported", "Ending a run is unavailable in this game version.");
            if (current != c.scenario) return Answer(c.sequence, "error", "not-current", "\"" + c.scenario + "\" is not the scenario being played.");
            ResetOverrides("run ended");
            if (m_seeding) Log("match seed: off (run ended)");
            m_seeding.reset();
            m_pending = Pending{c, now + 30.0};
            m_pending->issued = now;
            if (c.reset && !SetPlayType(manager, GameCommand::Mode::FreePlay))
            {
                m_pending.reset();
                return Answer(c.sequence, "error", "end-failed", "The game did not accept the play type.");
            }
            if (!StartScenario(c.scenario, c.reset))
            {
                m_pending.reset();
                return Answer(c.sequence, "error", "end-failed", "The game did not reload \"" + c.scenario + "\".");
            }
            m_pending->started = true;
            return Answer(c.sequence, "accepted", "ending", "");
        }
        if (load && current == c.scenario) return Answer(c.sequence, "done", "already-loaded", "");
        if (m_refreshing) return Answer(c.sequence, "error", "busy", "Scenarios are being refreshed. Try again in a moment.");
        if (!ScenarioKnown(manager, c.scenario))
        {
            // A scenario written after the game indexed its folder (for
            // example a multiplayer match): rescan once if its file exists.
            std::error_code error;
            const bool onDisk = IsScenarioFileName(c.scenario) && !m_scenarioFolder.empty() &&
                                std::filesystem::is_regular_file(m_scenarioFolder / std::filesystem::path(Widen(c.scenario + ".sce")), error);
            if (!onDisk || !m_refreshLocal.ok()) return Answer(c.sequence, "error", "unknown-scenario", "\"" + c.scenario + "\" is not installed.");
            Refresh("new scenario file");
            if (!ScenarioKnown(manager, c.scenario))
            {
                m_refreshing = Refreshing{c, now + 5.0, now + 0.25};
                return Answer(c.sequence, "accepted", "refreshing", "");
            }
        }
        Proceed(c, now, manager);
    }

    void GameControl::Tick(double now, const std::string& current, bool inChallenge, bool loading)
    {
        ++m_tick;
        if (m_seeding && m_seeding->active && current != m_seeding->scenario)
        {
            Log("match seed: off (scenario changed)");
            m_seeding.reset();
        }
        if (auto request = m_output.TakeCommand())
        {
            if (auto* error = std::get_if<CommandError>(&*request)) Answer(error->sequence, "error", error->code, error->message);
            else Begin(std::get<GameCommand>(*request), now, current, inChallenge, loading);
        }
        // Ranked protection: a challenge must never run with an override.
        if (m_overrides.active && inChallenge)
        {
            if (UObject* manager = m_scene.Manager()) m_cancel.Call(manager, [](std::uint8_t*, const Param&) {});
            ResetOverrides("challenge started");
            Warn("challenge cancelled: freeplay overrides were still active; start it again for a clean ranked run");
            Answer(0, "notice", "challenge-cancelled", "A challenge started while freeplay overrides were active and was cancelled. Start it again.");
        }
        if (current != m_lastScenario)
        {
            if (m_overrides.active && current != m_overrides.scenario && !m_pending) ResetOverrides("scenario changed");
            m_lastScenario = current;
        }
        if (m_refreshing && now >= m_refreshing->nextCheck)
        {
            Refreshing& r = *m_refreshing;
            r.nextCheck = now + 0.25;
            UObject* manager = m_scene.Manager();
            if (inChallenge || !manager)
            {
                const auto seq = r.command.sequence;
                m_refreshing.reset();
                return Answer(seq, "error", inChallenge ? "challenge-active" : "game-unavailable", "");
            }
            if (ScenarioKnown(manager, r.command.scenario))
            {
                GameCommand command = r.command;
                m_refreshing.reset();
                Proceed(command, now, manager);
                return;
            }
            if (!r.reloaded && m_reloadProfiles.ok())
            {
                // Second, broader rescan: the Reload Profiles path.
                r.reloaded = true;
                if (UObject* state = m_scene.GameState()) m_reloadProfiles.Call(state, [](std::uint8_t*, const Param&) {});
                Log("game control: profiles reloaded (new scenario file)");
            }
            if (now > r.deadline)
            {
                const GameCommand command = r.command;
                m_refreshing.reset();
                return Answer(command.sequence, "error", "unknown-scenario",
                              "\"" + command.scenario + "\" exists on disk but the game did not index it. Use Reload Profiles in KovaaK's, then try again.");
            }
            return;
        }
        if (m_quitting) return TickQuit(now, inChallenge);
        if (m_capture) return TickCapture(now, current, inChallenge, loading);
        if (!m_pending) return;
        Pending& p = *m_pending;
        const GameCommand& c = p.command;
        if (now > p.deadline)
        {
            const auto seq = c.sequence;
            m_pending.reset();
            return Answer(seq, "error", "timeout", "\"" + c.scenario + "\" did not finish loading.");
        }
        if (c.action == GameCommand::Action::EndRun && !p.sawLoading)
        {
            if (loading || current != c.scenario) p.sawLoading = true;
            else if (now - p.issued > 5.0)
            {
                const auto seq = c.sequence;
                m_pending.reset();
                return Answer(seq, "error", "end-failed", "The game did not reload the scenario.");
            }
            return;
        }
        if (current != c.scenario || loading)
        {
            p.loadedAt = -1;
            return;
        }
        if (p.loadedAt < 0) p.loadedAt = now;
        if (now - p.loadedAt < 1.0) return; // let the scenario settle
        const auto seq = c.sequence;
        if (c.action == GameCommand::Action::LoadScenario)
        {
            m_pending.reset();
            return Answer(seq, "done", "loaded", "");
        }
        if (c.action == GameCommand::Action::EndRun)
        {
            const bool reset = c.reset;
            m_pending.reset();
            if (inChallenge) return Answer(seq, "error", "mode-mismatch", "The game started a challenge instead of freeplay.");
            return Answer(seq, "done", reset ? "reset" : "stopped", "");
        }
        if (c.mode == GameCommand::Mode::Challenge)
        {
            if (inChallenge)
            {
                if (c.seed)
                {
                    m_seeding = Seeding{*c.seed, c.scenario};
                    m_seeding->active = true;
                    Reseed(0, "challenge start");
                }
                m_pending.reset();
                return Answer(seq, "done", "started", "");
            }
            // The browser path loads first; start the challenge explicitly once.
            if (now - p.loadedAt > 3.0 && m_playCurrent.ok() && p.started)
            {
                p.started = false;
                if (UObject* manager = m_scene.Manager())
                    m_playCurrent.Call(manager, [&](std::uint8_t* value, const Param& param) {
                        if (param.kind == Kind::UInt8) *value = param.name == "PlayType" ? PlayTypeChallenge : StartTypeStart;
                    });
            }
            return;
        }
        if (inChallenge)
        {
            // Freeplay was requested: never modify a challenge.
            m_pending.reset();
            return Answer(seq, "error", "mode-mismatch", "The game started a challenge instead of freeplay; no overrides were applied.");
        }
        const bool ok = !c.HasOverrides() || ApplyOverrides(c);
        if (c.seed)
        {
            m_seeding = Seeding{*c.seed, c.scenario};
            m_seeding->active = true;
            Reseed(0, "scenario start");
        }
        m_pending.reset();
        Answer(seq, ok ? "done" : "error", ok ? "started" : "override-failed", ok ? "" : "Some overrides could not be applied on this game version.");
    }
} // namespace aimmod
