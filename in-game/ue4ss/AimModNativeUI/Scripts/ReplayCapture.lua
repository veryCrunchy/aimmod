-- Read-only observer. Native scene playback is handled separately; this module
-- never drives game inputs, changes actors, starts recording on game components,
-- or invokes a scoring function. UObject reads stay on the game thread.
local M = {}
local active, disabled, started, lastAttempt = nil, false, false, nil
local pending
local recordingEnabled,settingsTicks,settingsWait=true,0,false
local resumeGate
local metricsSource
local attemptSequence=0
local lastFrames, lastInputs, lastReason, publishedStatus = 0, 0, '', nil
local MAX_ACTORS, MAX_FRAMES, MAX_BYTES = 128, 36000, 64 * 1024 * 1024
local function valid(o) return o and o:IsValid() end
local function finite(n) return type(n)=='number' and n==n and math.abs(n)<1e12 end
local function number(n) assert(finite(n), 'non-finite replay value'); return string.format('%.7g',n) end
local function quote(s)
    return '"' .. tostring(s):gsub('[%z\1-\31\\"]', function(c)
        if c=='\\' then return '\\\\' elseif c=='"' then return '\\"' end
        return string.format('\\u%04x',string.byte(c))
    end) .. '"'
end
function M.status()
    return {state=not recordingEnabled and 'disabled' or (disabled and 'error' or (active and 'recording' or ((pending or settingsWait) and (pending and pending.unsupported and 'unsupported' or 'awaiting') or 'ready'))),
        frames=active and active.frames or (pending and 0 or lastFrames),inputEvents=active and active.inputEvents or (pending and 0 or lastInputs),reason=lastReason}
end
local function readSettings(force)
    settingsTicks=settingsTicks+1
    if not force and settingsTicks<60 then return end
    settingsTicks=0
    local file=io.open((os.getenv('LOCALAPPDATA') or '')..'/AimMod/KovaaksNative/native-settings.tsv','rb')
    if not file then return end -- transient replacement/read lock retains state
    local ok,text=pcall(function()return file:read(1025)end);file:close()
    if not ok or text==nil then return end
    text=text:gsub('\r\n','\n')
    local capture,history=text:match('^AIMMOD_SETTINGS_1\nreplayRecordingEnabled\t([01])\nhubHistoryEnabled\t([01])\n$')
    -- Readable malformed settings fail closed, matching the worker store.
    recordingEnabled=capture=='1' and history~=nil
end
local function same(a,b) return valid(a) and valid(b) and a:GetAddress()==b:GetAddress() end
local function gateContext(context)
    local instance=context.helpers:GetGameInstance(context.player)
    assert(valid(instance),'capture game instance unavailable')
    local manager
    for _,candidate in ipairs(FindAllOf('ScenarioManager') or {}) do
        if valid(candidate) and same(candidate:GetOuter(),instance) then
            assert(not manager,'capture scenario manager ambiguous');manager=candidate
        end
    end
    assert(valid(manager),'capture scenario manager unavailable')
    context.manager=manager;context.instance=instance
    return context
end
local function challengeAdvancing(p)
    local c=p.context;local manager=c.manager
    assert(valid(c.player) and valid(c.world) and valid(manager),'capture gate context unavailable')
    assert(same(c.helpers:GetGameInstance(c.player),c.instance) and same(c.helpers:GetGameState(c.player),c.world),'capture gate world changed')
    local scenario=manager:GetCurrentScenario()
    assert(valid(scenario),'capture scenario unavailable')
    local name=scenario:GetName();if type(name)~='string' then name=name:ToString() end
    local queue=manager:GetChallengeQueueTimeRemaining();local elapsed=manager:GetChallengeTimeElapsed()
    assert(finite(queue) and finite(elapsed),'capture challenge timer unavailable')
    local running=name==p.scenario and manager:IsInChallenge() and scenario:IsActive() and scenario:IsInChallenge() and not manager:IsScenarioLoading() and queue<=0 and elapsed>=0
    local address=scenario:GetAddress()
    -- Keep the last accepted timer through inactive/queue phases. A transient
    -- native phase change is not a new attempt, and clearing this observation
    -- loses the evidence needed to recognize a real restart after that phase.
    if not running then p.rewind=nil;p.inactive=true;return false,false,false,false end
    local resumed=p.inactive;p.inactive=nil
    local previous=p.elapsed
    local changed=previous~=nil and address~=p.scenarioAddress
    local reset=changed
    local uncertain=false
    if not changed and previous~=nil and elapsed<previous-0.05 and elapsed<=1 then
        -- A restart returns to the start of the challenge. Require another
        -- advancing observation below the old timer before closing the writer:
        -- a single stale/zero read or millisecond jitter must not discard a run.
        reset=p.rewind~=nil and elapsed>p.rewind and elapsed<previous-0.05
        uncertain=not reset
        p.rewind=elapsed
    else
        p.rewind=nil
    end
    local advancing=not resumed and previous~=nil and address==p.scenarioAddress and elapsed>previous and not reset
    if previous==nil or reset or elapsed>previous then p.elapsed=elapsed end
    p.scenarioAddress=address
    if reset then p.rewind=nil end
    return advancing,true,reset,uncertain
end
local function publishStatus()
    local s=M.status()
    local line='{"state":'..quote(s.state)..',"frames":'..s.frames..',"inputEvents":'..s.inputEvents..',"reason":'..quote(s.reason)..'}\n'
    if line==publishedStatus then return end
    -- Best-effort, tiny snapshot. Readers should retain their previous valid
    -- value if they catch a partial write. No names, IDs or exception paths.
    local ok=pcall(function()
        local file=io.open((os.getenv('LOCALAPPDATA') or '')..'/AimMod/KovaaksNative/replay-status.json','wb')
        if not file then return end
        local written=file:write(line); file:close()
        if written then publishedStatus=line end
    end)
end
local function flush()
    if not active or #active.pending==0 then return end
    assert(active.file:write(table.concat(active.pending,'\n'),'\n'))
    active.file:flush(); active.pending={}
    publishStatus()
end
local function emit(line)
    if not active then return end
    active.bytes=active.bytes+#line+1
    if active.bytes>MAX_BYTES then error('replay size limit reached') end
    active.pending[#active.pending+1]=line
end
local function finish(reason,boundary,finalScore)
    if not active then return end
    local a=active
    print('[AimModReplay] recording finished; reason='..reason..'; boundary='..(boundary or reason)..'; notifications='..tostring(a.notifications or 1)..'; frames='..a.frames..'\n')
    -- Only a completed attempt is published. Preserve failed completion writes
    -- as temporary data, while ordinary canceled attempts discard their temp.
    local ok=pcall(function()
        local score=reason=='completed' and finite(finalScore) and ',"score":'..number(finalScore) or ''
        emit('{"kind":"end","reason":'..quote(reason)..',"frames":'..a.frames..',"inputEvents":'..a.inputEvents..score..'}')
        flush()
    end)
    a.file:close(); lastFrames=a.frames; lastInputs=a.inputEvents; lastReason=reason; active=nil
    if ok and reason=='completed' and a.frames>1 then os.rename(a.partial,a.final)
    elseif reason~='completed' then
        -- This is the exact temporary file owned by this recorder and closed
        -- above. Interrupted attempts are never published into the library.
        os.remove(a.partial)
    end
    publishStatus()
end
local function resolveContext()
    local helpers=StaticFindObject('/Script/Engine.Default__GameplayStatics')
    assert(valid(helpers),'GameplayStatics unavailable')
    local player
    for _,p in ipairs(FindAllOf('MetaPlayerController') or {}) do
        if valid(p) and not p:GetFullName():find('Default__',1,true) and valid(p.MyCharacter) then player=p; break end
    end
    assert(valid(player),'player unavailable')
    local camera=helpers:GetPlayerCameraManager(player,0)
    local world=helpers:GetGameState(player)
    assert(valid(camera) and valid(world),'camera or game state unavailable')
    return {helpers=helpers,player=player,camera=camera,world=world}
end
local function mapIdentity(context)
    -- GetGameState(player) resolves the same world as the recording controller.
    -- These reflected MetaGameState getters are read only; resolve once per run.
    local ok,name,scale=pcall(function()
        local world=context.world
        local class=StaticFindObject('/Script/GameSkillsTrainer.MetaGameState')
        if not valid(world) or not valid(class) or not world:IsA(class) then return end
        local value=world:GetCurrentMapName()
        -- UE4SS may return FString or a Lua string. Never stringify a UObject
        -- wrapper/address as a map identity, trim the name, or invent a default.
        if type(value)~='string' then
            if value==nil then return end
            value=value:ToString()
        end
        local mapScale=world:GetMapScale()
        if type(value)~='string' or not value:find('%S') or #value>1024 or value:find('[%z\1-\31]') then return end
        if not finite(mapScale) or mapScale<=0 then return end
        return value,mapScale
    end)
    if ok and name and scale then return ',"mapName":'..quote(name)..',"mapScale":'..string.format('%.9g',scale) end
    -- Older capture-only replays remain useful, but cannot claim native map fidelity.
    return ''
end
local function begin(state,context)
    local map=mapIdentity(context)
    local helpers,player,camera,world=context.helpers,context.player,context.camera,context.world
    local id=tostring(state.id)
    -- A native restart can occur without a fresh analytics ID. Never overwrite
    -- an earlier attempt, even when notifications are coalesced.
    attemptSequence=attemptSequence+1
    if context.reusedId then id=id..'-attempt-'..attemptSequence end
    assert(id:match('^[%w_-]+$') and #id<=100,'invalid replay id')
    local root=(os.getenv('LOCALAPPDATA') or '')..'/AimMod/KovaaksNative/replays/'
    local partial,final=root..id..'.partial',root..id..'.amreplay'
    -- The worker creates the directory; do not shell out from the game.
    local file=assert(io.open(partial,'wb'))
    active={gate=context.captureGate,scenario=state.scenario,notificationId=state.id,notifications=1,file=file,partial=partial,final=final,id=id,helpers=helpers,player=player,camera=camera,world=world,
        start=helpers:GetTimeSeconds(player),last=-1,flushAt=1,frames=0,bytes=0,pending={},ids={},nextId=0,inputEvents=0,axisValues={}}
    local character=player.MyCharacter
    local recorder=character.PlaybackComponent
    if valid(recorder) then
        local owner=recorder:GetOwner()
        if valid(owner) and owner:GetAddress()==character:GetAddress() then
            active.recorderAddress=recorder:GetAddress();active.inputOwnerAddress=character:GetAddress()
        end
    end
    lastReason=''; publishStatus()
    local source=''
    if state.startEvent=='started' or state.startEvent=='restarted' or state.startEvent=='replayed' then source=',"startEvent":'..quote(state.startEvent) end
    emit('{"kind":"header","version":1,"id":'..quote(id)..',"scenario":'..quote(state.scenario or '')..
        ',"recordedAt":'..quote(os.date('!%Y-%m-%dT%H:%M:%SZ'))..',"coordinates":"unreal-centimeters","nominalHz":60'..map..source..'}')
end
local function time()
    return active.helpers:GetTimeSeconds(active.player)-active.start
end
local function snapshot(a)
    a.readStage='camera'
    local pos,rot=a.camera:GetCameraLocation(),a.camera:GetCameraRotation()
    local camera={pos.X,pos.Y,pos.Z,rot.Pitch,rot.Yaw,rot.Roll,a.camera:GetFOVAngle()}
    for i,v in ipairs(camera) do camera[i]=number(v) end
    local actors,health,appearance={},{},{}
    a.profiles=a.profiles or {}
    -- Authoritative per-world character list, not global UObject scans per frame.
    local function recordActor(actor)
        a.readStage='target-reference'
        local direct=pcall(function() return actor:IsValid() end)
        if not direct then actor=actor:get() end
        if not valid(actor) or actor:GetAddress()==a.player.MyCharacter:GetAddress() or actor.bHidden==true then return end
        if #actors>=MAX_ACTORS then error('replay actor limit reached') end
        a.readStage='target-transform'
        local key=actor:GetFullName()..':'..tostring(actor:GetAddress())
        local id=a.ids[key]
        if not id then a.nextId=a.nextId+1; id=a.nextId; a.ids[key]=id end
        local p=actor:K2_GetActorLocation()
        local capsule=actor.CapsuleComponent
        if not valid(capsule) then return end
        a.readStage='target-capsule'
        local radius,half=capsule:GetScaledCapsuleRadius(),capsule:GetScaledCapsuleHalfHeight()
        actors[#actors+1]='['..id..','..number(p.X)..','..number(p.Y)..','..number(p.Z)..','..number(radius)..','..number(half)..']'
        local ok,percent=pcall(function()return actor:GetCurrentHealthPercent()end)
        if ok and finite(percent) and percent>=0 and percent<=1 then
            health[#health+1]='{"id":'..id..',"percent":'..number(percent)..'}'
        end
        if a.profiles[id]==nil then
            local profileOk,profile=pcall(function()
                local value=actor:GetCharacterProfileName()
                return type(value)=='string' and value or value:ToString()
            end)
            a.profiles[id]=profileOk and type(profile)=='string' and #profile>0 and #profile<=512 and not profile:find('[%z\1-\31]') and profile or false
        end
        if a.profiles[id] then
            local rotationOk,rotation=pcall(function()return actor:K2_GetActorRotation()end)
            if rotationOk and rotation and finite(rotation.Pitch) and finite(rotation.Yaw) and finite(rotation.Roll) then
                appearance[#appearance+1]='{"id":'..id..',"profile":'..quote(a.profiles[id])..',"rotation":['..number(rotation.Pitch)..','..number(rotation.Yaw)..','..number(rotation.Roll)..']}'
            end
        end
    end
    a.readStage='character-list'
    local characters=a.world:GetCharacters()
    if characters and type(characters.ForEach)=='function' then
        -- Native TArray wrappers expose ForEach; tests may model them as a Lua
        -- table too, so capability takes precedence over type(table).
        characters:ForEach(function(_,ref) recordActor(ref:get()) end)
    elseif type(characters)=='table' then
        for _,actor in ipairs(characters) do recordActor(actor) end
    else
        error('unsupported character collection')
    end
    return camera,actors,health,appearance
end
function M.probe()
    -- Use the exact capture API/collection path without opening a replay,
    -- simulating a challenge event, changing inputs, or touching live actors.
    local context
    local ok,result=pcall(function()
        context=resolveContext(); context.ids={}; context.nextId=0
        local camera,actors=snapshot(context)
        return #actors
    end)
    if ok then print('[AimModReplay] read-only capture probe succeeded; targets='..result..'\n')
    else print('[AimModReplay] read-only capture probe failed at '..(context and context.readStage or 'world-context')..'\n') end
    return ok,ok and result or (context and context.readStage or 'world-context')
end
local function sample()
    local a=active
    if not valid(a.player) or not valid(a.camera) or not valid(a.world) then finish('world-changed'); return end
    local t=time()
    -- Unreal game time freezes during pause. Do not emit duplicate pause frames
    -- or interpolate across world travel / clock resets.
    if t<a.last then finish('clock-reset'); return end
    if t<=a.last then return end
    if a.frames>=MAX_FRAMES then finish('frame-limit'); return end
    local camera,actors,health,appearance=snapshot(a)
    local fields={}
    local observed=metricsSource and metricsSource.state() or {}
    local metrics={}
    for key,value in pairs(observed) do metrics[key]=value end
    -- Current builds can omit UI metric notifications. Read only the local
    -- character's weapon session counters; never count other bots' damage.
    pcall(function()
        local character=a.player.MyCharacter
        local shots,hits,count=0,0,0
        local seen={}
        local function weapon(w)
            if not pcall(function()return w:IsValid()end) then w=w:get() end
            if not valid(w) then return end
            local address=w:GetAddress();if seen[address] then return end;seen[address]=true
            count=count+1;assert(count<=32,'too many replay weapons')
            local h,f=w.ShotsHitThisSession,w.ShotsFiredThisSession
            assert(finite(h) and finite(f) and h>=0 and f>=0,'weapon counters unavailable')
            hits=hits+h;shots=shots+f
        end
        local weapons=character.WeaponHandler:GetWeapons()
        if type(weapons)=='table' and weapons.ForEach==nil then for _,w in ipairs(weapons)do weapon(w)end
        else weapons:ForEach(function(_,w)weapon(w)end)end
        if count>0 then
            metrics.hits=hits;metrics.shots=shots
            if a.observedHits and hits>a.observedHits then
                a.observedHitTime=t;a.observedHitDelta=hits-a.observedHits
                if a.hitEvent and a.hitEvent.time<a.last then a.hitEvent=nil end
            end
            if a.observedHits and hits<a.observedHits then a.observedHitTime=nil;a.observedHitDelta=nil end
            a.observedHits=hits
            metrics.hitStamp=a.observedHitTime and a.start+a.observedHitTime or nil
            metrics.hitDelta=a.observedHitDelta
        end
        local kills,damage=character:GetKillCount(),character.DamageDone
        if not finite(metrics.kills) and finite(kills) and kills>=0 then metrics.kills=kills end
        if not finite(metrics.damage) and finite(damage) then metrics.damage=damage end
    end)
    if a.gate and finite(a.gate.elapsed) then metrics.seconds=a.gate.elapsed end
    if metrics.scenario==a.scenario then
        for _,key in ipairs({'score','shots','hits','kills','damage','seconds'}) do
            if finite(metrics[key]) then fields[#fields+1]=quote(key)..':'..number(metrics[key]) end
        end
        if a.hitEvent and a.hitEvent.time<=t then
            metrics.hitStamp=a.start+a.hitEvent.time;metrics.hitDelta=1
            fields[#fields+1]='"hitTarget":'..a.hitEvent.target
        end
        if finite(metrics.hitStamp) and metrics.hitStamp>=a.start and finite(metrics.hitDelta) then
            fields[#fields+1]='"hitTime":'..number(metrics.hitStamp-a.start)
            fields[#fields+1]='"hitDelta":'..number(metrics.hitDelta)
        end
    end
    local stats=#fields>0 and ',"stats":{'..table.concat(fields,',')..'}' or ''
    local healthData=#health>0 and ',"health":['..table.concat(health,',')..']' or ''
    local appearanceData=#appearance>0 and ',"appearance":['..table.concat(appearance,',')..']' or ''
    emit('{"kind":"frame","t":'..number(t)..',"camera":['..table.concat(camera,',')..'],"actors":['..table.concat(actors,',')..']'..healthData..appearanceData..stats..'}')
    a.frames=a.frames+1; a.last=t
    if t>=a.flushAt then flush(); a.flushAt=t+1 end
end
local function inputHooks()
    -- Observe the game's existing input recording entry points. Do not enable
    -- the game's recorder. Some builds may not call these while it is idle;
    -- actual observed event counts, not hook registration, establish coverage.
    local names={'AxisTurn','AxisLookUp','AxisMoveForward','AxisMoveRight','FirePressed','FireReleased',
        'AltFirePressed','AltFireReleased','JumpPressed','JumpReleased','CrouchPressed','CrouchReleased',
        'ReloadPressed','ReloadReleased','ADSPressed','ADSReleased','AbilityPressed','AbilityReleased',
        'WeaponPressed','WeaponReleased'}
    for _,name in ipairs(names) do
        local event=name
        local path='/Script/GameSkillsTrainer.MetaInputRecordingComponent:Record'..name
        if valid(StaticFindObject(path)) then
            RegisterHook(path,function() end,function(context,value)
                if not active or active.suspended then return end
                local ok,err=pcall(function()
                    local component=context:get()
                    -- A different recorder attached to the same character must
                    -- not double the input stream. Current builds expose the
                    -- player's concrete native playback/recording component.
                    -- Ownership is verified once at capture start, avoiding
                    -- thousands of reflected GetOwner calls per second.
                    if not active.recorderAddress or not valid(component) or component:GetAddress()~=active.recorderAddress then return end
                    if not valid(active.player.MyCharacter) or active.player.MyCharacter:GetAddress()~=active.inputOwnerAddress then return end
                    local v=value and value:get() or 1
                    if not finite(v) then return end
                    if event:sub(1,4)=='Axis' then
                        local previous=active.axisValues[event]
                        active.axisValues[event]=v
                        -- Mouse look values are additive deltas: repeated
                        -- nonzero values MUST survive. Only redundant zeros
                        -- are inert. Keep the first zero and every transition
                        -- back to zero, including paused-time transitions.
                        if v==0 and previous==0 then return end
                    end
                    emit('{"kind":"input","t":'..number(time())..',"action":'..quote(event)..',"value":'..number(v)..'}')
                    active.inputEvents=active.inputEvents+1
                end)
                if not ok then finish('input-error'); disabled=true; lastReason='input-error'; publishStatus(); print('[AimModReplay] '..tostring(err)..'\n') end
                -- Nil: no parameter or return override.
            end)
        end
    end
end
function M.start(telemetry)
    metricsSource=telemetry
    if started then return end
    started=true
    readSettings(true)
    if not recordingEnabled then lastReason='recording-disabled' end
    publishStatus()
    print('[AimModReplay] registering capture observers\n')
    inputHooks()
    -- This notification supplies both shooter and recipient. Observe only;
    -- returning nil preserves the game's hit result and scoring path.
    pcall(function()
        local path='/Script/GameSkillsTrainer.WeaponParentActor:Send_ShotHit'
        assert(valid(StaticFindObject(path)))
        RegisterHook(path,function()end,function(_,shooter,target)
            pcall(function()
                local a=active;if not a or a.suspended then return end
                local who=shooter:get();local recipient=target:get()
                if not valid(who) or not valid(recipient) or not same(who,a.player.MyCharacter) then return end
                if not same(a.helpers:GetGameState(recipient),a.world) then return end
                local key=recipient:GetFullName()..':'..tostring(recipient:GetAddress())
                -- Only targets already recorded in this world can receive a
                -- replay target marker. First-frame unknowns stay unmarked.
                local id=a.ids[key];if not id then return end
                local stamp=a.helpers:GetTimeSeconds(a.player)-a.start
                if finite(stamp) and stamp>=0 then a.hitEvent={time=stamp,target=id} end
            end)
        end)
    end)
    ExecuteInGameThreadWithDelay(0,function() M.probe() end)
    local completed=false
    RegisterHook('/Script/GameSkillsTrainer.AnalyticsManager:OnChallengeCompleted',function() end,function(_,scenario,score)
        -- Associate completion with the recorder itself. Telemetry hooks may
        -- already have changed active/id before this post hook is invoked.
        if not scenario then return end
        local ok,name=pcall(function()
            local value=scenario:get()
            return type(value)=='string' and value or value:ToString()
        end)
        if ok and settingsWait and name==telemetry.state().scenario then settingsWait=false;resumeGate=nil end
        if not active then return end
        if ok and name==active.scenario then
            local scoreOk,finalScore=pcall(function()return score:get()end)
            completed={intentId=telemetry.state().id,score=scoreOk and finite(finalScore) and finalScore or nil}
        end
    end)
    local function awaitAttempt(state,context,reusedId,nativeRestart)
        pending={id=state.id,scenario=state.scenario,startEvent=state.startEvent,nativeRestart=nativeRestart}
        local resolved,value=pcall(function()return context or gateContext(resolveContext())end)
        if resolved then
            pending.context=value;value.reusedId=reusedId;value.captureGate=pending
        else
            pending.unsupported=true;lastReason='capture-gate-unavailable'
            print('[AimModReplay] capture gate unavailable: '..tostring(value)..'\n')
        end
        publishStatus()
    end
    LoopInGameThreadWithDelay(16,function()
        if disabled then return end
        local ok,err=pcall(function()
            local state=telemetry.state()
            local wasEnabled=recordingEnabled
            readSettings(state.active and not active and not pending and state.id~=lastAttempt)
            if not recordingEnabled then
                -- Drop only this recorder's unfinished temp; score telemetry and
                -- the actual challenge continue untouched.
                if active then finish('recording-disabled','settings') end
                pending=nil;completed=false;lastAttempt=state.id
                settingsWait=state.active==true;resumeGate=nil;lastReason='recording-disabled';publishStatus();return
            end
            if not wasEnabled then lastReason=settingsWait and 'recording-next-run' or '';publishStatus()end
            if settingsWait then
                if not state.active then settingsWait=false;resumeGate=nil;lastReason='';publishStatus()
                else
                    if not resumeGate then
                        local resolved,context=pcall(function()return gateContext(resolveContext())end)
                        if resolved then resumeGate={context=context,scenario=state.scenario};pcall(challengeAdvancing,resumeGate)end
                    else
                        local resolved,_,running,reset=pcall(challengeAdvancing,resumeGate)
                        if resolved and (reset or state.scenario~=resumeGate.scenario)then settingsWait=false;resumeGate=nil;lastAttempt=nil;lastReason='' end
                    end
                    if settingsWait then lastAttempt=state.id;return end
                end
            end
            if active then
                if completed then
                    -- A new Play event can arrive before this scheduled poll.
                    -- Consume only the intent observed at completion, never the
                    -- newer state.id, so the next attempt can arm immediately.
                    local result=completed;lastAttempt=result.intentId;completed=false;finish('completed',nil,result.score);pending=nil
                elseif time()<active.last then
                    lastAttempt=state.id;finish('clock-reset');pending=nil
                elseif not state.active then
                    lastAttempt=state.id;finish('interrupted');pending=nil
                elseif state.scenario~=active.scenario then
                    finish('interrupted','scenario-changed');lastAttempt=state.id
                    awaitAttempt(state)
                elseif not active.helpers:IsGamePaused(active.player) then
                    local a=active
                    if a.notificationId~=state.id then a.notificationId=state.id;a.notifications=a.notifications+1 end
                    a.notifications=math.max(a.notifications,state.startNotifications or 1)
                    -- UI analytics are notifications, not attempt boundaries.
                    -- Replayed can repeat every second during one real run.
                    -- Only native scenario/timer transitions split recording.
                    local advancing,running,reset,uncertain=challengeAdvancing(a.gate)
                    if not running or uncertain then
                        -- Natural completion can make Scenario inactive before
                        -- its analytics notification arrives. Freeze sampling,
                        -- retain the writer and wait for completion/quit or an
                        -- advancing next attempt; never guess from duration.
                        a.suspended=true
                    elseif reset then
                        local context=a.gate.context
                        local reused=state.id==a.id
                        lastAttempt=state.id;finish('interrupted','native-timer-reset')
                        awaitAttempt(state,context,reused,true)
                    else
                        -- Resume the same writer after a transient inactive
                        -- phase or rejected timer reset. Game time owns frame
                        -- timestamps; never stitch separate confirmed attempts.
                        a.suspended=false
                    end
                end
            end
            if pending and not state.active then pending=nil;publishStatus() end
            if state.active and not active and not pending and state.id~=lastAttempt then
                lastAttempt=state.id;lastReason='';awaitAttempt(state)
            end
            if pending and state.active and pending.scenario~=state.scenario then
                awaitAttempt(state)
            end
            if pending and not pending.unsupported then
                pending.id=state.id;pending.startEvent=state.startEvent
                -- Keep the timer observation across repeated notifications.
                -- The ID is fixed only when native activity opens the writer.
                local supported,advancing=pcall(challengeAdvancing,pending)
                if not supported then
                    pending.unsupported=true;lastReason='capture-gate-unavailable';publishStatus()
                    print('[AimModReplay] capture gate unavailable: '..tostring(advancing)..'\n')
                elseif advancing then
                    readSettings(true)
                    if not recordingEnabled then return end
                    local intent,context=pending,pending.context
                    pending=nil
                    if telemetry.confirmAttempt then
                        local confirmed=telemetry.confirmAttempt(intent.nativeRestart,intent.elapsed)
                        intent.id=confirmed.id;intent.startEvent=confirmed.startEvent
                        context.reusedId=false
                    end
                    lastAttempt=intent.id;begin(intent,context)
                end
            end
            if active and not active.suspended then sample() end
        end)
        if not ok then finish('capture-error'); disabled=true; lastReason='capture-error'; publishStatus(); print('[AimModReplay] capture stopped: '..tostring(err)..'\n') end
    end)
end
return M
