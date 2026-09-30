local serial=0
local function obj(t)serial=serial+1;local id=serial;t=t or {};t.IsValid=function(self)return not self.dead end;t.GetAddress=function()return id end;return t end
local world=obj();local activeWorld=world;local shown,auto=true,true;local made={};local removed=0;local destroyed=0
local noop=function()end
local profileVisible=false
local settings=obj({GetBooleanUserSetting=function(_,id)assert(id==6 or id==32 or id==43);if id==6 then return shown elseif id==43 then return profileVisible else return auto end end})
local pc=obj({GetWorld=function()return world end});local owner=obj({GetWorld=function()return activeWorld end})
local scratch={} -- Model borrowed reflected return storage reused by the next call.
local source=obj({GetWidgetSpace=function()return 1 end,GetDrawSize=function()scratch.X=91;scratch.Y=17;return scratch end,GetPivot=function()scratch.X=.5;scratch.Y=.25;return scratch end,
    GetDrawAtDesiredSize=function()return true end,GetTwoSided=function()return false end,GetOwnerPlayer=function()return pc end})
local relative={Translation={X=1,Y=2,Z=3}}
local function actor()
    return obj({AddComponentByClass=function(self,class,manual,transform,deferred)
        assert(class.name=='WidgetComponent' and manual==false and transform==relative and deferred==true)
        local component=obj({SetCollisionEnabled=function(_,v)assert(v==0)end,SetSimulatePhysics=function(_,v)assert(v==false)end,
            SetWidgetSpace=function(_,v)assert(v==1)end,SetDrawSize=function(_,v)assert(v.X==91 and v.Y==17)end,
            SetPivot=function(_,v)assert(v.X==.5 and v.Y==.25)end,SetDrawAtDesiredSize=function(_,v)assert(v)end,
            SetTwoSided=function(_,v)assert(not v)end,SetWindowFocusable=function(_,v)assert(not v)end,
            SetOwnerPlayer=function(_,v)assert(v==pc)end,SetTickableWhenPaused=function(self,v)self.pause=v end,
            SetComponentTickEnabled=function(self,v)self.tick=v end,SetWidget=function(self,v)self.widget=v end,
            RequestRedraw=function(self)self.redraws=(self.redraws or 0)+1 end})
        self.component=component;return component
    end,FinishAddComponent=function(self,component,manual,transform)assert(self.component==component and not manual and transform==relative);component.finished=true end,
    K2_DestroyComponent=function(_,component)assert(not component.dead);component.dead=true;destroyed=destroyed+1 end})
end
local target=actor();local presentation={actor=target,profile='Synthetic bot',health={source=source,relative=relative}}
local scene={presentation=function(id)if id==1 then return presentation end end}
StaticFindObject=function(path)
    if path:find('MetaGameUserSettings',1,true)then return {Get=function()return settings end}end
    if path:find('GameplayStatics',1,true)then return {GetPlayerController=function()return pc end}end
    if path:find('WidgetBlueprintLibrary',1,true)then return {Create=function(_,context,class,controller)
        assert(context==owner and class.name=='Healthbar_C' and controller==pc)
        local widget=obj({PalettedProgressBar=obj({SetPercent=function(self,v)self.percent=v end}),ProfileName=obj({SetVisibility=function(self,v)self.visibility=v end}),
            SetPaletteColorType=function(self,v)assert(v==5);self.palette=v end,SetProfileName=function(self,v)self.profile=v end,
            SetVisibility=function(self,v)self.visibility=v end,RemoveFromParent=function()removed=removed+1 end})
        made[#made+1]=widget;return widget
    end}end
    return {name=path:match('%.([^%.]+)$')}
end
local M=dofile('../ue4ss/AimModNativeUI/Scripts/ReplayHealthBars.lua');local checks=0
local function check(v,label)assert(v,label);checks=checks+1 end
local function frame(p)return {health=p and {{id=1,percent=p}} or nil}end
local s=M.create(owner,scene);s.update(frame());check(#made==0,'old recordings do not fabricate health')
s.update(frame(.5));check(#made==1 and target.component.finished,'real game widget attached to native owned component')
check(target.component.tick and target.component.pause and target.component.redraws==1,'native health component updates while game paused')
check(made[1].Owner==nil and made[1].palette==5 and made[1].profile=='Synthetic bot','local native palette and profile without live character delegates')
check(made[1].PalettedProgressBar.percent==.5 and made[1].visibility==4,'native bar receives recorded percent')
check(made[1].bShowProfileName==false and made[1].ProfileName.visibility==1,'native profile label obeys disabled user setting')
profileVisible=true;s.update(frame(.5));check(made[1].bShowProfileName and made[1].ProfileName.visibility==0,'native profile label obeys enabled user setting')
s.update(frame(.4));check(#made==1 and made[1].PalettedProgressBar.percent==.4,'native component reused')
s.update(frame(1));check(made[1].visibility==2,'native full-health auto hide')
auto=false;s.update(frame(1));check(made[1].visibility==4,'user auto-hide disabled')
shown=false;s.update(frame(.5));check(destroyed==1,'user disabled healthbars destroys owned component')
shown=true;s.update(frame(.5));check(#made==2,'native component recreated when user enables healthbar')
s.update(frame());check(destroyed==2,'missing sample removes stale native health')
local old=presentation;presentation=nil;s.update(frame(.5));check(#made==2,'missing skin mapping never invents generic bar');presentation=old
s.update(frame(.5));local oldTarget=target;target=actor();presentation.actor=target;s.update(frame(.5));check(destroyed==3 and oldTarget.component.dead and target.component.finished,'target replacement destroys only previous owned component')
activeWorld=obj();s.update(frame(.5));check(s.closed,'world transition closes health presentation')
local previous=destroyed;s.close();check(destroyed==previous,'close idempotent')
activeWorld=world;s=M.create(owner,scene);local before=#made;s.update(frame(0/0));check(#made==before,'invalid percent ignored')
s.update({health={{id=2,percent=.5}}});check(#made==before,'unmatched target ignored');s.close()
s=M.create(owner,scene);s.update(frame(.5));local beforeReplacement=#made
target.component.dead=true;s.update(frame(.4))
check(#made==beforeReplacement+1 and target.component.finished,'expired owned component recreated before any native calls')
made[#made].dead=true;s.update(frame(.3))
check(#made==beforeReplacement+2 and made[#made].PalettedProgressBar.percent==.3,'expired widget replaced before updating progress')
made[#made].PalettedProgressBar.dead=true;s.update(frame(.2))
check(#made==beforeReplacement+3 and made[#made].PalettedProgressBar.percent==.2,'expired blueprint binding recreated safely')
s.close()
print(checks..' native health component checks passed')
