#pragma once
// World tags (DESIGN.md "World tags"): teammates' names over their heads, through walls like
// CS, and an enemy's name only while it is under the crosshair and in line of sight. The
// service's world-tags.tsv says who each avatar stream is; avatars.tsv (AimModSteam) says which
// actor is which stream; every frame the avatars are projected with the local camera
// (PlayerController:ProjectWorldLocationToScreen) and pushed to the notice layer's Gameface
// view as one JS event ("AimModTags", a small JSON string). Game thread only.
#include "GameBindings.hpp"

#include <aimmod/WorldTags.hpp>

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
        // eye and rotation (pitch, yaw, roll): the local camera. widget: the overlay host's Gameface
        // widget (null while it has none: nothing is drawn).
        void Tick(double now, game::UObject* player, const double eye[3], const double rotation[3], const std::vector<Avatar>& avatars, game::UObject* widget);

    private:
        bool Bind();
        void Push(game::UObject* widget, const std::string& json);
        void ReadRoster(double now);

        Output& m_output;
        bool m_bound{}, m_disabled{};
        game::Getter m_project, m_viewportSize, m_lineOfSight, m_createEvent, m_addString, m_trigger;
        worldtags::Roster m_roster;
        double m_nextRead{}, m_nextPush{};
        std::uint64_t m_rosterStamp{};
        bool m_shownLast{};
        std::string m_lastJson;
    };
} // namespace aimmod
