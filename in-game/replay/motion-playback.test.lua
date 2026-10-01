-- Lua 5.3+. Protocol 6 render-rate playback: parse the motion window and
-- advance the view every engine frame between 30 Hz file updates.
package.preload.ReplayHUD=function()return {}end
package.preload.ReplayEffects=function()return {create=function()return {update=function()return false end,close=function()end}end}end
package.preload.ReplayHealthBars=function()return {create=function()return {update=function()end,close=function()end}end}end
local checks=0;local function check(v,m)assert(v,m);checks=checks+1 end
local poses={}
lastPresented=nil
package.loaded.ReplayMainScene={
    proofFrame=function()return {camera={0,0,0,0,10,0,90},actors={}}end,
    verify=function()end,
    create=function()return {verify=function()end,bind=function()end,frame=function(_,presented)lastPresented=presented end,close=function()end,
        proxyVersion=1,proxies=function()return 'AIMMOD_PROXIES_1\ncamera\t/Game/Map.Map:PersistentLevel.CameraActor_1\n' end,
        pose=function(camera,moves)poses[#poses+1]={camera=camera,moves=moves}end}end
}
package.loaded.ReplayHUD={create=function()return {update=function()end,close=function()end}end}
local files,loops={},{}
local now,real=1000,50.0
os.time=function()return now end
os.getenv=function()return 'root'end
os.remove=function(p)files[p]=nil;return true end
os.rename=function(a,b)files[b]=files[a];files[a]=nil;return true end
io.open=function(path,mode)
    if mode=='rb' then
        if not files[path]then return nil end
        return {read=function(_,max)return files[path]:sub(1,max)end,close=function()return true end}
    end
    return {write=function(_,body)files[path]=body;return true end,close=function()return true end}
end
LoopInGameThreadWithDelay=function(ms,fn)loops[ms]=fn end
StaticFindObject=function()return {IsValid=function()return true end,GetRealTimeSeconds=function()return real end}end
local bridge=dofile('../ue4ss/AimModNativeUI/Scripts/ReplayMainBridge.lua')
local head='AIMMOD_REPLAY_6\t1\t1\nmeta\trun-1\tScenario\tMap\t1\ntime\t5\t60\t1\t1\ncamera\t0\t0\t0\t0\t0\t0\t90\nactor\t1\t100\t0\t0\t10\t20\n'
local motion='motion\t5\t0\t0\t0\t0\t0\t0\t90\nmotion\t5.1\t0\t0\t0\t1\t10\t0\t90\nmotion\t5.2\t0\t0\t0\t2\t-170\t0\t90\nvelocity\t1\t100\t0\t0\n'
local frame=bridge.parse(head..motion)
check(#frame.motion==3 and frame.motion[2][6]==10 and frame.velocity[1][1]==100,'motion window and velocity parsed')
for _,bad in ipairs({
    head:gsub('AIMMOD_REPLAY_6','AIMMOD_REPLAY_5')..motion,          -- not negotiated
    head..'motion\t4\t0\t0\t0\t0\t0\t0\t90\n',                      -- before the frame time
    head..'motion\t5.1\t0\t0\t0\t0\t0\t0\t90\nmotion\t5\t0\t0\t0\t0\t0\t0\t90\n', -- not ordered
    head..'velocity\t2\t0\t0\t0\n',                                -- unknown target
    head..'motion\t5\t0\t0\t0\t0\t0\t0\t200\n'})do                  -- invalid FOV
    check(not pcall(bridge.parse,bad),'invalid motion rejected')
end
check(bridge.parse(head).motion==nil,'motion stays optional')
local ghostRows='ghost\t0\t0\t0\t1\t2\t0\t90\nghostmotion\t5\t0\t0\t0\t1\t2\t0\t90\nghostmotion\t5.1\t0\t0\t0\t1\t4\t0\t90\n'
local versus=bridge.parse(head..motion..ghostRows)
check(versus.ghost[5]==2 and #versus.ghostMotion==2,'comparison run rows parsed')
check(not pcall(bridge.parse,head:gsub('AIMMOD_REPLAY_6','AIMMOD_REPLAY_5')..ghostRows) and not pcall(bridge.parse,head..'ghostmotion\t5.1\t0\t0\t0\t1\t4\t0\t90\nghostmotion\t5\t0\t0\t0\t1\t4\t0\t90\n'),
    'ghost rows need protocol 6 and ordered samples')

bridge.attach({IsValid=function()return true end},function()end,function()end)
local poll=loops[33]
for _=1,3 do poll()end
now=1001;for _=1,3 do poll()end
check(files['root/AimMod/KovaaksNative/native-replay-renderer.json']:find('"protocol":6',1,true),'renderer negotiates protocol 6')
files['root/AimMod/KovaaksNative/native-replay-worker.txt']='1001'
files['root/AimMod/KovaaksNative/replay-frame.tsv']=head..motion
for _=1,3 do poll()end
check(bridge.active() and loops[1]~=nil and #poses==1,'first frame poses immediately and starts the render loop')
check(poses[1].camera[5]==0,'pose starts at the published time')
real=real+0.05;loops[1]()
local p=poses[#poses]
check(math.abs(p.camera[5]-5)<1e-6 and math.abs(p.camera[4]-0.5)<1e-6,'view advances between file updates')
check(math.abs(p.moves[1][2]-105)<1e-6,'target extrapolated with its velocity')
real=real+0.1;loops[1]()
p=poses[#poses]
check(math.abs(p.camera[5]-(10+(-180)*0.5))<1e-6,'yaw interpolates across the wrap')
real=real+0.2;loops[1]();real=real+0.2;loops[1]()
check(math.abs(poses[#poses].camera[5]-(-170))<1e-6,'clock clamps to the end of the window')
-- A paused frame (no motion) stops render-rate posing.
files['root/AimMod/KovaaksNative/replay-frame.tsv']=head:gsub('\t1\t1\n','\t2\t1\n',1):gsub('time\t5\t60\t1','time\t5\t60\t0')
poll()
local count=#poses;real=real+0.05;loops[1]()
check(#poses==count,'paused playback is not advanced')
check(files['root/AimMod/KovaaksNative/replay-proxies.tsv']:find('CameraActor_1',1,true),'proxy paths published for the native presenter')
-- Native presenter: while its apply count advances Lua leaves the view alone.
local presenterFile='root/AimMod/KovaaksNative/replay-presenter.tsv'
local revision=3
local function playFrame()
    revision=revision+1
    files['root/AimMod/KovaaksNative/replay-frame.tsv']=head:gsub('\t1\t1\n','\t'..revision..'\t1\n',1)..motion
end
files[presenterFile]='AIMMOD_PRESENTER_1\t4\t10\n';playFrame();poll()
files[presenterFile]='AIMMOD_PRESENTER_1\t5\t20\n';playFrame();poll()
count=#poses;real=real+0.05;loops[1]()
check(#poses==count and lastPresented==true,'presenter active: Lua neither poses nor moves proxies')
for _=1,3 do playFrame();poll()end -- count stopped advancing
real=real+0.05;loops[1]()
check(#poses>count and lastPresented==false,'stalled presenter hands the view back to Lua')
print('PASS '..checks..' render-rate motion checks')
