#include "HudHost.hpp"

#include "Log.hpp"
#include "Reflect.hpp"

#include <Unreal/FProperty.hpp>
#include <Unreal/UClass.hpp>
#include <Unreal/UFunction.hpp>
#include <Unreal/UObject.hpp>
#include <Unreal/UObjectGlobals.hpp>

#include <cstring>

namespace aimmod
{
    using namespace RC::Unreal;
    using namespace reflect;
    using game::Shape;

    namespace
    {
        constexpr const wchar_t* ViewClass = STR("/Game/UI/MetaGraphWidget.MetaGraphWidget_C");
        constexpr const wchar_t* HostClass = STR("/Game/FirstPersonBP/Blueprints/UI/Palette/PalettedBorderWidget.PalettedBorderWidget_C");
        constexpr const wchar_t* WidgetLibrary = STR("/Script/UMG.Default__WidgetBlueprintLibrary");
        constexpr std::int32_t ZOrder = 5000; // below the notice layer (20000) and KovaaK's menus' input
        constexpr std::uint8_t Collapsed = 1, HitTestInvisible = 3;

        void WriteInt32(std::uint8_t* value, std::int32_t number) { std::memcpy(value, &number, sizeof number); }
        std::wstring Wide(const std::string& ascii) { return std::wstring(ascii.begin(), ascii.end()); }
        std::string Name(UObject* object)
        {
            if (!object) return "none";
            std::string out;
            for (wchar_t c : object->GetFullName()) out.push_back(c < 128 ? static_cast<char>(c) : '?');
            return out;
        }
        void Visibility(UObject* widget, std::uint8_t value)
        {
            if (widget) Call(widget, STR("/Script/UMG.Widget:SetVisibility"), [value](const std::wstring& n, FProperty*, std::uint8_t* v) {
                if (n == STR("InVisibility")) *v = value;
            });
        }
    } // namespace

    void HudHost::Tick(double now, bool replayActive, bool inChallenge)
    {
        if (now < m_nextTick) return;
        m_nextTick = now + 0.1;
        const Output::OverlayInputs in = m_output.overlay();
        if (!in.hudNative)
        {
            if (m_view.Get() || m_ready) Remove("ui-host.tsv selects LiveHUD.lua");
            return;
        }
        UObject* player = m_scene.Player();
        if (!player || !Alive(player))
        {
            if (m_view.Get()) Remove("the player controller is gone (level change)");
            return;
        }
        if (!m_bound)
        {
            m_bound = true;
            m_getPauseMenu.BindPath(STR("/Script/GameSkillsTrainer.MetaPlayerController:GetPauseMenu"), Shape::Object);
            m_isVisible.BindPath(STR("/Script/UMG.Widget:IsVisible"), Shape::Bool);
            m_isInViewport.BindPath(STR("/Script/UMG.UserWidget:IsInViewport"), Shape::Bool);
        }
        UObject* host = m_host.Get();
        if (host && (m_player.Get() != player || !Alive(host) || !m_view.Get()))
        {
            Remove("the player controller changed");
            host = nullptr;
        }
        if (host && now >= m_nextCheck)
        {
            m_nextCheck = now + 1.0;
            if (m_isInViewport.ok() && !m_isInViewport.Bool(host).value_or(false))
            {
                Remove("the view left the viewport");
                host = nullptr;
            }
        }
        if (!host && !in.hudUrl.empty() && now >= m_nextCreate)
        {
            m_nextCreate = now + (m_failures < 3 ? 2.0 : 15.0);
            if (Create(player, in.hudUrl)) host = m_host.Get();
        }
        if (!host) return;
        if (in.hudUrl != m_url && !in.hudUrl.empty())
            if (UObject* widget = m_widget.Get())
            {
                Call(widget, STR("/Script/CohtmlPlugin.CohtmlWidget:Load"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                    if (n == STR("Path")) WriteFString(v, Wide(in.hudUrl));
                });
                m_url = in.hudUrl;
                m_deliveries = 0;
                Log("hud: the overlay page moved; reloaded it");
            }
        overlay::HudFrame frame;
        frame.enabled = in.gameEnabled && !in.hudUrl.empty();
        frame.replay = replayActive;
        frame.panelOpen = in.panelOpen;
        frame.inChallenge = inChallenge;
        if (UObject* menu = m_getPauseMenu.ok() ? m_getPauseMenu.Object(player) : nullptr; menu && Alive(menu))
            frame.pauseMenuVisible = m_isVisible.ok() && m_isVisible.Bool(menu).value_or(false);
        if (frame.pauseMenuVisible)
            if (UObject* statics = Default(STR("/Script/Engine.Default__GameplayStatics")))
                Call(statics, STR("/Script/Engine.GameplayStatics:IsGamePaused"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                    if (n == STR("WorldContextObject")) WriteObject(v, player);
                }, nullptr, [&](const std::wstring& n, FProperty*, const std::uint8_t* v) {
                    if (n == STR("ReturnValue")) frame.paused = *v != 0;
                });
        const bool visible = overlay::HudVisible(frame);
        if (visible != m_visible)
        {
            Show(visible, now);
            Log(std::string("hud: ") + (visible ? "shown" : "hidden") + " (" +
                (visible ? "menus and scenarios" :
                 !frame.enabled ? "off in the overlay settings" : frame.replay ? "replay" : frame.panelOpen ? "AimMod panel open" : "KovaaK's pause menu over a paused game or a challenge") +
                ")");
        }
        Deliver(now);
    }

    bool HudHost::Create(UObject* player, const std::string& url)
    {
        UObject* library = Default(WidgetLibrary);
        UClass* type = UObjectGlobals::StaticFindObject<UClass*>(nullptr, nullptr, ViewClass);
        UClass* hostType = UObjectGlobals::StaticFindObject<UClass*>(nullptr, nullptr, HostClass);
        UClass* canvasType = UObjectGlobals::StaticFindObject<UClass*>(nullptr, nullptr, STR("/Script/UMG.CanvasPanel"));
        if (!library || !type || !hostType || !canvasType)
        {
            if (!m_warned) Log("hud: waiting for KovaaK's widget classes to load (MetaGraphWidget, PalettedBorderWidget)");
            m_warned = true;
            return false;
        }
        auto create = [&](UClass* cls) {
            UObject* made = nullptr;
            Call(library, STR("/Script/UMG.WidgetBlueprintLibrary:Create"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                if (n == STR("WorldContextObject") || n == STR("OwningPlayer")) WriteObject(v, player);
                else if (n == STR("WidgetType")) WriteObject(v, cls);
            }, &made);
            return Alive(made) ? made : nullptr;
        };
        auto fail = [&](const std::string& why, UObject* a, UObject* b) {
            ++m_failures;
            Warn("hud: could not create the HUD view: " + why + " (attempt " + std::to_string(m_failures) + ")");
            if (a) Call(a, STR("/Script/UMG.Widget:RemoveFromParent"));
            if (b) Call(b, STR("/Script/UMG.Widget:RemoveFromParent"));
            return false;
        };
        UObject* host = create(hostType);
        UObject* tree = host ? GetObject(host, STR("WidgetTree")) : nullptr;
        if (!host || !Alive(tree)) return fail("no host widget", host, nullptr);
        FStaticConstructObjectParameters params(canvasType, tree);
        UObject* canvas = UObjectGlobals::StaticConstructObject(params);
        if (!Alive(canvas) || !SetObject(tree, STR("RootWidget"), canvas)) return fail("no canvas", host, nullptr);
        Visibility(canvas, HitTestInvisible);
        UObject* view = create(type);
        if (!view) return fail("no Gameface view", host, nullptr);
        UObject* widget = GetObject(view, STR("mCohtmlWidget"));
        if (!Alive(widget)) Call(view, STR("/Script/GameSkillsTrainer.BaseMetaUIWidget:GetCohtmlWidget"), {}, &widget);
        if (!Alive(widget)) return fail("the view has no Gameface widget", host, view);
        UObject* slot = nullptr;
        Call(canvas, STR("/Script/UMG.CanvasPanel:AddChildToCanvas"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
            if (n == STR("Content")) WriteObject(v, view);
        }, &slot);
        if (!Alive(slot)) return fail("the canvas took no slot", host, view);
        // The whole screen: anchors 0..1, no offsets.
        Call(slot, STR("/Script/UMG.CanvasPanelSlot:SetAutoSize"), [](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteBoolParam(v, p, false); });
        Call(slot, STR("/Script/UMG.CanvasPanelSlot:SetAnchors"), [](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteFloats(v, p, {0.0f, 0.0f, 1.0f, 1.0f}); });
        Call(slot, STR("/Script/UMG.CanvasPanelSlot:SetAlignment"), [](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteFloats(v, p, {0.0f, 0.0f}); });
        Call(slot, STR("/Script/UMG.CanvasPanelSlot:SetOffsets"), [](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteFloats(v, p, {0.0f, 0.0f, 0.0f, 0.0f}); });
        // Never takes input: nothing in it is hit-testable or focusable, and Gameface gets no input.
        auto quiet = [&]() {
            SetBool(host, STR("bIsFocusable"), false);
            SetBool(view, STR("bIsFocusable"), false);
            SetBool(widget, STR("bReceiveInput"), false);
            Visibility(view, HitTestInvisible);
            Visibility(widget, HitTestInvisible);
        };
        quiet();
        Visibility(host, Collapsed);
        Call(host, STR("/Script/UMG.UserWidget:AddToViewport"), [](const std::wstring& n, FProperty*, std::uint8_t* v) {
            if (n == STR("ZOrder")) WriteInt32(v, ZOrder);
        });
        quiet(); // Blueprint Construct can restore defaults
        Call(widget, STR("/Script/CohtmlPlugin.CohtmlWidget:Load"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
            if (n == STR("Path")) WriteFString(v, Wide(url));
        });
        m_host = FWeakObjectPtr(host);
        m_view = FWeakObjectPtr(view);
        m_widget = FWeakObjectPtr(widget);
        m_player = FWeakObjectPtr(player);
        m_url = url;
        m_visible = false;
        m_deliveries = 0;
        m_failures = 0;
        m_ready = true;
        Log("hud: created the HUD view " + Name(view) + " (Gameface widget " + Name(widget) + ") full screen in host " + Name(host) + " at z-order " +
            std::to_string(ZOrder) + ", never taking input; loading the overlay page");
        return true;
    }

    void HudHost::Show(bool visible, double now)
    {
        m_visible = visible;
        Visibility(m_host.Get(), visible ? HitTestInvisible : Collapsed);
        m_deliveries = 0;
        m_nextDelivery = now;
    }

    // AimModVisibility(visible) to the page, a few times a second apart once it can take bindings
    // (as LiveHUD.lua did): hidden, the page stops polling the service.
    void HudHost::Deliver(double now)
    {
        UObject* widget = m_widget.Get();
        if (!widget || m_deliveries >= 4 || now < m_nextDelivery) return;
        bool ready = false;
        Call(widget, STR("/Script/CohtmlPlugin.CohtmlWidget:IsReadyForBindings"), {}, nullptr, [&](const std::wstring& n, FProperty*, const std::uint8_t* v) {
            if (n == STR("ReturnValue")) ready = *v != 0;
        });
        if (!ready) return;
        UObject* event = nullptr;
        if (!Call(widget, STR("/Script/CohtmlPlugin.CohtmlWidget:CreateJSEvent"), {}, &event) || !Alive(event)) return;
        const bool visible = m_visible;
        Call(event, STR("/Script/CohtmlPlugin.CohtmlJSEvent:AddBool"), [visible](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteBoolParam(v, p, visible); });
        Call(widget, STR("/Script/CohtmlPlugin.CohtmlWidget:TriggerJSEvent"), [event](const std::wstring& n, FProperty*, std::uint8_t* v) {
            if (n == STR("Name")) WriteFString(v, L"AimModVisibility");
            else if (n == STR("EventData")) WriteObject(v, event);
        });
        ++m_deliveries;
        m_nextDelivery = now + 1.0;
    }

    void HudHost::Remove(const char* why)
    {
        if (UObject* host = m_host.Get(); host && Alive(host)) Call(host, STR("/Script/UMG.Widget:RemoveFromParent"));
        if (m_view.Get() || m_ready) Log(std::string("hud: removed the HUD view (") + why + ")");
        m_host = m_view = m_widget = m_player = FWeakObjectPtr{};
        m_visible = false;
        m_ready = false;
    }

    void HudHost::Shutdown() { Remove("shutting down"); }
} // namespace aimmod
