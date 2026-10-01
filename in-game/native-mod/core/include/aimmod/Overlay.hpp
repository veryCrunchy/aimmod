#pragma once
// AimMod's in-game overlay host, the engine-independent part (DESIGN.md
// "Overlay host"). AimModCore shows the multiplayer notice layer (notices,
// mode HUDs, standings, the CS buy menu) in one Gameface view of its own and
// owns the input around it: the notify page itself (ui/notify.html) is the
// service's, unchanged. This decides, every frame, what the host does; the
// mod applies it.
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

namespace aimmod::overlay
{
    // multiplayer-notify.json, the fields the host acts on.
    struct Notice
    {
        bool content{};     // a notice, badge, mode HUD or standings: the layer shows
        bool full{};        // layout "full" (HUDs at the screen edges) or the top-centre toast
        bool interactive{}; // something on it takes clicks (buttons, the buy menu)
        bool cursor{};      // the CS buy menu is open: it needs the mouse in game
        bool swallowMenu{}; // Escape just closed the buy menu: KovaaK's pause menu it opened closes again
        bool boardHeld{};   // the full scoreboard (its key is held)
    };
    inline constexpr std::size_t MaxNoticeBytes = 16384;
    // nullopt: absent, too large or not an AimMod notice (the layer hides).
    std::optional<Notice> ParseNotice(std::string_view json);

    // The notify page URL from live-overlay-url.txt ("http://127.0.0.1:<port>/<cap>/overlay?surface=game").
    std::optional<std::string> NoticeUrl(std::string_view overlayUrlFile);

    // ui-host.tsv: who hosts the notice layer. "native" (default, also when the file is absent)
    // or "lua" (AimModNativeUI's Notify.lua, the fallback).
    //   AIMMOD_UIHOST_1\nnotice\tnative\n
    enum class Host { Native, Lua };
    Host ParseUiHost(std::string_view text);

    // aimmod-panel.tsv, written by AimModNativeUI's Menu.lua: the AimMod panel is on screen.
    //   AIMMOD_PANEL_1\t<0|1>\t<unix seconds>\n   (stale after 3 s: closed)
    bool PanelOpen(std::string_view text, std::int64_t nowUnix);

    // lua-notice.tsv, written by Notify.lua once a second while its own notice layer is on
    // screen: AIMMOD_LUANOTICE_1\t<unix seconds>\n (stale after 3 s). Two layers at once is a bug.
    bool LuaLayerActive(std::string_view text, std::int64_t nowUnix);

    enum class Visibility : std::uint8_t { Visible = 0, Collapsed = 1, Hidden = 2, HitTestInvisible = 3, SelfHitTestInvisible = 4 };

    // What the host sees this frame.
    struct Frame
    {
        std::optional<Notice> notice;
        bool panelOpen{};        // the AimMod panel shows the same things itself
        bool replay{};           // replay playback owns the screen and input
        bool pauseMenuVisible{}; // KovaaK's own menus are up (they own input and the cursor)
        bool gameCursor{};       // the game shows its cursor anyway
        bool haveView{};         // the Gameface view exists
    };
    // What the host does.
    struct Plan
    {
        bool visible{};
        bool full{};
        bool clickable{};       // view and Gameface widget hit-testable and taking input
        bool holdMenuInput{};   // UI-only focused on our widget, cursor on, fire blocked; re-asserted every frame
        bool enterMenuInput{};  // this frame: take it
        bool releaseMenuInput{};// this frame: give the game its input back (game-only, cursor off, fire unblocked)
        bool forgetMenuInput{}; // this frame: stop holding without touching input (KovaaK's menu or the panel took over)
        bool hidePauseMenu{};   // KovaaK's pause menu opened by the Escape that closed (or was over) the buy menu
        bool focusViewport{};   // scoreboard held in game: keyboard focus back to the game viewport
        Visibility viewVisibility{Visibility::Collapsed};
        Visibility widgetVisibility{Visibility::HitTestInvisible};
        std::int32_t zOrder{};
    };
    inline constexpr std::int32_t ZOrder = 20000; // above KovaaK's HUD and menus (5000-10000)

    class Machine
    {
    public:
        Plan Next(const Frame& frame);
        bool holding() const { return m_holding; }

    private:
        bool m_holding{};
        bool m_pauseWasVisible{};
    };

    // One "[AimModCore] overlay: ..." line for a change of plan (empty when nothing changed that matters).
    std::string Describe(const Plan& before, const Plan& after);
} // namespace aimmod::overlay
