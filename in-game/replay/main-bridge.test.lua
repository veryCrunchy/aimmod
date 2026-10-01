package.preload.ReplayMainScene=function()return {}end
package.preload.ReplayHUD=function()return {}end
package.preload.ReplayEffects=function()return {create=function()return {update=function()return false end,close=function()end}end}end
package.preload.ReplayHealthBars=function()return {create=function()return {update=function()end,close=function()end}end}end
local bridge=dofile('../ue4ss/AimModNativeUI/Scripts/ReplayMainBridge.lua')
local data='AIMMOD_REPLAY_2\t1\t1\nmeta\trun-1\tScenario%20A\tMap%20A\t1\ntime\t5\t60\t1\t1\ncamera\t0\t0\t0\t0\t0\t0\t90\nactor\t1\t100\t0\t0\t10\t20\n'
local frame=bridge.parse(data)
assert(frame.meta.mapName=='Map A' and frame.meta.scenario=='Scenario A' and frame.time.playing and frame.time.time==5)
assert(#frame.actors==1 and frame.camera[7]==90)
local measured=data..'stats\t123\t10\t8\t0\t\t5\ntransport\t4\nhit\t4.9\t1\t8\t1\n'
local stats=bridge.parse(measured)
assert(stats.stats.score==123 and stats.stats.kills==0 and stats.stats.damage==nil and stats.transport==4 and stats.hit.hits==8)
assert(stats.hit.target==1,'confirmed hit target retained')
assert(not frame.stats and not frame.hit,'legacy recordings do not invent score or hit events')
local healthy=data:gsub('AIMMOD_REPLAY_2','AIMMOD_REPLAY_4')..'health\t1\t0.4\n'
local styled=data:gsub('AIMMOD_REPLAY_2','AIMMOD_REPLAY_5')..'appearance\t1\tBot%20A\t0\t45\t0\nresult\t3310\n'
assert(bridge.parse(styled).appearance[1].profile=='Bot A' and bridge.parse(styled).result==3310,'profile and final result parsed')
for _,bad in ipairs({styled..'result\t0\n',styled:gsub('appearance\t1','appearance\t2'),styled:gsub('AIMMOD_REPLAY_5','AIMMOD_REPLAY_4')})do assert(not pcall(bridge.parse,bad),'invalid appearance/result rejected')end
assert(bridge.parse(healthy).health[1].percent==0.4,'recorded health retained')
for _,bad in ipairs({healthy..'health\t1\t0.5\n',healthy:gsub('health\t1','health\t2'),healthy:gsub('0.4\n','1.4\n'),data..'health\t1\t0.4\n'})do
    assert(not pcall(bridge.parse,bad),'invalid or unnegotiated health rejected')
end
for _,bad in ipairs({measured..'transport\t5\n',measured..'stats\t1\t\t\t\t\t\n',data..'hit\t6\t1\t1\n',data..'stats\t0\t-1\t\t\t\t\n'})do
    assert(not pcall(bridge.parse,bad),'invalid replay statistics accepted')
end
assert(not bridge.parse('AIMMOD_REPLAY_2\t2\t0\n').visible)
for _,bad in ipairs({
    data:sub(1,-2),data:gsub('AIMMOD_REPLAY_2','AIMMOD_REPLAY_1'),
    data:gsub('Map%%20A',''),data:gsub('time\t5\t60','time\t61\t60'),
    data:gsub('0\t90\n','0\t180\n'),data..'actor\t1\t100\t0\t0\t10\t20\n'
})do assert(not pcall(bridge.parse,bad),'invalid main replay frame accepted')end
assert(loadfile('../ue4ss/AimModNativeUI/Scripts/ReplayHUD.lua'))
assert(loadfile('../ue4ss/AimModNativeUI/Scripts/Menu.lua'))
print('Main replay bridge checks passed: v2 metadata/time/actors, hidden frames, malformed payload rejection, Lua syntax.')

local files,loop,send,entered,left={},nil,nil,0,0
local rendered
local worldReady=true
local now=1000
os.time=function()return now end
os.getenv=function()return 'synthetic-local'end
os.remove=function(path)files[path]=nil;return true end
os.rename=function(a,b)files[b]=files[a];files[a]=nil;return true end
io.open=function(path,mode)
    if mode=='rb' then
        if not files[path]then return nil end
        return {read=function(_,max)return files[path]:sub(1,max)end,close=function()return true end}
    end
    return {write=function(_,body)files[path]=body;return true end,close=function()return true end}
end
LoopInGameThreadWithDelay=function(_,callback)loop=callback end
local base='synthetic-local/AimMod/KovaaksNative/'
package.loaded.ReplayMainScene={
    proofFrame=function()return {camera={0,0,0,0,10,0,90},actors={{1,100,0,0,10,10}}}end,
    verify=function(_,meta)assert(worldReady,'world-unavailable');assert(meta.proof or meta.mapName=='Map A','wrong map')end,
    create=function()return {proof=function()end,verify=function()end,bind=function()end,frame=function(value)rendered=value end,close=function()end}end
}
package.loaded.ReplayHUD={create=function(_,callback)send=callback;return {update=function()end,close=function()end}end}
bridge=dofile('../ue4ss/AimModNativeUI/Scripts/ReplayMainBridge.lua')
bridge.attach({IsValid=function()return true end},function()entered=entered+1 end,function()left=left+1 end)
local function tick()for _=1,3 do loop()end end
worldReady=false;tick();assert(not bridge.active())
assert(files[base..'native-replay-renderer.json']:find('world-unavailable',1,true))
worldReady=true;now=1001;tick()
assert(files[base..'native-replay-renderer.json']:find('ready',1,true),'automatic preflight recovers without approval')
files[base..'native-replay-worker.txt']='1000';files[base..'replay-frame.tsv']=data;tick()
assert(bridge.active() and entered==1,'guarded load transfers to main viewport')
assert(files[base..'native-replay-renderer.json']:find('"active":true',1,true),'keyboard ack requires actual attached HUD')
assert(files[base..'native-replay-renderer.json']:find('"replayId":"run-1"',1,true),'keyboard ack tied to displayed replay identity')
assert(files[base..'native-replay-renderer.json']:find('"revision":1',1,true),'keyboard ack tied to displayed load revision')
send('close');assert(not bridge.active() and left==1);tick();assert(entered==1,'close must not reopen stale frame')
assert(files[base..'native-replay-command.tsv']:find('\tclose\t',1,true))
assert(files[base..'native-replay-renderer.json']:find('"active":false',1,true),'closing immediately revokes keyboard scope')
files[base..'replay-frame.tsv']='AIMMOD_REPLAY_2\t2\t0\n';tick()
files[base..'replay-frame.tsv']=data:gsub('\t1\t1\n','\t3\t1\n',1);tick();assert(bridge.active())
files[base..'native-replay-worker.txt']=nil;now=1002;tick()
assert(bridge.active(),'brief heartbeat replacement gap preserves playback')
now=1005;tick();assert(not bridge.active() and left==2,'stale worker restores menu')
assert(files[base..'native-replay-renderer.json']:find('"mode":"main"',1,true))
files[base..'native-replay-worker.txt']='1005';tick()
assert(not bridge.active(),'failed visible frame cannot resurrect after heartbeat recovers')
files[base..'replay-frame.tsv']=data:gsub('\t1\t1\n','\t4\t1\n',1);tick()
assert(not bridge.active(),'new visible revision also remains fenced until hidden acknowledgement')
files[base..'replay-frame.tsv']='AIMMOD_REPLAY_2\t5\t0\n';tick()
files[base..'replay-frame.tsv']=data:gsub('\t1\t1\n','\t6\t1\n',1);tick()
assert(bridge.active(),'fresh load after close acknowledgement recovers')
-- Rejected frames are evaluated once per change, not on every idle poll.
local realPrint,diagnostics=print,0
print=function(s)if tostring(s):find('main renderer diagnostic',1,true)then diagnostics=diagnostics+1 end end
local realParse,parses=bridge.parse,0
bridge.parse=function(value)parses=parses+1;return realParse(value)end
files[base..'replay-frame.tsv']='AIMMOD_REPLAY_2\t7\t1\nmalformed\n';for _=1,5 do tick()end
assert(diagnostics==1 and not bridge.active(),'malformed frame logged once and fails closed')
parses=0;for _=1,5 do tick()end
assert(parses==0,'unchanged malformed frame is not re-parsed')
files[base..'replay-frame.tsv']=data:gsub('\t1\t1\n','\t8\t1\n',1);parses=0;for _=1,5 do tick()end
assert(parses==1 and not bridge.active(),'fenced visible frame parsed once while awaiting close acknowledgement')
now=1006;files[base..'native-replay-worker.txt']='1006'
files[base..'replay-frame.tsv']='AIMMOD_REPLAY_2\t9\t0\n';tick()
files[base..'replay-frame.tsv']=data:gsub('\t1\t1\n','\t10\t1\n',1);tick()
assert(bridge.active(),'hidden acknowledgement clears the rejection cache')
bridge.parse=realParse;print=realPrint
print('Main replay lifecycle checks passed: automatic preflight, transient recovery, transfer, close acknowledgement, worker timeout, stale-frame fence, restoration.')
