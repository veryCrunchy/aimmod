#include "CsFeel.hpp"

#include "Log.hpp"
#include "Reflect.hpp"
#include "World.hpp"

#include <Unreal/Core/HAL/UnrealMemory.hpp>
#include <Unreal/FProperty.hpp>
#include <Unreal/NameTypes.hpp>
#include <Unreal/Property/FBoolProperty.hpp>
#include <Unreal/Property/FStructProperty.hpp>
#include <Unreal/UClass.hpp>
#include <Unreal/UObject.hpp>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <fstream>

#include <Windows.h>

namespace aimmod
{
    using namespace reflect;
    using game::Kind;
    using game::Param;
    using game::Shape;
    using RC::Unreal::FWeakObjectPtr;

    namespace
    {
        constexpr double Pi = 3.14159265358979323846, Deg = Pi / 180.0;
        // The four FWeaponProfileNative copies a weapon carries; the next shot reads one of them.
        constexpr const char* ProfileCopies[] = {"CurrentWeaponProfile", "NextShotWeaponProfile", "HipfireWeaponProfile", "ADSWeaponProfile"};
        constexpr const wchar_t* WeaponClass = STR("/Script/GameSkillsTrainer.WeaponParentActor");

        std::int64_t UnixMs()
        {
            return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
        }
        void WriteF(const std::uint8_t* at, double v)
        {
            const float f = static_cast<float>(v);
            std::memcpy(const_cast<std::uint8_t*>(at), &f, sizeof f);
        }
        float ReadF(const std::uint8_t* at)
        {
            float f;
            std::memcpy(&f, at, sizeof f);
            return f;
        }
        std::optional<bool> Flag(UObject* object, const wchar_t* name)
        {
            const auto b = GetByte(object, name);
            if (!b) return std::nullopt;
            return *b != 0;
        }
        void CameraBasis(double pitch, double yaw, double f[3], double r[3], double u[3])
        {
            const double p = pitch * Deg, y = yaw * Deg;
            f[0] = std::cos(p) * std::cos(y), f[1] = std::cos(p) * std::sin(y), f[2] = std::sin(p);
            r[0] = -std::sin(y), r[1] = std::cos(y), r[2] = 0;
            u[0] = -std::sin(p) * std::cos(y), u[1] = -std::sin(p) * std::sin(y), u[2] = std::cos(p);
        }
        double AngleBetween(const double a[3], const double b[3])
        {
            const double dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
            return std::acos(std::clamp(dot, -1.0, 1.0)) / Deg;
        }
        std::string Fixed(double v, int decimals)
        {
            char buffer[32];
            std::snprintf(buffer, sizeof buffer, "%.*f", decimals, std::isfinite(v) ? v : 0.0);
            return buffer;
        }
    } // namespace

    bool CsFeel::Bind()
    {
        if (m_bound) return !m_copies.empty() || m_pbsMissing;
        m_bound = true;
        m_keyJustPressed.BindPath(STR("/Script/Engine.PlayerController:WasInputKeyJustPressed"), Shape::Command);
        m_keyDown.BindPath(STR("/Script/Engine.PlayerController:IsInputKeyDown"), Shape::Command);
        m_isFalling.BindPath(STR("/Script/Engine.NavMovementComponent:IsFalling"), Shape::Bool);
        m_isCrouching.BindPath(STR("/Script/Engine.NavMovementComponent:IsCrouching"), Shape::Bool);
        m_zoomIn.BindPath(STR("/Script/GameSkillsTrainer.WeaponHandler:ZoomIn"), Shape::Command);
        m_zoomOut.BindPath(STR("/Script/GameSkillsTrainer.WeaponHandler:ZoomOut"), Shape::Command);
        m_fullZoomIn.BindPath(STR("/Script/GameSkillsTrainer.WeaponParentActor:FullZoomIn"), Shape::Command);
        m_movement.Bind(game::FindClass(STR("/Script/Engine.Character")), STR("CharacterMovement"));
        UClass* weapon = game::FindClass(WeaponClass);
        int pbs = 0;
        for (const char* copy : ProfileCopies)
        {
            Copy c;
            const std::string base = copy;
            if (weapon && c.pbs.Bind(weapon, base + ".PerBulletSpread.SingleBulletSpread") && c.pbs.elementStruct())
            {
                c.distance.Bind(c.pbs.elementStruct(), "Distance");
                c.angle.Bind(c.pbs.elementStruct(), "Angle");
            }
            if (weapon)
            {
                c.use.Bind(weapon, base + ".PerBulletSpread.UsePBS");
                c.sens.Bind(weapon, base + ".ADSZoomSensFactor");
                c.tbs.Bind(weapon, base + ".TimeBetweenShots");
            }
            if (c.pbs.ok() && c.distance.ok() && c.angle.ok() && c.distance.kind() == Kind::Float && c.angle.kind() == Kind::Float) ++pbs;
            m_copies.push_back(std::move(c));
        }
        m_pbsMissing = pbs == 0;
        Log(std::string("cs feel: ready (per-bullet spread in ") + std::to_string(pbs) + " of 4 profile copies" + (m_pbsMissing ? ": the spread rides on AimModCore's own ray" : "") +
            ", zoom " + (m_zoomIn.ok() && m_zoomOut.ok() ? "ok" : "missing") + ", keys " + (m_keyJustPressed.ok() && m_keyDown.ok() ? "ok" : "missing") + ", movement " +
            (m_movement.ok() && m_isFalling.ok() && m_isCrouching.ok() ? "ok" : "partial") + ")");
        return true;
    }

    bool CsFeel::Key(UObject* player, const char* key, bool held) const
    {
        const game::Getter& getter = held ? m_keyDown : m_keyJustPressed;
        if (!player || !getter.ok()) return false;
        bool on = false;
        const RC::Unreal::FName name(Widen(key).c_str(), RC::Unreal::FNAME_Add);
        getter.Call(
            player,
            [&](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Other && p.structType && p.size >= static_cast<std::int32_t>(sizeof(name))) std::memcpy(value, &name, sizeof(name));
            },
            [&](const std::uint8_t* buffer, const std::vector<Param>& params) {
                for (const Param& p : params)
                    if (p.ret && p.boolProperty) on = p.boolProperty->GetPropertyValue(const_cast<std::uint8_t*>(buffer) + p.offset);
            });
        return on;
    }

    void CsFeel::Motion(UObject* character, cs::Motion& m) const
    {
        m = {};
        UObject* movement = character && m_movement.ok() ? m_movement.Object(character) : nullptr;
        if (!movement || !Alive(movement)) return;
        if (FProperty* p = PropertyOf(movement->GetClassPrivate(), STR("Velocity")); p && p->GetSize() >= 12)
        {
            float v[3];
            std::memcpy(v, At(movement, p), sizeof v);
            if (std::isfinite(v[0]) && std::isfinite(v[1])) m.speed = std::hypot(v[0], v[1]) / cs::UnitCm;
        }
        m.air = m_isFalling.Bool(movement).value_or(false);
        m.crouch = m_isCrouching.Bool(movement).value_or(false);
    }

    // ------------------------------------------------------------- spread

    bool CsFeel::WritePbs(UObject* weapon, double distance, double angle)
    {
        int written = 0;
        bool used = false;
        for (const Copy& c : m_copies)
        {
            if (!c.pbs.ok() || !c.distance.ok() || !c.angle.ok()) continue;
            std::vector<const std::uint8_t*> entries;
            if (!c.pbs.Elements(weapon, entries, 4) || entries.empty()) continue;
            WriteF(c.distance.At(entries[0]), distance);
            WriteF(c.angle.At(entries[0]), angle);
            ++written;
            if (c.use.ok() && c.use.kind() == Kind::Bool && *c.use.At(weapon) != 0) used = true;
        }
        return written > 0 && used;
    }

    std::optional<std::pair<double, double>> CsFeel::ReadPbsRotator(UObject* weapon) const
    {
        float r[3]{};
        bool ok = false;
        const std::int32_t bullet = 0;
        Call(weapon, STR("/Script/GameSkillsTrainer.WeaponParentActor:GetPerBulletSpread"),
             [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                 if (n == STR("BulletNum")) std::memcpy(v, &bullet, sizeof bullet);
             },
             nullptr, [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
                 if (n == STR("ReturnValue")) ok = ReadFloats(v, p, r, 3);
             });
        if (!ok || !std::isfinite(r[0]) || !std::isfinite(r[1])) return std::nullopt;
        return std::pair<double, double>{r[0], r[1]}; // FRotator: pitch, yaw, roll
    }

    // KovaaK's entry is a distance and an angle; how they become the rotator the shot is turned by
    // isn't documented, so it is measured: entries (1, 0) and (1, 90) give the basis, a third one
    // checks that the mapping is linear.
    bool CsFeel::Calibrate(UObject* weapon)
    {
        if (m_calib != Calib::Unknown) return m_calib == Calib::Ready;
        const auto fail = [&](const std::string& why) {
            m_calib = Calib::Failed;
            WritePbs(weapon, 0, 0);
            Log("cs feel: KovaaK's per-bullet spread can't carry the spread (" + why + "); it rides on AimModCore's own ray (the game's trace stays on the crosshair)");
            return false;
        };
        if (m_pbsMissing) return fail("no per-bullet spread in the weapon profile");
        if (!WritePbs(weapon, 1, 0)) return fail("the profile's per-bullet spread is off or empty");
        const auto r0 = ReadPbsRotator(weapon);
        WritePbs(weapon, 1, 90);
        const auto r90 = ReadPbsRotator(weapon);
        if (!r0 || !r90) return fail("GetPerBulletSpread can't be read");
        const double det = r0->first * r90->second - r90->first * r0->second;
        if (std::fabs(det) < 1e-6) return fail("entries (1, 0) and (1, 90) turn the shot the same way");
        WritePbs(weapon, 2, 30);
        const auto r30 = ReadPbsRotator(weapon);
        const double c = std::cos(30 * Deg), s = std::sin(30 * Deg);
        const double wantP = 2 * (c * r0->first + s * r90->first), wantY = 2 * (c * r0->second + s * r90->second);
        if (!r30 || std::hypot(r30->first - wantP, r30->second - wantY) > 0.02 * std::hypot(wantP, wantY) + 1e-4)
            return fail("the entry is not linear in its distance (" + (r30 ? Fixed(r30->first, 3) + ", " + Fixed(r30->second, 3) : std::string("unreadable")) + " for (2, 30))");
        m_basis[0][0] = r0->first, m_basis[0][1] = r0->second, m_basis[1][0] = r90->first, m_basis[1][1] = r90->second;
        WritePbs(weapon, 0, 0);
        m_calib = Calib::Ready;
        Log("cs feel: per-bullet spread measured: entry (1, 0) turns the shot by pitch " + Fixed(r0->first, 4) + ", yaw " + Fixed(r0->second, 4) + "; (1, 90) by pitch " +
            Fixed(r90->first, 4) + ", yaw " + Fixed(r90->second, 4) + " (degrees)");
        return true;
    }

    // Whether the game adds the entry's rotator to the camera's or turns in the camera's frame: one
    // look at where the next shot would go (GetHitscanDestination) while looking up or down.
    void CsFeel::CheckComposition(double now, UObject* weapon, double pitch, double yaw, const cs::Offset& o, double addPitch, double addYaw)
    {
        if (m_compose != Compose::Unknown || now < m_nextComposeCheck || std::fabs(pitch) < 15 || std::fabs(addYaw) < 0.3) return;
        m_nextComposeCheck = now + 0.5;
        float origin[3]{}, dest[3]{};
        bool haveOrigin = false, haveDest = false;
        const std::int32_t bullet = 0;
        Call(weapon, STR("/Script/GameSkillsTrainer.WeaponParentActor:GetShotOrigin"), {}, nullptr, [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
            if (n == STR("ReturnValue")) haveOrigin = ReadFloats(v, p, origin, 3);
        });
        Call(weapon, STR("/Script/GameSkillsTrainer.WeaponParentActor:GetHitscanDestination"),
             [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                 if (n == STR("BulletNum")) std::memcpy(v, &bullet, sizeof bullet);
             },
             nullptr, [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
                 if (n == STR("ReturnValue")) haveDest = ReadFloats(v, p, dest, 3);
             });
        if (!haveOrigin || !haveDest)
        {
            m_compose = Compose::Add;
            Log("cs feel: the shot's destination can't be read; the spread is added to the camera's rotation");
            return;
        }
        double measured[3] = {dest[0] - origin[0], dest[1] - origin[1], dest[2] - origin[2]};
        const double len = std::sqrt(measured[0] * measured[0] + measured[1] * measured[1] + measured[2] * measured[2]);
        if (len < 1) return;
        for (double& v : measured) v /= len;
        // Added: the bullet's ray exactly. In the camera's frame: that rotator turns the camera's axes.
        double added[3], f[3], r[3], u[3];
        cs::SpreadDirection(pitch, yaw, o, added);
        CameraBasis(pitch, yaw, f, r, u);
        const double lp = addPitch * Deg, ly = addYaw * Deg;
        const double l[3] = {std::cos(lp) * std::cos(ly), std::cos(lp) * std::sin(ly), std::sin(lp)};
        double local[3];
        for (int i = 0; i < 3; ++i) local[i] = l[0] * f[i] + l[1] * r[i] + l[2] * u[i];
        const double toAdded = AngleBetween(measured, added), toLocal = AngleBetween(measured, local), apart = AngleBetween(added, local);
        if (apart < 0.2) return;
        if (toAdded < apart * 0.35) m_compose = Compose::Add;
        else if (toLocal < apart * 0.35) m_compose = Compose::Local;
        else
        {
            if (now >= m_nextMismatchLog) Log("cs feel: the shot's destination matches neither composition (" + Fixed(toAdded, 2) + " / " + Fixed(toLocal, 2) + " degrees off); measuring again");
            m_nextMismatchLog = now + 5;
            return;
        }
        Log(std::string("cs feel: the game ") + (m_compose == Compose::Add ? "adds the per-bullet spread to the camera's rotation" : "turns the shot in the camera's frame") + " (" +
            Fixed(toAdded, 3) + " / " + Fixed(toLocal, 3) + " degrees from the two predictions)");
    }

    void CsFeel::Arm(double now, UObject* player, UObject* weapon, const cs::WeaponFeel& w, std::uint64_t nextShot, std::uint64_t salt)
    {
        const double inaccuracy = m_accuracy.inaccuracy();
        const cs::Offset o = cs::SpreadOffset(salt, nextShot, inaccuracy, w.spread);
        bool applied = false;
        UObject* camera = player ? m_b.cameraManager.Object(player) : nullptr;
        double rotation[3]{};
        const bool view = camera && m_b.cameraRotation.Vector(camera, rotation);
        if (view && Calibrate(weapon))
        {
            double addPitch = 0, addYaw = 0, localPitch = 0, localYaw = 0;
            cs::SpreadRotator(rotation[0], rotation[1], o, addPitch, addYaw);
            cs::SpreadLocalRotator(o, localPitch, localYaw);
            const bool local = m_compose == Compose::Local;
            const double dp = local ? localPitch : addPitch, dy = local ? localYaw : addYaw;
            // dp, dy = u * (1, 0)'s rotator + v * (1, 90)'s rotator: KovaaK's entry is (hypot(u, v), atan2(v, u)).
            const double det = m_basis[0][0] * m_basis[1][1] - m_basis[1][0] * m_basis[0][1];
            const double u = (dp * m_basis[1][1] - dy * m_basis[1][0]) / det, v = (m_basis[0][0] * dy - m_basis[0][1] * dp) / det;
            applied = WritePbs(weapon, std::hypot(u, v), std::atan2(v, u) / Deg);
            // Read back: the game must turn this shot by exactly the bullet's offset.
            if (applied && (m_verified < 30 || now >= m_nextVerify))
            {
                const auto got = ReadPbsRotator(weapon);
                const double error = got ? std::hypot(got->first - dp, got->second - dy) : 1e9;
                if (error > 0.02 + 0.02 * std::hypot(dp, dy))
                {
                    applied = false;
                    ++m_mismatches;
                    if (now >= m_nextMismatchLog)
                    {
                        m_nextMismatchLog = now + 5;
                        Log("cs feel: the per-bullet spread read back " + (got ? Fixed(got->first, 3) + ", " + Fixed(got->second, 3) : std::string("nothing")) + " for " + Fixed(dp, 3) + ", " +
                            Fixed(dy, 3) + "; this shot's spread rides on AimModCore's own ray");
                    }
                    if (m_verified == 0 && m_mismatches >= 20)
                    {
                        m_calib = Calib::Failed;
                        WritePbs(weapon, 0, 0);
                        Log("cs feel: the per-bullet spread never reads back as written; it stays off for this session");
                    }
                }
                else
                {
                    ++m_verified;
                    m_nextVerify = now + 0.5;
                }
            }
            if (applied) CheckComposition(now, weapon, rotation[0], rotation[1], o, addPitch, addYaw);
        }
        if (!applied && m_calib == Calib::Ready) WritePbs(weapon, 0, 0); // the crosshair, and AimModCore's ray carries it
        if (!m_loggedArm)
        {
            m_loggedArm = true;
            Log(std::string("cs feel: first shot armed (") + w.id + ", inaccuracy " + Fixed(inaccuracy * 1000, 2) + " mrad, the game's trace " + (applied ? "follows it" : "stays on the crosshair") + ")");
        }
        m_armed = {true, m_hand, nextShot, salt, inaccuracy, w.spread, applied};
    }

    bool CsFeel::Bullet(int slot, ShotRecord& r, double dir[3]) const
    {
        std::copy(r.direction, r.direction + 3, dir);
        if (!m_armed.on || slot != m_armed.slot || !(m_armed.inaccuracy > 0)) return false;
        const double pitch = std::asin(std::clamp(r.direction[2], -1.0, 1.0)) / Deg, yaw = std::atan2(r.direction[1], r.direction[0]) / Deg;
        cs::SpreadDirection(pitch, yaw, cs::SpreadOffset(m_armed.salt, m_armed.shot, m_armed.inaccuracy, m_armed.spread), dir);
        r.inaccuracy = m_armed.inaccuracy;
        r.spreadShot = m_armed.shot;
        r.spreadApplied = m_armed.applied;
        return true;
    }

    void CsFeel::Fired(double now, int slot)
    {
        if (!m_feel || slot != m_hand) return;
        m_accuracy.Shot(*m_feel);
        if (m_feel->zoomLevels > 0 && m_scope.level() > 0)
        {
            // Out of the scope until the bolt is back (the profile's fire interval).
            double bolt = 1.4;
            if (UObject* weapon = m_weapon.Get(); weapon && Alive(weapon) && !m_copies.empty() && m_copies[0].tbs.ok() && m_copies[0].tbs.kind() == Kind::Float)
                if (const double t = ReadF(m_copies[0].tbs.At(weapon)); t > 0.1 && t < 5) bolt = t;
            m_scope.Shot(now, bolt);
        }
    }

    bool CsFeel::WorldBetween(UObject* context, const double from[3], const double to[3]) const
    {
        UObject* kismet = Default(STR("/Script/Engine.Default__KismetSystemLibrary"));
        if (!context || !kismet) return false;
        struct RawBytes { std::uint8_t* data; std::int32_t num, max; };
        bool hit = false;
        std::uint8_t* types = nullptr;
        std::uint8_t* ignore = nullptr;
        const bool ran = Call(kismet, STR("/Script/Engine.KismetSystemLibrary:LineTraceSingleForObjects"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("WorldContextObject")) WriteObject(v, context);
            else if (n == STR("Start")) WriteFloats(v, p, {static_cast<float>(from[0]), static_cast<float>(from[1]), static_cast<float>(from[2])});
            else if (n == STR("End")) WriteFloats(v, p, {static_cast<float>(to[0]), static_cast<float>(to[1]), static_cast<float>(to[2])});
            else if (n == STR("ObjectTypes"))
            {
                auto* data = static_cast<std::uint8_t*>(RC::Unreal::FMemory::Malloc(1));
                if (!data) return;
                data[0] = 0; // WorldStatic: the map
                const RawBytes raw{data, 1, 1};
                std::memcpy(v, &raw, sizeof raw);
                types = v;
            }
            else if (n == STR("ActorsToIgnore")) WriteObjectArray(v, context), ignore = v;
            else if (n == STR("bIgnoreSelf")) WriteBoolParam(v, p, true);
        }, nullptr, [&](const std::wstring& n, FProperty*, const std::uint8_t* v) {
            if (n == STR("ReturnValue")) hit = *v != 0;
            if (n == STR("ObjectTypes") || n == STR("ActorsToIgnore"))
            {
                RawBytes raw;
                std::memcpy(&raw, v, sizeof raw);
                if (raw.data) RC::Unreal::FMemory::Free(raw.data);
                if (n == STR("ObjectTypes")) types = nullptr;
                else ignore = nullptr;
            }
        });
        if (!ran)
            for (std::uint8_t* left : {types, ignore})
                if (left)
                {
                    RawBytes raw;
                    std::memcpy(&raw, left, sizeof raw);
                    if (raw.data) RC::Unreal::FMemory::Free(raw.data);
                }
        return ran && hit;
    }

    // --------------------------------------------------------------- scope

    void CsFeel::SetZoomFov(UObject* weapon, const cs::WeaponFeel& w, int level)
    {
        const auto fov = GetFloat(weapon, STR("FullZoomUnscaledFOV"));
        if (!fov) return;
        // The game's own first level (ADSFOVOverride through its FOV scaling), seen before any write of ours.
        if (m_fovBase <= 0 && level == 1 && *fov > 1 && *fov < 170) m_fovBase = *fov;
        if (m_fovBase <= 0) return;
        const double want = level >= 2 ? cs::LevelFov(m_fovBase, w, 2) : m_fovBase;
        if (std::fabs(*fov - want) > 1e-3) SetFloat(weapon, STR("FullZoomUnscaledFOV"), static_cast<float>(want));
    }

    void CsFeel::SetSensitivity(UObject* weapon, const cs::WeaponFeel& w, int level, double adsSensitivity)
    {
        if (m_sensBase < 0)
        {
            m_sensBase = adsSensitivity;
            if (!m_copies.empty() && m_copies[0].sens.ok() && m_copies[0].sens.kind() == Kind::Float)
                if (const double s = ReadF(m_copies[0].sens.At(weapon)); s > 0.01 && s < 20) m_sensBase = s;
        }
        const double want = cs::LevelSensitivity(m_sensBase, w, level >= 2 ? 2 : 1);
        for (const Copy& c : m_copies)
            if (c.sens.ok() && c.sens.kind() == Kind::Float) WriteF(c.sens.At(weapon), want);
    }

    void CsFeel::Zoom(double now, UObject* player, UObject* handler, UObject* weapon, const cs::WeaponFeel& w, double adsSensitivity)
    {
        // The game's own right mouse stays out of the scope: the handler blocks it while we drive it.
        if (m_blockedHandler.Get() != handler && !m_blockGivenUp)
        {
            if (UObject* before = m_blockedHandler.Get(); before && Alive(before)) SetBool(before, STR("BlockADS"), m_blockBefore);
            m_blockBefore = Flag(handler, STR("BlockADS")).value_or(false);
            SetBool(handler, STR("BlockADS"), true);
            m_blockedHandler = FWeakObjectPtr(handler);
            m_zoomTries = 0;
        }
        if (Key(player, "RightMouseButton", false)) m_scope.Press(w, now);
        if (Flag(weapon, STR("bIsReloading")).value_or(false)) m_scope.Out();
        m_scope.Tick(now, Key(player, "RightMouseButton", true));
        const int level = m_scope.level();
        const bool zoomed = Flag(handler, STR("CurrentlyZoomed")).value_or(level > 0);
        if ((level > 0) != zoomed)
        {
            if (now >= m_nextZoomCall)
            {
                m_nextZoomCall = now + 0.12;
                const game::Getter& call = level > 0 ? m_zoomIn : m_zoomOut;
                call.Call(handler, [](std::uint8_t* v, const Param& p) { if (p.boolProperty) p.boolProperty->SetPropertyValue(v, true); });
                if (++m_zoomTries == 6 && !m_blockGivenUp)
                {
                    // The game won't zoom while its own right mouse is blocked: give it back, keep its wish in step with ours.
                    m_blockGivenUp = true;
                    SetBool(handler, STR("BlockADS"), m_blockBefore);
                    Log("cs feel: the game doesn't zoom while its right mouse is blocked; the scope now follows it through ZoomDesired");
                }
            }
        }
        else m_zoomTries = 0;
        SetBool(handler, STR("ZoomDesired"), level > 0);
        if (level != m_appliedLevel)
        {
            const bool between = level > 0 && m_appliedLevel > 0; // 1 <-> 2 while zoomed
            if (level > 0) SetSensitivity(weapon, w, level, adsSensitivity);
            SetZoomFov(weapon, w, level > 0 ? level : 1);
            if (between && m_fullZoomIn.ok()) m_fullZoomIn.Call(weapon, [](std::uint8_t*, const Param&) {});
            m_appliedLevel = level;
            m_fullZoomDone = level != 2;
        }
        if (level >= 1) SetZoomFov(weapon, w, level);
        if (level == 2 && !m_fullZoomDone && m_scope.Blend(now) >= 1)
        {
            m_fullZoomDone = true;
            if (m_fullZoomIn.ok()) m_fullZoomIn.Call(weapon, [](std::uint8_t*, const Param&) {});
        }
    }

    void CsFeel::HideCrosshair(UObject* player, bool hidden)
    {
        UObject* hud = player ? GetObject(player, STR("MyHUD")) : nullptr;
        if (!hud) return;
        if (hud->GetClassPrivate() != m_hudClass)
        {
            m_hudClass = hud->GetClassPrivate();
            m_hudTexture.Bind(m_hudClass, "Crosshair.CrosshairTexture");
        }
        if (!m_hudTexture.ok() || m_hudTexture.kind() != Kind::Object) return;
        auto* slot = reinterpret_cast<UObject**>(const_cast<std::uint8_t*>(m_hudTexture.At(hud)));
        if (hidden)
        {
            // Every frame: the game may cache it again (a weapon change).
            if (*slot)
            {
                m_crosshairTexture = FWeakObjectPtr(*slot);
                *slot = nullptr;
            }
            m_crosshairHidden = true;
            return;
        }
        if (!m_crosshairHidden) return;
        m_crosshairHidden = false;
        if (UObject* texture = m_crosshairTexture.Get(); texture && !*slot && InObjectArray(texture)) *slot = texture;
    }

    void CsFeel::HideWeapon(UObject* character, UObject* handler, bool hidden)
    {
        if (hidden == m_weaponHidden || !character || !handler) return;
        UObject* view = GetObject(character, STR("ViewModel_Native"));
        UObject* current = nullptr;
        Call(handler, STR("/Script/GameSkillsTrainer.WeaponHandler:GetCurrentWeapon"), {}, &current);
        if (!view || !current) return;
        Call(view, STR("/Script/GameSkillsTrainer.FPSPlayer_WeaponComponentActor:UpdateViewModel"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("bHideWeapon")) WriteBoolParam(v, p, hidden);
            else if (n == STR("Weapon")) WriteObject(v, current);
        });
        m_weaponHidden = hidden;
    }

    // --------------------------------------------------------------- speed

    // Each max speed as the game last set it, times the weapon's CS speed. A value that isn't the one
    // written here is the game's own (a walk or crouch ability, a respawn): it becomes the new base.
    void CsFeel::Speed(double now, UObject* character, const cs::WeaponFeel* w, bool scoped, const std::filesystem::path& root)
    {
        UObject* movement = character && m_movement.ok() ? m_movement.Object(character) : nullptr;
        if (!movement || !Alive(movement)) return;
        if (m_speedBody.Get() != character)
        {
            m_speedBody = FWeakObjectPtr(character);
            m_speeds = {{STR("MaxWalkSpeed")}, {STR("MaxWalkSpeedCrouched")}, {STR("MaxSpeed")}};
        }
        const double share = cs::SpeedShare(w, scoped);
        double walkBase = 0;
        for (SpeedField& f : m_speeds)
        {
            const auto value = GetFloat(movement, f.name);
            if (!value || !std::isfinite(*value) || *value <= 0) continue;
            if (!f.have || *value != f.written)
            {
                f.game = *value;
                f.have = true;
            }
            const float want = static_cast<float>(f.game * share);
            if (*value != want) SetFloat(movement, f.name, want);
            f.written = want;
            if (std::wstring(f.name) == STR("MaxWalkSpeed")) walkBase = f.game;
        }
        const std::string key = (w ? std::string(w->id) : "none") + (scoped ? " scoped" : "");
        if (key != m_speedLogged && walkBase > 0)
        {
            m_speedLogged = key;
            Log("cs feel: speed with " + key + ": x" + Fixed(share, 2) + " (run " + std::to_string(std::lround(walkBase)) + " -> " + std::to_string(std::lround(walkBase * share)) + " cm/s)");
        }
        m_speedShare = share;
        // AimModSteam's bots walk at the unscaled speed times their own weapon's share.
        if (walkBase > 0 && now >= m_nextMovementFile)
        {
            m_nextMovementFile = now + 1.0;
            const auto file = root / L"cs-movement.tsv", temp = root / L"cs-movement.tsv.tmp";
            {
                std::ofstream out(temp, std::ios::binary | std::ios::trunc);
                out << "AIMMOD_CSMOVE_1\t" << UnixMs() << "\t" << std::lround(walkBase) << "\t" << Fixed(share, 3) << "\n";
            }
            MoveFileExW(temp.c_str(), file.c_str(), MOVEFILE_REPLACE_EXISTING);
        }
    }

    void CsFeel::RestoreSpeed(UObject* character)
    {
        UObject* movement = character && m_movement.ok() && m_speedBody.Get() == character ? m_movement.Object(character) : nullptr;
        if (movement && Alive(movement))
            for (const SpeedField& f : m_speeds)
                if (const auto value = GetFloat(movement, f.name); f.have && value && *value == f.written) SetFloat(movement, f.name, static_cast<float>(f.game));
        m_speeds.clear();
        m_speedBody = FWeakObjectPtr();
        m_speedLogged.clear();
    }

    // ---------------------------------------------------------------- tick

    void CsFeel::Tick(double now, UObject* player, UObject* character, UObject* handler, int hand, const cs::Loadout& loadout, const std::optional<RoundState::Feel>& feel,
                      std::uint64_t nextShot, const std::filesystem::path& root)
    {
        if (!Bind() || !player || !character || !handler) return;
        const double dt = m_last < 0 ? 0 : std::clamp(now - m_last, 0.0, 0.25);
        m_last = now;
        m_engaged = true;
        m_player = FWeakObjectPtr(player);
        if (feel)
        {
            m_crosshair = feel->crosshair;
            m_zoomMode = feel->zoom;
        }
        UObject* weapon = nullptr;
        Call(handler, STR("/Script/GameSkillsTrainer.WeaponHandler:GetCurrentWeapon"), {}, &weapon);
        const cs::WeaponFeel* w = hand >= 0 && loadout.Has(hand) ? cs::FeelByProfile(loadout.names[static_cast<std::size_t>(hand)]) : nullptr;
        if (weapon != m_weapon.Get() || hand != m_hand || character != m_character.Get() || w != m_feel)
        {
            // Another weapon, slot or body: fresh accuracy, out of the scope, the old weapon's zoom undone.
            if (UObject* old = m_weapon.Get(); old && Alive(old) && m_feel && m_feel->zoomLevels > 0 && m_fovBase > 0) SetFloat(old, STR("FullZoomUnscaledFOV"), static_cast<float>(m_fovBase));
            m_scope.Out();
            m_accuracy.Reset();
            m_weapon = FWeakObjectPtr(weapon);
            m_character = FWeakObjectPtr(character);
            m_hand = hand;
            m_feel = w;
            m_fovBase = 0;
            m_sensBase = -1;
            m_appliedLevel = 0;
            m_fullZoomDone = false;
            m_armed = {};
        }
        Motion(character, m_motion);
        const bool scopeWeapon = w && w->zoomLevels > 0 && m_zoomMode != "off" && weapon;
        if (scopeWeapon) Zoom(now, player, handler, weapon, *w, feel ? feel->adsSensitivity : 1);
        else
        {
            m_scope.Out();
            if (UObject* blocked = m_blockedHandler.Get(); blocked && Alive(blocked) && !m_blockGivenUp) SetBool(blocked, STR("BlockADS"), m_blockBefore);
            m_blockedHandler = FWeakObjectPtr();
        }
        const bool scoped = m_scope.level() > 0;
        const double blend = m_scope.Blend(now);
        if (w) m_accuracy.Tick(dt, *w, m_motion, blend);
        if (w && w->Spreads() && feel && weapon && Alive(weapon)) Arm(now, player, weapon, *w, nextShot, feel->salt);
        else m_armed = {};
        HideCrosshair(player, m_crosshair || scoped);
        HideWeapon(character, handler, scoped);
        Speed(now, character, w, scoped, root);
    }

    void CsFeel::Sights(double now, UObject* widget)
    {
        if (now < m_nextSights) return;
        m_nextSights = now + 1.0 / 60;
        std::string json;
        UObject* player = m_player.Get();
        if (m_engaged && player)
        {
            UObject* camera = m_b.cameraManager.Object(player);
            const double fov = camera ? m_b.cameraFov.Number(camera).value_or(90) : 90;
            const double blend = m_scope.Blend(now);
            const double cone = m_feel ? m_accuracy.settled() + m_feel->spread : 0;
            json = "{\"on\":1,\"lvl\":" + std::to_string(m_scope.level()) + ",\"in\":" + Fixed(blend, 2) + ",\"blur\":" + Fixed(m_feel ? cs::ScopeBlur(*m_feel, m_accuracy.settled(), blend) : 0, 2) +
                   ",\"gap\":" + Fixed(cs::CrosshairGap(cone, fov), 4) + ",\"xh\":" + (m_crosshair && m_scope.level() == 0 ? "1" : "0") + "}";
        }
        else if (!m_sightsSent.empty()) json = "{\"on\":0}";
        if (json.empty() || json == m_sightsSent || !widget) return;
        if (!m_sightsBound)
        {
            m_sightsBound = true;
            m_createEvent.BindPath(STR("/Script/CohtmlPlugin.CohtmlWidget:CreateJSEvent"), Shape::Object);
            m_addString.BindPath(STR("/Script/CohtmlPlugin.CohtmlJSEvent:AddString"), Shape::Command);
            m_trigger.BindPath(STR("/Script/CohtmlPlugin.CohtmlWidget:TriggerJSEvent"), Shape::Command);
            m_ready.BindPath(STR("/Script/CohtmlPlugin.CohtmlWidget:IsReadyForBindings"), Shape::Bool);
        }
        if (!m_createEvent.ok() || !m_addString.ok() || !m_trigger.ok() || (m_ready.ok() && !m_ready.Bool(widget).value_or(false))) return;
        UObject* event = m_createEvent.Object(widget);
        if (!event) return;
        m_addString.Call(event, [&](std::uint8_t* value, const Param& p) {
            if (p.kind == Kind::String) game::WriteString(value, json);
        });
        m_trigger.Call(widget, [&](std::uint8_t* value, const Param& p) {
            if (p.kind == Kind::String) game::WriteString(value, "AimModScope");
            else if (p.kind == Kind::Object) std::memcpy(value, &event, sizeof event);
        });
        m_sightsSent = json == "{\"on\":0}" ? std::string() : json;
    }

    void CsFeel::Release(UObject* player, UObject* character, const char* why)
    {
        if (!m_engaged) return;
        UObject* handler = m_blockedHandler.Get();
        if (handler && Alive(handler))
        {
            if (m_scope.level() > 0 && m_zoomOut.ok()) m_zoomOut.Call(handler, [](std::uint8_t* v, const Param& p) { if (p.boolProperty) p.boolProperty->SetPropertyValue(v, true); });
            if (!m_blockGivenUp) SetBool(handler, STR("BlockADS"), m_blockBefore);
        }
        if (UObject* weapon = m_weapon.Get(); weapon && Alive(weapon))
        {
            if (m_fovBase > 0) SetFloat(weapon, STR("FullZoomUnscaledFOV"), static_cast<float>(m_fovBase));
            if (m_calib == Calib::Ready) WritePbs(weapon, 0, 0);
        }
        if (player) HideCrosshair(player, false);
        if (character && handler && Alive(handler)) HideWeapon(character, handler, false);
        RestoreSpeed(character);
        m_blockedHandler = FWeakObjectPtr();
        m_weapon = FWeakObjectPtr();
        m_character = FWeakObjectPtr();
        m_scope.Out();
        m_scope.Tick(0, false);
        m_accuracy.Reset();
        m_armed = {};
        m_feel = nullptr;
        m_hand = -1;
        m_last = -1;
        m_engaged = false;
        m_weaponHidden = false;
        m_fovBase = 0;
        m_sensBase = -1;
        m_appliedLevel = 0;
        Log(std::string("cs feel: released (") + why + ")");
    }
} // namespace aimmod
