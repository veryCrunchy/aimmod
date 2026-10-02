#include "WorldTags.hpp"

#include "Log.hpp"
#include "Output.hpp"

#include <aimmod/MatchPlay.hpp>

#include <Unreal/CoreUObject/UObject/UnrealType.hpp>
#include <Unreal/UObject.hpp>

#include <Windows.h>

#include <cmath>
#include <cstring>
#include <fstream>
#include <iterator>

namespace aimmod
{
    using namespace game;

    namespace
    {
        constexpr double PushInterval = 1.0 / 60.0, ReadInterval = 0.25;
        constexpr std::uint64_t RosterMaxAge = 5ull * 10000000ull; // 5 s in FILETIME units
        constexpr double HeadAbove = 24.0;                       // cm above the capsule top
        constexpr double AimRange = 10000.0;                     // cm: names under the crosshair up to 100 m
        constexpr double DegToRad = 3.14159265358979323846 / 180.0;
        void WriteFloats(std::uint8_t* value, const double v[3])
        {
            const float f[3] = {static_cast<float>(v[0]), static_cast<float>(v[1]), static_cast<float>(v[2])};
            std::memcpy(value, f, sizeof f);
        }
    } // namespace

    bool WorldTags::Bind()
    {
        if (m_bound) return !m_disabled;
        m_bound = true;
        m_project.BindPath(STR("/Script/Engine.PlayerController:ProjectWorldLocationToScreen"), Shape::Command);
        m_viewportSize.BindPath(STR("/Script/Engine.PlayerController:GetViewportSize"), Shape::Command);
        m_lineOfSight.BindPath(STR("/Script/Engine.Controller:LineOfSightTo"), Shape::Command);
        m_createEvent.BindPath(STR("/Script/CohtmlPlugin.CohtmlWidget:CreateJSEvent"), Shape::Object);
        m_addString.BindPath(STR("/Script/CohtmlPlugin.CohtmlJSEvent:AddString"), Shape::Command);
        m_trigger.BindPath(STR("/Script/CohtmlPlugin.CohtmlWidget:TriggerJSEvent"), Shape::Command);
        m_disabled = !m_project.ok() || !m_viewportSize.ok() || !m_createEvent.ok() || !m_addString.ok() || !m_trigger.ok();
        Log(std::string("world tags: ") + (m_disabled ? "unavailable" : "ready") + " (project " + (m_project.ok() ? "ok" : "missing") + ", viewport " +
            (m_viewportSize.ok() ? "ok" : "missing") + ", line of sight " + (m_lineOfSight.ok() ? "ok" : "missing: no enemy names") + ", js event " +
            (m_createEvent.ok() && m_addString.ok() && m_trigger.ok() ? "ok" : "missing") + ")");
        return !m_disabled;
    }

    // world-tags.tsv from the service, re-read when it changes; stale (5 s) or missing: nobody.
    void WorldTags::ReadRoster(double now)
    {
        if (now < m_nextRead) return;
        m_nextRead = now + ReadInterval;
        const auto path = m_output.root() / L"world-tags.tsv";
        WIN32_FILE_ATTRIBUTE_DATA data{};
        std::uint64_t stamp = 0;
        if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &data))
            stamp = (static_cast<std::uint64_t>(data.ftLastWriteTime.dwHighDateTime) << 32) | data.ftLastWriteTime.dwLowDateTime;
        FILETIME nowFile{};
        GetSystemTimeAsFileTime(&nowFile);
        const std::uint64_t nowStamp = (static_cast<std::uint64_t>(nowFile.dwHighDateTime) << 32) | nowFile.dwLowDateTime;
        if (stamp == 0 || nowStamp - stamp >= RosterMaxAge || data.nFileSizeLow > worldtags::MaxBytes)
        {
            m_roster.clear();
            m_rosterStamp = 0;
            return;
        }
        if (stamp == m_rosterStamp) return;
        m_rosterStamp = stamp;
        std::ifstream in(path, std::ios::binary);
        const std::string text((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
        if (auto roster = worldtags::Parse(text)) m_roster = std::move(*roster);
        else m_roster.clear();
    }

    void WorldTags::Push(UObject* widget, const std::string& json)
    {
        UObject* event = m_createEvent.Object(widget);
        if (!event) return;
        m_addString.Call(event, [&](std::uint8_t* value, const Param& p) { if (p.kind == Kind::String) WriteString(value, json); });
        m_trigger.Call(widget, [&](std::uint8_t* value, const Param& p) {
            if (p.kind == Kind::String) WriteString(value, "AimModTags");
            else if (p.kind == Kind::Object) std::memcpy(value, &event, sizeof event);
        });
    }

    void WorldTags::Tick(double now, UObject* player, const double eye[3], const double rotation[3], const std::vector<Avatar>& avatars, UObject* widget)
    {
        if (now < m_nextPush) return;
        m_nextPush = now + PushInterval;
        ReadRoster(now);
        const bool any = widget && player && !m_roster.empty() && !avatars.empty();
        if (!any)
        {
            // Clear once when tags go away (or the view went: nothing to clear).
            if (m_shownLast && widget && Bind()) Push(widget, worldtags::Json({}));
            m_shownLast = false;
            m_lastJson.clear();
            return;
        }
        if (!Bind()) return;
        std::int32_t width = 0, height = 0;
        m_viewportSize.Call(player, [](std::uint8_t*, const Param&) {}, [&](const std::uint8_t* buffer, const std::vector<Param>& params) {
            int i = 0;
            for (const Param& p : params)
                if (p.out && p.kind == Kind::Int32) std::memcpy(i++ == 0 ? &width : &height, buffer + p.offset, 4);
        });
        if (width <= 0 || height <= 0) return;
        const double pitch = rotation[0] * DegToRad, yaw = rotation[1] * DegToRad;
        const double dir[3] = {std::cos(pitch) * std::cos(yaw), std::cos(pitch) * std::sin(yaw), std::sin(pitch)};
        std::vector<worldtags::ScreenTag> tags;
        for (const Avatar& a : avatars)
        {
            const auto it = m_roster.find(a.stream);
            if (it == m_roster.end()) continue;
            const worldtags::Who& who = it->second;
            const auto hit = who.friendly ? std::nullopt : RayCapsule(eye, dir, a.centre, a.radius, a.half);
            const bool aimed = hit && *hit <= AimRange;
            bool visible = false;
            if (aimed && m_lineOfSight.ok())
                m_lineOfSight.Call(
                    player,
                    [&](std::uint8_t* value, const Param& p) {
                        if (p.kind == Kind::Object) std::memcpy(value, &a.actor, sizeof a.actor);
                        else if (p.kind == Kind::Vector) WriteFloats(value, eye);
                    },
                    [&](const std::uint8_t* buffer, const std::vector<Param>& params) {
                        for (const Param& p : params)
                            if (p.ret && p.boolProperty) visible = p.boolProperty->GetPropertyValue(const_cast<std::uint8_t*>(buffer) + p.offset);
                    });
            if (!worldtags::Shown(who, aimed, visible)) continue;
            const double head[3] = {a.centre[0], a.centre[1], a.centre[2] + a.half + HeadAbove};
            bool onScreen = false;
            float screen[2]{};
            m_project.Call(
                player,
                [&](std::uint8_t* value, const Param& p) {
                    if (p.kind == Kind::Vector) WriteFloats(value, head);
                    else if (p.kind == Kind::Bool && p.boolProperty) p.boolProperty->SetPropertyValue(value, false); // viewport, not player-relative
                },
                [&](const std::uint8_t* buffer, const std::vector<Param>& params) {
                    for (const Param& p : params)
                    {
                        if (p.ret && p.boolProperty) onScreen = p.boolProperty->GetPropertyValue(const_cast<std::uint8_t*>(buffer) + p.offset);
                        else if (p.out && p.structType && p.size >= 8) std::memcpy(screen, buffer + p.offset, 8);
                    }
                });
            if (!onScreen) continue;
            const double dx = head[0] - eye[0], dy = head[1] - eye[1], dz = head[2] - eye[2];
            tags.push_back({who.name, who.team, who.friendly, who.alive, aimed, screen[0] / width, screen[1] / height, std::sqrt(dx * dx + dy * dy + dz * dz) / 100.0, who.gear});
        }
        std::string json = worldtags::Json(tags);
        if (json == m_lastJson && tags.empty()) return;
        m_lastJson = json;
        Push(widget, json);
        m_shownLast = !tags.empty();
    }
} // namespace aimmod
