-- Synthetic native ownership and noninteraction checks. No game is launched.
local checks=0
local function check(value,message)assert(value,message);checks=checks+1 end
local function obj(t)t=t or {};t.IsValid=function(self)return not self.invalid end;return t end
local now=100
local realNow=100
local clockUnavailable=false
os.time=function()return now end
local settings='{"gameEnabled":true,"obsEnabled":false,"opacity":1,"stats":{"visible":true,"x":2,"y":12,"width":240},"versus":{"visible":true,"x":75,"y":12,"width":300}}'
local files={['overlay-settings.json']=settings,['live-overlay-url.txt']='http://127.0.0.1:12345/abc123/overlay?surface=game'}
io.open=function(path,mode)
    check(mode=='rb','only reads local configuration')
    local text=files[path:match('[^/]+$')];if not text then return nil end
    return {read=function(_,limit)return text:sub(1,limit)end,close=function()end}
end
RegisterKeyBind=function()error('no input-thread callbacks')end
RegisterHook=function()error('no input hooks')end
local player=obj();local owner=obj({GetOwningPlayer=function()return player end})
local widgets,renderers,slots={}, {}, {}
local function widget()
    local w=obj({WidgetTree=obj()})
    w.SetVisibility=function(self,value)self.visibility=value end
    w.RemoveFromParent=function(self)self.removed=true end
    w.SetUserFocus=function()error('live HUD must not take focus')end
    w.AddToViewport=function(self,z)
        check(z==5000,'live surface below replay controls')
        check(self.WidgetTree.RootWidget.kind=='CanvasPanel','palette root replaced before mounting')
        check(self.visibility==1,'mount hidden without flash')
        self.bIsFocusable=true -- Simulate Blueprint Construct restoring defaults.
        self.attached=true
    end
    widgets[#widgets+1]=w;return w
end
local lib=obj({SetInputMode_UIOnlyEx=function()error('must preserve game input')end})
lib.Create=function(_,context,class,controller)
    check(context==owner and controller==player,'owned player context')
    local w=widget()
    if class.path:find('MetaGraphWidget',1,true)then
        local r=obj({events={},loads={}})
        r.SetVisibility=function(self,value)self.visibility=value end
        r.Load=function(self,url)self.loads[#self.loads+1]=url end
        r.IsReadyForBindings=function(self)return not self.notReady end
        r.CreateJSEvent=function()return {AddBool=function(self,value)self.value=value end}end
        r.TriggerJSEvent=function(self,name,event)check(name=='AimModVisibility','visibility contract');self.events[#self.events+1]=event.value end
        w.GetCohtmlWidget=function()return r end;renderers[#renderers+1]=r
    end
    return w
end
StaticFindObject=function(path)
    if path:find('Default__GameplayStatics',1,true)then return obj({GetRealTimeSeconds=function(_,context)assert(context==owner);if clockUnavailable then error('synthetic clock unavailable')end;return realNow end})end
    if path:find('Default__WidgetBlueprintLibrary',1,true)then return lib end
    return obj({path=path})
end
StaticConstructObject=function(class,outer)
    check(class.path=='/Script/UMG.CanvasPanel','only native canvas constructed')
    local canvas=obj({kind='CanvasPanel'})
    canvas.SetVisibility=function(self,value)self.visibility=value end
    canvas.AddChildToCanvas=function(_,child)
        local slot={child=child}
        for _,name in ipairs({'Anchors','Alignment','AutoSize','Offsets'})do slot['Set'..name]=function(self,value)self[name]=value end end
        slots[#slots+1]=slot;return slot
    end
    return canvas
end
local hud=dofile('../ue4ss/AimModNativeUI/Scripts/LiveHUD.lua')
local active={active=true,paused=false,id='synthetic-run'}
local function update(menu,replay,state)hud.update(menu or false,replay or false,state or active)end
local function tick()now=now+1;realNow=realNow+1 end
hud.attach(owner)
update(true)
check(#widgets==0,'no view created in menus')
tick();update()
local host,view,renderer=widgets[1],widgets[2],renderers[1]
check(host.attached and host.visibility==3,'live HUD shown hit-test-invisible')
check(not host.bIsFocusable and not view.bIsFocusable,'blueprint focusability reset')
check(renderer.bReceiveInput==false and renderer.visibility==3,'Gameface cannot receive input')
check(host.WidgetTree.RootWidget.visibility==3 and view.visibility==3,'all owned surfaces noninteractive')
check(slots[1].Anchors.Maximum.X==1 and slots[1].Anchors.Maximum.Y==1,'viewport stretched both axes')
check(slots[1].Offsets.Left==0 and slots[1].Offsets.Top==0 and slots[1].Offsets.Right==0 and slots[1].Offsets.Bottom==0,'full viewport without inherited padding')
check(slots[1].AutoSize==false,'explicit canvas stretching')
check(renderer.events[#renderer.events]==true,'web polling resumed')
update(true)
check(host.visibility==1 and renderer.events[#renderer.events]==false,'menu hides view and web polling immediately')
update();update(false,true)
check(host.visibility==1,'replay hides live surface')
update(false,false,{active=true,paused=true})
check(host.visibility==1,'paused live run hidden')
update(false,false,{active=false,paused=false})
check(host.visibility==1,'idle hidden')
update(false,false,{active=true})
check(host.visibility==1,'unknown pause state fails closed')
update()
check(host.visibility==3 and #widgets==2,'same view reused across sessions')
local transient={active=true,paused=false,transient=true,id='synthetic-run'}
local eventCount=#renderer.events
realNow=realNow+.1;update(false,false,transient)
check(host.visibility==3 and #renderer.events==eventCount,'short proven transient does not flicker visibility or emit hide event')
realNow=realNow+.11;update(false,false,transient)
check(host.visibility==1,'transient grace expires after 200ms real time')
realNow=realNow+.01;update(false,false,transient)
check(host.visibility==1,'repeated transient samples cannot renew grace or reopen hidden HUD')
update();update(false,false,{active=true,paused=false,transient=true,id='different-run'})
check(host.visibility==1,'new run cannot borrow previous run grace')
update();update(false,false,{active=false,paused=false,transient=true,id='synthetic-run'})
check(host.visibility==1,'explicit run end hides immediately even with transient flag')
update();update(false,false,{active=true,paused=true,transient=true,id='synthetic-run'})
check(host.visibility==1,'pause hides immediately during grace')
update();update(true,false,transient)
check(host.visibility==1,'menu hides immediately during grace')
update();update(false,true,transient)
check(host.visibility==1,'replay hides immediately during grace')
update();hud.update(false,false,nil)
check(host.visibility==1,'unknown telemetry cannot borrow grace')
update();realNow=realNow-1;update(false,false,transient)
check(host.visibility==1,'native clock reset invalidates grace')
clockUnavailable=true;update();update(false,false,transient)
check(host.visibility==1,'unavailable wall clock fails closed instead of extending grace')
clockUnavailable=false;update()

for _,text in ipairs({settings:gsub('true','false',1),settings..'x',settings:sub(1,-2),settings:gsub('"opacity":1','"opacity":+1'),settings:gsub('"opacity":1','"opacity":01'),settings:gsub('"width":240','"width":700'),settings:gsub('"gameEnabled":true','"gameEnabled":true,"gameEnabled":true'),'{}',string.rep('x',4097)})do
    files['overlay-settings.json']=text;tick();update()
    check(host.visibility==1,'invalid or disabled configuration fails closed')
end
files['overlay-settings.json']=nil;tick();update();check(host.visibility==1,'missing configuration fails closed')
files['overlay-settings.json']=settings;tick();update();check(host.visibility==3,'valid settings recover')
files['live-overlay-url.txt']='https://example.com/abc/overlay?surface=game';tick();update();check(host.visibility==1,'reject nonlocal URL')
files['live-overlay-url.txt']='http://127.0.0.1:23456/def456/overlay?surface=game';tick();update()
check(#renderer.loads==2 and renderer.loads[2]==files['live-overlay-url.txt'],'worker capability replacement reloads owned view')
local count=#renderer.events
for _=1,10 do update()end
check(#renderer.events==count,'unchanged rapid updates do not spam events')
hud.close();check(host.removed and host.visibility==1,'close removes owned viewport view')
tick();update();check(#widgets==4,'can recreate owned view after closure')
owner.invalid=true;update();check(widgets[3].removed,'world owner invalidation removes surface')
print('live HUD '..checks..' checks passed')
