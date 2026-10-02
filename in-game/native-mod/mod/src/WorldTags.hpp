#pragma once
// World tags (DESIGN.md "World tags"): teammates' names over their heads, through walls like
// CS, and an enemy's name only while it is under the crosshair and in line of sight. The
// service's world-tags.tsv says who each avatar stream is; avatars.tsv (AimModSteam) says which
// actor is which stream; every frame the avatars are projected with the local camera
// (PlayerController:ProjectWorldLocationToScreen) and pushed to the notice layer's Gameface
// view as one JS event ("AimModTags", a small JSON string). Game thread only.
//
// The log says where the chain stands: once when tags first have something to work with, then
// every 10 s while it changes (every 60 s while it doesn't): the characters in the world, the
// avatars matched to a stream (avatars.tsv), to a roster row (world-tags.tsv), shown, on screen,
// and the events pushed to the notice view (or skipped while it can't take them yet).
#include "GameBindings.hpp"

#include <aimmod/WorldTags.hpp>

#include <cstdint>
#include <string>
#include <vector>

namespace aimmod
{
    class Output;

    class WorldTags
    {
    public:
        explicit WorldTags(Output& output) : m_output(output) {}
        struct Avatar
        {
            game::UObject* actor{};
            std::string stream;
            double centre[3]{};
            double radius{}, half{};
        };
        // What the caller saw this frame: other characters in the world, and AimModSteam's stream rows.
        struct Census
        {
            int characters{}, streams{};
            bool camera{};
        };
        // eye and rotation (pitch, yaw, roll): the local camera. widget: the overlay host's Gameface
        // widget (null while it has none: nothing is drawn).
        void Tick(double now, game::UObject* player, const double eye[3], const double rotation[3], const std::vector<Avatar>& avatars, game::UObject* widget,
                  const Census& census = {});

    private:
        bool Bind();
        bool Push(game::UObject* widget, const std::string& json);
        void ReadRoster(double now);
        void Report(double now, game::UObject* widget);

        Output& m_output;
        bool m_bound{}, m_disabled{};
        game::Getter m_project, m_viewportSize, m_lineOfSight, m_createEvent, m_addString, m_trigger, m_ready;
        worldtags::Roster m_roster;
        double m_nextRead{}, m_nextPush{};
        std::uint64_t m_rosterStamp{};
        bool m_shownLast{};
        std::string m_lastJson;
        // Diagnostics: the roster file's state, and counts since the last report.
        std::string m_rosterState{"not read yet"};
        struct Counts
        {
            long frames{}, characters{}, streamed{}, matched{}, shown{}, projected{}, pushed{}, tags{}, notReady{}, noView{}, noCamera{};
            int maxStreams{};
        } m_counts;
        double m_nextReport{};
        bool m_reported{};
        std::string m_lastReport;
        double m_lastReportAt{-1e9};
    };
} // namespace aimmod
