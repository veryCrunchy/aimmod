#include "OverlayHost.hpp"

#include "Log.hpp"
#include "Reflect.hpp"

#include <Unreal/FProperty.hpp>
#include <Unreal/UClass.hpp>
#include <Unreal/UFunction.hpp>
#include <Unreal/UObject.hpp>
#include <Unreal/UObjectGlobals.hpp>

#include <Windows.h>

#include <cstring>

namespace aimmod
{
    using namespace RC::Unreal;
    using namespace reflect;
    using game::Shape;

    namespace
    {
        constexpr const wchar_t* ViewClass = STR("/Game/UI/MetaGraphWidget.MetaGraphWidget_C");
        constexpr const wchar_t* WidgetLibrary = STR("/Script/UMG.Default__WidgetBlueprintLibrary");
        constexpr const wchar_t* Statics = STR("/Script/Engine.Default__GameplayStatics");
        // Toast: top centre, room for a card with buttons; full: the whole viewport.
        constexpr float ToastWidth = 620.0f, ToastHeight = 340.0f;
        // While the buy menu holds input: the cursor and fire block every frame, the input mode
        // every 250 ms and at once when the game hid the cursor (KovaaK's resets it).
        constexpr double InputInterval = 0.25, FocusInterval = 1.0 / 30.0;

        void WriteInt32(std::uint8_t* value, std::int32_t number) { std::memcpy(value, &number, sizeof number); }
        std::wstring Wide(const std::string& ascii) { return std::wstring(ascii.begin(), ascii.end()); }
    } // namespace

    std::string OverlayHost::Name(UObject* object) const
    {
        if (!object) return "none";
        const std::wstring name = object->GetFullName();
        std::string out;
        out.reserve(name.size());
        for (wchar_t c : name) out.push_back(c < 128 ? static_cast<char>(c) : '?');
        return out;
    }

    bool OverlayHost::Bind()
    {
        if (m_bound) return true;
        m_getPauseMenu.BindPath(STR("/Script/GameSkillsTrainer.MetaPlayerController:GetPauseMenu"), Shape::Object);
        m_isVisible.BindPath(STR("/Script/UMG.Widget:IsVisible"), Shape::Bool);
        m_isInViewport.BindPath(STR("/Script/UMG.UserWidget:IsInViewport"), Shape::Bool);
        m_showCursor.Bind(game::FindClass(STR("/Script/Engine.PlayerController")), STR("bShowMouseCursor"));
        m_blockingAttack.Bind(game::FindClass(STR("/Script/GameSkillsTrainer.MetaCharacter")), STR("bAbilityBlockingAttack"));
        m_bound = m_isInViewport.ok() && m_showCursor.ok();
        Log(std::string("overlay: bindings ") + (m_bound ? "ok" : "unavailable") + " (GetPauseMenu " + (m_getPauseMenu.ok() ? "ok" : "missing") +
            ", IsVisible " + (m_isVisible.ok() ? "ok" : "missing") + ", IsInViewport " + (m_isInViewport.ok() ? "ok" : "missing") +
            ", bShowMouseCursor " + (m_showCursor.ok() ? "ok" : "missing") + ", bAbilityBlockingAttack " + (m_blockingAttack.ok() ? "ok" : "missing") + ")");
        return m_bound;
    }

    UObject* OverlayHost::PauseMenu(UObject* player) const
    {
        if (!player || !m_getPauseMenu.ok()) return nullptr;
        UObject* menu = m_getPauseMenu.Object(player);
        return Alive(menu) ? menu : nullptr;
    }

    bool OverlayHost::CursorShown(UObject* player) const
    {
        if (!player || !m_showCursor.ok()) return false;
        return m_showCursor.Bool(player).value_or(false);
    }

    void OverlayHost::Tick(double now, bool replayActive)
    {
        const Output::OverlayInputs in = m_output.overlay();
        if (!in.native)
        {
            if (m_view.Get() || m_ready) Remove("ui-host.tsv selects the Lua notice layer");
            m_ready = false;
            return;
        }
        UObject* player = m_scene.Player();
        if (!player || !Alive(player))
        {
            if (m_view.Get()) Remove("the player controller is gone (level change)");
            return;
        }
        if (!Bind()) return;
        // One view per player controller; a new controller (level change) gets a new view.
        UObject* view = m_view.Get();
        if (view && (m_player.Get() != player || !Alive(view))) { Remove("the player controller changed"); view = nullptr; }
        if (view && now >= m_nextCheck)
        {
            m_nextCheck = now + 1.0;
            if (!m_isInViewport.Bool(view).value_or(false)) { Remove("the view left the viewport"); view = nullptr; }
        }
        if (!view && !in.url.empty() && now >= m_nextCreate)
        {
            m_nextCreate = now + (m_failures < 3 ? 2.0 : 15.0);
            if (Create(player, in.url, now)) view = m_view.Get();
        }
        if (view && in.url != m_url && !in.url.empty())
            if (UObject* widget = m_widget.Get())
            {
                Call(widget, STR("/Script/CohtmlPlugin.CohtmlWidget:Load"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                    if (n == STR("Path")) WriteFString(v, Wide(in.url));
                });
                m_url = in.url;
                Log("overlay: the notify page moved; reloaded it");
            }
        // Notify.lua must stand down while this layer is up (it reads "overlay" in core-active.tsv).
        const bool lua = m_ready && in.luaLayer;
        if (lua != m_luaSeen)
        {
            m_luaSeen = lua;
            if (lua) Warn("overlay: AimModNativeUI's Lua notice layer is on screen too (lua-notice.tsv is fresh); two layers will show and fight over input");
            else Log("overlay: the Lua notice layer is off; only AimModCore's shows");
        }
        // KovaaK's window in front, and a real Escape press (for the pause menu it opens).
        HWND foreground = GetForegroundWindow();
        DWORD owner = 0;
        if (foreground) GetWindowThreadProcessId(foreground, &owner);
        const bool focused = foreground && owner == GetCurrentProcessId();
        if (focused && (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0) m_escapeAt = now;
        overlay::Frame frame;
        frame.focused = focused;
        frame.escapeRecent = now - m_escapeAt < 0.6;
        frame.notice = in.notice;
        frame.panelOpen = in.panelOpen;
        frame.replay = replayActive;
        frame.haveView = view != nullptr;
        UObject* menu = PauseMenu(player);
        frame.pauseMenuVisible = menu && m_isVisible.ok() && m_isVisible.Bool(menu).value_or(false);
        frame.gameCursor = !m_machine.holding() && CursorShown(player);
        const overlay::Plan plan = m_machine.Next(frame);
        if (plan.hidePauseMenu && menu) HidePauseMenu(player, menu);
        Apply(plan, player, now);
    }

    bool OverlayHost::Create(UObject* player, const std::string& url, double now)
    {
        UObject* library = Default(WidgetLibrary);
        UClass* type = UObjectGlobals::StaticFindObject<UClass*>(nullptr, nullptr, ViewClass);
        if (!library || !type)
        {
            if (!m_warned) Log("overlay: waiting for KovaaK's Gameface view class (MetaGraphWidget) to load");
            m_warned = true;
            return false;
        }
        UObject* view = nullptr;
        const bool created = Call(library, STR("/Script/UMG.WidgetBlueprintLibrary:Create"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
            if (n == STR("WorldContextObject") || n == STR("OwningPlayer")) WriteObject(v, player);
            else if (n == STR("WidgetType")) WriteObject(v, type);
        }, &view);
        if (!created || !Alive(view))
        {
            ++m_failures;
            Warn("overlay: could not create the notice view (attempt " + std::to_string(m_failures) + ")");
            return false;
        }
        // UBaseMetaUIWidget (the view's native base) keeps its Gameface widget in mCohtmlWidget.
        UObject* widget = GetObject(view, STR("mCohtmlWidget"));
        if (!Alive(widget)) Call(view, STR("/Script/GameSkillsTrainer.BaseMetaUIWidget:GetCohtmlWidget"), {}, &widget);
        if (!Alive(widget))
        {
            ++m_failures;
            Warn("overlay: the notice view has no Gameface widget; removed it");
            Call(view, STR("/Script/UMG.Widget:RemoveFromParent"));
            return false;
        }
        // Never focusable by Tab navigation; no input until a plan makes it clickable.
        SetBool(view, STR("bIsFocusable"), false);
        SetBool(widget, STR("bReceiveInput"), false);
        Call(view, STR("/Script/UMG.Widget:SetVisibility"), [](const std::wstring& n, FProperty*, std::uint8_t* v) {
            if (n == STR("InVisibility")) *v = static_cast<std::uint8_t>(overlay::Visibility::Collapsed);
        });
        Call(view, STR("/Script/UMG.UserWidget:AddToViewport"), [](const std::wstring& n, FProperty*, std::uint8_t* v) {
            if (n == STR("ZOrder")) WriteInt32(v, overlay::ZOrder);
        });
        SetBool(view, STR("bIsFocusable"), false); // Construct may restore the Blueprint default
        Call(widget, STR("/Script/CohtmlPlugin.CohtmlWidget:Load"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
            if (n == STR("Path")) WriteFString(v, Wide(url));
        });
        m_view = FWeakObjectPtr(view);
        m_widget = FWeakObjectPtr(widget);
        m_player = FWeakObjectPtr(player);
        m_url = url;
        m_layoutSet = false;
        m_fresh = true;
        m_plan = overlay::Plan{};
        m_failures = 0;
        m_ready = true;
        m_nextCheck = now + 1.0;
        Log("overlay: created the notice view " + Name(view) + " with Gameface widget " + Name(widget) + " at z-order " + std::to_string(overlay::ZOrder) +
            " for controller " + Name(player) + "; loading the notify page");
        return true;
    }

    void OverlayHost::Remove(const char* why)
    {
        UObject* player = m_player.Get();
        if (m_machine.holding() || m_blocking)
        {
            if (player && Alive(player)) ReleaseMenuInput(player);
        }
        m_machine = overlay::Machine{};
        if (UObject* view = m_view.Get(); view && Alive(view)) Call(view, STR("/Script/UMG.Widget:RemoveFromParent"));
        if (m_view.Get() || m_ready) Log(std::string("overlay: removed the notice view (") + why + ")");
        m_view = m_widget = m_player = m_character = FWeakObjectPtr{};
        m_plan = overlay::Plan{};
        m_layoutSet = false;
        m_ready = false;
    }

    void OverlayHost::Shutdown() { Remove("shutting down"); }

    void OverlayHost::Layout(bool full)
    {
        UObject* view = m_view.Get();
        if (!view || (m_layoutSet && m_layoutFull == full)) return;
        m_layoutSet = true;
        m_layoutFull = full;
        Call(view, STR("/Script/UMG.UserWidget:SetAnchorsInViewport"), [&](const std::wstring&, FProperty* p, std::uint8_t* v) {
            if (full) WriteFloats(v, p, {0.0f, 0.0f, 1.0f, 1.0f});
            else WriteFloats(v, p, {0.5f, 0.0f, 0.5f, 0.0f});
        });
        Call(view, STR("/Script/UMG.UserWidget:SetAlignmentInViewport"), [&](const std::wstring&, FProperty* p, std::uint8_t* v) {
            WriteFloats(v, p, {full ? 0.0f : 0.5f, 0.0f});
        });
        // No SetPositionInViewport: in UE 4.26 it also resets the anchors to the top-left corner
        // (which shrank the full-screen layout to the view's desired size). The position is (0, 0).
        Call(view, STR("/Script/UMG.UserWidget:SetDesiredSizeInViewport"), [&](const std::wstring&, FProperty* p, std::uint8_t* v) {
            if (full) WriteFloats(v, p, {0.0f, 0.0f});
            else WriteFloats(v, p, {ToastWidth, ToastHeight});
        });
    }

    void OverlayHost::Apply(const overlay::Plan& plan, UObject* player, double now)
    {
        const std::string change = overlay::Describe(m_plan, plan);
        UObject* view = m_view.Get();
        UObject* widget = m_widget.Get();
        if (view && widget)
        {
            if (plan.visible) Layout(plan.full);
            if (plan.viewVisibility != m_plan.viewVisibility || m_fresh)
                Call(view, STR("/Script/UMG.Widget:SetVisibility"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                    if (n == STR("InVisibility")) *v = static_cast<std::uint8_t>(plan.viewVisibility);
                });
            if (plan.widgetVisibility != m_plan.widgetVisibility || plan.clickable != m_plan.clickable || m_fresh)
            {
                Call(widget, STR("/Script/UMG.Widget:SetVisibility"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                    if (n == STR("InVisibility")) *v = static_cast<std::uint8_t>(plan.widgetVisibility);
                });
                SetBool(widget, STR("bReceiveInput"), plan.clickable);
            }
            m_fresh = false;
        }
        if (plan.enterMenuInput) EnterMenuInput(player);
        if (plan.releaseMenuInput) ReleaseMenuInput(player);
        if (plan.forgetMenuInput) BlockFire(player, false);
        if (plan.holdMenuInput) Reassert(player, now);
        if (plan.forwardPointer) ForwardPointer(now);
        else if (m_plan.forwardPointer)
        {
            m_pointerX = m_pointerY = -1;
            m_pointerDown = false;
        }
        if (plan.focusViewport && now >= m_nextFocus)
        {
            m_nextFocus = now + FocusInterval;
            if (UObject* library = Default(WidgetLibrary)) Call(library, STR("/Script/UMG.WidgetBlueprintLibrary:SetFocusToGameViewport"));
        }
        if (!change.empty())
            Log("overlay: " + change + (view ? "" : " (view not created yet)") + (plan.clickable ? ", widget " + Name(widget) : std::string()));
        m_plan = plan;
    }

    bool OverlayHost::ShowCursor(UObject* player, bool on)
    {
        const bool k2 = Call(player, STR("/Script/GameSkillsTrainer.MetaPlayerController:K2_SetShowMouseCursor"),
                             [on](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                                 if (n == STR("bShow")) WriteBoolParam(v, p, on);
                             });
        const bool field = SetBool(player, STR("bShowMouseCursor"), on);
        return k2 || field;
    }

    bool OverlayHost::UiOnly(UObject* player)
    {
        UObject* library = Default(WidgetLibrary);
        UObject* widget = m_widget.Get();
        if (!library) return false;
        return Call(library, STR("/Script/UMG.WidgetBlueprintLibrary:SetInputMode_UIOnlyEx"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
            if (n == STR("PlayerController")) WriteObject(v, player);
            else if (n == STR("InWidgetToFocus")) WriteObject(v, widget);
            else if (n == STR("InMouseLockMode")) *v = 0; // DoNotLock
        });
    }

    bool OverlayHost::GameOnly(UObject* player)
    {
        UObject* library = Default(WidgetLibrary);
        if (!library) return false;
        return Call(library, STR("/Script/UMG.WidgetBlueprintLibrary:SetInputMode_GameOnly"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
            if (n == STR("PlayerController")) WriteObject(v, player);
        });
    }

    // While a menu needs the mouse the local character can't attack (IsAttackInputBlocked);
    // the previous value comes back afterwards.
    void OverlayHost::BlockFire(UObject* player, bool block)
    {
        UObject* character = player ? GetObject(player, STR("MyCharacter")) : nullptr;
        if (block)
        {
            if (!Alive(character)) return;
            if (!m_blocking || m_character.Get() != character)
            {
                m_savedBlock = m_blockingAttack.ok() && m_blockingAttack.Bool(character).value_or(false);
                m_character = FWeakObjectPtr(character);
                m_blocking = true;
            }
            SetBool(character, STR("bAbilityBlockingAttack"), true);
            return;
        }
        if (!m_blocking) return;
        if (UObject* held = m_character.Get(); held && Alive(held)) SetBool(held, STR("bAbilityBlockingAttack"), m_savedBlock);
        m_blocking = false;
        m_character = FWeakObjectPtr{};
    }

    void OverlayHost::EnterMenuInput(UObject* player)
    {
        const bool cursor = ShowCursor(player, true);
        const bool mode = UiOnly(player);
        BlockFire(player, true);
        m_resets = 0;
        m_nextInput = 0.0;
        Log(std::string("overlay: buy menu input: cursor ") + (cursor ? "on" : "FAILED") + " (bShowMouseCursor " + (CursorShown(player) ? "true" : "false") +
            "), UI-only " + (mode ? "set" : "FAILED") + " focused on " + Name(m_widget.Get()) + ", fire " + (m_blocking ? "blocked" : "not blocked (no character)") +
            ", controller " + Name(player));
    }

    void OverlayHost::ReleaseMenuInput(UObject* player)
    {
        BlockFire(player, false);
        const bool cursor = ShowCursor(player, false);
        const bool mode = GameOnly(player);
        Log(std::string("overlay: input back to the game: game-only ") + (mode ? "set" : "FAILED") + ", cursor " + (cursor ? "off" : "FAILED") +
            ", fire unblocked, controller " + Name(player));
    }

    // Every frame while held: cursor and fire block; the input mode every 250 ms, or at once
    // when KovaaK's hid the cursor (it resets input along with it).
    void OverlayHost::Reassert(UObject* player, double now)
    {
        const bool reset = !CursorShown(player);
        if (reset)
        {
            ++m_resets;
            if (m_resets <= 3 || m_resets % 50 == 0)
                Log("overlay: the game hid the cursor during the buy menu (" + std::to_string(m_resets) + "x); taking input back");
            ShowCursor(player, true);
        }
        BlockFire(player, true);
        if (reset || now >= m_nextInput)
        {
            m_nextInput = now + InputInterval;
            UiOnly(player);
        }
    }

    // Pointer fallback (the buy menu works even if Gameface delivers no mouse events): the cursor
    // in the game window's client pixels and the left button, sent to the page as
    // AimModPointer(x, y, down, width, height) when they change. The page finds the button under
    // it and presses it (ui/notify.js); it ignores this while Gameface's own clicks arrive.
    void OverlayHost::ForwardPointer(double now)
    {
        if (now < m_nextPointer) return;
        m_nextPointer = now + 1.0 / 120.0;
        UObject* widget = m_widget.Get();
        HWND window = GetForegroundWindow();
        DWORD owner = 0;
        if (window) GetWindowThreadProcessId(window, &owner);
        if (!widget || !window || owner != GetCurrentProcessId()) return;
        POINT at{};
        RECT client{};
        if (!GetCursorPos(&at) || !ScreenToClient(window, &at) || !GetClientRect(window, &client)) return;
        const long width = client.right - client.left, height = client.bottom - client.top;
        if (width <= 0 || height <= 0) return;
        const bool down = (GetAsyncKeyState(GetSystemMetrics(SM_SWAPBUTTON) ? VK_RBUTTON : VK_LBUTTON) & 0x8000) != 0;
        if (at.x == m_pointerX && at.y == m_pointerY && down == m_pointerDown) return;
        const bool pressed = down && !m_pointerDown;
        m_pointerX = at.x;
        m_pointerY = at.y;
        m_pointerDown = down;
        UObject* event = nullptr;
        if (!Call(widget, STR("/Script/CohtmlPlugin.CohtmlWidget:CreateJSEvent"), {}, &event) || !Alive(event)) return;
        auto addFloat = [event](float number) {
            Call(event, STR("/Script/CohtmlPlugin.CohtmlJSEvent:AddFloat"), [number](const std::wstring&, FProperty*, std::uint8_t* v) { std::memcpy(v, &number, sizeof number); });
        };
        addFloat(static_cast<float>(at.x));
        addFloat(static_cast<float>(at.y));
        Call(event, STR("/Script/CohtmlPlugin.CohtmlJSEvent:AddBool"), [down](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteBoolParam(v, p, down); });
        addFloat(static_cast<float>(width));
        addFloat(static_cast<float>(height));
        const bool sent = Call(widget, STR("/Script/CohtmlPlugin.CohtmlWidget:TriggerJSEvent"), [event](const std::wstring& n, FProperty*, std::uint8_t* v) {
            if (n == STR("Name")) WriteFString(v, L"AimModPointer");
            else if (n == STR("EventData")) WriteObject(v, event);
        });
        if (sent) ++m_pointerMoves;
        if (sent && pressed) ++m_pointerPresses;
        if (pressed || now >= m_nextPointerLog)
        {
            m_nextPointerLog = now + 5.0;
            Log("overlay: pointer fallback: " + std::string(sent ? "sent" : "FAILED to send") + " AimModPointer (" + std::to_string(m_pointerMoves) + " updates, " +
                std::to_string(m_pointerPresses) + " presses so far) at " + std::to_string(at.x) + "," + std::to_string(at.y) + " of " + std::to_string(width) + "x" +
                std::to_string(height) + (down ? ", button down" : ""));
        }
    }

    void OverlayHost::HidePauseMenu(UObject* player, UObject* menu)
    {
        Call(menu, STR("/Script/UMG.Widget:SetVisibility"), [](const std::wstring& n, FProperty*, std::uint8_t* v) {
            if (n == STR("InVisibility")) *v = static_cast<std::uint8_t>(overlay::Visibility::Collapsed);
        });
        bool paused = false;
        if (UObject* statics = Default(Statics))
        {
            Call(statics, STR("/Script/Engine.GameplayStatics:IsGamePaused"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                if (n == STR("WorldContextObject")) WriteObject(v, player);
            }, nullptr, [&](const std::wstring& n, FProperty*, const std::uint8_t* v) {
                if (n == STR("ReturnValue")) paused = *v != 0;
            });
            if (paused)
                Call(statics, STR("/Script/Engine.GameplayStatics:SetGamePaused"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                    if (n == STR("WorldContextObject")) WriteObject(v, player);
                    else if (n == STR("bPaused")) WriteBoolParam(v, p, false);
                });
        }
        Log(std::string("overlay: Escape over the buy menu: hid KovaaK's pause menu ") + Name(menu) + (paused ? " and unpaused the game" : ""));
    }
} // namespace aimmod
