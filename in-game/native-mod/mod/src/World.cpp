#include "World.hpp"

#include <Unreal/UClass.hpp>
#include <Unreal/UObject.hpp>

#include <deque>

namespace aimmod::game
{
    namespace
    {
        // Few distinct classes are ever seen (player, bot types, weapons);
        // deque keeps references stable while it grows.
        std::deque<ClassInfo> g_classes;
        constexpr std::size_t MaxClasses = 256;
        const ClassInfo g_empty{};
    } // namespace

    const ClassInfo& Describe(UObject* object)
    {
        if (!object) return g_empty;
        UClass* cls = object->GetClassPrivate();
        for (const ClassInfo& info : g_classes)
            if (info.cls == cls) return info;
        if (g_classes.size() >= MaxClasses) return g_empty;
        ClassInfo& info = g_classes.emplace_back();
        info.cls = cls;
        info.healthPercent.BindName(cls, STR("GetCurrentHealthPercent"), Shape::Number);
        info.profileName.BindName(cls, STR("GetCharacterProfileName"), Shape::String);
        info.killCount.BindName(cls, STR("GetKillCount"), Shape::Number);
        info.lastTimeToKill.BindName(cls, STR("GetLastTTK"), Shape::Timespan);
        info.damageDone.Bind(cls, STR("DamageDone"));
        info.weaponHandler.Bind(cls, STR("WeaponHandler"));
        info.playbackComponent.Bind(cls, STR("PlaybackComponent"));
        info.weapons.BindName(cls, STR("GetWeapons"), Shape::ObjectArray);
        info.shotsFired.Bind(cls, STR("ShotsFiredThisSession"));
        info.shotsHit.Bind(cls, STR("ShotsHitThisSession"));
        info.weaponDamage.Bind(cls, STR("DamageDoneThisSession"));
        return info;
    }

    void ClearClassCache() { g_classes.clear(); }

    LocalCounters ReadLocalCounters(UObject* character)
    {
        LocalCounters out;
        if (!character) return out;
        const ClassInfo& c = Describe(character);
        out.kills = c.killCount.Number(character);
        out.damage = c.damageDone.Number(character);
        if (c.lastTimeToKill.ok()) out.lastTimeToKill = c.lastTimeToKill.TimespanSeconds(character);
        UObject* handler = c.weaponHandler.Object(character);
        if (!handler) return out;
        const ClassInfo& h = Describe(handler);
        std::vector<UObject*> weapons;
        if (!h.weapons.Objects(handler, weapons, 32)) return out;
        double shots = 0, hits = 0;
        int counted = 0;
        UObject* previous[32]{};
        for (UObject* weapon : weapons)
        {
            bool duplicate = false;
            for (int i = 0; i < counted; ++i) duplicate |= previous[i] == weapon;
            if (duplicate) continue;
            const ClassInfo& w = Describe(weapon);
            auto fired = w.shotsFired.Number(weapon);
            auto hit = w.shotsHit.Number(weapon);
            if (!fired || !hit || *fired < 0 || *hit < 0) return out; // counters unavailable: no partial sums
            shots += *fired;
            hits += *hit;
            previous[counted++] = weapon;
        }
        if (counted > 0)
        {
            out.shots = shots;
            out.hits = hits;
        }
        return out;
    }

    bool ReadWeaponCounters(UObject* character, std::vector<WeaponCount>& out)
    {
        out.clear();
        if (!character) return false;
        UObject* handler = Describe(character).weaponHandler.Object(character);
        if (!handler) return false;
        std::vector<UObject*> weapons;
        if (!Describe(handler).weapons.Objects(handler, weapons, 32)) return false;
        for (std::size_t i = 0; i < weapons.size(); ++i)
        {
            UObject* weapon = weapons[i];
            bool duplicate = false;
            for (const WeaponCount& c : out) duplicate |= c.weapon == weapon;
            if (duplicate || !weapon) continue;
            const ClassInfo& w = Describe(weapon);
            auto fired = w.shotsFired.Number(weapon);
            auto hit = w.shotsHit.Number(weapon);
            if (!fired || !hit || *fired < 0 || *hit < 0)
            {
                out.clear();
                return false;
            }
            const auto damage = w.weaponDamage.ok() ? w.weaponDamage.Number(weapon) : std::nullopt;
            out.push_back({weapon, static_cast<int>(i), *fired, *hit, damage && *damage >= 0 ? *damage : -1});
        }
        return !out.empty();
    }
} // namespace aimmod::game
