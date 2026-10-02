#include "MatchPlay.hpp"

#include "Log.hpp"
#include "Output.hpp"
#include "Reflect.hpp"
#include "World.hpp"

#include <aimmod/Formats.hpp>
#include <aimmod/CsGear.hpp>
#include <aimmod/GameCommand.hpp>

#include <Unreal/CoreUObject/UObject/UnrealType.hpp>
#include <Unreal/FProperty.hpp>
#include <Unreal/NameTypes.hpp>
#include <Unreal/Property/FBoolProperty.hpp>
#include <Unreal/Property/FStructProperty.hpp>
#include <Unreal/Property/FStrProperty.hpp>
#include <Unreal/UObjectGlobals.hpp>
#include <Unreal/UClass.hpp>
#include <Unreal/UObject.hpp>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <fstream>

#include <Windows.h>

namespace aimmod
{
    using namespace game;
    using RC::Unreal::FName;
    using RC::Unreal::FWeakObjectPtr;
    namespace UObjectGlobals = RC::Unreal::UObjectGlobals;
    using RC::Unreal::FNAME_Add;

    namespace
    {
        constexpr int MaxShotsPerFrame = 8;
        constexpr double HookHitSeconds = 0.25; // a Send_ShotHit target waits this long for its shot

        std::int64_t UnixMs()
        {
            return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
        }
        void WriteFloat(std::uint8_t* value, double v)
        {
            const float f = static_cast<float>(v);
            std::memcpy(value, &f, 4);
        }
        void WriteBool(std::uint8_t* value, const Param& p, bool v)
        {
            if (p.boolProperty) p.boolProperty->SetPropertyValue(value, v);
        }
        constexpr double DegToRad = 3.14159265358979323846 / 180.0;
    } // namespace

    void MatchPlay::Tick(double now, const std::string& scenario, bool inChallenge, bool loading, const PoseId& poseId,
                         const std::unordered_map<std::uint32_t, std::string>& poseNames)
    {
        TickShots(now, poseId, poseNames);
        TickPlayState(now, scenario, inChallenge, loading);
        TickRound(now, scenario, inChallenge, loading);
    }

    // ------------------------------------------------------------ restart lock

    namespace
    {
        constexpr const char* RestartAction = "ResetSession";            // KovaaK's restart (F3 / middle mouse by default)
        constexpr const wchar_t* RestartLocked = STR("AimModRestartOff"); // its name while a match runs
    } // namespace

    bool MatchPlay::BindLock()
    {
        if (m_lockBound) return !m_lockDisabled;
        m_lockBound = true;
        m_inputSettings = UObjectGlobals::StaticFindObject<UObject*>(nullptr, nullptr, STR("/Script/Engine.Default__InputSettings"));
        m_actionMappings.Bind(FindClass(STR("/Script/Engine.InputSettings")), "ActionMappings");
        if (m_actionMappings.ok() && m_actionMappings.elementStruct())
        {
            m_actionName.Bind(m_actionMappings.elementStruct(), "ActionName");
            m_actionKey.Bind(m_actionMappings.elementStruct(), "Key");
        }
        m_rebuildKeymaps.BindPath(STR("/Script/Engine.InputSettings:ForceRebuildKeymaps"), Shape::Command);
        m_saveKeyMappings.BindPath(STR("/Script/Engine.InputSettings:SaveKeyMappings"), Shape::Command);
        m_keyJustPressed.BindPath(STR("/Script/Engine.PlayerController:WasInputKeyJustPressed"), Shape::Command);
        m_setVisibility.BindPath(STR("/Script/UMG.Widget:SetVisibility"), Shape::Command);
        m_getVisibility.BindPath(STR("/Script/UMG.Widget:GetVisibility"), Shape::Number);
        m_resetButton.Bind(FindClass(STR("/Script/GameSkillsTrainer.PauseBoxWidget")), STR("ResetChallengeButton"));
        if (!m_inputSettings || !m_actionName.ok() || m_actionName.kind() != Kind::Name || !m_rebuildKeymaps.ok())
        {
            m_lockDisabled = true;
            Log("match play: restart lock disabled (input settings bindings missing)");
            return false;
        }
        // A lock left behind (the game closed mid-match and the binds were saved since): undo it.
        if (RenameActions(Narrow(RestartLocked), STR("ResetSession"), false) > 0)
        {
            m_rebuildKeymaps.Call(m_inputSettings, [](std::uint8_t*, const Param&) {});
            if (m_saveKeyMappings.ok()) m_saveKeyMappings.Call(m_inputSettings, [](std::uint8_t*, const Param&) {});
            Log("match play: restored a restart bind a previous match had switched off");
        }
        Log(std::string("match play: restart lock ready") + (m_resetButton.ok() && m_setVisibility.ok() ? "" : " (pause menu button unsupported)"));
        return true;
    }

    int MatchPlay::RenameActions(const std::string& from, const wchar_t* to, bool keepKeys)
    {
        std::vector<const std::uint8_t*> elements;
        if (!m_actionMappings.Elements(m_inputSettings, elements, 512)) return 0;
        int renamed = 0;
        for (const std::uint8_t* element : elements)
        {
            std::string name;
            if (!m_actionName.Name(element, name) || name != from) continue;
            *reinterpret_cast<FName*>(const_cast<std::uint8_t*>(m_actionName.At(element))) = FName(to, FNAME_Add);
            if (keepKeys && m_actionKey.ok() && m_actionKey.size() > 0 && m_actionKey.size() <= 64)
            {
                const std::uint8_t* key = m_actionKey.At(element);
                m_lockedKeys.emplace_back(key, key + m_actionKey.size());
            }
            ++renamed;
        }
        return renamed;
    }

    void MatchPlay::ShowRestartButtons(bool hidden)
    {
        if (!m_resetButton.ok() || !m_setVisibility.ok()) return;
        auto set = [&](UObject* button, std::uint8_t visibility) {
            m_setVisibility.Call(button, [&](std::uint8_t* value, const Param& p) { if (p.kind == Kind::UInt8) *value = visibility; });
        };
        if (!hidden)
        {
            for (auto& [weak, before] : m_hiddenButtons)
                if (UObject* button = weak.Get(); button && IsLiveInstance(button)) set(button, before);
            m_hiddenButtons.clear();
            return;
        }
        std::vector<UObject*> boxes;
        UObjectGlobals::FindAllOf(STR("PauseBox_C"), boxes);
        for (UObject* box : boxes)
        {
            if (!IsLiveInstance(box)) continue;
            UObject* button = m_resetButton.Object(box);
            if (!button || !IsLiveInstance(button)) continue;
            const auto known = std::find_if(m_hiddenButtons.begin(), m_hiddenButtons.end(), [&](auto& entry) { return entry.first.Get() == button; });
            const double shown = m_getVisibility.ok() ? m_getVisibility.Number(button).value_or(0) : 0;
            if (known == m_hiddenButtons.end()) m_hiddenButtons.emplace_back(FWeakObjectPtr(button), static_cast<std::uint8_t>(std::clamp(shown, 0.0, 4.0)));
            else if (shown == 1) continue;
            set(button, 1); // Collapsed
        }
    }

    void MatchPlay::TickRestartLock(double now, bool wanted)
    {
        if (wanted == m_locked)
        {
            if (!m_locked) return;
            // The pause menu is rebuilt with each opening: collapse its restart button again.
            if (now >= m_nextButtonScan)
            {
                m_nextButtonScan = now + 0.25;
                ShowRestartButtons(true);
            }
            // A press of a switched-off restart key: tell the service, which says why nothing happened.
            UObject* player = m_scene.Player();
            if (player && m_keyJustPressed.ok())
                for (const auto& key : m_lockedKeys)
                {
                    bool pressed = false;
                    m_keyJustPressed.Call(
                        player,
                        [&](std::uint8_t* value, const Param& p) {
                            if (p.kind == Kind::Other && p.structType && p.size == static_cast<std::int32_t>(key.size())) std::memcpy(value, key.data(), key.size());
                        },
                        [&](const std::uint8_t* buffer, const std::vector<Param>& params) {
                            for (const Param& p : params)
                                if (p.ret && p.boolProperty) pressed = p.boolProperty->GetPropertyValue(const_cast<std::uint8_t*>(buffer) + p.offset);
                        });
                    if (!pressed) continue;
                    ++m_blockedPresses;
                    const auto file = m_output.root() / L"match-lock.tsv", temp = m_output.root() / L"match-lock.tsv.tmp";
                    {
                        std::ofstream out(temp, std::ios::binary | std::ios::trunc);
                        out << "AIMMOD_LOCK_1\t" << m_blockedPresses << "\t" << UnixMs() << "\n";
                    }
                    MoveFileExW(temp.c_str(), file.c_str(), MOVEFILE_REPLACE_EXISTING);
                    break;
                }
            return;
        }
        if (!BindLock()) return;
        if (wanted)
        {
            m_lockedKeys.clear();
            const int count = RenameActions(RestartAction, RestartLocked, true);
            m_rebuildKeymaps.Call(m_inputSettings, [](std::uint8_t*, const Param&) {});
            m_locked = true;
            m_nextButtonScan = 0;
            Log("match play: restart off for the match (" + std::to_string(count) + " bind" + (count == 1 ? "" : "s") + ")");
        }
        else
        {
            RenameActions(Narrow(RestartLocked), STR("ResetSession"), false);
            m_rebuildKeymaps.Call(m_inputSettings, [](std::uint8_t*, const Param&) {});
            ShowRestartButtons(false);
            m_lockedKeys.clear();
            m_locked = false;
            Log("match play: restart back on");
        }
    }

    // Frozen: jumping off (JumpMaxCount 0) and the movement component stopped (MOVE_None), so
    // nobody moves or jumps before go-live; looking stays. Restored exactly at unfreeze.
    void MatchPlay::FreezeBody(UObject* character, bool frozen)
    {
        if (!m_freezeBound)
        {
            m_freezeBound = true;
            m_characterMovement.Bind(FindClass(STR("/Script/Engine.Character")), STR("CharacterMovement"));
            m_setMovementMode.BindPath(STR("/Script/Engine.CharacterMovementComponent:SetMovementMode"), Shape::Command);
        }
        if (!frozen && !m_frozenBody.Get() && !m_jumpBefore && !m_movementOff) return;
        auto mode = [&](UObject* body, std::uint8_t movementMode) {
            if (UObject* movement = m_characterMovement.ok() ? m_characterMovement.Object(body) : nullptr)
                m_setMovementMode.Call(movement, [&](std::uint8_t* value, const Param& p) {
                    if (p.kind == Kind::UInt8) *value = p.name == "NewMovementMode" ? movementMode : 0;
                });
        };
        UObject* previous = m_frozenBody.Get();
        if (previous && previous != character && IsLiveInstance(previous)) FreezeBody(previous, false); // a respawn: the old body first
        if (frozen && character)
        {
            if (previous != character)
            {
                auto* jumps = character->GetValuePtrByPropertyNameInChain<std::int32_t>(STR("JumpMaxCount"));
                m_jumpBefore = jumps ? std::optional<std::int32_t>(*jumps) : std::nullopt;
                if (jumps) *jumps = 0;
                m_frozenBody = FWeakObjectPtr(character);
                m_movementOff = false;
            }
            // Movement stops once the character stands at its spawn (no teleport pending).
            if (!m_movementOff && !m_pendingSpawn)
            {
                mode(character, 0); // MOVE_None
                m_movementOff = true;
            }
            return;
        }
        UObject* body = m_frozenBody.Get();
        if (body && IsLiveInstance(body))
        {
            if (m_jumpBefore)
                if (auto* jumps = body->GetValuePtrByPropertyNameInChain<std::int32_t>(STR("JumpMaxCount"))) *jumps = *m_jumpBefore;
            if (m_movementOff) mode(body, 1); // MOVE_Walking; the component falls if there is no floor
        }
        m_frozenBody = FWeakObjectPtr();
        m_jumpBefore.reset();
        m_movementOff = false;
    }

    // ---------------------------------------------------------------- shots

    namespace
    {
        // The shot's target: a drawn capsule, the point where the ray meets (or passes nearest) it.
        void Aim(ShotRecord& r, const double direction[3], const Capsule& c, double along, double z, int source)
        {
            r.target = c.id;
            std::copy(c.center, c.center + 3, r.targetCenter);
            r.targetRadius = c.radius;
            r.targetHalfHeight = c.halfHeight;
            const double point[3] = {r.origin[0] + direction[0] * along, r.origin[1] + direction[1] * along, z};
            r.headshot = IsHeadHit(point, c.center, c.halfHeight);
            r.source = source;
        }
    } // namespace

    void MatchPlay::OnShotHit(UObject*, UObject* target, double damage)
    {
        ++m_shotStats.hookCalls;
        if (!m_shotsWanted || !target) return;
        UObject* player = m_scene.Player();
        UObject* character = player ? m_b.myCharacter.Object(player) : nullptr;
        if (!character || target == character) return; // the local player being hit is the host's business
        if (m_hookHits.size() >= 16) m_hookHits.erase(m_hookHits.begin());
        m_hookHits.push_back({RC::Unreal::FWeakObjectPtr(target), damage, m_lastShotsTick});
    }

    void MatchPlay::DrawnTargets(UObject* character, const PoseId& poseId, std::vector<Capsule>& capsules, std::vector<UObject*>& actors)
    {
        capsules.clear();
        actors.clear();
        std::vector<UObject*> all;
        UObject* state = m_scene.GameState();
        if (!state || !m_b.characters.Objects(state, all, 33)) return;
        for (UObject* actor : all)
        {
            if (!actor || actor == character) continue;
            if (auto hidden = m_b.hidden.Bool(actor); hidden && *hidden) continue;
            UObject* capsule = m_b.capsule.Object(actor);
            auto radius = capsule ? m_b.capsuleRadius.Number(capsule) : std::nullopt;
            auto half = capsule ? m_b.capsuleHalfHeight.Number(capsule) : std::nullopt;
            Capsule c;
            if (!radius || !half || *radius <= 0 || *half < *radius || !m_b.actorLocation.Vector(actor, c.center)) continue;
            c.radius = *radius;
            c.halfHeight = *half;
            c.id = poseId(actor);
            capsules.push_back(c);
            actors.push_back(actor);
        }
    }

    void MatchPlay::LogShotStats(const char* why)
    {
        const ShotStats& s = m_shotStats;
        Log("match play: shots (" + std::string(why) + "): " + std::to_string(s.shots) + " fired, " + std::to_string(s.gameHits) + " game hits (" +
            std::to_string(s.onCapsule) + " on the drawn capsule, " + std::to_string(s.nearCapsule) + " just beside it, " + std::to_string(s.named) +
            " named by the game, " + std::to_string(s.gameHitNoTarget) + " without a target), " + std::to_string(s.rayOnly) +
            " ray hits the game counted as misses, Send_ShotHit calls " + std::to_string(s.hookCalls) + "; last shot #" + std::to_string(m_shotSequence) +
            ", acknowledged #" + std::to_string(m_shots.acked()) + (m_shots.ackSeen() ? "" : " (no acknowledgement yet)") + ", lost " +
            std::to_string(m_shots.lost()));
        m_shotStatsLogged = s;
    }

    void MatchPlay::TickShots(double now, const PoseId& poseId, const std::unordered_map<std::uint32_t, std::string>& poseNames)
    {
        // Shots counted together in one frame are spread over it (see below).
        const double frameSeconds = m_lastShotsTick < 0 ? 0 : std::clamp(now - m_lastShotsTick, 0.0, 0.25);
        m_lastShotsTick = now;
        if (!m_output.shotsRequested())
        {
            if (m_shotsWanted) LogShotStats("stream stopped, no request");
            m_shotsWanted = false;
            m_weapons.clear();
            m_shots.Clear();
            m_hookHits.clear();
            return;
        }
        if (!m_shotsWanted)
        {
            m_shotsWanted = true;
            m_session = UnixMs();
            m_shots.Clear();
            m_shotStats = m_shotStatsLogged = {};
            m_nextShotLog = now + 10;
            Log("match play: shot stream requested (session " + std::to_string(m_session) + ")");
        }
        // The service's acknowledgement (self-shots.request): what it took leaves the log.
        if (const auto ack = m_output.shotsAck(); ack && ack->session == m_session) m_shots.Ack(ack->sequence);
        std::erase_if(m_hookHits, [&](const HookHit& h) { return h.at < now - HookHitSeconds || !h.target.Get(); });
        if (now >= m_nextShotLog)
        {
            m_nextShotLog = now + 10;
            if (m_shotStats.shots != m_shotStatsLogged.shots) LogShotStats("so far");
        }
        UObject* player = m_scene.Player();
        UObject* character = player ? m_b.myCharacter.Object(player) : nullptr;
        // CS: the knife's right-mouse stab has no weapon of its own; it is claimed as slot 4 on the camera ray.
        if (m_gear.TakeStab() && character)
        {
            ShotRecord r;
            if (AimRay(player, character, poseId, r))
            {
                r.unixMs = UnixMs();
                r.sequence = ++m_shotSequence;
                r.slot = cs::StabSlot;
                m_shots.Add(r);
                ++m_shotStats.shots;
                PublishShots(poseNames);
            }
        }
        std::vector<WeaponCount> counts;
        if (!character || !ReadWeaponCounters(character, counts))
        {
            m_weapons.clear();
            return;
        }
        // A new character or weapon set only sets the baseline.
        bool same = character == m_shotsCharacter && counts.size() == m_weapons.size();
        for (std::size_t i = 0; same && i < counts.size(); ++i)
            same = counts[i].weapon == m_weapons[i].weapon && counts[i].shots >= m_weapons[i].shots && counts[i].hits >= m_weapons[i].hits;
        if (!same)
        {
            m_shotsCharacter = character;
            m_weapons.clear();
            for (const WeaponCount& c : counts) m_weapons.push_back({c.weapon, c.shots, c.hits, c.damage});
            return;
        }
        int fired = 0;
        for (std::size_t i = 0; i < counts.size(); ++i) fired += static_cast<int>(std::min(counts[i].shots - m_weapons[i].shots, static_cast<double>(MaxShotsPerFrame)));
        if (fired == 0)
        {
            for (std::size_t i = 0; i < counts.size(); ++i) m_weapons[i].damage = counts[i].damage;
            return;
        }

        UObject* camera = m_b.cameraManager.Object(player);
        double origin[3], rotation[3];
        const bool view = camera && m_b.cameraLocation.Vector(camera, origin) && m_b.cameraRotation.Vector(camera, rotation);
        if (view)
        {
            const double pitch = rotation[0] * DegToRad, yaw = rotation[1] * DegToRad;
            const double direction[3] = {std::cos(pitch) * std::cos(yaw), std::cos(pitch) * std::sin(yaw), std::sin(pitch)};
            // What the shooter sees: every drawn character capsule (no world occlusion test; the game's
            // own hit counter says whether the shot landed).
            std::vector<Capsule> capsules;
            std::vector<UObject*> actors;
            DrawnTargets(character, poseId, capsules, actors);
            const std::int64_t ms = UnixMs();
            int index = 0;
            for (std::size_t i = 0; i < counts.size(); ++i)
            {
                const int shots = static_cast<int>(std::min(counts[i].shots - m_weapons[i].shots, static_cast<double>(MaxShotsPerFrame)));
                int hits = static_cast<int>(std::min(counts[i].hits - m_weapons[i].hits, static_cast<double>(shots)));
                // The game's damage per hit this frame (its headshot multiplier shows in it).
                const double perHit = hits > 0 && counts[i].damage >= 0 && m_weapons[i].damage >= 0 && counts[i].damage >= m_weapons[i].damage
                                          ? (counts[i].damage - m_weapons[i].damage) / hits
                                          : -1;
                for (int k = 0; k < shots; ++k, ++index)
                {
                    ShotRecord r;
                    // Several shots counted in one frame (a hitch, or a weapon faster than the frame
                    // rate) are spread back over that frame, so the host sees them apart.
                    r.unixMs = ms - static_cast<std::int64_t>(std::llround(frameSeconds * 1000.0 * (fired - 1 - index) / fired));
                    r.sequence = ++m_shotSequence;
                    std::copy(origin, origin + 3, r.origin);
                    std::copy(direction, direction + 3, r.direction);
                    r.slot = counts[i].slot;
                    r.gameHit = hits-- > 0;
                    r.gameDamage = r.gameHit ? perHit : -1;
                    // CS: the bullet's own ray (the camera ray turned by its seeded spread). The record keeps
                    // the camera ray; the host turns it the same way. When the game's trace didn't follow the
                    // spread, its hit counter speaks for the crosshair, not the bullet: only this ray counts.
                    double bullet[3];
                    const bool spread = m_feel.Bullet(r.slot, r, bullet);
                    const bool ownRay = spread && !r.spreadApplied;
                    if (ownRay) r.gameHit = false, r.gameDamage = -1;
                    const auto onRay = PickTarget(origin, bullet, capsules, 0);
                    const auto nearRay = onRay ? onRay : PickTarget(origin, bullet, capsules, GameHitToleranceCm);
                    ++m_shotStats.shots;
                    if (r.gameHit) ++m_shotStats.gameHits;
                    // The target: the actor the game's hit named, else the capsule the ray meets, else
                    // (a game hit only) the capsule it passes nearest.
                    bool aimed = false;
                    if (r.gameHit && !m_hookHits.empty())
                    {
                        UObject* named = m_hookHits.front().target.Get();
                        m_hookHits.erase(m_hookHits.begin());
                        for (std::size_t j = 0; j < actors.size(); ++j)
                        {
                            if (actors[j] != named) continue;
                            const auto pass = RayCapsulePass(origin, bullet, capsules[j].center, capsules[j].radius, capsules[j].halfHeight);
                            Aim(r, bullet, capsules[j], pass.along, pass.z, SourceGame);
                            aimed = true;
                            ++m_shotStats.named;
                            break;
                        }
                    }
                    else if (ownRay && !m_hookHits.empty()) m_hookHits.erase(m_hookHits.begin()); // the crosshair's hit, not this bullet's
                    if (!aimed && onRay)
                    {
                        const Capsule& c = capsules[onRay->index];
                        const double point[3] = {origin[0] + bullet[0] * onRay->along, origin[1] + bullet[1] * onRay->along, origin[2] + bullet[2] * onRay->along};
                        // Only this ray decides: the map must not stand in its way.
                        if (!ownRay || !m_feel.WorldBetween(character, origin, point))
                        {
                            Aim(r, bullet, c, onRay->along, point[2], SourceRay);
                            ++(r.gameHit || ownRay ? m_shotStats.onCapsule : m_shotStats.rayOnly);
                        }
                    }
                    else if (!aimed && r.gameHit && nearRay)
                    {
                        const Capsule& c = capsules[nearRay->index];
                        const auto pass = RayCapsulePass(origin, bullet, c.center, c.radius, c.halfHeight);
                        Aim(r, bullet, c, pass.along, pass.z, SourceNear);
                        ++m_shotStats.nearCapsule;
                    }
                    else if (!aimed && r.gameHit)
                    {
                        ++m_shotStats.gameHitNoTarget;
                        if (now >= m_nextNoTargetLog)
                        {
                            m_nextNoTargetLog = now + 1;
                            const auto nearest = PickTarget(origin, bullet, capsules, 1e9);
                            Log("match play: shot #" + std::to_string(r.sequence) + " (slot " + std::to_string(r.slot) + ") is a game hit but no drawn target is near the ray (" +
                                (nearest ? "nearest capsule " + std::to_string(static_cast<int>(nearest->gap)) + " cm off" : std::to_string(capsules.size()) + " drawn") +
                                "); not claimed");
                        }
                    }
                    m_shots.Add(r);
                    m_feel.Fired(now, r.slot);
                }
            }
        }
        for (std::size_t i = 0; i < counts.size(); ++i) m_weapons[i] = {counts[i].weapon, counts[i].shots, counts[i].hits, counts[i].damage};
        if (!view) return;
        PublishShots(poseNames);
    }

    bool MatchPlay::AimRay(UObject* player, UObject* character, const PoseId& poseId, ShotRecord& r)
    {
        UObject* camera = m_b.cameraManager.Object(player);
        double rotation[3];
        if (!camera || !m_b.cameraLocation.Vector(camera, r.origin) || !m_b.cameraRotation.Vector(camera, rotation)) return false;
        const double pitch = rotation[0] * DegToRad, yaw = rotation[1] * DegToRad;
        r.direction[0] = std::cos(pitch) * std::cos(yaw), r.direction[1] = std::cos(pitch) * std::sin(yaw), r.direction[2] = std::sin(pitch);
        std::vector<Capsule> capsules;
        std::vector<UObject*> actors;
        DrawnTargets(character, poseId, capsules, actors);
        if (const auto pick = PickTarget(r.origin, r.direction, capsules, 0))
            Aim(r, r.direction, capsules[pick->index], pick->along, r.origin[2] + r.direction[2] * pick->along, SourceRay);
        return true;
    }

    void MatchPlay::PublishShots(const std::unordered_map<std::uint32_t, std::string>& poseNames)
    {
        m_shots.Prune(UnixMs());
        std::string body = "AIMMOD_SHOTS_1\t" + std::to_string(++m_shotsPublish) + "\t" + std::to_string(m_session) + "\n";
        std::string tags;
        const auto avatars = m_output.avatars();
        std::vector<std::uint32_t> tagged;
        for (const ShotRecord& r : m_shots.shots())
        {
            body += FormatShot(r);
            if (!r.target || avatars->empty() || std::find(tagged.begin(), tagged.end(), r.target) != tagged.end()) continue;
            tagged.push_back(r.target);
            if (auto name = poseNames.find(r.target); name != poseNames.end())
                if (auto tag = avatars->find(name->second); tag != avatars->end()) tags += "tag\t" + std::to_string(r.target) + "\t" + tag->second + "\n";
        }
        m_output.PublishSelfShots(std::move(body) + tags);
    }

    // ----------------------------------------------------------- play state

    bool MatchPlay::BindCharacter(UObject* character)
    {
        UClass* cls = character->GetClassPrivate();
        if (cls == m_class) return !m_disabled;
        m_class = cls;
        m_handleDamage.Reset();
        m_setHealth.Reset();
        m_respawn.Reset();
        m_killed.Reset();
        m_overrideInvulnerable.Reset();
        m_resetInvulnerable.Reset();
        m_currentHealth.Reset();
        m_respawnTimer.Reset();
        m_handleDamage.BindName(cls, STR("HandleDamage"), Shape::Command);
        m_setHealth.BindName(cls, STR("SetHealth"), Shape::Command);
        m_respawn.BindName(cls, STR("Respawn"), Shape::Command);
        m_killed.BindName(cls, STR("OnCharacterKilled"), Shape::Command);
        m_overrideInvulnerable.BindName(cls, STR("OverrideInvulnerable"), Shape::Command);
        m_resetInvulnerable.BindName(cls, STR("ResetInvulnerable"), Shape::Command);
        m_currentHealth.BindName(cls, STR("GetCurrentHealth"), Shape::Number);
        m_respawnTimer.BindName(cls, STR("SetRespawnTimer"), Shape::Command); // optional
        std::string missing;
        const std::pair<const Getter*, const char*> required[] = {
            {&m_handleDamage, "HandleDamage"},       {&m_setHealth, "SetHealth"},
            {&m_respawn, "Respawn"},                 {&m_killed, "OnCharacterKilled"},
            {&m_overrideInvulnerable, "OverrideInvulnerable"}, {&m_resetInvulnerable, "ResetInvulnerable"},
            {&m_currentHealth, "GetCurrentHealth"},
        };
        for (const auto& [getter, name] : required)
            if (!getter->ok()) missing += std::string(missing.empty() ? "" : ", ") + name + (getter->error().empty() ? "" : " (" + getter->error() + ")");
        if (!missing.empty())
        {
            m_disabled = true;
            Log("match play: disabled; missing character bindings on " + ClassName(character) + ": " + missing);
            return false;
        }
        Log("match play: character bindings ready on " + ClassName(character) + (m_respawnTimer.ok() ? "" : " (no SetRespawnTimer)"));
        return true;
    }

    std::optional<double> MatchPlay::Health(UObject* character) const { return m_currentHealth.Number(character); }

    void MatchPlay::SetHealth(UObject* character, double health)
    {
        m_setHealth.Call(character, [&](std::uint8_t* value, const Param& p) {
            if (p.kind == Kind::Float) WriteFloat(value, health);
        });
    }

    void MatchPlay::Release(const char* why)
    {
        if (!m_engaged) return;
        UObject* player = m_scene.Player();
        UObject* character = player ? m_b.myCharacter.Object(player) : nullptr;
        if (m_protectionSet && character && character->GetClassPrivate() == m_class)
            m_resetInvulnerable.Call(character, [](std::uint8_t*, const Param&) {});
        m_protectionSet = m_protected = false;
        m_engaged = false;
        m_dead = false;
        m_stateSequence = 0;
        Log(std::string("match play: released (") + why + "); " + std::to_string(m_applied) + " update(s) applied");
        m_applied = 0;
    }

    void MatchPlay::TickPlayState(double now, const std::string& scenario, bool inChallenge, bool loading)
    {
        const auto snapshot = m_output.playState();
        if (snapshot.version != m_version)
        {
            m_version = snapshot.version;
            m_state = snapshot.state;
        }
        const PlayState* s = m_state.get();
        // Gate: generated match scenarios in freeplay only; never a challenge.
        const char* closed = nullptr;
        if (!s) closed = "no play state";
        else if (inChallenge) closed = "challenge";
        else if (loading) closed = "loading";
        else if (!scenario.starts_with(MatchScenarioPrefix)) closed = "not a match scenario";
        else if (s->scenario != scenario) closed = "play state is for another scenario";
        else if (m_disabled) closed = "disabled";
        UObject* player = m_scene.Player();
        UObject* character = player ? m_b.myCharacter.Object(player) : nullptr;
        if (!closed && !character) closed = "no character";
        if (!closed && !BindCharacter(character)) closed = "disabled";
        if (closed)
        {
            if (m_engaged) Release(closed);
            else if (s && m_closedReason != closed)
                Log(std::string("match play: play state ignored (") + closed + ")");
            m_closedReason = s ? closed : "";
            return;
        }
        m_closedReason.clear();
        if (!m_engaged)
        {
            m_engaged = true;
            m_dead = Health(character).value_or(1) <= 0;
            // A hit reported before engaging is history, not an effect to replay.
            m_hitSequence = s->lastHit ? s->lastHit->sequence : 0;
            Log("match play: engaged in \"" + scenario + "\"");
        }

        const bool update = s->sequence != m_stateSequence;
        if (update)
        {
            m_stateSequence = s->sequence;
            ++m_applied;
            if (s->spawnProtected != m_protected || !m_protectionSet)
            {
                m_overrideInvulnerable.Call(character, [&](std::uint8_t* value, const Param& p) {
                    if (p.kind == Kind::Bool) WriteBool(value, p, s->spawnProtected);
                });
                m_protected = s->spawnProtected;
                m_protectionSet = true;
            }
            // The hit effect: the game's own damage handling (flash, sound,
            // indicator), never lethal by itself; health is set right after.
            if (s->lastHit && s->lastHit->sequence > m_hitSequence)
            {
                m_hitSequence = s->lastHit->sequence;
                const double current = Health(character).value_or(0);
                const double amount = std::clamp(s->lastHit->damage, 0.0, std::max(0.0, current - 1.0));
                double location[3]{};
                if (!m_dead && current > 1 && m_b.actorLocation.Vector(character, location))
                {
                    const double* d = s->lastHit->direction;
                    const double origin[3] = {location[0] - d[0] * 100, location[1] - d[1] * 100, location[2] - d[2] * 100};
                    m_handleDamage.Call(character, [&](std::uint8_t* value, const Param& p) {
                        if (p.kind == Kind::Float) WriteFloat(value, p.name == "amount" ? amount : 0.0); // WeaponKB 0: no knockback
                        else if (p.kind == Kind::Vector && p.size == 12)
                        {
                            const float f[3] = {static_cast<float>(origin[0]), static_cast<float>(origin[1]), static_cast<float>(origin[2])};
                            std::memcpy(value, f, 12);
                        }
                        // Attacker stays null: no game character is credited.
                    });
                }
            }
            if (!s->alive && !m_dead)
            {
                m_killed.Call(character, [](std::uint8_t*, const Param&) {});
                m_dead = true;
                if (m_respawnTimer.ok() && s->respawnAtMs > 0)
                {
                    const double seconds = std::clamp((s->respawnAtMs - UnixMs()) / 1000.0, 0.1, 30.0);
                    m_respawnTimer.Call(character, [&](std::uint8_t* value, const Param& p) {
                        if (p.kind == Kind::Float) WriteFloat(value, seconds);
                    });
                }
                Log("match play: died" + (s->lastHit ? " (attacker " + s->lastHit->attacker + ")" : std::string()));
            }
            else if (s->alive && m_dead)
            {
                // The native respawn timer may already have brought the character back.
                if (Health(character).value_or(0) <= 0)
                    m_respawn.Call(character, [](std::uint8_t* value, const Param& p) {
                        if (p.kind == Kind::Bool) WriteBool(value, p, true);
                    });
                m_dead = false;
                Log("match play: respawned");
            }
        }
        // Health follows the host (also undoing local regeneration or damage).
        if (s->alive && !m_dead && (update || now >= m_nextHealthCheck))
        {
            m_nextHealthCheck = now + 0.25;
            if (auto current = Health(character); current && std::fabs(*current - s->health) > 0.5) SetHealth(character, s->health);
        }
    }

    // ----------------------------------------------------------- round state

    bool MatchPlay::BindRound()
    {
        if (m_roundBound) return !m_roundDisabled;
        m_roundBound = true;
        m_teleport.BindPath(STR("/Script/Engine.Actor:K2_TeleportTo"), Shape::Command);
        m_controlRotation.BindPath(STR("/Script/Engine.Controller:SetControlRotation"), Shape::Command);
        m_ignoreMove.BindPath(STR("/Script/Engine.Controller:SetIgnoreMoveInput"), Shape::Command);
        m_moveIgnored.BindPath(STR("/Script/Engine.Controller:IsMoveInputIgnored"), Shape::Bool);
        m_setWeapon.BindPath(STR("/Script/GameSkillsTrainer.WeaponHandler:SetWeaponProfileByString"), Shape::Command);
        m_loadWeapons.BindPath(STR("/Script/GameSkillsTrainer.WeaponHandler:LoadWeapons"), Shape::Command);
        m_selectWeapon.BindPath(STR("/Script/GameSkillsTrainer.WeaponHandler:SetSelectedWeapon"), Shape::Command);
        m_selectedWeapon.BindPath(STR("/Script/GameSkillsTrainer.WeaponHandler:GetSelectedWeapon"), Shape::Number);
        m_selectable.Bind(FindClass(STR("/Script/GameSkillsTrainer.WeaponHandler")), "SelectableWeapon");
        std::string missing;
        const std::pair<const Getter*, const char*> required[] = {
            {&m_teleport, "K2_TeleportTo"}, {&m_controlRotation, "SetControlRotation"}, {&m_ignoreMove, "SetIgnoreMoveInput"},
            {&m_setWeapon, "SetWeaponProfileByString"}, {&m_loadWeapons, "LoadWeapons"},
        };
        for (const auto& [getter, name] : required)
            if (!getter->ok()) missing += std::string(missing.empty() ? "" : ", ") + name;
        if (!missing.empty())
        {
            m_roundDisabled = true;
            Log("match play: round state disabled; missing " + missing);
            return false;
        }
        Log(std::string("match play: round state bindings ready") +
            (m_selectable.ok() && m_selectWeapon.ok() && m_selectedWeapon.ok() ? "" : " (empty slots unsupported)"));
        return true;
    }

    void MatchPlay::ApplyLoadout(UObject* character, const RoundState::Loadout& l)
    {
        UObject* handler = Describe(character).weaponHandler.Object(character);
        if (!handler) return;
        const std::string key = l.primary + "\t" + l.pistol + "\t" + l.knife + "\t" + l.grenade + "\t" + l.bomb;
        // A new handler (respawn) starts from the scenario loadout again.
        if (handler == m_loadoutHandler && key == m_loadoutKey) return;
        if (handler != m_loadoutHandler)
        {
            m_selectableBefore.clear();
            m_csLoadout.reset();
        }
        m_loadoutHandler = handler;
        m_loadoutKey = key;
        m_loadoutChanged = true;
        std::vector<const std::uint8_t*> slots;
        const bool canEmpty = m_selectable.ok() && m_selectable.Elements(handler, slots, 8);
        // Slots 0-4: primary, pistol, and in CS the knife, grenades and the bomb (an empty name leaves the slot as it is).
        const std::string names[cs::Slots] = {l.primary, l.pistol, l.knife, l.grenade, l.bomb};
        std::string result;
        for (int slot = 0; slot < cs::Slots; ++slot)
        {
            if (names[slot].empty()) continue;
            const bool empty = names[slot] == "-";
            if (canEmpty && static_cast<std::size_t>(slot) < slots.size())
            {
                auto* flag = const_cast<std::uint8_t*>(slots[static_cast<std::size_t>(slot)]);
                if (std::none_of(m_selectableBefore.begin(), m_selectableBefore.end(), [&](const auto& e) { return e.first == slot; }))
                    m_selectableBefore.push_back({slot, *flag != 0});
                *flag = empty ? 0 : 1;
            }
            if (empty)
            {
                result += " slot" + std::to_string(slot) + (canEmpty ? "=empty" : "=empty-unsupported");
                continue;
            }
            // CS: a slot whose profile didn't change keeps its weapon (and its magazine) when another
            // slot changes (the bomb picked up or dropped, the last grenade thrown).
            if (m_csLoadout && m_csLoadout->names[static_cast<std::size_t>(slot)] == names[slot])
            {
                result += " slot" + std::to_string(slot) + "=kept";
                continue;
            }
            std::int32_t code = -1;
            m_setWeapon.Call(
                handler,
                [&](std::uint8_t* value, const Param& p) {
                    if (p.kind == Kind::String) WriteString(value, names[slot]);
                    else if (p.kind == Kind::Int32) std::memcpy(value, &slot, 4);
                },
                [&](const std::uint8_t* buffer, const std::vector<Param>& params) {
                    for (const Param& p : params)
                        if (p.ret && p.kind == Kind::Int32) std::memcpy(&code, buffer + p.offset, 4);
                });
            result += " slot" + std::to_string(slot) + "=\"" + names[slot] + "\"->" + std::to_string(code);
        }
        Log("match play: loadout" + result);
        if (!l.knife.empty())
        {
            // CS: CsGear draws what CS would (a purchase, the best weapon when the slot in hand emptied).
            const cs::Loadout now = cs::FromRound(l);
            m_gear.LoadoutApplied(m_csLoadout ? &*m_csLoadout : nullptr, now);
            m_csLoadout = now;
            return;
        }
        // Never leave an emptied slot selected.
        if (canEmpty && m_selectWeapon.ok() && m_selectedWeapon.ok())
        {
            const int selected = static_cast<int>(m_selectedWeapon.Number(handler).value_or(0));
            if (selected >= 0 && selected < 2 && names[selected] == "-" && names[1 - selected] != "-")
            {
                const int other = 1 - selected;
                m_selectWeapon.Call(handler, [&](std::uint8_t* value, const Param& p) { if (p.kind == Kind::Int32) std::memcpy(value, &other, 4); });
            }
        }
    }

    // KovaaK's "Show Weapon" setting (weaponsettings.ini WeaponHidden=true) hides the whole first-person
    // view model: arms and weapon. In an AimMod match the weapon in hand is part of the game, so each of
    // the player's weapons gets bWeaponHidden cleared in memory (WeaponSettingsNative and the ADS copy)
    // and the view model is refreshed. Nothing is saved: the setting stays as it is outside matches,
    // where the scenario's weapons are loaded again with it.
    void MatchPlay::ShowWeapons(double now, UObject* character)
    {
        if (now < m_nextShowCheck) return;
        m_nextShowCheck = now + 0.5;
        UObject* handler = Describe(character).weaponHandler.Object(character);
        std::vector<UObject*> weapons;
        if (!handler || !Describe(handler).weapons.Objects(handler, weapons, 8)) return;
        int cleared = 0, silenced = 0;
        for (std::size_t slot = 0; slot < weapons.size(); ++slot)
        {
            UObject* weapon = weapons[slot];
            if (!weapon || !reflect::Alive(weapon)) continue;
            // CS: the knife and the bomb make no gunshot (the service plays the knife's own sounds). The
            // user's shot sounds live in the weapon settings, so their names are emptied in memory.
            const bool quiet = m_csLoadout && (slot == cs::KnifeSlot || slot == cs::GrenadeSlot || slot == cs::BombSlot);
            for (const wchar_t* field : {STR("WeaponSettingsNative"), STR("ADSWeaponSettingsNative")})
            {
                auto* settings = quiet ? RC::Unreal::CastField<RC::Unreal::FStructProperty>(reflect::PropertyOf(weapon->GetClassPrivate(), field)) : nullptr;
                for (const wchar_t* sound : {STR("ShootSound"), STR("ShootPressedSound"), STR("ShootReleasedSound")})
                {
                    auto* p = settings ? RC::Unreal::CastField<RC::Unreal::FStrProperty>(reflect::PropertyOf(settings->GetStruct(), sound)) : nullptr;
                    if (!p) continue;
                    auto* text = reinterpret_cast<game::ArrayView*>(reflect::At(weapon, settings) + p->GetOffset_Internal());
                    if (text->num <= 1) continue;
                    text->num = 0; // empty; the buffer stays the engine's
                    ++silenced;
                }
            }
            for (const wchar_t* field : {STR("WeaponSettingsNative"), STR("ADSWeaponSettingsNative")})
            {
                auto* settings = RC::Unreal::CastField<RC::Unreal::FStructProperty>(reflect::PropertyOf(weapon->GetClassPrivate(), field));
                auto* hidden = settings ? RC::Unreal::CastField<RC::Unreal::FBoolProperty>(reflect::PropertyOf(settings->GetStruct(), STR("bWeaponHidden"))) : nullptr;
                if (!hidden) continue;
                std::uint8_t* at = reflect::At(weapon, settings) + hidden->GetOffset_Internal();
                if (!hidden->GetPropertyValue(at)) continue;
                hidden->SetPropertyValue(at, false);
                ++cleared;
            }
        }
        if (silenced && !m_knifeQuietLogged)
        {
            m_knifeQuietLogged = true;
            Log("match play: no gunshot for the knife and the bomb (" + std::to_string(silenced) + " shot sound(s) emptied in memory)");
        }
        if (cleared == 0) return;
        // Show the arms and the weapon in hand again (the game hid them with the old setting).
        UObject* view = reflect::GetObject(character, STR("ViewModel_Native"));
        UObject* current = nullptr;
        reflect::Call(handler, STR("/Script/GameSkillsTrainer.WeaponHandler:GetCurrentWeapon"), {}, &current);
        const bool refreshed = view && current && reflect::Call(view, STR("/Script/GameSkillsTrainer.FPSPlayer_WeaponComponentActor:UpdateViewModel"),
                                                               [&](const std::wstring& n, RC::Unreal::FProperty* p, std::uint8_t* v) {
                                                                   if (n == STR("bHideWeapon")) reflect::WriteBoolParam(v, p, false);
                                                                   else if (n == STR("Weapon")) reflect::WriteObject(v, current);
                                                               });
        if (!m_weaponShownLogged || !refreshed)
            Log("match play: weapon shown in first person for the match (KovaaK's Show Weapon is off; " + std::to_string(cleared) + " setting(s) cleared in memory, view " +
                (refreshed ? "refreshed" : "not refreshed") + ")");
        m_weaponShownLogged = true;
    }

    void MatchPlay::ReleaseRound(const char* why)
    {
        if (!m_roundEngaged) return;
        UObject* player = m_scene.Player();
        UObject* character = player ? m_b.myCharacter.Object(player) : nullptr;
        if (m_frozen && player) m_ignoreMove.Call(player, [](std::uint8_t* value, const Param& p) { if (p.boolProperty) p.boolProperty->SetPropertyValue(value, false); });
        FreezeBody(character, false);
        if (m_loadoutChanged && character)
        {
            UObject* handler = Describe(character).weaponHandler.Object(character);
            if (handler && handler == m_loadoutHandler)
            {
                std::vector<const std::uint8_t*> slots;
                if (m_selectable.ok() && m_selectable.Elements(handler, slots, 8))
                    for (const auto& [slot, value] : m_selectableBefore)
                        if (static_cast<std::size_t>(slot) < slots.size()) *const_cast<std::uint8_t*>(slots[static_cast<std::size_t>(slot)]) = value ? 1 : 0;
                m_loadWeapons.Call(handler, [](std::uint8_t*, const Param&) {}); // the scenario's own loadout
            }
        }
        m_gear.Release(player, why);
        m_grenades.Release(why);
        m_feel.Release(player, character, why);
        m_csLoadout.reset();
        Log(std::string("match play: round state released (") + why + ")" + (m_frozen ? "; movement restored" : "") +
            (m_loadoutChanged ? "; scenario loadout restored" : ""));
        m_roundEngaged = m_frozen = m_loadoutChanged = false;
        m_loadoutHandler = nullptr;
        m_loadoutKey.clear();
        m_selectableBefore.clear();
        m_pendingSpawn.reset();
    }

    void MatchPlay::TickRound(double now, const std::string& scenario, bool inChallenge, bool loading)
    {
        const auto snapshot = m_output.roundState();
        if (snapshot.version != m_roundVersion)
        {
            m_roundVersion = snapshot.version;
            m_round = snapshot.state;
        }
        const RoundState* r = m_round.get();
        // Restart lock: any match whose fresh round state names the scenario on screen.
        TickRestartLock(now, r && !loading && r->scenario == scenario);
        const char* closed = nullptr;
        if (!r) closed = "no round state";
        else if (inChallenge) closed = "challenge";
        else if (loading) closed = "loading";
        else if (!scenario.starts_with(MatchScenarioPrefix)) closed = "not a match scenario";
        else if (r->scenario != scenario) closed = "round state is for another scenario";
        UObject* player = m_scene.Player();
        UObject* character = player ? m_b.myCharacter.Object(player) : nullptr;
        if (!closed && (!player || !character)) closed = "no character";
        if (!closed && !BindRound()) closed = "disabled";
        if (closed)
        {
            if (m_roundEngaged) ReleaseRound(closed);
            else if (r && m_roundClosed != closed) Log(std::string("match play: round state ignored (") + closed + ")");
            m_roundClosed = r ? closed : "";
            return;
        }
        m_roundClosed.clear();
        const bool freezePhase = r->phase && r->phase->name == "freeze";
        if (!m_roundEngaged)
        {
            m_roundEngaged = true;
            // A spawn already in the file is history, except at a round start.
            m_spawnId = r->spawn && !freezePhase ? r->spawn->id : "";
            Log("match play: round state engaged in \"" + scenario + "\"");
        }
        // Spawn: once per id. At a CS round start a dead player respawns first.
        if (r->spawn && r->spawn->id != m_spawnId)
        {
            m_spawnId = r->spawn->id;
            double delay = 0;
            if (freezePhase && BindCharacter(character) && Health(character).value_or(1) <= 0)
            {
                m_respawn.Call(character, [](std::uint8_t* value, const Param& p) { if (p.boolProperty) p.boolProperty->SetPropertyValue(value, true); });
                m_dead = false;
                delay = 0.15; // let the respawn place the character first
                Log("match play: respawned for the round start");
            }
            m_pendingSpawn = PendingSpawn{*r->spawn, now + delay, now + 2.0};
        }
        if (m_pendingSpawn && now >= m_pendingSpawn->notBefore)
        {
            const RoundState::Spawn& s = m_pendingSpawn->spawn;
            const double location[3] = {s.x, s.y, s.z}, rotation[3] = {0, s.yaw, 0};
            bool moved = false;
            m_teleport.Call(
                character,
                [&](std::uint8_t* value, const Param& p) {
                    if ((p.kind == Kind::Vector || p.kind == Kind::Rotator) && p.size == 12)
                    {
                        const double* v = p.kind == Kind::Vector ? location : rotation;
                        const float f[3] = {static_cast<float>(v[0]), static_cast<float>(v[1]), static_cast<float>(v[2])};
                        std::memcpy(value, f, 12);
                    }
                },
                [&](const std::uint8_t* buffer, const std::vector<Param>& params) {
                    for (const Param& p : params)
                        if (p.ret && p.boolProperty) moved = p.boolProperty->GetPropertyValue(const_cast<std::uint8_t*>(buffer) + p.offset);
                });
            if (moved)
            {
                m_controlRotation.Call(player, [&](std::uint8_t* value, const Param& p) {
                    if (p.kind == Kind::Rotator && p.size == 12)
                    {
                        const float f[3] = {0, static_cast<float>(s.yaw), 0};
                        std::memcpy(value, f, 12);
                    }
                });
                Log("match play: teleported to spawn " + s.id);
                m_pendingSpawn.reset();
            }
            else if (now > m_pendingSpawn->giveUp)
            {
                Log("match play: teleport to spawn " + s.id + " refused by the game (blocked location?)");
                m_pendingSpawn.reset();
            }
        }
        // Freeze: movement off, looking stays. Re-applied if a respawn or
        // possession reset the controller's ignore flags.
        const bool wantFrozen = r->phase && r->phase->frozen;
        auto setIgnore = [&](bool on) {
            m_ignoreMove.Call(player, [&](std::uint8_t* value, const Param& p) { if (p.boolProperty) p.boolProperty->SetPropertyValue(value, on); });
        };
        if (wantFrozen != m_frozen)
        {
            setIgnore(wantFrozen);
            m_frozen = wantFrozen;
            Log(wantFrozen ? "match play: frozen (movement and jump off)" : "match play: unfrozen");
        }
        else if (m_frozen && m_moveIgnored.ok() && !m_moveIgnored.Bool(player).value_or(true)) setIgnore(true);
        FreezeBody(character, m_frozen);
        if (r->loadout) ApplyLoadout(character, *r->loadout);
        ShowWeapons(now, character);
        // CS: switching (wheel, Q, purchases), the knife and bomb in the hand, the bomb in the world.
        if (r->loadout && !r->loadout->knife.empty())
        {
            UObject* handler = Describe(character).weaponHandler.Object(character);
            m_gear.Tick(now, player, character, handler, *r);
            m_grenades.Tick(character, m_gear.hand(), m_output.root(), scenario);
            // CS weapon feel: spread, scope, speed (CsFeel.hpp).
            if (m_csLoadout) m_feel.Tick(now, player, character, handler, m_gear.hand(), *m_csLoadout, r->feel, m_shotSequence + 1, m_output.root());
        }
    }
} // namespace aimmod
