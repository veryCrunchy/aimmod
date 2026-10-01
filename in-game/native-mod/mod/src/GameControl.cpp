#include "GameControl.hpp"

#include "Log.hpp"
#include "Output.hpp"
#include "World.hpp"

#include <Unreal/CoreUObject/UObject/UnrealType.hpp>
#include <Unreal/UObject.hpp>
#include <Unreal/UObjectGlobals.hpp>

#include <Windows.h>

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
        {
            wchar_t exe[MAX_PATH * 4]{};
            const DWORD length = GetModuleFileNameW(nullptr, exe, static_cast<DWORD>(std::size(exe)));
            if (length > 0 && length < std::size(exe))
                m_scenarioFolder = std::filesystem::path(exe).parent_path().parent_path().parent_path() / L"Saved" / L"SaveGames" / L"Scenarios";
        }
        m_canLoad = m_start.ok() && m_activate.ok() && m_startDefault && m_localHash.ok() && m_b.lifecycleReady();
        m_canStart = m_canLoad && m_persistentPlayType.ok() && m_cancel.ok();
        auto st = [](const Getter& g) { return g.ok() ? std::string("ok") : g.error(); };
        Log("game control: load=" + std::string(m_canLoad ? "ok" : "unavailable") + " start=" + (m_canStart ? "ok" : "unavailable") +
            " Start_Scenario=" + st(m_start) + " Activate=" + st(m_activate) + " SetPersistentPlayType=" + st(m_persistentPlayType) +
            " GetLocalScenarioHash=" + st(m_localHash) + " CancelChallenge=" + st(m_cancel) + " timeScale=" + st(m_timeDilation) +
            " mapScale=" + st(m_mapScale) + " adaptive=" + st(m_adaptiveOverride) + " weapon=" + st(m_weapon) + " refresh=" + st(m_refreshLocal) +
            " reloadProfiles=" + st(m_reloadProfiles));
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
        const bool load = c.action == GameCommand::Action::LoadScenario;
        ResetOverrides("scenario-change");
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
        // Never interrupt a challenge: leaving it would cancel a ranked attempt.
        if (inChallenge) return Answer(c.sequence, "error", "challenge-active", "A challenge is running. Finish or quit it first.");
        if (loading || m_pending) return Answer(c.sequence, "error", "busy", "A scenario is loading. Try again in a moment.");
        const bool load = c.action == GameCommand::Action::LoadScenario;
        if (load ? !m_canLoad : !m_canStart)
            return Answer(c.sequence, "error", "unsupported", "Load \"" + c.scenario + "\" in KovaaK's; automatic loading is unavailable in this game version.");
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
        if (!m_pending) return;
        Pending& p = *m_pending;
        const GameCommand& c = p.command;
        if (now > p.deadline)
        {
            const auto seq = c.sequence;
            m_pending.reset();
            return Answer(seq, "error", "timeout", "\"" + c.scenario + "\" did not finish loading.");
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
        if (c.mode == GameCommand::Mode::Challenge)
        {
            if (inChallenge)
            {
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
        m_pending.reset();
        Answer(seq, ok ? "done" : "error", ok ? "started" : "override-failed", ok ? "" : "Some overrides could not be applied on this game version.");
    }
} // namespace aimmod
