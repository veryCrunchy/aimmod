// Overlay host checks (included by CoreTests.cpp): notice parsing, the switch,
// and the input state machine that decides visibility, clicks, input and focus.
#include <aimmod/Overlay.hpp>

namespace overlay_checks
{
    using namespace aimmod::overlay;

    inline void Run()
    {
        // Notice parsing: only AimMod notices, only the fields the host acts on.
        auto toast = ParseNotice(R"({"version":1,"active":true,"layout":"toast","interactive":true,"actions":[{"label":"Retry"}]})");
        CHECK(toast && toast->content && !toast->full && toast->interactive && !toast->cursor, "toast notice with buttons");
        auto buy = ParseNotice(R"({"version":1,"active":false,"layout":"full","interactive":true,"cursor":true,"cs":{"phase":"freeze","buyOpen":true}})");
        CHECK(buy && buy->content && buy->full && buy->cursor && !buy->boardHeld, "CS buy menu");
        auto board = ParseNotice(R"({"version":1,"active":false,"layout":"full","board":{"rows":[]},"boardFull":{"rows":[]}})");
        CHECK(board && board->content && board->boardHeld && !board->interactive, "held scoreboard");
        auto empty = ParseNotice(R"({"version":1,"active":false})");
        CHECK(empty && !empty->content, "nothing to show");
        CHECK(!ParseNotice(R"({"version":2,"active":true})") && !ParseNotice("") && !ParseNotice("{\"version\":1,") &&
                  !ParseNotice(std::string(MaxNoticeBytes + 1, ' ')),
              "other versions, malformed or oversized notices are ignored");
        // The notify page URL comes from the overlay URL: loopback and a hex capability only.
        CHECK(NoticeUrl("http://127.0.0.1:54321/abc123/overlay?surface=game\r\n") == std::optional<std::string>("http://127.0.0.1:54321/abc123/notify"),
              "notify URL");
        CHECK(!NoticeUrl("http://example.com:1/abc/overlay?surface=game") && !NoticeUrl("http://127.0.0.1:1/../overlay?surface=game") &&
                  !NoticeUrl("http://127.0.0.1:x/abc/overlay?surface=game") && !NoticeUrl("http://127.0.0.1:1/abc/overlay"),
              "only the service's own page");
        // The switch: native unless ui-host.tsv says lua.
        CHECK(ParseUiHost("") == Host::Native && ParseUiHost("AIMMOD_UIHOST_1\nnotice\tnative\n") == Host::Native, "native by default");
        CHECK(ParseUiHost("AIMMOD_UIHOST_1\r\nnotice\tlua\r\n") == Host::Lua && ParseUiHost("notice\tlua\n") == Host::Native, "lua fallback needs the header");
        CHECK(PanelOpen("AIMMOD_PANEL_1\t1\t1000\n", 1002) && !PanelOpen("AIMMOD_PANEL_1\t1\t1000\n", 1004) && !PanelOpen("AIMMOD_PANEL_1\t0\t1000\n", 1000) &&
                  !PanelOpen("x\t1\t1000", 1000),
              "panel open while fresh");

        // The input state machine.
        aimmod::overlay::Machine m;
        aimmod::overlay::Frame f;
        f.haveView = true;
        f.notice = buy;
        aimmod::overlay::Plan p = m.Next(f);
        CHECK(p.visible && p.full && p.holdMenuInput && p.enterMenuInput && p.clickable && p.zOrder >= 20000, "buy menu opens: UI-only input held, clickable, on top");
        CHECK(p.viewVisibility == Visibility::SelfHitTestInvisible && p.widgetVisibility == Visibility::Visible, "only the Gameface widget takes clicks");
        p = m.Next(f);
        CHECK(p.holdMenuInput && !p.enterMenuInput && !p.releaseMenuInput, "held, not re-entered, while open");
        // Escape: KovaaK's pause menu opens over the buy menu; it closes again and the hold continues.
        f.pauseMenuVisible = true;
        p = m.Next(f);
        CHECK(p.hidePauseMenu && p.holdMenuInput && !p.releaseMenuInput && !p.forgetMenuInput, "pause menu over the buy menu closes, input stays ours");
        f.pauseMenuVisible = false;
        // The buy menu closes (Escape or B): input goes back to the game once.
        aimmod::overlay::Notice closed = *buy;
        closed.cursor = false;
        closed.interactive = false;
        f.notice = closed;
        p = m.Next(f);
        CHECK(p.releaseMenuInput && !p.holdMenuInput && !p.clickable && p.viewVisibility == Visibility::HitTestInvisible,
              "closing gives input back; the HUD stays click-through");
        p = m.Next(f);
        CHECK(!p.releaseMenuInput && !p.enterMenuInput, "released once");
        // Escape just closed it (swallowMenu): the pause menu it opened closes, and the game keeps its input.
        closed.swallowMenu = true;
        f.notice = closed;
        f.pauseMenuVisible = true;
        p = m.Next(f);
        CHECK(p.hidePauseMenu && p.releaseMenuInput && !p.holdMenuInput, "Escape's pause menu closes after the buy menu did");
        // A deliberate Escape later (no buy menu, no swallow): KovaaK's menu is the player's.
        closed.swallowMenu = false;
        f.notice = closed;
        f.pauseMenuVisible = false;
        m.Next(f);
        f.pauseMenuVisible = true;
        p = m.Next(f);
        CHECK(!p.hidePauseMenu && !p.holdMenuInput && !p.enterMenuInput, "a deliberate pause menu is left alone");
        // In KovaaK's menus a notice with buttons takes clicks (the menu shows the cursor); no input changes.
        f.notice = toast;
        p = m.Next(f);
        CHECK(p.visible && p.clickable && !p.full && !p.holdMenuInput && !p.enterMenuInput && !p.releaseMenuInput, "menu notice clickable, input untouched");
        // In game without a cursor the same notice is click-through (the hotkey answers it).
        f.pauseMenuVisible = false;
        p = m.Next(f);
        CHECK(p.visible && !p.clickable && p.viewVisibility == Visibility::HitTestInvisible, "in game without a cursor: click-through");
        // The AimMod panel shows the same things: the layer hides; a held buy menu is handed over, not released.
        aimmod::overlay::Machine m2;
        aimmod::overlay::Frame g;
        g.haveView = true;
        g.notice = buy;
        m2.Next(g);
        g.panelOpen = true;
        p = m2.Next(g);
        CHECK(!p.visible && p.forgetMenuInput && !p.releaseMenuInput && p.viewVisibility == Visibility::Collapsed, "panel open: hidden, input left to the panel");
        // Replay playback owns the screen.
        aimmod::overlay::Machine m3;
        aimmod::overlay::Frame r;
        r.haveView = true;
        r.notice = buy;
        r.replay = true;
        p = m3.Next(r);
        CHECK(!p.visible && !p.enterMenuInput, "never during a replay");
        // The scoreboard is display-only: focus back to the viewport, never input or clicks.
        aimmod::overlay::Machine m4;
        aimmod::overlay::Frame t;
        t.haveView = true;
        t.notice = board;
        p = m4.Next(t);
        CHECK(p.focusViewport && !p.clickable && !p.holdMenuInput && !p.enterMenuInput, "held scoreboard keeps game focus only");
        t.pauseMenuVisible = true;
        p = m4.Next(t);
        CHECK(!p.focusViewport, "never steals focus from KovaaK's menus");
        // No view yet: nothing is clickable, the hold still starts (applied once the view exists).
        aimmod::overlay::Machine m5;
        aimmod::overlay::Frame v;
        v.notice = buy;
        p = m5.Next(v);
        CHECK(p.holdMenuInput && !p.clickable, "no view: hold, not clickable yet");
        // One log line per change.
        aimmod::overlay::Machine m6;
        aimmod::overlay::Plan before{}, after = m6.Next(f);
        CHECK(Describe(before, after).find("shown") != std::string::npos && Describe(after, after).empty(), "describe changes only");
    }
} // namespace overlay_checks
