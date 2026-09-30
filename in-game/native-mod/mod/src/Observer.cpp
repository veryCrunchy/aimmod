#include "Observer.hpp"

#include "Log.hpp"
#include "Output.hpp"

#include <Unreal/CoreUObject/UObject/Class.hpp>
#include <Unreal/CoreUObject/UObject/UnrealType.hpp>
#include <Unreal/FFrame.hpp>
#include <Unreal/Hooks/Hooks.hpp>
#include <Unreal/UClass.hpp>
#include <Unreal/UFunction.hpp>
#include <Unreal/UObject.hpp>
#include <Unreal/UObjectGlobals.hpp>

#include <Windows.h>
#include <TlHelp32.h>

#include <chrono>
#include <cstring>
#include <ctime>

namespace aimmod
{
    using namespace RC::Unreal;
    using game::Getter;
    using game::Shape;

    namespace
    {
        constexpr double PollInterval = 0.05;         // lifecycle + values, 20 Hz
        constexpr double SampleInterval = 1.0 / 60.0; // replay frames
        constexpr double RebindInterval = 1.0;
        constexpr double WatchInterval = 5.0;

        double Seconds()
        {
            return std::chrono::duration<double>(std::chrono::steady_clock::now().time_since_epoch()).count();
        }

        // The Unreal game thread is the process's first thread.
        std::uint32_t FindMainThread()
        {
            const DWORD pid = GetCurrentProcessId();
            HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
            if (snapshot == INVALID_HANDLE_VALUE) return 0;
            THREADENTRY32 entry{sizeof(entry)};
            std::uint32_t best = 0;
            ULONGLONG bestTime = ~0ULL;
            for (BOOL ok = Thread32First(snapshot, &entry); ok; ok = Thread32Next(snapshot, &entry))
            {
                if (entry.th32OwnerProcessID != pid) continue;
                HANDLE thread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ThreadID);
                if (!thread) continue;
                FILETIME created, exited, kernel, user;
                if (GetThreadTimes(thread, &created, &exited, &kernel, &user))
                {
                    ULONGLONG t = (static_cast<ULONGLONG>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
                    if (t < bestTime)
                    {
                        bestTime = t;
                        best = entry.th32ThreadID;
                    }
                }
                CloseHandle(thread);
            }
            CloseHandle(snapshot);
            return best;
        }

        UObject* FindLive(const wchar_t* className, UObject* preferredOuter, bool (*accept)(UObject*, void*) = nullptr, void* data = nullptr)
        {
            std::vector<UObject*> found;
            UObjectGlobals::FindAllOf(className, found);
            UObject* fallback = nullptr;
            for (UObject* object : found)
            {
                if (!game::IsLiveInstance(object)) continue;
                if (accept && !accept(object, data)) continue;
                if (preferredOuter && object->GetOuterPrivate() == preferredOuter) return object;
                if (!fallback) fallback = object;
            }
            return fallback;
        }

        std::string Status(const Getter& g)
        {
            return g.ok() ? "ok" : (g.error().empty() ? "missing" : g.error());
        }

        bool Finite(const std::optional<double>& v) { return v && IsUsableNumber(*v); }

        // Monotonic within an attempt: counters never go backwards, so late
        // reads after the challenge stopped cannot erase measured values.
        void KeepMax(std::optional<double>& target, const std::optional<double>& value)
        {
            if (!Finite(value) || *value < 0) return;
            if (!target || *value >= *target) target = value;
        }
    } // namespace

    Observer::Observer(Output& output, std::string version)
        : m_output(output), m_version(std::move(version)),
          m_lifecycle(std::to_string(static_cast<long long>(std::time(nullptr))) + "-" + std::to_string(GetCurrentProcessId())),
          m_sampler(m_b, m_scene, output), m_presenter(m_b, m_scene, output)
    {
    }

    Observer::~Observer() { Shutdown(); }

    bool Observer::OnGameThread() const
    {
        const std::uint32_t id = m_gameThread.load(std::memory_order_relaxed);
        return id != 0 && id == GetCurrentThreadId();
    }

    std::string Observer::Capabilities() const
    {
        std::string caps;
        if (m_b.lifecycleReady()) caps = "telemetry";
        if (m_b.replayReady()) caps += caps.empty() ? "replay" : ",replay";
        if (m_presenter.ready()) caps += caps.empty() ? "presenter" : ",presenter";
        return caps;
    }

    void Observer::BindFunctions()
    {
        const wchar_t* manager = STR("/Script/GameSkillsTrainer.ScenarioManager:");
        auto path = [](const wchar_t* prefix, const wchar_t* name) { return std::wstring(prefix) + name; };
        m_b.isInChallenge.BindPath(path(manager, STR("IsInChallenge")).c_str(), Shape::Bool);
        m_b.isScenarioLoading.BindPath(path(manager, STR("IsScenarioLoading")).c_str(), Shape::Bool);
        m_b.queueRemaining.BindPath(path(manager, STR("GetChallengeQueueTimeRemaining")).c_str(), Shape::Number);
        m_b.timeElapsed.BindPath(path(manager, STR("GetChallengeTimeElapsed")).c_str(), Shape::Number);
        m_b.timeRemaining.BindPath(path(manager, STR("GetChallengeTimeRemaining")).c_str(), Shape::Number);
        m_b.currentScenario.BindPath(path(manager, STR("GetCurrentScenario")).c_str(), Shape::Object);
        const wchar_t* scenario = STR("/Script/GameSkillsTrainer.Scenario:");
        m_b.scenarioActive.BindPath(path(scenario, STR("IsActive")).c_str(), Shape::Bool);
        m_b.scenarioInChallenge.BindPath(path(scenario, STR("IsInChallenge")).c_str(), Shape::Bool);
        m_b.scenarioName.BindPath(path(scenario, STR("GetName")).c_str(), Shape::String);
        const wchar_t* stats = STR("/Script/GameSkillsTrainer.StatsManager:");
        m_b.lastScore.BindPath(path(stats, STR("GetLastScore")).c_str(), Shape::Number);
        m_b.lastTimeRemaining.BindPath(path(stats, STR("GetLastChallengeTimeRemaining")).c_str(), Shape::Number);
        const wchar_t* indicators = STR("/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:");
        m_b.indicatorScore.BindPath(path(indicators, STR("Get_Score_ValueElse")).c_str(), Shape::ValueElse);
        m_b.indicatorShots.BindPath(path(indicators, STR("Get_ShotsFired_ValueElse")).c_str(), Shape::ValueElse);
        m_b.indicatorHits.BindPath(path(indicators, STR("Get_ShotsHit_ValueElse")).c_str(), Shape::ValueElse);
        m_b.indicatorKills.BindPath(path(indicators, STR("Get_Kills_ValueElse")).c_str(), Shape::ValueElse);
        m_b.indicatorDamage.BindPath(path(indicators, STR("Get_DamageDone_ValueElse")).c_str(), Shape::ValueElse);
        m_b.timeSeconds.BindPath(STR("/Script/Engine.GameplayStatics:GetTimeSeconds"), Shape::Number);
        m_b.gamePaused.BindPath(STR("/Script/Engine.GameplayStatics:IsGamePaused"), Shape::Bool);
        m_b.statics = UObjectGlobals::StaticFindObject<UObject*>(nullptr, nullptr, STR("/Script/Engine.Default__GameplayStatics"));
        m_b.cameraLocation.BindPath(STR("/Script/Engine.PlayerCameraManager:GetCameraLocation"), Shape::Vector);
        m_b.cameraRotation.BindPath(STR("/Script/Engine.PlayerCameraManager:GetCameraRotation"), Shape::Vector);
        m_b.cameraFov.BindPath(STR("/Script/Engine.PlayerCameraManager:GetFOVAngle"), Shape::Number);
        m_b.actorLocation.BindPath(STR("/Script/Engine.Actor:K2_GetActorLocation"), Shape::Vector);
        m_b.actorRotation.BindPath(STR("/Script/Engine.Actor:K2_GetActorRotation"), Shape::Vector);
        m_b.capsuleRadius.BindPath(STR("/Script/Engine.CapsuleComponent:GetScaledCapsuleRadius"), Shape::Number);
        m_b.capsuleHalfHeight.BindPath(STR("/Script/Engine.CapsuleComponent:GetScaledCapsuleHalfHeight"), Shape::Number);
        m_b.cameraManager.Bind(game::FindClass(STR("/Script/Engine.PlayerController")), STR("PlayerCameraManager"));
        m_b.myCharacter.Bind(game::FindClass(STR("/Script/GameSkillsTrainer.MetaPlayerController")), STR("MyCharacter"));
        m_b.hidden.Bind(game::FindClass(STR("/Script/Engine.Actor")), STR("bHidden"));
        m_b.capsule.Bind(game::FindClass(STR("/Script/Engine.Character")), STR("CapsuleComponent"));
        const wchar_t* state = STR("/Script/GameSkillsTrainer.MetaGameState:");
        m_b.characters.BindPath(path(state, STR("GetCharacters")).c_str(), Shape::ObjectArray);
        m_b.mapName.BindPath(path(state, STR("GetCurrentMapName")).c_str(), Shape::String);
        m_b.mapScale.BindPath(path(state, STR("GetMapScale")).c_str(), Shape::Number);
    }

    void Observer::RegisterCallbacks()
    {
        using namespace RC::Unreal::Hook;
        FCallbackOptions tick{};
        tick.bReadonly = true;
        tick.OwnerModName = STR("AimModCore");
        tick.HookName = STR("AimModCore.Tick");
        if (auto id = RegisterEngineTickPostCallback([this](TCallbackIterationData<void>&, UEngine*, float, bool) {
                if (m_shutdown.load(std::memory_order_relaxed)) return;
                if (!m_engineTick.load(std::memory_order_relaxed))
                {
                    m_gameThread = GetCurrentThreadId();
                    m_engineTick = true;
                }
                OnTick();
            }, tick);
            id != ERROR_ID)
            m_callbacks.push_back(id);

        // Replay presentation before the world ticks, so this frame's camera
        // update renders the pose at this frame's playback instant.
        FCallbackOptions present{};
        present.bReadonly = true;
        present.OwnerModName = STR("AimModCore");
        present.HookName = STR("AimModCore.Present");
        if (auto id = RegisterEngineTickPreCallback([this](TCallbackIterationData<void>&, UEngine*, float, bool) {
                if (m_shutdown.load(std::memory_order_relaxed) || !OnGameThread()) return;
                m_presenter.Apply();
            }, present);
            id != ERROR_ID)
            m_callbacks.push_back(id);

        FCallbackOptions pe{};
        pe.bReadonly = true;
        pe.OwnerModName = STR("AimModCore");
        pe.HookName = STR("AimModCore.Observe");
        if (auto id = RegisterProcessEventPostCallback([this](TCallbackIterationData<void>&, UObject* context, UFunction* function, void*) {
                OnProcessEvent(context, function);
            }, pe);
            id != ERROR_ID)
            m_callbacks.push_back(id);

        // Kill credit is called through reflection on 3.9.11 (observed live).
        if (UFunction* kill = game::FindFunction(STR("/Script/GameSkillsTrainer.ScenarioManager:NotifyPlayerKillCredit")))
        {
            auto ids = UObjectGlobals::RegisterHook(
                kill, [](UnrealScriptFunctionCallableContext&, void*) {},
                [](UnrealScriptFunctionCallableContext&, void* data) { static_cast<Observer*>(data)->m_killCredits.fetch_add(1, std::memory_order_relaxed); },
                this);
            m_hooks.emplace_back(kill, ids);
        }

        // Replay target markers: shooter and recipient of a registered hit.
        if (UFunction* hit = game::FindFunction(STR("/Script/GameSkillsTrainer.WeaponParentActor:Send_ShotHit")))
        {
            Getter shape;
            shape.Bind(hit, Shape::Observe);
            int shooter = -1, target = -1;
            for (int i = 0; i < static_cast<int>(shape.params().size()); ++i)
            {
                const auto& p = shape.params()[static_cast<std::size_t>(i)];
                if (p.kind != game::Kind::Object || p.ret || p.out) continue;
                if (shooter < 0) shooter = p.offset;
                else if (target < 0) target = p.offset;
            }
            if (shooter >= 0 && target >= 0)
            {
                static std::pair<int, int> offsets;
                offsets = {shooter, target};
                auto ids = UObjectGlobals::RegisterHook(
                    hit, [](UnrealScriptFunctionCallableContext&, void*) {},
                    [](UnrealScriptFunctionCallableContext& context, void* data) {
                        auto* self = static_cast<Observer*>(data);
                        self->m_shotHits.fetch_add(1, std::memory_order_relaxed);
                        if (!self->OnGameThread() || !context.TheStack.Locals()) return;
                        UObject *who, *whom;
                        std::memcpy(&who, context.TheStack.Locals() + offsets.first, sizeof(who));
                        std::memcpy(&whom, context.TheStack.Locals() + offsets.second, sizeof(whom));
                        self->m_sampler.OnShotHit(who, whom);
                    },
                    this);
                m_hooks.emplace_back(hit, ids);
            }
        }

        // Replay inputs: the game's own input recording entry points (observed
        // only; the game's recorder is never enabled).
        static constexpr const wchar_t* inputs[] = {
            STR("AxisTurn"),       STR("AxisLookUp"),      STR("AxisMoveForward"), STR("AxisMoveRight"),  STR("FirePressed"),
            STR("FireReleased"),   STR("AltFirePressed"),  STR("AltFireReleased"), STR("JumpPressed"),    STR("JumpReleased"),
            STR("CrouchPressed"),  STR("CrouchReleased"),  STR("ReloadPressed"),   STR("ReloadReleased"), STR("ADSPressed"),
            STR("ADSReleased"),    STR("AbilityPressed"),  STR("AbilityReleased"), STR("WeaponPressed"),  STR("WeaponReleased")};
        for (const wchar_t* name : inputs)
        {
            std::wstring fullPath = std::wstring(STR("/Script/GameSkillsTrainer.MetaInputRecordingComponent:Record")) + name;
            UFunction* function = game::FindFunction(fullPath.c_str());
            if (!function) continue;
            auto hook = std::make_unique<InputHook>();
            hook->self = this;
            hook->action = game::Narrow(name);
            hook->axis = hook->action.starts_with("Axis");
            hook->function = function;
            Getter shape;
            shape.Bind(function, Shape::Observe);
            for (const auto& p : shape.params())
            {
                if (p.ret || p.out || !game::IsNumeric(p.kind)) continue;
                hook->valueOffset = p.offset;
                hook->valueKind = p.kind;
                break;
            }
            if (hook->axis && hook->valueOffset < 0) continue;
            hook->ids = UObjectGlobals::RegisterHook(
                function, [](UnrealScriptFunctionCallableContext&, void*) {},
                [](UnrealScriptFunctionCallableContext& context, void* data) {
                    auto* h = static_cast<InputHook*>(data);
                    Observer* self = h->self;
                    self->m_inputEvents.fetch_add(1, std::memory_order_relaxed);
                    if (!self->OnGameThread() || !self->m_sampler.recording()) return;
                    double value = 1.0;
                    if (h->valueOffset >= 0)
                    {
                        if (!context.TheStack.Locals()) return;
                        auto v = game::ReadNumber(context.TheStack.Locals() + h->valueOffset, h->valueKind);
                        if (!v) return;
                        value = *v;
                    }
                    self->m_sampler.OnInput(context.Context, h->action.c_str(), h->axis, value);
                },
                hook.get());
            m_inputHooks.push_back(std::move(hook));
        }
    }

    void Observer::Initialize()
    {
        if (m_initialized.exchange(true)) return;
        m_mainThread = FindMainThread();
        BindFunctions();
        m_presenter.Bind();
        RegisterCallbacks();
        m_presenter.Start();
        m_output.SetCapabilities(Capabilities());
        m_output.PublishLive(FormatLiveOverlay({}));
        m_output.PublishReplayStatus(FormatReplayStatus(m_b.replayReady() ? "ready" : "unsupported", 0, 0, ""));
        Log("started " + m_version + "; capabilities=" + (Capabilities().empty() ? std::string("none") : Capabilities()));
        const Getter* signatures[] = {&m_b.isInChallenge, &m_b.timeElapsed, &m_b.timeRemaining, &m_b.currentScenario, &m_b.scenarioName,
                                      &m_b.lastScore, &m_b.lastTimeRemaining, &m_b.indicatorScore, &m_b.indicatorShots, &m_b.indicatorKills,
                                      &m_b.characters, &m_b.timeSeconds};
        for (const Getter* g : signatures)
            if (g->ok()) Log("  signature " + g->Signature());
        // Diagnostic only (never called): candidate entry points for loading a
        // replay's scenario on request, to choose a safe one from real signatures.
        for (const wchar_t* path : {STR("/Script/GameSkillsTrainer.ScenarioLoader:LoadScenario"), STR("/Script/GameSkillsTrainer.ScenarioLoader:LoadScenarioFromManager"),
                                    STR("/Script/GameSkillsTrainer.ScenarioLoader:LoadScenarioAfterDelay"), STR("/Script/GameSkillsTrainer.Start_Scenario:Start_Scenario"),
                                    STR("/Script/GameSkillsTrainer.ScenarioManager:GetLocalScenarioByName"), STR("/Script/GameSkillsTrainer.ScenarioManager:PlayCurrentScenario"),
                                    STR("/Script/GameSkillsTrainer.ScenarioManager:InitializeScenario"), STR("/Script/GameSkillsTrainer.ScenarioManager:SetCurrentScenarioPlayType"),
                                    STR("/Script/GameSkillsTrainer.ScenarioManager:SetLoadingScenario")})
        {
            Getter candidate;
            if (candidate.BindPath(path, Shape::Observe)) Log("  scenario-load candidate " + candidate.Signature());
            else Log("  scenario-load candidate missing: " + game::Narrow(path));
        }
        LogCompatibility("startup");
    }

    void Observer::Shutdown()
    {
        if (!m_initialized.load() || m_shutdown.exchange(true)) return;
        for (std::uint64_t id : m_callbacks) RC::Unreal::Hook::UnregisterCallback(id);
        m_callbacks.clear();
        m_presenter.Stop();
        for (auto& [function, ids] : m_hooks) UObjectGlobals::UnregisterHook(function, ids);
        m_hooks.clear();
        for (auto& hook : m_inputHooks) UObjectGlobals::UnregisterHook(hook->function, hook->ids);
        // Hooks may still reference InputHook data if a callback is mid-flight;
        // the objects are intentionally kept until the module unloads.
        if (m_sampler.recording()) m_sampler.Finish("interrupted", std::nullopt);
        m_watch.store(nullptr);
    }

    void Observer::LogCompatibility(const char* reason)
    {
        auto found = [](UObject* o) { return o ? "found" : "missing"; };
        Log(std::string("compatibility (") + reason + "):");
        Log("  lifecycle: poll=" + std::string(m_b.lifecycleReady() ? "ok" : "unavailable") + " IsInChallenge=" + Status(m_b.isInChallenge) +
            " GetChallengeTimeElapsed=" + Status(m_b.timeElapsed) + " GetChallengeTimeRemaining=" + Status(m_b.timeRemaining) +
            " GetCurrentScenario=" + Status(m_b.currentScenario) + " Scenario.GetName=" + Status(m_b.scenarioName) +
            " manager=" + found(m_scene.Manager()));
        Log("  score: GetLastScore=" + Status(m_b.lastScore) + " GetLastChallengeTimeRemaining=" + Status(m_b.lastTimeRemaining) +
            " Get_Score_ValueElse=" + Status(m_b.indicatorScore) + " stats=" + found(m_scene.Stats()) + " indicators=" + found(m_scene.Indicators()));
        Log("  replay: " + std::string(m_b.replayReady() ? "ok" : "unavailable") + " camera=" + Status(m_b.cameraLocation) +
            " characters=" + Status(m_b.characters) + " player=" + found(m_scene.Player()) + " gameState=" + found(m_scene.GameState()) +
            " inputHooks=" + std::to_string(m_inputHooks.size()) + "/20 recording=" + (m_output.recordingEnabled() ? "enabled" : "disabled"));
        Log("  events: broadcast functions=" + std::to_string(m_broadcastFound) + "/" + std::to_string(m_broadcastFunctions) +
            " delegate handlers=" + std::to_string(m_delegateHandlers) + " killCredit hook=" + (m_hooks.empty() ? "missing" : "ok"));
    }

    void Observer::Rebind(double now)
    {
        m_nextRebind = now + RebindInterval;
        bool changed = false;
        if (!m_scene.Manager())
        {
            m_scene = {};
            if (UObject* manager = FindLive(STR("ScenarioManager"), nullptr))
            {
                m_scene.manager = manager;
                m_scene.instance = manager->GetOuterPrivate();
                changed = true;
            }
        }
        if (m_scene.Manager())
        {
            if (!m_scene.Stats())
                if (UObject* stats = FindLive(STR("StatsManager"), m_scene.instance))
                {
                    m_scene.stats = stats;
                    changed = true;
                }
            if (!m_scene.Indicators())
                if (UObject* indicators = FindLive(STR("PerformanceIndicatorsStateReceiver"), m_scene.instance))
                {
                    m_scene.indicators = indicators;
                    changed = true;
                }
        }
        UObject* player = m_scene.Player();
        if (!player || !m_b.myCharacter.Object(player))
        {
            auto withCharacter = [](UObject* o, void* data) { return static_cast<game::Bindings*>(data)->myCharacter.Object(o) != nullptr; };
            UObject* next = FindLive(STR("MetaPlayerController"), nullptr, withCharacter, &m_b);
            if (!next && !player) next = FindLive(STR("MetaPlayerController"), nullptr);
            if (next && next != player)
            {
                m_scene.player = next;
                changed = true;
            }
        }
        if (!m_scene.GameState())
            if (UObject* state = FindLive(STR("MetaGameState"), nullptr))
            {
                m_scene.gameState = state;
                changed = true;
            }
        if (now >= m_nextWatch)
        {
            m_nextWatch = now + WatchInterval;
            RefreshWatch();
        }
        if (changed)
        {
            m_replayProbed = false;
            if (m_compatibilityDue < 0) m_compatibilityDue = now + 10;
        }
        // Log once the scene is complete (or after 10 s), not per object found.
        const bool complete = m_scene.Manager() && m_scene.Stats() && m_scene.Indicators() && m_scene.Player() && m_scene.GameState();
        if (m_compatibilityDue >= 0 && (complete || now >= m_compatibilityDue))
        {
            m_compatibilityDue = -1;
            LogCompatibility("world");
        }
        if (!m_replayProbed && m_scene.Player() && m_scene.GameState())
        {
            m_replayProbed = true;
            std::string detail;
            bool ok = m_sampler.Probe(detail);
            Log(std::string("replay probe ") + (ok ? "ok: " : "failed: ") + detail);
        }
        m_output.SetCapabilities(Capabilities());
    }

    void Observer::RefreshWatch()
    {
        struct Named
        {
            const wchar_t* path;
            Signal signal;
            bool counts;
        };
        static constexpr Named functions[] = {
            {STR("/Script/KovaaKFramework.ScenarioBroadcastReceiver:Send_ChallengeQueued"), Signal::Start, false},
            {STR("/Script/KovaaKFramework.ScenarioBroadcastReceiver:Send_Start"), Signal::Start, false},
            {STR("/Script/KovaaKFramework.ScenarioBroadcastReceiver:Send_PostStart"), Signal::Start, false},
            {STR("/Script/KovaaKFramework.ScenarioBroadcastReceiver:Send_ChallengeComplete"), Signal::Complete, false},
            {STR("/Script/KovaaKFramework.ScenarioBroadcastReceiver:Send_PostChallengeComplete"), Signal::Complete, false},
            {STR("/Script/KovaaKFramework.ScenarioBroadcastReceiver:Send_ChallengeCanceled"), Signal::Cancel, false},
            {STR("/Script/GameSkillsTrainer.ScenarioManager:BroadcastChallengeCompleted"), Signal::Complete, false},
            {STR("/Script/GameSkillsTrainer.PerformanceIndicatorsBroadcastReceiver:Send_ShotFired"), Signal::Start, true},
            {STR("/Script/GameSkillsTrainer.PerformanceIndicatorsBroadcastReceiver:Send_ShotHit"), Signal::Start, true},
            {STR("/Script/GameSkillsTrainer.PerformanceIndicatorsBroadcastReceiver:Send_Kill"), Signal::Start, true},
        };
        auto next = std::make_unique<WatchSet>();
        auto counter = [this](const std::string& name) -> std::uint16_t {
            for (std::size_t i = 0; i < m_watchCounts.size(); ++i)
                if (m_watchCounts[i].first == name) return static_cast<std::uint16_t>(i);
            m_watchCounts.emplace_back(name, 0);
            m_watchSeen.push_back(~0ULL);
            return static_cast<std::uint16_t>(m_watchCounts.size() - 1);
        };
        m_broadcastFunctions = std::size(functions);
        m_broadcastFound = 0;
        for (const Named& n : functions)
        {
            if (UFunction* f = game::FindFunction(n.path))
            {
                ++m_broadcastFound;
                std::string name = game::Narrow(n.path);
                name = name.substr(name.rfind('.') + 1);
                next->items.push_back({f, nullptr, n.signal, n.counts, counter(name)});
            }
        }
        // Blueprint handlers bound to the framework's lifecycle delegates are
        // dispatched through ProcessEvent even when Send_* is called natively.
        // The invocation lists are only read, never modified.
        std::size_t handlers = 0;
        std::vector<UObject*> receivers;
        UObjectGlobals::FindAllOf(STR("ScenarioBroadcastReceiver"), receivers);
        for (UObject* receiver : receivers)
        {
            if (!game::IsLiveInstance(receiver)) continue;
            for (FProperty* p : receiver->GetClassPrivate()->ForEachPropertyInChain())
            {
                auto* delegate = CastField<FMulticastInlineDelegateProperty>(p);
                if (!delegate) continue;
                UFunction* signature = delegate->GetSignatureFunction();
                if (!signature) continue;
                const std::string sig = game::Narrow(signature->GetName());
                std::optional<Signal> signal;
                if (sig.find("ChallengeComplete") != std::string::npos) signal = Signal::Complete;
                else if (sig.find("ChallengeCanceled") != std::string::npos) signal = Signal::Cancel;
                else if (sig.find("ChallengeQueued") != std::string::npos || sig.starts_with("Receive_Start") || sig.starts_with("Receive_PostStart"))
                    signal = Signal::Start;
                if (!signal) continue;
                const auto* list = reinterpret_cast<const FMulticastScriptDelegate*>(reinterpret_cast<const std::uint8_t*>(receiver) + p->GetOffset_Internal());
                for (const auto& binding : list->InvocationList)
                {
                    UObject* target = binding.GetUObject();
                    if (!target || handlers >= 64) continue;
                    UFunction* handler = target->GetFunctionByNameInChain(binding.GetFunctionName());
                    if (!handler) continue;
                    next->items.push_back({handler, target, *signal, false, counter(sig.substr(0, sig.find("__")))});
                    ++handlers;
                }
            }
        }
        m_delegateHandlers = handlers;
        const WatchSet* current = m_watch.load();
        bool same = current && current->items.size() == next->items.size();
        for (std::size_t i = 0; same && i < next->items.size(); ++i)
            same = current->items[i].function == next->items[i].function && current->items[i].object == next->items[i].object;
        if (same) return;
        m_watch.store(next.get(), std::memory_order_release);
        // Retired sets stay allocated: a concurrent reader may still hold one.
        m_watchSets.push_back(std::move(next));
    }

    void Observer::OnProcessEvent(UObject* context, UFunction* function)
    {
        if (m_shutdown.load(std::memory_order_relaxed)) return;
        const WatchSet* set = m_watch.load(std::memory_order_acquire);
        if (set)
        {
            for (const Watch& w : set->items)
            {
                if (w.function != function || (w.object && w.object != context)) continue;
                if (!OnGameThread()) break;
                auto& seen = m_watchSeen[w.counter];
                if (seen == m_tickIndex) break; // another handler of the same broadcast
                seen = m_tickIndex;
                ++m_watchCounts[w.counter].second;
                if (!w.counts) m_pendingSignals.push_back(w.signal);
                break;
            }
        }
        // Fallback scheduler when the engine tick hook is disabled.
        if (!m_engineTick.load(std::memory_order_relaxed) && m_mainThread != 0 && GetCurrentThreadId() == m_mainThread && !m_inTick)
        {
            const double now = Seconds();
            if (now - m_lastTick >= 0.004)
            {
                m_gameThread = m_mainThread;
                OnTick();
            }
        }
    }

    void Observer::OnTick()
    {
        if (m_inTick || m_shutdown.load(std::memory_order_relaxed)) return;
        m_inTick = true;
        const double now = Seconds();
        m_lastTick = now;
        ++m_tickIndex;
        if (now >= m_nextRebind) Rebind(now);
        if (!m_pendingSignals.empty())
        {
            std::vector<Signal> signals;
            signals.swap(m_pendingSignals);
            for (Signal s : signals) Handle(m_lifecycle.OnSignal(s, now, std::nullopt));
        }
        if (now >= m_nextPoll)
        {
            m_nextPoll = now + PollInterval;
            Poll(now);
        }
        if (m_sampler.recording())
        {
            // Every engine frame: its game time and the inputs it consumed.
            // State samples at 60 Hz while the challenge runs.
            const bool sample = m_running && !m_paused && now >= m_nextSample;
            if (sample) m_nextSample = now + SampleInterval * 0.9; // never slower than 60 Hz at 60+ fps
            if (!m_output.recordingEnabled()) m_sampler.Finish("recording-disabled", std::nullopt);
            else m_sampler.Tick(m_stats, sample);
        }
        m_inTick = false;
    }

    void Observer::UpdateMeasurements(bool running, double elapsed, double remaining, const Getter::ValueElseResult& score)
    {
        UObject* indicators = m_scene.Indicators();
        UObject* player = m_scene.Player();
        UObject* character = player ? m_b.myCharacter.Object(player) : nullptr;
        if (!m_b.indicatorScore.ok()) m_scoreStatus = "getter-unavailable";
        else if (!indicators) m_scoreStatus = "receiver-unavailable";
        else if (!score.called) m_scoreStatus = "getter-unavailable";
        else m_scoreStatus = score.hasValue ? "available" : "no-native-value";
        if (running)
        {
            // Never carry a previous indicator value through "Else".
            m_stats.score = score.hasValue && IsUsableNumber(score.value) ? std::optional<double>(score.value) : std::nullopt;
            m_stats.seconds = elapsed >= 0 ? std::optional<double>(elapsed) : std::nullopt;
            m_remaining = remaining >= 0 ? std::optional<double>(remaining) : std::nullopt;
        }
        auto indicator = [&](const Getter& g) -> std::optional<double> {
            if (!indicators || !g.ok()) return std::nullopt;
            auto r = g.ValueElse(indicators);
            return r.hasValue ? std::optional<double>(r.value) : std::nullopt;
        };
        auto shots = indicator(m_b.indicatorShots), hits = indicator(m_b.indicatorHits), kills = indicator(m_b.indicatorKills),
             damage = indicator(m_b.indicatorDamage);
        game::LocalCounters local = game::ReadLocalCounters(character);
        std::string sources;
        auto pick = [&](const char* key, std::optional<double>& target, const std::optional<double>& primary, const std::optional<double>& fallback) {
            const bool usePrimary = Finite(primary);
            const auto& value = usePrimary ? primary : fallback;
            if (running)
            {
                if (Finite(value) && *value >= 0) target = value;
            }
            else KeepMax(target, value);
            if (Finite(value))
            {
                sources += sources.empty() ? "" : ",";
                sources += std::string(key) + (usePrimary ? "=indicator" : "=local");
            }
        };
        pick("shots", m_stats.shots, shots, local.shots);
        pick("hits", m_stats.hits, hits, local.hits);
        std::optional<double> credited;
        if (!Finite(kills) && !Finite(local.kills))
            credited = static_cast<double>(m_killCredits.load(std::memory_order_relaxed) - m_attemptKillBase);
        pick("kills", m_stats.kills, kills, Finite(local.kills) ? local.kills : credited);
        pick("damage", m_stats.damage, damage, local.damage);
        if (Finite(local.lastTimeToKill) && *local.lastTimeToKill > 0 && Finite(m_stats.kills) && *m_stats.kills > 0 && m_stats.seconds &&
            *local.lastTimeToKill <= *m_stats.seconds)
            m_lastTimeToKill = local.lastTimeToKill;
        if (m_scoreStatus == "available") sources = "score=indicator" + std::string(sources.empty() ? "" : ",") + sources;
        m_sources = std::move(sources);
    }

    void Observer::Poll(double now)
    {
        ++m_polls;
        PollSample s;
        s.now = now;
        UObject* manager = m_scene.Manager();
        UObject* player = m_scene.Player();
        UObject* scenario = nullptr;
        if (manager && m_b.lifecycleReady())
        {
            auto inChallenge = m_b.isInChallenge.Bool(manager);
            auto elapsed = m_b.timeElapsed.Number(manager);
            scenario = m_b.currentScenario.Object(manager);
            if (inChallenge && Finite(elapsed))
            {
                s.available = true;
                s.elapsed = *elapsed;
                auto remaining = m_b.timeRemaining.Number(manager);
                s.remaining = Finite(remaining) ? *remaining : -1;
                bool running = *inChallenge && scenario && *elapsed >= 0;
                if (running && m_b.isScenarioLoading.ok()) running = !m_b.isScenarioLoading.Bool(manager).value_or(true);
                if (running && m_b.queueRemaining.ok()) running = m_b.queueRemaining.Number(manager).value_or(1) <= 0;
                if (running && m_b.scenarioActive.ok()) running = m_b.scenarioActive.Bool(scenario).value_or(false);
                if (running && m_b.scenarioInChallenge.ok()) running = m_b.scenarioInChallenge.Bool(scenario).value_or(false);
                s.running = running;
                const auto key = reinterpret_cast<std::uint64_t>(scenario);
                if (key != m_scenarioKey)
                {
                    m_scenarioKey = key;
                    m_scenarioName.clear();
                    if (scenario && !m_b.scenarioName.String(scenario, m_scenarioName)) m_scenarioName.clear();
                    if (m_scenarioName.size() > 512) m_scenarioName.clear();
                }
                s.scenarioKey = key;
                s.scenario = m_scenarioName;
                if (m_scenarioName.empty()) s.running = false; // an attempt needs a scenario identity
            }
        }
        if (player && m_b.gamePaused.ok()) s.paused = m_b.gamePaused.Bool(m_b.statics, player).value_or(false);
        if (UObject* stats = m_scene.Stats())
        {
            auto last = m_b.lastScore.Number(stats, player);
            if (Finite(last)) s.lastScore = last;
            auto remaining = m_b.lastTimeRemaining.Number(stats, player);
            if (Finite(remaining)) s.lastTimeRemaining = remaining;
        }
        Getter::ValueElseResult score;
        if (UObject* indicators = m_scene.Indicators(); indicators && m_b.indicatorScore.ok())
        {
            score = m_b.indicatorScore.ValueElse(indicators);
            if (score.hasValue && IsUsableNumber(score.value)) s.indicatorScore = score.value;
        }
        m_running = s.available && s.running;
        m_paused = s.paused;
        // Values are read every poll while an attempt is open, and before the
        // lifecycle step: a completion in this poll must journal the final
        // counters, not the previous poll's (shots keep landing until the end).
        const bool before = m_lifecycle.active();
        if (before) UpdateMeasurements(m_running, s.elapsed, s.remaining, score);
        // The game's stats CSV is the completion record and the final values.
        if (m_statsWatch)
            if (auto stats = m_output.TakeGameStats())
            {
                m_gameStats = std::move(stats);
                Handle(m_lifecycle.OnSignal(Signal::Complete, now, m_gameStats->score));
            }
        Handle(m_lifecycle.Poll(s));
        if (m_lifecycle.state() == Lifecycle::State::Ending && !m_statsWatch)
        {
            m_statsWatch = true;
            m_output.WatchGameStats(m_lifecycle.attemptScenario(), m_attemptUnixMs, m_attemptLocalStart);
        }
        if (!before && m_lifecycle.active()) UpdateMeasurements(m_running, s.elapsed, s.remaining, score);
        if (m_polls % 2 == 0) PublishLive(s, m_running);
        if (m_polls % 5 == 0) PublishScene(s, manager);
    }

    // What the game shows now, for the service's replay start gate: the
    // scenario and map a replay must match, and whether a challenge runs.
    void Observer::PublishScene(const PollSample& s, UObject* manager)
    {
        UObject* state = m_scene.GameState();
        if (state != m_mapState || s.scenarioKey != m_mapScenarioKey)
        {
            m_mapState = state;
            m_mapScenarioKey = s.scenarioKey;
            m_mapName.clear();
            m_mapScale.reset();
            if (state && m_b.mapName.String(state, m_mapName)) m_mapScale = m_b.mapScale.Number(state);
        }
        const bool inChallenge = manager && m_b.isInChallenge.Bool(manager).value_or(false);
        const bool loading = manager && m_b.isScenarioLoading.ok() && m_b.isScenarioLoading.Bool(manager).value_or(false);
        std::string body = "{\"version\":1,\"available\":";
        body += s.available ? "true" : "false";
        body += ",\"scenario\":";
        AppendJsonString(body, m_scenarioName);
        body += ",\"mapName\":";
        AppendJsonString(body, m_mapName);
        if (m_mapScale && IsUsableNumber(*m_mapScale))
        {
            body += ",\"mapScale\":";
            AppendNumber(body, *m_mapScale, 9);
        }
        body += std::string(",\"inChallenge\":") + (inChallenge ? "true" : "false") + ",\"running\":" + (s.running ? "true" : "false") +
                ",\"loading\":" + (loading ? "true" : "false") + ",\"paused\":" + (s.paused ? "true" : "false") + "}";
        m_output.PublishScene(std::move(body));
    }

    void Observer::PublishLive(const PollSample& s, bool running)
    {
        LiveSnapshot live;
        if (m_output.playbackActive() || !m_lifecycle.active())
        {
            m_output.PublishLive(FormatLiveOverlay(live));
            return;
        }
        live.id = m_lifecycle.attemptId();
        live.scenario = m_lifecycle.attemptScenario();
        live.paused = s.paused;
        if (!s.available)
        {
            // Transient read failure: the HUD keeps an already visible run.
            live.active = !s.paused;
            live.transient = live.active;
        }
        else if (running)
        {
            live.active = true;
            live.scoreStatus = m_scoreStatus;
            live.score = m_stats.score;
            live.seconds = m_stats.seconds;
            live.shots = m_stats.shots;
            live.hits = m_stats.hits;
            live.kills = m_stats.kills;
            live.damage = m_stats.damage;
            live.remainingSeconds = m_remaining;
            live.lastTimeToKillSeconds = m_lastTimeToKill;
        }
        m_output.PublishLive(FormatLiveOverlay(live));
    }

    void Observer::Handle(const std::vector<LifecycleEvent>& events)
    {
        for (const LifecycleEvent& e : events)
        {
            switch (e.kind)
            {
            case LifecycleEvent::Kind::Started:
            {
                ++m_attempts;
                m_stats = {};
                m_gameStats.reset();
                if (m_statsWatch) m_output.StopGameStats();
                m_statsWatch = false;
                m_attemptUnixMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
                {
                    SYSTEMTIME local;
                    GetLocalTime(&local);
                    // The attempt is confirmed one poll after the timer starts.
                    m_attemptLocalStart = local.wHour * 3600.0 + local.wMinute * 60.0 + local.wSecond + local.wMilliseconds / 1000.0;
                }
                m_remaining.reset();
                m_lastTimeToKill.reset();
                m_attemptKillBase = m_killCredits.load(std::memory_order_relaxed);
                m_nextSample = 0;
                std::string replay = "off";
                if (m_output.recordingEnabled() && !m_output.playbackActive())
                    replay = m_sampler.Begin(e.id, e.scenario, e.startEvent) ? "recording" : m_sampler.stopReason();
                Log("attempt started id=" + e.id + " source=" + e.startEvent + " replay=" + replay);
                break;
            }
            case LifecycleEvent::Kind::Completed:
            {
                ++m_completed;
                if (m_statsWatch) m_output.StopGameStats();
                m_statsWatch = false;
                // Final values from the game's completion record when present.
                if (m_gameStats)
                {
                    m_stats.hits = m_gameStats->hits;
                    m_stats.shots = m_gameStats->shots();
                    m_stats.kills = m_gameStats->kills;
                    m_stats.damage = m_gameStats->damage;
                    m_sources = "game-stats";
                }
                m_gameStats.reset();
                const auto& st = m_stats;
                if (m_sampler.recording()) m_sampler.Finish("completed", e.score);
                if (!e.score)
                {
                    Warn("completed run not saved: final score unavailable (id=" + e.id + ")");
                    break;
                }
                JournalRun run;
                run.id = e.id;
                run.scenario = e.scenario;
                run.score = *e.score;
                if (Finite(st.shots) && *st.shots > 0 && Finite(st.hits) && *st.hits >= 0 && *st.hits <= *st.shots)
                    run.accuracy = *st.hits / *st.shots * 100.0;
                if (e.duration > 0) run.duration = e.duration;
                run.kills = st.kills;
                run.damage = st.damage;
                run.completedAt = std::time(nullptr);
                m_output.AppendJournal(FormatJournalLine(run));
                ++m_journalled;
                Log("completed run saved id=" + e.id + " score=" + FormatNumber(*e.score, 0) + " source=" + e.scoreSource +
                    " duration=" + FormatNumber(e.duration, 6) + " shots=" + (st.shots ? FormatNumber(*st.shots, 0) : "-") +
                    " hits=" + (st.hits ? FormatNumber(*st.hits, 0) : "-") + " kills=" + (st.kills ? FormatNumber(*st.kills, 0) : "-") +
                    " values=" + (m_sources.empty() ? "none" : m_sources) + " replayFrames=" + std::to_string(m_sampler.frames()));
                break;
            }
            case LifecycleEvent::Kind::Canceled:
                if (m_statsWatch) m_output.StopGameStats();
                m_statsWatch = false;
                m_gameStats.reset();
                if (m_sampler.recording()) m_sampler.Finish("interrupted", std::nullopt);
                Log("attempt ended without completion id=" + e.id + " reason=" + e.reason);
                break;
            }
        }
        if (!events.empty())
        {
            std::string counts;
            for (const auto& [name, count] : m_watchCounts)
                if (count) counts += (counts.empty() ? "" : " ") + name + "=" + std::to_string(count);
            Log("  observed: killCredit=" + std::to_string(m_killCredits.load()) + " inputs=" + std::to_string(m_inputEvents.load()) +
                " shotHit=" + std::to_string(m_shotHits.load()) + (counts.empty() ? "" : " " + counts));
        }
    }
} // namespace aimmod
