-- File transport for the main Unreal viewport. No embedded replay surface.
local M={}
local Scene=require('ReplayMainScene')
local HUD=require('ReplayHUD')
local Effects=require('ReplayEffects')
local HealthBars=require('ReplayHealthBars')
local base=(os.getenv('LOCALAPPDATA') or '')..'/AimMod/KovaaksNative/'
local owner,scene,hud,effects,onEnter,onExit
local healthBars
local started,approved,active=false,false,false
local nextAck,lastData,lastRevision,errorCode=0,nil,nil,nil
local sequence=0
local awaitingClose=false
local nonce=tostring(os.time())..'-'..tostring(math.floor(os.clock()*1000000))
local safety
local currentId,proofActive=nil,false
local proofFrame,currentMeta
local lastPreflightError
local lastWorkerHeartbeat
local rejectedData
local presenterCount,presenterSeen,publishedProxies,publishedVersion
local presenting=false
local motion,motionClock,motionAt,realClock
local function finite(n)return type(n)=='number' and n==n and math.abs(n)<1e12 end
local function read(name,max)
    local file=io.open(base..name,'rb');if not file then return end
    local data=file:read(max+1);file:close();if data and #data<=max then return data end
end
local function publish(name,body)
    local file=io.open(base..name..'.next','wb');if not file then return false end
    local written=file:write(body);file:close();if not written then return false end
    os.remove(base..name);return os.rename(base..name..'.next',base..name)~=nil
end
local function workerAvailable(now)
    local raw=read('native-replay-worker.txt',32)
    local heartbeat=tonumber(raw or '')
    -- File replacement can briefly deny a reader access on Windows. Retain the
    -- last observed timestamp, never renew it merely because a read failed.
    if raw~=nil then lastWorkerHeartbeat=heartbeat end
    heartbeat=lastWorkerHeartbeat
    return finite(heartbeat) and math.abs(now-heartbeat)<=3
end
local function ack(state,detail,revision)
    local keyboardActive=active and hud~=nil and not proofActive and type(currentId)=='string' and currentId:match('^[%w_-]+$')~=nil
    local id=keyboardActive and currentId or ''
    publish('native-replay-renderer.json','{"state":"'..state..'","mode":"main","protocol":6,"detail":"'..detail..'","active":'..tostring(keyboardActive or false)..',"replayId":"'..id..'","revision":'..tostring(revision or lastRevision or 0)..'}\n')
end
local function cells(line)local a={};for value in (line..'\t'):gmatch('(.-)\t')do a[#a+1]=value end;return a end
local function decode(value)
    assert(not value:gsub('%%(%x%x)',''):find('%%'),'invalid replay metadata escape')
    return (value:gsub('%%(%x%x)',function(hex)return string.char(tonumber(hex,16))end))
end
function M.parse(data)
    assert(type(data)=='string' and #data<=524288 and data:sub(-1)=='\n','incomplete replay frame')
    local lines={};for line in data:gmatch('([^\n]+)\n')do lines[#lines+1]=cells(line:gsub('\r$',''))end
    local h=lines[1] or {};assert(#h==3 and (h[1]=='AIMMOD_REPLAY_2' or h[1]=='AIMMOD_REPLAY_3' or h[1]=='AIMMOD_REPLAY_4' or h[1]=='AIMMOD_REPLAY_5' or h[1]=='AIMMOD_REPLAY_6'),'unsupported main replay frame')
    local v6=h[1]=='AIMMOD_REPLAY_6'
    local revision=tonumber(h[2]);assert(finite(revision) and revision>=0 and revision%1==0,'invalid revision')
    assert(h[3]=='0' or h[3]=='1','invalid visibility')
    local frame={revision=revision,visible=h[3]=='1',actors={}}
    if not frame.visible then assert(#lines==1,'hidden frame payload');return frame end
    local meta=lines[2] or {};assert(#meta==5 and meta[1]=='meta','missing replay metadata')
    frame.meta={id=decode(meta[2]),scenario=decode(meta[3]),mapName=decode(meta[4]),mapScale=tonumber(meta[5])}
    assert(#frame.meta.id>0 and #frame.meta.mapName>0 and finite(frame.meta.mapScale) and frame.meta.mapScale>0,'invalid map metadata')
    local t=lines[3] or {};assert(#t==5 and t[1]=='time' and (t[4]=='0' or t[4]=='1'),'missing replay time')
    frame.time={time=tonumber(t[2]),duration=tonumber(t[3]),playing=t[4]=='1',speed=tonumber(t[5])}
    assert(finite(frame.time.time) and finite(frame.time.duration) and frame.time.time>=0 and frame.time.duration>=frame.time.time
        and finite(frame.time.speed) and frame.time.speed>=.1 and frame.time.speed<=4,'invalid replay time')
    local c=lines[4] or {};assert(#c==8 and c[1]=='camera','missing camera');frame.camera={}
    for i=2,8 do local n=tonumber(c[i]);assert(finite(n),'invalid camera');frame.camera[#frame.camera+1]=n end
    assert(frame.camera[7]>1 and frame.camera[7]<179,'invalid field of view')
    local seen={}
    for i=5,#lines do
        local row=lines[i]
        if row[1]=='stats' then
            assert(#row==7 and not frame.stats,'invalid replay statistics')
            frame.stats={}
            for j,key in ipairs({'score','shots','hits','kills','damage','seconds'})do
                if row[j+1]~='' then
                    local n=tonumber(row[j+1]);assert(finite(n) and (key=='score' or n>=0),'invalid replay statistic')
                    frame.stats[key]=n
                end
            end
        elseif row[1]=='transport' then
            assert(#row==2 and frame.transport==nil,'invalid replay transport')
            local n=tonumber(row[2]);assert(finite(n) and n>=0 and n%1==0,'invalid replay transport');frame.transport=n
        elseif row[1]=='hit' then
            assert((#row==4 or #row==5) and not frame.hit,'invalid replay hit')
            local t,delta,hits=tonumber(row[2]),tonumber(row[3]),tonumber(row[4])
            assert(finite(t) and t>=0 and t<=frame.time.time+0.0001 and finite(delta) and delta>0 and finite(hits) and hits>=delta,'invalid replay hit')
            frame.hit={time=t,delta=delta,hits=hits}
            if row[5] and row[5]~='' then
                local id=tonumber(row[5]);assert(finite(id) and id>0 and id%1==0,'invalid hit target');frame.hit.target=id
            end
        elseif row[1]=='health' then
            assert((h[1]=='AIMMOD_REPLAY_4' or h[1]=='AIMMOD_REPLAY_5' or v6) and #row==3,'invalid replay health')
            local id,percent=tonumber(row[2]),tonumber(row[3])
            frame.health=frame.health or {};frame.healthIds=frame.healthIds or {}
            assert(finite(id) and id>0 and id%1==0 and not frame.healthIds[id] and finite(percent) and percent>=0 and percent<=1 and #frame.health<128,'invalid replay health')
            frame.healthIds[id]=true;frame.health[#frame.health+1]={id=id,percent=percent}
        elseif row[1]=='result' then
            assert((h[1]=='AIMMOD_REPLAY_5' or v6) and #row==2 and frame.result==nil,'invalid replay result')
            frame.result=tonumber(row[2]);assert(finite(frame.result),'invalid replay result')
        elseif row[1]=='appearance' then
            assert((h[1]=='AIMMOD_REPLAY_5' or v6) and #row==6,'invalid replay appearance')
            frame.appearance=frame.appearance or {};frame.appearanceIds=frame.appearanceIds or {}
            local id=tonumber(row[2]);local profile=decode(row[3]);local rotation={tonumber(row[4]),tonumber(row[5]),tonumber(row[6])}
            assert(finite(id) and id>0 and id%1==0 and not frame.appearanceIds[id] and #frame.appearance<128 and #profile>0 and #profile<=256 and not profile:find('%c'),'invalid replay appearance')
            for i=1,3 do assert(finite(rotation[i]),'invalid replay rotation')end
            frame.appearanceIds[id]=true;frame.appearance[#frame.appearance+1]={id=id,profile=profile,rotation=rotation}
        elseif row[1]=='motion' then
            -- Protocol 6: camera samples ahead of this frame for render-rate playback.
            assert(v6 and #row==9,'invalid replay motion')
            frame.motion=frame.motion or {}
            local m={};for j=2,9 do local n=tonumber(row[j]);assert(finite(n),'invalid replay motion');m[#m+1]=n end
            local previous=frame.motion[#frame.motion]
            assert(#frame.motion<64 and m[1]>=frame.time.time-0.0001 and (not previous or m[1]>=previous[1]) and m[8]>1 and m[8]<179,'invalid replay motion')
            frame.motion[#frame.motion+1]=m
        elseif row[1]=='clock' then
            -- Wall-clock publication instant for the native presenter.
            assert(v6 and #row==3 and tonumber(row[2]) and finite(tonumber(row[3])),'invalid replay clock')
        elseif row[1]=='velocity' then
            assert(v6 and #row==5,'invalid replay velocity')
            local id,x,y,z=tonumber(row[2]),tonumber(row[3]),tonumber(row[4]),tonumber(row[5])
            assert(finite(id) and id>0 and id%1==0 and finite(x) and finite(y) and finite(z),'invalid replay velocity')
            frame.velocity=frame.velocity or {};assert(frame.velocity[id]==nil,'invalid replay velocity');frame.velocity[id]={x,y,z}
        else
            assert(#row==7 and row[1]=='actor' and #frame.actors<128,'invalid actor row')
            local a={};for j=2,7 do local n=tonumber(row[j]);assert(finite(n),'invalid actor');a[#a+1]=n end
            assert(a[1]>0 and a[1]%1==0 and not seen[a[1]] and a[5]>0 and a[6]>=a[5],'invalid target')
            seen[a[1]]=true;frame.actors[#frame.actors+1]=a
        end
    end
    for _,health in ipairs(frame.health or {})do assert(seen[health.id],'unknown health target')end
    for _,appearance in ipairs(frame.appearance or {})do assert(seen[appearance.id],'unknown appearance target')end
    for id in pairs(frame.velocity or {})do assert(seen[id],'unknown velocity target')end
    frame.healthIds=nil
    frame.appearanceIds=nil
    return frame
end
local function close()
    if healthBars then pcall(healthBars.close);healthBars=nil end
    if effects then pcall(effects.close);effects=nil end
    if hud then pcall(hud.close);hud=nil end
    if scene then pcall(scene.close);scene=nil end
    if active and onExit then pcall(onExit)end
    active=false;lastData=nil;lastRevision=nil;currentId=nil;proofActive=false;proofFrame=nil;currentMeta=nil;lastWorkerHeartbeat=nil
    motion=nil;motionClock=nil;motionAt=nil
    if publishedProxies then publish('replay-proxies.tsv','AIMMOD_PROXIES_1\n');publishedProxies=nil;publishedVersion=nil end
    presenting=false
    ack(approved and 'ready' or 'unverified','validated')
    if safety then safety.snapshot()end
end
local function send(action,value)
    sequence=sequence+1
    publish('native-replay-command.tsv',nonce..'-'..sequence..'\t'..action..'\t'..tostring(value or '')..'\n')
    if action=='close' then awaitingClose=true;close()end
end
local function fail(code)
    errorCode=code;approved=false;send('close');ack('error',code)
end
local function errorDetail(reason)
    local text=tostring(reason)
    for _,code in ipairs({'map-mismatch','scenario-mismatch','challenge-active','scenario-active','world-unavailable'})do
        if text:find(code,1,true)then return code end
    end
    if text:find('identity mismatch',1,true)then return 'map-mismatch'end
    if text:find('inactive challenge',1,true)then return 'challenge-active'end
    if text:find('context unavailable',1,true) or text:find('context changed',1,true) or text:find('map state unavailable',1,true)then return 'world-unavailable'end
    if text:find('worker unavailable',1,true)then return 'worker-unavailable'end
    return 'main-render-failed'
end
-- Render-rate playback (protocol 6). The file carries 0.4 s of camera samples
-- ahead of its playback time; a local clock advances every engine frame and
-- is pulled gently toward the published time, so the view moves at render
-- rate instead of the 30 Hz publication rate. Targets are extrapolated with
-- their published velocity until the next frame.
local function realTime()
    local ok,value=pcall(function()
        if not realClock or not realClock:IsValid() then realClock=StaticFindObject('/Script/Engine.Default__GameplayStatics')end
        return realClock:GetRealTimeSeconds(owner)
    end)
    if ok and finite(value) then return value end
end
local function lerpAngle(a,b,u)return a+(((b-a+540)%360)-180)*u end
local function applyMotion(now)
    local m=motion
    if not m or not scene or not active or proofActive then return end
    local dt=motionAt and now-motionAt or 0;motionAt=now
    if dt<0 or dt>0.25 then dt=0 end
    motionClock=(motionClock or m.time)+dt*m.speed
    local samples=m.samples
    local t=math.max(samples[1][1],math.min(motionClock,samples[#samples][1]))
    local i=1;while i<#samples and samples[i+1][1]<t do i=i+1 end
    local a,b=samples[i],samples[math.min(i+1,#samples)]
    local u=b[1]>a[1] and (t-a[1])/(b[1]-a[1]) or 0
    local camera={a[2]+(b[2]-a[2])*u,a[3]+(b[3]-a[3])*u,a[4]+(b[4]-a[4])*u,a[5]+(b[5]-a[5])*u,
        lerpAngle(a[6],b[6],u),lerpAngle(a[7],b[7],u),a[8]+(b[8]-a[8])*u}
    local moves={}
    local ahead=math.min(t-m.time,0.1)
    for _,actor in ipairs(m.actors)do
        local v=m.velocity[actor[1]]
        if v then moves[#moves+1]={actor[1],actor[2]+v[1]*ahead,actor[3]+v[2]*ahead,actor[4]+v[3]*ahead}end
    end
    scene.pose(camera,moves)
end
local function takeMotion(frame,now)
    if not frame.motion or #frame.motion<2 or not frame.time.playing or not scene or not scene.pose then motion=nil;motionClock=nil;return end
    local published=frame.time.time
    -- Seeks, pauses and large drift snap; small drift is corrected slowly.
    if not motion or not motionClock or math.abs(motionClock-published)>0.25 then motionClock=published
    else motionClock=motionClock+(published-motionClock)*0.1 end
    motion={samples=frame.motion,time=published,speed=frame.time.speed,actors=frame.actors,velocity=frame.velocity or {}}
    if now then motionAt=now;applyMotion(now)end
end
-- Native presenter (AimModCore) handshake: while its apply count advances it
-- owns the view and target locations; Lua then only spawns and styles.
local function presenterActive()
    local raw=read('replay-presenter.tsv',64)
    local count=raw and tonumber(raw:match('^AIMMOD_PRESENTER_1\t%d+\t(%d+)'))
    if count and count>0 and count~=presenterCount then presenterSeen=0 else presenterSeen=(presenterSeen or 3)+1 end
    if count then presenterCount=count end
    return presenterSeen~=nil and presenterSeen<=2
end
local function publishProxies()
    if not scene or not scene.proxies or (publishedProxies and scene.proxyVersion==publishedVersion) then return end
    local ok,body=pcall(scene.proxies)
    if ok and (body==publishedProxies or publish('replay-proxies.tsv',body))then publishedProxies=body;publishedVersion=scene.proxyVersion end
end
local motionStarted=false
local function startMotionLoop()
    if motionStarted then return end;motionStarted=true
    LoopInGameThreadWithDelay(1,function()
        if not motion or presenting then return end
        local ok,reason=pcall(function()local now=realTime();if now then applyMotion(now)end end)
        if not ok then motion=nil;print('[AimModReplay] motion playback stopped: '..tostring(reason)..'\n')end
    end)
end
function M.active()return active end
function M.close()send('close')end
function M.attach(menu,enter,leave)
    close();owner=menu;onEnter=enter;onExit=leave;approved=false;errorCode=nil
    ack('unverified','checking-world')
    if started then return end;started=true
    local idleTicks=0
    LoopInGameThreadWithDelay(33,function()
        if not active then idleTicks=idleTicks+1;if idleTicks%3~=0 then return end end
        local ok,reason=pcall(function()
            if not owner or not owner:IsValid()then if active then fail('owner-unavailable')end;return end
            local request=read('main-replay-proof.request',32)
            if request then
                os.remove(base..'main-replay-proof.request');request=request:gsub('%s+$','')
                if request=='close' then approved=false;errorCode=nil;close();ack('unverified','checking-world')
                elseif request=='approve' then assert(proofActive and active and scene,'main proof required');scene.verify();approved=true;close();ack('ready','validated')
                elseif request=='pan' then
                    assert(proofActive and proofFrame and scene,'main sphere proof required')
                    local camera={};for i,v in ipairs(proofFrame.camera)do camera[i]=v end
                    camera[5]=camera[5]+15
                    scene.frame({camera=camera,actors=proofFrame.actors});ack('unverified','main-pan-displayed')
                    if safety then safety.snapshot()end
                elseif request=='sphere' then
                    approved=false;errorCode=nil;close();local okProbe,probe=pcall(require,'ReplaySafetyProbe');if okProbe then safety=probe;safety.start();safety.snapshot()end
                    proofFrame=Scene.proofFrame(owner)
                    scene=Scene.create(owner);scene.bind({proof=true});scene.frame(proofFrame);active=true;proofActive=true
                    if onEnter then onEnter()end
                    hud=HUD.create(owner,send);ack('unverified','main-sphere-displayed')
                else error('unknown main proof')end
                return
            end
            if proofActive and scene then scene.verify();return end
            -- Read-only preflight recovers when a challenge, loading screen or world
            -- transition ends. A failed visible transport remains fenced until close.
            local now=os.time()
            if not active and now>=nextAck then
                local ready,why=pcall(Scene.verify,owner,{proof=true})
                if not ready and tostring(why)~=lastPreflightError then
                    lastPreflightError=tostring(why);print('[AimModReplay] preflight: '..lastPreflightError..'\n')
                elseif ready then lastPreflightError=nil end
                -- A newly approved world re-evaluates a frame rejected earlier.
                if ready and not approved then rejectedData=nil end
                approved=ready
                if ready then errorCode=nil else errorCode=errorDetail(why)end
            end
            if active and not proofActive then
                assert(workerAvailable(now),'replay worker unavailable')
            end
            if now>=nextAck then nextAck=now+1;ack(approved and 'ready' or (errorCode and 'error' or 'unverified'),errorCode or (approved and 'validated' or 'checking-world'))end
            if active then presenting=presenterActive() end
            local data=read('replay-frame.tsv',524288)
            if not data or data==lastData or (not active and data==rejectedData) then
                if active and scene then scene.verify()end
                if hud and hud.refreshControls then hud.refreshControls()end
                return
            end
            local parsed,frame=pcall(M.parse,data)
            -- A malformed file fails once, not on every poll until it changes.
            if not parsed then rejectedData=data;error(frame,0)end
            if not frame.visible then close();awaitingClose=false;rejectedData=nil;lastData=data;return end
            -- Fenced or unapproved frames are not re-parsed (up to 512 KiB) on
            -- every idle poll; a changed file or a new approval re-evaluates.
            if awaitingClose or not approved then rejectedData=data;return end
            assert(workerAvailable(now),'replay worker unavailable')
            if scene and currentId~=frame.meta.id then close()end
            if not scene then
                Scene.verify(owner,frame.meta)
                scene=Scene.create(owner);scene.bind(frame.meta)
                -- Window closes only after the scene has verified map and score isolation.
                scene.frame(frame);active=true;currentId=frame.meta.id;currentMeta=frame.meta;publishProxies()
                if onEnter then onEnter()end
                hud=HUD.create(owner,send)
                effects=Effects.create(owner)
                healthBars=HealthBars.create(owner,scene)
                ack('ready','validated',frame.revision)
            else
                assert(frame.meta.scenario==currentMeta.scenario,'scenario-mismatch')
                assert(frame.meta.mapName==currentMeta.mapName and frame.meta.mapScale==currentMeta.mapScale,'map-mismatch')
                scene.frame(frame,frame.time.playing and presenting);publishProxies()
            end
            if presenting then motion=nil else takeMotion(frame,frame.motion and realTime() or nil)end
            if motion then startMotionLoop()end
            local feedback=effects and effects.update(frame) or false
            if healthBars then healthBars.update(frame)end
            hud.update(frame.time,frame.stats,feedback,frame.transport,frame.result);lastData=data;lastRevision=frame.revision
        end)
        if not ok then print('[AimModReplay] main renderer diagnostic: '..tostring(reason)..'\n');fail(errorDetail(reason))end
    end)
end
return M
