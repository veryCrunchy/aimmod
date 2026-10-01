#include <aimmod/Json.hpp>
#include <aimmod/Overlay.hpp>

#include <cctype>

namespace aimmod::overlay
{
    namespace
    {
        bool True(const json::Value& root, std::string_view key)
        {
            const json::Value* v = root.find(key);
            return v && v->isBool() && v->boolean;
        }
        bool IsObject(const json::Value& root, std::string_view key)
        {
            const json::Value* v = root.find(key);
            return v && v->isObject();
        }
        std::string_view Trim(std::string_view s)
        {
            while (!s.empty() && std::isspace(static_cast<unsigned char>(s.back()))) s.remove_suffix(1);
            while (!s.empty() && std::isspace(static_cast<unsigned char>(s.front()))) s.remove_prefix(1);
            return s;
        }
        bool Digits(std::string_view s)
        {
            if (s.empty() || s.size() > 5) return false;
            for (char c : s)
                if (c < '0' || c > '9') return false;
            return true;
        }
        bool Hex(std::string_view s)
        {
            if (s.empty() || s.size() > 128) return false;
            for (char c : s)
                if (!std::isxdigit(static_cast<unsigned char>(c))) return false;
            return true;
        }
        const char* VisibilityName(Visibility v)
        {
            switch (v)
            {
            case Visibility::Visible: return "Visible";
            case Visibility::Collapsed: return "Collapsed";
            case Visibility::Hidden: return "Hidden";
            case Visibility::HitTestInvisible: return "HitTestInvisible";
            case Visibility::SelfHitTestInvisible: return "SelfHitTestInvisible";
            }
            return "?";
        }
    } // namespace

    std::optional<Notice> ParseNotice(std::string_view text)
    {
        if (text.empty() || text.size() > MaxNoticeBytes) return std::nullopt;
        auto root = json::Parse(text, nullptr, MaxNoticeBytes);
        if (!root || !root->isObject()) return std::nullopt;
        const json::Value* version = root->find("version");
        if (!version || !version->isNumber() || version->number != 1) return std::nullopt;
        Notice n;
        const json::Value* badge = root->find("badge");
        n.content = True(*root, "active") || (badge && badge->isString()) || IsObject(*root, "duel") || IsObject(*root, "combat") ||
                    IsObject(*root, "cs") || IsObject(*root, "board") || IsObject(*root, "boardFull");
        const json::Value* layout = root->find("layout");
        n.full = layout && layout->isString() && layout->string == "full";
        n.interactive = True(*root, "interactive");
        n.cursor = True(*root, "cursor");
        n.swallowMenu = True(*root, "swallowMenu");
        n.boardHeld = IsObject(*root, "boardFull");
        return n;
    }

    std::optional<std::string> NoticeUrl(std::string_view file)
    {
        std::string_view url = Trim(file);
        constexpr std::string_view Prefix = "http://127.0.0.1:", Suffix = "/overlay?surface=game";
        if (url.size() > 512 || url.substr(0, Prefix.size()) != Prefix || url.size() < Prefix.size() + Suffix.size() ||
            url.substr(url.size() - Suffix.size()) != Suffix)
            return std::nullopt;
        std::string_view middle = url.substr(Prefix.size(), url.size() - Prefix.size() - Suffix.size()); // <port>/<cap>
        const auto slash = middle.find('/');
        if (slash == std::string_view::npos || !Digits(middle.substr(0, slash)) || !Hex(middle.substr(slash + 1))) return std::nullopt;
        return std::string(url.substr(0, url.size() - Suffix.size())) + "/notify";
    }

    Host ParseUiHost(std::string_view text)
    {
        constexpr std::string_view Header = "AIMMOD_UIHOST_1";
        if (text.substr(0, Header.size()) != Header) return Host::Native;
        std::size_t at = 0;
        while (at < text.size())
        {
            std::size_t end = text.find('\n', at);
            std::string_view line = Trim(text.substr(at, end == std::string_view::npos ? std::string_view::npos : end - at));
            at = end == std::string_view::npos ? text.size() : end + 1;
            if (line == "notice\tlua") return Host::Lua;
        }
        return Host::Native;
    }

    bool PanelOpen(std::string_view text, std::int64_t nowUnix)
    {
        constexpr std::string_view Header = "AIMMOD_PANEL_1\t";
        text = Trim(text);
        if (text.substr(0, Header.size()) != Header) return false;
        text.remove_prefix(Header.size());
        if (text.size() < 3 || (text[0] != '0' && text[0] != '1') || text[1] != '\t') return false;
        std::int64_t stamp = 0;
        for (char c : text.substr(2))
        {
            if (c < '0' || c > '9' || stamp > 100000000000LL) return false;
            stamp = stamp * 10 + (c - '0');
        }
        const std::int64_t age = nowUnix - stamp;
        return text[0] == '1' && age >= -3 && age <= 3;
    }

    bool LuaLayerActive(std::string_view text, std::int64_t nowUnix)
    {
        constexpr std::string_view Header = "AIMMOD_LUANOTICE_1\t";
        text = Trim(text);
        if (text.substr(0, Header.size()) != Header) return false;
        text.remove_prefix(Header.size());
        if (text.empty() || text.size() > 12) return false;
        std::int64_t stamp = 0;
        for (char c : text)
        {
            if (c < '0' || c > '9') return false;
            stamp = stamp * 10 + (c - '0');
        }
        const std::int64_t age = nowUnix - stamp;
        return age >= -3 && age <= 3;
    }

    Plan Machine::Next(const Frame& f)
    {
        Plan p;
        p.zOrder = ZOrder;
        const bool blocked = f.panelOpen || f.replay;
        const Notice n = f.notice.value_or(Notice{});
        p.visible = f.notice.has_value() && n.content && !blocked;
        p.full = n.full;
        p.focused = f.focused;
        // Escape over the buy menu: KovaaK's pause menu opened by an Escape press (window in front)
        // while the buy menu held input, or right after Escape closed it (swallowMenu). It closes
        // again and the game keeps its input. A menu KovaaK's opened for anything else (focus
        // loss, its own keys, the player later) is never touched.
        bool pause = f.pauseMenuVisible;
        if (f.pauseMenuVisible && !m_pauseWasVisible && f.focused && f.escapeRecent && !blocked && (m_holding || n.swallowMenu))
        {
            p.hidePauseMenu = true;
            pause = false;
        }
        m_pauseWasVisible = pause;
        // The buy menu holds input only while KovaaK's is in front: alt-tab lets go at once.
        const bool menu = p.visible && n.cursor && !pause && f.focused;
        p.holdMenuInput = menu;
        p.forwardPointer = menu && f.haveView;
        if (menu && !m_holding)
        {
            p.enterMenuInput = true;
            m_suspended = false;
        }
        if (!menu && m_holding)
        {
            // KovaaK's menu or the AimMod panel took over: leave their input alone. Focus loss: let
            // go without touching input (KovaaK's may open its menu now); settled when focus returns.
            if (pause || blocked) p.forgetMenuInput = true;
            else if (!f.focused)
            {
                p.forgetMenuInput = true;
                m_suspended = true;
            }
            else p.releaseMenuInput = true;
        }
        // The pause menu Escape opened is gone again and the buy menu is closed: the game gets its input back.
        if (p.hidePauseMenu && !menu) p.releaseMenuInput = true;
        // Back from alt-tab with the buy menu closed and no KovaaK's menu up: the game gets its input back.
        if (m_suspended && !menu && f.focused && !pause && !blocked)
        {
            p.releaseMenuInput = true;
            m_suspended = false;
        }
        if (pause || blocked) m_suspended = false; // KovaaK's menu or the panel owns input now
        m_holding = menu;
        p.clickable = p.visible && f.haveView && n.interactive && (pause || menu || f.gameCursor);
        // The scoreboard is display-only: when Tab moved keyboard focus off the game, it goes back.
        p.focusViewport = p.visible && n.boardHeld && !menu && !pause;
        // The view itself never takes a click; only the Gameface widget inside it does, and only when clickable.
        p.viewVisibility = !p.visible ? Visibility::Collapsed : p.clickable ? Visibility::SelfHitTestInvisible : Visibility::HitTestInvisible;
        p.widgetVisibility = p.clickable ? Visibility::Visible : Visibility::HitTestInvisible;
        return p;
    }

    std::string Describe(const Plan& a, const Plan& b)
    {
        std::string out;
        auto add = [&out](const std::string& part) {
            if (!out.empty()) out += "; ";
            out += part;
        };
        if (a.visible != b.visible) add(b.visible ? "shown" : "hidden");
        if (b.visible && (a.full != b.full || a.visible != b.visible)) add(std::string("layout ") + (b.full ? "full screen" : "toast"));
        if (a.clickable != b.clickable || a.viewVisibility != b.viewVisibility)
            add(std::string(b.clickable ? "takes clicks" : "click-through") + " (view " + VisibilityName(b.viewVisibility) + ", Gameface widget " +
                VisibilityName(b.widgetVisibility) + ")");
        if (b.enterMenuInput) add("buy menu input: UI-only focused on our Gameface widget, cursor on, fire blocked");
        if (b.releaseMenuInput) add("input back to the game: game-only, cursor off, fire unblocked");
        if (b.forgetMenuInput) add("buy menu input handed to KovaaK's menu or the AimMod panel (input left as they set it)");
        if (b.hidePauseMenu) add("closed KovaaK's pause menu opened by Escape over the buy menu");
        if (a.focused != b.focused) add(b.focused ? "KovaaK's window is in front again" : "KovaaK's window lost focus (alt-tab): held input let go");
        if (a.focusViewport != b.focusViewport) add(b.focusViewport ? "scoreboard held: keeping keyboard focus on the game viewport" : "scoreboard released");
        return out;
    }
} // namespace aimmod::overlay
