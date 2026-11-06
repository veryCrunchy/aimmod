// Overlay host checks (included by CoreTests.cpp): notice parsing, the switch,
// and the input state machine that decides visibility, clicks, input and focus.
#include <aimmod/Overlay.hpp>

namespace overlay_checks
{
    using namespace aimmod::overlay;

    inline Plan Next(Machine& machine, const aimmod::overlay::Frame& frame)
    {
        auto plan = machine.Next(frame);
        if (plan.enterMenuInput) machine.Acquired(true);
        return plan;
    }
    inline void Run()
    {
        CHECK(LeaseFresh(10000000, 20000000) && !LeaseFresh(10000000, 40000000) &&
              !LeaseFresh(0, 1) && !LeaseFresh(20000000, 10000000), "service lease expiry and invalid timestamps");
        Notice typingBefore, typingAfter;
        typingAfter.typing = true;
        CHECK(typingBefore != typingAfter, "typing-only changes are distinct");
        Machine input;
        aimmod::overlay::Frame state;
        state.notice = Notice{.content = true, .interactive = true, .cursor = true, .typing = true};
        CHECK(!input.Next(state).holdMenuInput, "no view cannot take input");
        state.haveView = true; state.haveWidget = true;
        state.haveWidget = false;
        CHECK(!input.Next(state).holdMenuInput, "no widget cannot take input");
        state.haveWidget = true;
        CHECK(input.Next(state).enterMenuInput, "usable view requests input");
        input.Acquired(false);
        CHECK(!input.holding() && input.Next(state).enterMenuInput, "failed acquisition retries");
        input.Acquired(true);
        state.serviceAlive = false;
        auto expired = input.Next(state);
        CHECK(expired.releaseMenuInput && !expired.captureText && !expired.clickable, "service loss releases typing and input");
        state.serviceAlive = true;
        input.Next(state);
        input.Acquired(true);
        state.haveView = false;
        CHECK(input.Next(state).releaseMenuInput, "view loss releases input");
        state.haveView = true;
        input.Next(state);
        input.Acquired(true);
        state.haveWidget = false;
        CHECK(input.Next(state).releaseMenuInput, "widget loss releases input");
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
        CHECK(LuaLayerActive("AIMMOD_LUANOTICE_1\t1000\n", 1002) && !LuaLayerActive("AIMMOD_LUANOTICE_1\t1000\n", 1005) &&
                  !LuaLayerActive("AIMMOD_LUANOTICE_1\tx\n", 1000) && !LuaLayerActive("", 1000),
              "a Lua notice layer is seen while its heartbeat is fresh");
        // The HUD: its own switch, URL and setting, and when it shows.
        CHECK(ParseUiHost("AIMMOD_UIHOST_1\nhud\tlua\n", "hud") == Host::Lua && ParseUiHost("AIMMOD_UIHOST_1\nhud\tlua\n") == Host::Native &&
                  ParseUiHost("AIMMOD_UIHOST_1\nnotice\tlua\n", "hud") == Host::Native && ParseUiHost("", "hud") == Host::Native,
              "hud and notice switch separately");
        CHECK(HudUrl("http://127.0.0.1:54321/abc123/overlay?surface=game\n") == std::optional<std::string>("http://127.0.0.1:54321/abc123/overlay?surface=game") &&
                  !HudUrl("http://127.0.0.1:1/abc/overlay?surface=obs") && !HudUrl("http://example.com:1/abc/overlay?surface=game"),
              "the in-game HUD page only");
        CHECK(GameEnabled(R"({"gameEnabled":true,"obsEnabled":false,"scenes":[]})") && !GameEnabled(R"({"gameEnabled":false})") && !GameEnabled("") && !GameEnabled("{"),
              "the HUD follows gameEnabled and fails closed");
        HudFrame hud;
        hud.enabled = true;
        CHECK(HudVisible(hud), "shown in menus and scenarios");
        hud.pauseMenuVisible = true;
        CHECK(HudVisible(hud), "KovaaK's menus (not paused, no challenge): still shown");
        hud.paused = true;
        CHECK(!HudVisible(hud), "the pause menu over a paused game: hidden");
        hud.paused = false;
        hud.inChallenge = true;
        CHECK(!HudVisible(hud), "the pause menu over a challenge: hidden");
        hud.pauseMenuVisible = false;
        CHECK(HudVisible(hud), "the challenge itself: shown");
        hud.panelOpen = true;
        CHECK(!HudVisible(hud), "the AimMod panel: hidden");
        hud.panelOpen = false;
        hud.replay = true;
        CHECK(!HudVisible(hud), "replays: hidden");
        hud.replay = false;
        hud.enabled = false;
        CHECK(!HudVisible(hud), "off in the overlay settings: hidden");

        // The input state machine.
        aimmod::overlay::Machine m;
        aimmod::overlay::Frame f;
        f.haveView = true; f.haveWidget = true;
        f.notice = buy;
        aimmod::overlay::Plan p = Next(m, f);
        CHECK(p.visible && p.full && p.holdMenuInput && p.enterMenuInput && p.clickable && p.zOrder >= 20000, "buy menu opens: UI-only input held, clickable, on top");
        CHECK(p.viewVisibility == Visibility::SelfHitTestInvisible && p.widgetVisibility == Visibility::Visible, "only the Gameface widget takes clicks");
        p = Next(m, f);
        CHECK(p.holdMenuInput && !p.enterMenuInput && !p.releaseMenuInput, "held, not re-entered, while open");
        // Escape: KovaaK's pause menu opens over the buy menu; it closes again and the hold continues.
        f.pauseMenuVisible = true;
        f.escapeRecent = true;
        p = Next(m, f);
        f.escapeRecent = false;
        CHECK(p.hidePauseMenu && p.holdMenuInput && !p.releaseMenuInput && !p.forgetMenuInput, "pause menu over the buy menu closes, input stays ours");
        f.pauseMenuVisible = false;
        // The buy menu closes (Escape or B): input goes back to the game once.
        aimmod::overlay::Notice closed = *buy;
        closed.cursor = false;
        closed.interactive = false;
        f.notice = closed;
        p = Next(m, f);
        CHECK(p.releaseMenuInput && !p.holdMenuInput && !p.clickable && p.viewVisibility == Visibility::HitTestInvisible,
              "closing gives input back; the HUD stays click-through");
        p = Next(m, f);
        CHECK(!p.releaseMenuInput && !p.enterMenuInput, "released once");
        // Escape just closed it (swallowMenu): the pause menu it opened closes, and the game keeps its input.
        closed.swallowMenu = true;
        f.notice = closed;
        f.pauseMenuVisible = true;
        f.escapeRecent = true;
        p = Next(m, f);
        f.escapeRecent = false;
        CHECK(p.hidePauseMenu && p.releaseMenuInput && !p.holdMenuInput, "Escape's pause menu closes after the buy menu did");
        // A deliberate Escape later (no buy menu, no swallow): KovaaK's menu is the player's.
        closed.swallowMenu = false;
        f.notice = closed;
        f.pauseMenuVisible = false;
        Next(m, f);
        f.pauseMenuVisible = true;
        p = Next(m, f);
        CHECK(!p.hidePauseMenu && !p.holdMenuInput && !p.enterMenuInput, "a deliberate pause menu is left alone");
        // In KovaaK's menus a notice with buttons takes clicks (the menu shows the cursor); no input changes.
        f.notice = toast;
        p = Next(m, f);
        CHECK(p.visible && p.clickable && !p.full && !p.holdMenuInput && !p.enterMenuInput && !p.releaseMenuInput, "menu notice clickable, input untouched");
        // In game without a cursor the same notice is click-through (the hotkey answers it).
        f.pauseMenuVisible = false;
        p = Next(m, f);
        CHECK(p.visible && !p.clickable && p.viewVisibility == Visibility::HitTestInvisible, "in game without a cursor: click-through");
        // The AimMod panel shows the same things: the layer hides; a held buy menu is handed over, not released.
        aimmod::overlay::Machine m2;
        aimmod::overlay::Frame g;
        g.haveView = true; g.haveWidget = true;
        g.notice = buy;
        Next(m2, g);
        g.panelOpen = true;
        p = Next(m2, g);
        CHECK(!p.visible && p.forgetMenuInput && !p.releaseMenuInput && p.viewVisibility == Visibility::Collapsed, "panel open: hidden, input left to the panel");
        // Replay playback owns the screen.
        aimmod::overlay::Machine m3;
        aimmod::overlay::Frame r;
        r.haveView = true; r.haveWidget = true;
        r.notice = buy;
        r.replay = true;
        p = Next(m3, r);
        CHECK(!p.visible && !p.enterMenuInput, "never during a replay");
        // The scoreboard is display-only: focus back to the viewport, never input or clicks.
        aimmod::overlay::Machine m4;
        aimmod::overlay::Frame t;
        t.haveView = true; t.haveWidget = true;
        t.notice = board;
        p = Next(m4, t);
        CHECK(p.focusViewport && !p.clickable && !p.holdMenuInput && !p.enterMenuInput, "held scoreboard keeps game focus only");
        t.pauseMenuVisible = true;
        p = Next(m4, t);
        CHECK(!p.focusViewport, "never steals focus from KovaaK's menus");
        // No view yet: input remains with the game.
        aimmod::overlay::Machine m5;
        aimmod::overlay::Frame v;
        v.notice = buy;
        p = Next(m5, v);
        CHECK(!p.holdMenuInput && !p.clickable, "no view: no input hold");
        // Alt-tab with the buy menu open: input is let go at once (never set to game-only while
        // KovaaK's may be opening its menu), and the pause menu focus loss opens is never hidden.
        aimmod::overlay::Machine m7;
        aimmod::overlay::Frame a;
        a.haveView = true; a.haveWidget = true;
        a.notice = buy;
        p = Next(m7, a);
        CHECK(p.enterMenuInput && p.forwardPointer, "buy menu holds input and forwards the mouse");
        a.focused = false;
        p = Next(m7, a);
        CHECK(p.forgetMenuInput && !p.releaseMenuInput && !p.holdMenuInput && !p.forwardPointer, "focus lost: let go without touching input");
        a.pauseMenuVisible = true; // KovaaK's opened its menu on focus loss
        p = Next(m7, a);
        CHECK(!p.hidePauseMenu && !p.releaseMenuInput, "a menu opened by focus loss is left alone");
        a.focused = true;
        p = Next(m7, a);
        CHECK(!p.hidePauseMenu && !p.holdMenuInput && !p.releaseMenuInput, "back in front: KovaaK's menu stays, its Escape or Resume returns to the game");
        a.pauseMenuVisible = false; // the player closed it
        p = Next(m7, a);
        CHECK(p.enterMenuInput, "menu closed and the buy menu still open: input taken again");
        // Alt-tab, the buy menu closes meanwhile, no KovaaK's menu: input goes back to the game on return.
        aimmod::overlay::Machine m8;
        aimmod::overlay::Frame b;
        b.haveView = true; b.haveWidget = true;
        b.notice = buy;
        Next(m8, b);
        b.focused = false;
        Next(m8, b);
        b.notice = closed;
        p = Next(m8, b);
        CHECK(!p.releaseMenuInput, "nothing while away");
        b.focused = true;
        p = Next(m8, b);
        CHECK(p.releaseMenuInput && !p.holdMenuInput, "on return: game-only, cursor off");
        // Without an Escape press a pause menu over the buy menu is the game's own: never hidden.
        aimmod::overlay::Machine m9;
        aimmod::overlay::Frame c;
        c.haveView = true; c.haveWidget = true;
        c.notice = buy;
        Next(m9, c);
        c.pauseMenuVisible = true;
        p = Next(m9, c);
        CHECK(!p.hidePauseMenu && p.forgetMenuInput, "no Escape: KovaaK's menu is left alone and input handed to it");
        // KovaaK's menu closes while the buy menu is closed too: the game gets game-only input, once.
        aimmod::overlay::Notice shut = *buy;
        shut.cursor = false;
        shut.interactive = false;
        c.notice = shut;
        c.pauseMenuVisible = false;
        p = Next(m9, c);
        CHECK(p.releaseMenuInput && !p.holdMenuInput, "after KovaaK's menu: game-only, never left in UI-only");
        p = Next(m9, c);
        CHECK(!p.releaseMenuInput, "once");
        // The chat input: held like the buy menu (UI-only on our widget, fire blocked), with the typed
        // characters relayed; Enter or Escape (typing off), alt-tab and KovaaK's menu always give the game its input back.
        auto chat = ParseNotice(R"({"version":1,"active":false,"layout":"full","interactive":true,"cursor":false,"typing":true,"chat":{"open":"team","session":3,"lines":2}})");
        CHECK(chat && chat->content && chat->typing && !chat->cursor && chat->interactive, "chat input notice");
        auto feed = ParseNotice(R"({"version":1,"active":false,"layout":"full","chat":{"open":null,"session":3,"lines":2}})");
        CHECK(feed && feed->content && !feed->typing && !feed->interactive, "a chat feed alone shows the layer, click-through");
        aimmod::overlay::Machine mc;
        aimmod::overlay::Frame k;
        k.haveView = true; k.haveWidget = true;
        k.notice = chat;
        p = Next(mc, k);
        CHECK(p.enterMenuInput && p.holdMenuInput && p.captureText && p.clickable && p.forwardPointer, "chat opens: input held, typed characters relayed");
        p = Next(mc, k);
        CHECK(p.holdMenuInput && p.captureText && !p.enterMenuInput, "held while typing");
        k.notice = feed;
        p = Next(mc, k);
        CHECK(p.releaseMenuInput && !p.holdMenuInput && !p.captureText && !p.clickable, "Enter or Escape: input back to the game, no more characters");
        k.notice = chat;
        Next(mc, k);
        k.focused = false;
        p = Next(mc, k);
        CHECK(p.forgetMenuInput && !p.captureText && !p.holdMenuInput, "alt-tab while typing: let go at once");
        k.notice = feed; // the service closes the input when the window loses focus
        k.focused = true;
        p = Next(mc, k);
        CHECK(p.releaseMenuInput && !p.captureText, "back in front: the game has its input");
        k.notice = chat;
        Next(mc, k);
        k.pauseMenuVisible = true;
        k.escapeRecent = true;
        p = Next(mc, k);
        k.escapeRecent = false;
        CHECK(p.hidePauseMenu && p.captureText, "Escape's pause menu over the chat input closes again");
        k.pauseMenuVisible = false;
        aimmod::overlay::Notice closedChat = *chat;
        closedChat.typing = false;
        closedChat.interactive = false;
        closedChat.swallowMenu = true;
        k.notice = closedChat;
        p = Next(mc, k);
        CHECK(p.releaseMenuInput && !p.captureText, "the input closed by Escape: the game gets its input back");
        CHECK(Describe(aimmod::overlay::Plan{}, [&] { aimmod::overlay::Machine md; aimmod::overlay::Frame d; d.haveView = true; d.haveWidget = true; d.notice = chat; return Next(md, d); }()).find("chat input") != std::string::npos,
              "chat input changes are logged");
        // One log line per change.
        aimmod::overlay::Machine m6;
        aimmod::overlay::Plan before{}, after = Next(m6, f);
        CHECK(Describe(before, after).find("shown") != std::string::npos && Describe(after, after).empty(), "describe changes only");
    }
} // namespace overlay_checks
