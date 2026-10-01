#pragma once
// Overlay host (DESIGN.md "Overlay host"): AimModCore's own Gameface view for
// the multiplayer notice layer (ui/notify.html from the service, unchanged),
// and the input around it. It creates exactly one view (a KovaaK's
// MetaGraphWidget, the Gameface host AimMod's panel uses too), keeps typed weak
// references to it and its Gameface widget, adds it to the viewport above
// KovaaK's UI and applies overlay::Machine's plan every frame: visibility and
// hit-testing, the toast or full-screen layout, and while the CS buy menu is
// open UI-only input focused on our Gameface widget with the cursor on and
// fire blocked, re-asserted while held. Game thread only.
#include "GameBindings.hpp"
#include "Output.hpp"
#include "World.hpp"

#include <aimmod/Overlay.hpp>

#include <Unreal/FWeakObjectPtr.hpp>

#include <cstdint>
#include <string>

namespace aimmod
{
    class OverlayHost
    {
    public:
        OverlayHost(game::Scene& scene, Output& output) : m_scene(scene), m_output(output) {}
        void Tick(double now, bool replayActive);
        void Shutdown();
        // The host runs (switch native) and its view exists: AimModNativeUI's Notify.lua stands down.
        bool ready() const { return m_ready; }
        // The notice layer's Gameface widget while the host runs (world tags draw into it); else null.
        UObject* Gameface() const { return m_ready ? m_widget.Get() : nullptr; }

    private:
        using UObject = game::UObject;
        bool Bind();
        bool Create(UObject* player, const std::string& url, double now);
        void Remove(const char* why);
        void Apply(const overlay::Plan& plan, UObject* player, double now);
        void Layout(bool full);
        void EnterMenuInput(UObject* player);
        void ReleaseMenuInput(UObject* player);
        void Reassert(UObject* player, double now);
        void BlockFire(UObject* player, bool block);
        bool ShowCursor(UObject* player, bool on);
        bool UiOnly(UObject* player);
        bool GameOnly(UObject* player);
        void HidePauseMenu(UObject* player, UObject* menu);
        void ForwardPointer(double now);
        UObject* PauseMenu(UObject* player) const;
        bool CursorShown(UObject* player) const;
        std::string Name(UObject* object) const;

        game::Scene& m_scene;
        Output& m_output;
        bool m_bound{}, m_ready{}, m_warned{}, m_luaSeen{};
        game::Getter m_getPauseMenu, m_isVisible, m_isInViewport;
        game::Field m_showCursor, m_blockingAttack;
        RC::Unreal::FWeakObjectPtr m_view, m_widget, m_player, m_character;
        std::string m_url;
        overlay::Machine m_machine;
        overlay::Plan m_plan;
        bool m_layoutFull{}, m_layoutSet{}, m_fresh{};
        bool m_blocking{}, m_savedBlock{};
        int m_failures{}, m_resets{};
        double m_nextCreate{}, m_nextCheck{}, m_nextInput{}, m_nextFocus{}, m_escapeAt{-10.0}, m_nextPointer{};
        // Pointer fallback: the last position and button sent, and counts for the log.
        long m_pointerX{-1}, m_pointerY{-1};
        bool m_pointerDown{};
        int m_pointerMoves{}, m_pointerPresses{};
        double m_nextPointerLog{};
        std::uint64_t m_lastVersion{~0ull};
    };
} // namespace aimmod
