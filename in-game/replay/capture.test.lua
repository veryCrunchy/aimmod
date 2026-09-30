-- Lua 5.3+; Unreal objects and all filesystem writes are test doubles.
local checks=0
local function check(value,message) assert(value,message); checks=checks+1 end
for _,mode in ipairs({'native-proxy','plain-array','wrapped-array','missing-map','invalid-map-scale','wrong-map-class'}) do
    local clock,loops,hooks,files,opens,scans=10,{},{},{},0,0
    local settingsText,settingsReadFailure;local settingsReads=0
    local mapReads,scaleReads=0,0
    local profileReads=0
    local nativeTime
    local nativeActive=true
    local paused=false
    local state={active=false,id='synthetic',scenario='Synthetic "target"\nTest',startEvent='started'}
    local address=1000
    local function object(t) address=address+1;local id=address;t.GetAddress=t.GetAddress or function()return id end;t.IsValid=function() return true end; return t end
    local instance=object({})
    local scenario=object({GetName=function()return state.scenario end,IsActive=function()return nativeActive end,IsInChallenge=function()return nativeActive end})
    local manager=object({GetOuter=function()return instance end,GetCurrentScenario=function()return scenario end,IsInChallenge=function()return true end,
        IsScenarioLoading=function()return false end,GetChallengeQueueTimeRemaining=function()return -1 end,GetChallengeTimeElapsed=function()return nativeTime or clock end})
    local playerActor=object({GetAddress=function() return 50 end})
    -- Distinct wrappers for the same UObject must match by native address.
    local playerAlias=object({GetAddress=function() return 50 end})
    local ownerReads=0
    local recorder=object({GetAddress=function()return 51 end,GetOwner=function()ownerReads=ownerReads+1;return playerAlias end})
    playerActor.PlaybackComponent=recorder
    local weapon=object({ShotsHitThisSession=0,ShotsFiredThisSession=0})
    playerActor.WeaponHandler=object({GetWeapons=function()return {weapon,weapon}end})
    playerActor.GetKillCount=function()return 2 end;playerActor.DamageDone=12
    local actor=object({GetFullName=function() return 'test-target' end,GetAddress=function() return 100 end,
        GetCharacterProfileName=function()profileReads=profileReads+1;return 'Synthetic target profile' end,
        K2_GetActorRotation=function()return {Pitch=10,Yaw=20,Roll=30}end,
        K2_GetActorLocation=function() return {X=100,Y=0,Z=0} end,GetCurrentHealthPercent=function()return 0.75 end,
        CapsuleComponent=object({GetScaledCapsuleRadius=function() return 10 end,GetScaledCapsuleHalfHeight=function() return 10 end})})
    local player=object({MyCharacter=playerActor,GetFullName=function() return 'test-player' end})
    local camera=object({GetCameraLocation=function() return {X=0,Y=0,Z=0} end,
        GetCameraRotation=function() return {Pitch=0,Yaw=0,Roll=0} end,GetFOVAngle=function() return 90 end})
    local function ref(o) return setmetatable({get=function() return o end},{__index=function(_,key) error('RemoteUnrealParam has no '..key) end}) end
    local world=object({IsA=function() return mode~='wrong-map-class' end,
        GetCurrentMapName=function()
            mapReads=mapReads+1
            if mode=='missing-map' then return '' end
            if mode=='plain-array' then return 'Synthetic Arena.json' end
            return {ToString=function() return 'Synthetic Arena.json' end}
        end,
        GetMapScale=function() scaleReads=scaleReads+1; return mode=='invalid-map-scale' and 0/0 or 4 end,
        GetCharacters=function()
        if mode=='plain-array' then return {playerAlias,actor} end
        if mode=='wrapped-array' then return {ref(playerAlias),ref(actor)} end
        -- Table-backed proxy catches the previous bug where type(table) won
        -- over ForEach and silently skipped all target data in the old test.
        return {ForEach=function(_,fn) fn(1,ref(playerAlias)); fn(2,ref(actor)) end}
    end})
    local helpers=object({IsGamePaused=function()return paused end,GetGameInstance=function()return instance end,GetPlayerCameraManager=function() return camera end,GetGameState=function(_,context) check(context==player or context==actor,'world resolves from recorded player or hit target'); return world end,GetTimeSeconds=function() return clock end})
    StaticFindObject=function(path) if path:find('GameplayStatics',1,true) then return helpers else return object({}) end end
    FindAllOf=function(name) scans=scans+1; return name=='ScenarioManager' and {manager} or {player} end
    RegisterHook=function(path,_,post) hooks[path]=post end
    LoopInGameThreadWithDelay=function(_,fn) loops[#loops+1]=fn end
    ExecuteInGameThreadWithDelay=function(_,fn) fn() end
    io.open=function(path,mode)
        if mode=='rb' then
            settingsReads=settingsReads+1
            if settingsText==nil then return nil end
            return {read=function()if settingsReadFailure then error('synthetic settings sharing violation')end;return settingsText end,close=function()end}
        end
        if path:sub(-8)=='.partial' then opens=opens+1 end
        files[path]=''
        return {write=function(_,...) files[path]=files[path]..table.concat({...}); return true end,flush=function() end,close=function() end}
    end
    os.getenv=function() return 'test-private-root' end
    os.date=function() return '2026-01-01T00:00:00Z' end
    os.remove=function(path) files[path]=nil; return true end
    os.rename=function(from,to) files[to]=files[from]; files[from]=nil; return true end
    local capture=dofile('../ue4ss/AimModNativeUI/Scripts/ReplayCapture.lua')
    local confirmations={}
    local completionListener
    local telemetry={state=function()return state end,onCompleted=function(fn)completionListener=fn end}
    if mode=='plain-array' then
        telemetry.confirmAttempt=function(restarted,elapsed)
            confirmations[#confirmations+1]={restarted=restarted,elapsed=elapsed}
            if restarted then state.id='confirmed-native-restart' end
            return state
        end
    end
    capture.start(telemetry);capture.start(telemetry)
    check(#loops==1,'start is idempotent')
    local function complete(name)
        completionListener({scenario=name or state.scenario,id=state.id,score=123})
    end
    local tick=loops[1]; tick(); check(opens==0,'idle does not open a replay')
    check(capture.status().state=='ready','ready status exported')
    state.active=true; tick();check(opens==0 and capture.status().state=='awaiting','intent waits for native timer advancement')
    clock=10.001;tick(); check(opens==1,'capture starts after timer advances')
    check(capture.status().state=='recording' and capture.status().frames==1,'recording progress exported: '..mode)
    local hit=hooks['/Script/GameSkillsTrainer.WeaponParentActor:Send_ShotHit']
    hit({},ref(playerActor),ref(actor),ref(5))
    hit({},ref(actor),ref(playerActor),ref(5))
    weapon.ShotsHitThisSession=1;weapon.ShotsFiredThisSession=2
    clock=10.02; tick(); tick()
    hooks['/Script/GameSkillsTrainer.MetaInputRecordingComponent:RecordFirePressed']({get=function()
        return recorder
    end})
    local axis=hooks['/Script/GameSkillsTrainer.MetaInputRecordingComponent:RecordAxisTurn']
    local function axisValue(v,component)axis({get=function()return component or recorder end},{get=function()return v end})end
    axisValue(0);axisValue(0);axisValue(2);axisValue(2);axisValue(0);axisValue(0)
    axisValue(3,object({GetAddress=function()return 52 end,GetOwner=function()return playerAlias end}))
    axisValue(3,object({GetAddress=function()return 53 end,GetOwner=function()return actor end}))
    for n=1,40 do
        state.id='notification-'..n;tick()
    end
    check(opens==1 and capture.status().frames==2,'repeated start notifications never split or reset an active native attempt')
    -- Telemetry may run its completion post hook before the capture observer.
    paused=true;nativeActive=false;tick();paused=false;nativeActive=true;clock=10.025;tick()
    check(opens==1 and capture.status().frames==3,'paused native phase changes do not split or suspend recording')
    complete('Different scenario');tick()
    check(capture.status().state=='recording','unrelated scenario completion cannot end capture')
    nativeActive=false;clock=10.03;tick()
    check(capture.status().state=='recording' and opens==1,'inactive phase retains attempt for delayed completion without splitting')
    state.active=false
    complete()
    tick()
    local path='test-private-root/AimMod/KovaaksNative/replays/synthetic.amreplay'
    check(files[path]~=nil,'completion publishes replay')
    check(files[path]:find('"shots":2,"hits":1',1,true)~=nil,'local weapon metrics captured without UI callbacks and duplicate weapon references deduplicated')
    check(files[path]:find('"hitDelta":1',1,true)~=nil,'confirmed local weapon increment captured as hit')
    check(files[path]:find('"hitTarget":1',1,true)~=nil,'player attributed native notification maps recorded target')
    check(files[path]:find('"startEvent":"started"',1,true)~=nil,'private header preserves source event without UI changes')
    check(files[path]:find('"frames":3',1,true)~=nil,'pause does not duplicate frames')
    check(files[path]:find('"actors":[[1,100,0,0,10,10]]',1,true)~=nil,mode..' records target geometry and excludes player')
    check(files[path]:find('"inputEvents":5',1,true)~=nil,'only exact player recorder; redundant zeros removed')
    local _,nonzeroCount=files[path]:gsub('"action":"AxisTurn","value":2','')
    check(nonzeroCount==2,'equal nonzero mouse deltas are preserved')
    local _,zeroCount=files[path]:gsub('"action":"AxisTurn","value":0','')
    check(zeroCount==2,'first zero and transition to zero preserved')
    check(ownerReads==1,'native recorder ownership verified once, not for every input')
    check(files[path]:find('"reason":"completed"',1,true)~=nil,'completion classified')
    check(files[path]:find('"score":123',1,true)~=nil,'completion event score saved even without live score notifications')
    check(files[path]:find('"health":[{"id":1,"percent":0.75}]',1,true)~=nil,'measured target health is associated with recorded identity')
    check(files[path]:find('"appearance":[{"id":1,"profile":"Synthetic target profile","rotation":[10,20,30]}]',1,true)~=nil,'native profile and rotation recorded with exact target identity')
    check(profileReads==2,'profile getter runs once per target per probe/recording, never every frame')
    if mode=='missing-map' or mode=='invalid-map-scale' or mode=='wrong-map-class' then
        check(not files[path]:find('"mapName"',1,true) and not files[path]:find('"mapScale"',1,true),'unavailable map identity omitted without guesses')
    else
        check(files[path]:find('"mapName":"Synthetic Arena.json","mapScale":4',1,true)~=nil,'exact native map identity and scale recorded')
    end
    check(mapReads==(mode=='wrong-map-class' and 0 or 1) and scaleReads==(mode=='wrong-map-class' and 0 or 1),'map getters only at capture start, not probe or frames')
    check(scans==3,'only startup probe and intent context discovery scan globally')
    local status=capture.status()
    check(status.state=='ready' and status.frames==3 and status.reason=='completed','completion status retained')
    local snapshot=files['test-private-root/AimMod/KovaaksNative/replay-status.json']
    check(snapshot:find('"state":"ready"',1,true)~=nil and not snapshot:find('Synthetic',1,true),'status snapshot excludes private run details')
    local priorOpens=opens; local priorFrames=capture.status().frames
    local probeOk,count=capture.probe()
    check(probeOk and count==1,'probe validates same target read path: '..mode)
    check(opens==priorOpens and capture.status().frames==priorFrames,'probe creates no replay and changes no capture state')
    nativeActive=true;state.active=true; tick(); check(opens==1,'same run does not overwrite replay')
    state.id='synthetic-2'; clock=11; tick();clock=11.001;tick()
    clock=11.02;tick()
    nativeTime=0;clock=11.03;tick()
    check(opens==2 and capture.status().state=='recording','one backward native timer read retains current writer')
    nativeTime=0.05;clock=11.08;tick()
    check(opens==2 and capture.status().state=='awaiting','confirmed advancing timer reset closes interrupted attempt before starting another')
    nativeTime=0.1;clock=11.13;tick()
    check(opens==3,'native restart works without another analytics ID')
    nativeTime=0.2;clock=11.23;tick()
    state.active=false;complete();tick()
    check(files['test-private-root/AimMod/KovaaksNative/replays/synthetic-2.amreplay']==nil and files['test-private-root/AimMod/KovaaksNative/replays/synthetic-2.partial']==nil,'interrupted temporary attempt removed without publishing a replay')
    local restartId=mode=='plain-array' and 'confirmed-native-restart' or 'synthetic-2-attempt-3'
    local restarted=files['test-private-root/AimMod/KovaaksNative/replays/'..restartId..'.amreplay']
    check(restarted and restarted:find('"reason":"completed"',1,true),'restart gets unique recording and completion even without ID change')
    if mode=='plain-array' then
        check(confirmations[3].restarted and confirmations[3].elapsed==0.1,'native boundary confirms shared telemetry identity and timer')
        check(state.id==restartId,'replay ID matches completed-history attempt ID')
    end
    nativeTime=nil;state.active=true;state.id='race-old';clock=12;tick();clock=12.001;tick();clock=12.02;tick()
    state.active=false;complete()
    state={active=true,id='race-next',scenario=state.scenario,startEvent='replayed'}
    nativeTime=0;clock=12.03;tick()
    check(capture.status().state=='awaiting','completion followed by replay before poll arms new attempt')
    nativeTime=0.1;clock=12.13;tick()
    check(opens==5 and capture.status().state=='recording','immediate replay is recorded without another event')
    clock=10;tick();tick()
    check(opens==5,'clock reset does not restart capture loop')
    nativeTime=nil
    check(capture.status().reason=='clock-reset','clock reset status retained')
    state.id='scenario-old';clock=13;tick();clock=13.01;tick();clock=13.03;tick()
    local beforeScenarioChange=opens
    state.id='scenario-new';state.scenario='Another synthetic scenario';clock=13.04;tick();clock=13.06;tick()
    check(opens==beforeScenarioChange+1 and capture.status().state=='recording','scenario change resolves a new context and records without waiting forever')
    clock=13.08;tick();state.active=false;complete();tick()
    local settingsPath='test-private-root/AimMod/KovaaksNative/replays/'
    settingsText='AIMMOD_SETTINGS_1\nreplayRecordingEnabled\t0\nhubHistoryEnabled\t1\n'
    for _=1,60 do tick()end
    check(capture.status().state=='disabled','persisted settings disable replay capture')
    local disabledOpens=opens;state.active=true;state.id='disabled-attempt';clock=13.2
    for _=1,120 do tick();clock=clock+.016 end
    check(opens==disabledOpens,'disabled capture never creates a replay')
    local readsBefore=settingsReads
    for _=1,59 do tick()end
    check(settingsReads-readsBefore<=1,'settings file reads are bounded outside per-frame work')
    settingsReadFailure=true;settingsText='AIMMOD_SETTINGS_1\nreplayRecordingEnabled\t1\nhubHistoryEnabled\t1\n'
    for _=1,60 do tick()end
    check(capture.status().state=='disabled','transient read failure retains disabled preference')
    settingsReadFailure=false
    for _=1,60 do tick()end
    check(capture.status().state=='awaiting' and opens==disabledOpens,'re-enable waits next native attempt instead of recording partial current run')
    state.active=false;tick();state.active=true;state.id='settings-recorded';clock=16;tick();clock=16.01;tick()
    check(opens==disabledOpens+1 and capture.status().state=='recording','next attempt records after settings re-enable')
    settingsText='invalid preferences'
    for _=1,60 do tick()end
    check(capture.status().state=='disabled' and files[settingsPath..'settings-recorded.partial']==nil,'malformed saved preferences fail closed and discard owned unfinished data')
    check(files[settingsPath..'settings-recorded.amreplay']==nil,'settings disable never publishes a partial replay')
    settingsText='AIMMOD_SETTINGS_1\nreplayRecordingEnabled\t1\nhubHistoryEnabled\t1\n';state.active=false
    for _=1,60 do tick()end
    state.active=true;state.id='synthetic-3'; clock=14; world.GetCharacters=function() return nil end
    tick();clock=14.001;tick()
    check(capture.status().state=='error' and capture.status().reason=='capture-error','unsupported collections fail visibly')
    local before=opens; tick(); check(opens==before,'error disables repeated expensive probing')
end
print('Replay capture checks passed ('..checks..'; target wrappers and map identity metadata).')
