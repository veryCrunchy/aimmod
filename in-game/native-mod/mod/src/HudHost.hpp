#pragma once
// In-game HUD view (DESIGN.md "Overlay host", phase 2): AimModCore hosts the
// service's overlay scenes page (<workspace>/overlay?surface=game) in a Gameface
// view of its own, in place of AimModNativeUI's LiveHUD.lua. The view never takes
// input (hit-test invisible, not focusable, no Gameface input), fills the screen
// through a canvas slot of its own host widget, sits below the notice layer, and
// tells the page when it is hidden (AimModVisibility) so it stops polling.
// Game thread only.
#include "GameBindings.hpp"
#include "Output.hpp"
#include "World.hpp"

#include <aimmod/Overlay.hpp>

#include <Unreal/FWeakObjectPtr.hpp>

#include <string>

namespace aimmod
{
    class HudHost
    {
    public:
        HudHost(game::Scene& scene, Output& output) : m_scene(scene), m_output(output) {}
        void Tick(double now, bool replayActive, bool inChallenge);
        void Shutdown();
        // The switch selects AimModCore and its view exists: LiveHUD.lua stands down.
        bool ready() const { return m_ready; }

    private:
        using UObject = game::UObject;
        bool Create(UObject* player, const std::string& url);
        void Remove(const char* why);
        void Show(bool visible, double now);
        void Deliver(double now);

        game::Scene& m_scene;
        Output& m_output;
        bool m_bound{}, m_ready{}, m_warned{}, m_visible{};
        game::Getter m_getPauseMenu, m_isVisible, m_isInViewport;
        RC::Unreal::FWeakObjectPtr m_host, m_view, m_widget, m_player;
        std::string m_url;
        int m_failures{}, m_deliveries{};
        double m_nextTick{}, m_nextCreate{}, m_nextCheck{}, m_nextDelivery{};
    };
} // namespace aimmod
