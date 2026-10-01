-- Lua 5.3+. Mod entry point with synthetic UE4SS globals and module doubles.
local checks=0;local function check(v,m)assert(v,m);checks=checks+1 end
local realPrint=print;local printed={}
print=function(s)printed[#printed+1]=tostring(s)end
local calls={}
local function install()
    for _,name in ipairs({'Telemetry','ReplayCapture','Menu'})do package.loaded[name]=nil end
    package.preload.Telemetry=function()return {start=function()calls[#calls+1]='telemetry';error('synthetic telemetry failure')end}end
    package.preload.ReplayCapture=function()return {start=function()calls[#calls+1]='capture' end}end
    package.preload.Menu=function()return {start=function()calls[#calls+1]='menu' end}end
end
local globals={'RegisterHook','StaticFindObject','StaticConstructObject','FindAllOf','FindFirstOf','LoopInGameThreadWithDelay','ExecuteInGameThreadWithDelay','FName','FText'}
-- Missing game-thread scheduler: nothing registers.
install()
for _,name in ipairs(globals)do _G[name]=function()end end
LoopInGameThreadWithDelay=nil
dofile('../ue4ss/AimModNativeUI/Scripts/main.lua')
check(#calls==0,'missing UE4SS scheduler disables every observer')
check(printed[#printed]:find('LoopInGameThreadWithDelay',1,true),'missing API is reported')
-- Complete API: one failing component does not prevent the others.
install();LoopInGameThreadWithDelay=function()end
dofile('../ue4ss/AimModNativeUI/Scripts/main.lua')
check(calls[1]=='telemetry' and calls[2]=='capture' and calls[3]=='menu','components start independently after a failure')
print=realPrint
print('PASS '..checks..' mod entry checks')
