local transition=dofile('../ue4ss/AimModNativeUI/Scripts/ReplayMapTransition.lua')
local checks=0
local function check(ok,name) assert(ok,name); checks=checks+1 end
local function copy(t) local r={} for k,v in pairs(t) do r[k]=v end return r end
local function fixture()
    local s={worldId=1,controllerId=2,scenarioId=3,repositoryId=4,paused=true,active=false,challenge=false,
        captureActive=false,editor=false,loading=false,downloading=false,mapLoading=false,queueSeconds=0,
        mapName='Original.json',mapScale=1,geometryReady=true,fingerprint='original'}
    local time,loads,restores=0,0,0; local tx
    local a={observerReady=true,read=function()return copy(s)end,clock=function()return time end,
        save=function()return copy(s)end,
        load=function(t)loads=loads+1;s.mapName=t.mapName;s.mapScale=t.mapScale;s.fingerprint=t.fingerprint end,
        restore=function(old)restores=restores+1;s=copy(old);tx.observe(4,true)end}
    tx=transition.new(a)
    return {tx=tx,adapter=a,state=function()return s end,loads=function()return loads end,restores=function()return restores end,timeout=function()time=11 end}
end
local desired={mapName='Replay.json',mapScale=4,fingerprint='replay'}
for _,name in ipairs({'../map.json','folder/map.json','folder\\map.json','C:map.json'}) do
    local f=fixture();check(not f.tx.begin({mapName=name,mapScale=1}) and f.loads()==0,'map filename rejects path '..name)
end
for field,value in pairs({paused=false,active=true,challenge=true,captureActive=true,editor=true,loading=true,downloading=true,mapLoading=true,queueSeconds=1}) do
    local f=fixture();f.state()[field]=value
    check(not f.tx.begin(desired) and f.loads()==0,'reject unsafe state '..field)
end
local f=fixture();local ok,reason=f.tx.begin(desired)
check(not ok and reason=='map-loading' and f.loads()==1,'name mutation without parser proof not ready')
f.tx.observe(999,true);check(not f.tx.poll(),'unrelated repository success ignored')
f.tx.observe(4,true);check(f.tx.poll() and f.tx.phase=='ready','matching successful parser permits ready')
check(f.tx.close() and f.tx.phase=='closed' and f.state().mapName=='Original.json','map restored with proof')
check(f.restores()==1 and f.loads()==1,'single load and restore')
f=fixture();f.tx.begin(desired);f.tx.observe(4,false)
check(not f.tx.poll() and f.tx.reason=='map-parse-failed','parse failure overrides matching name')
check(f.tx.close(),'failed parse remains recoverable')
f=fixture();f.tx.begin(desired);f.timeout()
check(not f.tx.poll() and f.tx.reason=='map-load-timeout','missing completion times out')
f=fixture();f.tx.begin(desired);f.tx.observe(4,true);f.state().geometryReady=false
check(not f.tx.poll(),'empty geometry is not a completed load')
f=fixture();f.tx.begin(desired);f.tx.observe(4,true);f.state().fingerprint='wrong'
check(not f.tx.poll() and f.tx.reason=='map-fingerprint-mismatch','geometry mismatch rejected')
f=fixture();f.tx.begin(desired);f.state().worldId=99
check(not f.tx.close() and f.restores()==0,'never restore through world travel')
f=fixture();f.tx.begin(desired);f.state().active=true
check(not f.tx.close() and f.restores()==0,'never cancel new legitimate session to restore')
f=fixture();f.adapter.observerReady=false
check(not f.tx.begin(desired) and f.loads()==0,'missing parser observer prevents mutation')
f=fixture();f.adapter.save=function()return nil end
check(not f.tx.begin(desired) and f.loads()==0,'no mutation without restore snapshot')
local registered,removed,pre,post=0,0
StaticFindObject=function()return {IsValid=function()return true end}end
RegisterHook=function(_,before,after)registered=registered+1;pre=before;post=after;return 1,2 end
UnregisterHook=function(_,a,b)check(a==1 and b==2,'correct unregister pair');removed=removed+1 end
local received
local stop=transition.observeParser(function(id,result)received={id,result}end)
local ref=function(v)return {get=function()return v end}end
local context=ref({IsValid=function()return true end,GetAddress=function()return 4 end})
check(pre()==nil and post(context,ref(true),ref({}),ref(4))==nil,'observer never overrides native result')
check(received[1]==4 and received[2]==true,'original return read from second post argument')
post(context,ref(false));check(received[2]==false,'negative parser result preserved')
stop();check(registered==1 and removed==1,'observer lifetime explicit')
print('Replay map transition checks passed ('..checks..').')
