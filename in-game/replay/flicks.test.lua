local M=dofile('../ue4ss/AimModNativeUI/Scripts/ReplayFlicks.lua')
local data='AIMMOD_FLICKS_1\tsynthetic\t1\n0.2\t0.5\t0.9\t0,0;10,2\t0,0;10,0\n'
local f,d=M.parse(data,'synthetic')
assert(#f==1 and d==1 and f[1].recorded[2][2]==2 and f[1].ideal[2][2]==0)
assert(#M.parse(data,'other')==0)
assert(#M.parse(data:sub(1,-2),'synthetic')==0)
assert(#M.parse(data:gsub('0.2','nan',1),'synthetic')==0)
assert(#M.parse(data:gsub('10,2','inf,2'),'synthetic')==0)
print('5 flick parser checks passed')

local opened=io.open
io.open=function()return {read=function()return data end,close=function()end}end
FText=function(s)return s end
local made,buttons,commands={},{},{}
local function make(kind)
    local n={kind=kind,Slot={SetOffsets=function(self,v)self.offsets=v end}}
    for _,method in ipairs({'SetVisibility','SetPadding','SetRenderTransformPivot','SetRenderTransformAngle','SetBrushColor','SetText'})do
        n[method]=function(self,v)self[method..'Value']=v end
    end
    made[#made+1]=n;return n
end
local function text(value)local n=make('TextBlock');n:SetText(value);return n end
local function button(label,x,w,callback)buttons[label]=callback end
local update=M.create('synthetic',make,function()end,text,{},button,function(a,v)commands[#commands+1]={a,v}end)
update({time=.2,playing=false})
assert(made[1].SetVisibilityValue==3 and made[2].SetTextValue=='Flick efficiency: 90%')
update({time=.2,playing=true});assert(made[1].SetVisibilityValue==2)
buttons['|']();assert(commands[1][1]=='flick-seek' and commands[1][2]==0)
buttons.Paths();update({time=.2,playing=false});assert(made[1].SetVisibilityValue==2)
io.open=opened
print('4 flick rendering and interaction checks passed')
