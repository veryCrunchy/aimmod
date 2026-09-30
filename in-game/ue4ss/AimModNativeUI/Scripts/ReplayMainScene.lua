-- Main game viewport replay. Only native inert visuals; no gameplay replay,
-- character spawning, scoring callbacks, input playback or scenario transitions.
local M={}
local function valid(o) local ok,v=pcall(function() return o:IsValid() end);return ok and v end
local function same(a,b) return valid(a) and valid(b) and a:GetAddress()==b:GetAddress() end
local function unwrap(o) if valid(o) then return o end;local ok,v=pcall(function()return o:get()end);return ok and v end
local function each(a,fn)
    local ok,f=pcall(function()return a.ForEach end)
    if ok and type(f)=='function' then a:ForEach(function(_,v)fn(unwrap(v))end)
    elseif type(a)=='table' then for _,v in ipairs(a)do fn(unwrap(v))end
    else error('replay actor collection unavailable') end
end
local function str(s) if type(s)=='string' then return s end;return s:ToString() end
local function finite(n) return type(n)=='number' and n==n and math.abs(n)<1e12 end
local function vector(x,y,z) assert(finite(x) and finite(y) and finite(z),'invalid replay position');return {X=x,Y=y,Z=z}end
local function inspect(owner,meta,cached)
    assert(valid(owner),'replay owner unavailable')
    local api=StaticFindObject('/Script/Engine.Default__GameplayStatics');assert(valid(api),'gameplay API unavailable')
    local world=owner:GetWorld();local instance=api:GetGameInstance(owner);local controller=api:GetPlayerController(owner,0)
    assert(valid(world) and valid(instance) and valid(controller) and same(controller:GetWorld(),world),'world-unavailable')
    local manager=cached and cached.manager
    if cached then
        assert(same(cached.world,world) and same(cached.controller,controller) and valid(manager) and same(manager:GetOuter(),instance),'world-unavailable')
    else
        each(FindAllOf('ScenarioManager') or {},function(o)
            if valid(o) and same(o:GetOuter(),instance) then assert(not manager,'ambiguous scenario manager');manager=o end
        end)
    end
    assert(valid(manager),'scenario manager unavailable')
    assert(not manager:IsScenarioLoading() and not manager:IsInScenarioEditor() and not manager:IsCurrentlyInBenchmark(),'scenario-unavailable')
    local scenario=manager:GetCurrentScenario();assert(valid(scenario),'scenario-unavailable')
    local phase={managerChallenge=manager:IsInChallenge(),challenge=scenario:IsInChallenge(),active=scenario:IsActive()}
    local live=phase.managerChallenge or phase.challenge or phase.active
    if live then
        -- A pause menu leaves the underlying challenge active. Replay visuals
        -- may borrow that frozen world, but must never cancel or resume it.
        assert(api:IsGamePaused(owner),(phase.managerChallenge or phase.challenge) and 'challenge-active' or 'scenario-active')
        if not cached then assert(owner:IsVisible(),'pause-menu-unavailable')end
        phase.elapsed=manager:GetChallengeTimeElapsed();phase.queue=manager:GetChallengeQueueTimeRemaining()
        phase.clock=api:GetTimeSeconds(owner)
        assert(finite(phase.elapsed) and finite(phase.queue) and finite(phase.clock),'challenge-clock-unavailable')
    end
    if cached then
        local previous=cached.phase
        assert(previous.managerChallenge==phase.managerChallenge and previous.challenge==phase.challenge and previous.active==phase.active,'replay context changed')
        if live then
            -- Compare with the fixed bind snapshot, not the last frame: even
            -- tiny cumulative advancement must eventually fail closed. Keep
            -- separate diagnostics so a timer anomaly is not confused with
            -- the world actually running while replay visuals are active.
            assert(math.abs(previous.clock-phase.clock)<0.0001,'replay world clock advanced')
            assert(math.abs(previous.elapsed-phase.elapsed)<0.0001,'replay challenge elapsed changed')
            assert(math.abs(previous.queue-phase.queue)<0.0001,'replay challenge queue changed')
        end
    end
    local gs=api:GetGameState(owner);assert(valid(gs) and same(gs:GetWorld(),world),'map state unavailable')
    assert(gs.bMapLoading==false and gs.bFullyLoaded==true,'map is not ready')
    local identity={scenario=str(scenario:GetName()),mapName=str(gs:GetCurrentMapName()),mapScale=gs:GetMapScale()}
    assert(identity.scenario~='' and identity.mapName~='' and finite(identity.mapScale) and identity.mapScale>0,'map identity unavailable')
    if not (meta and meta.proof==true) then
        assert(type(meta)=='table' and meta.scenario==identity.scenario,'scenario-mismatch')
        assert(meta.mapName==identity.mapName and finite(meta.mapScale) and math.abs(meta.mapScale-identity.mapScale)<=1e-6*math.max(1,math.abs(identity.mapScale)),'map-mismatch')
    end
    return {api=api,world=world,controller=controller,manager=manager,scenario=scenario,gameState=gs,identity=identity,phase=phase}
end
function M.verify(owner,meta) local c=inspect(owner,meta);return c.identity end
function M.proofFrame(owner)
    local c=inspect(owner,{proof=true});local camera=c.api:GetPlayerCameraManager(owner,0)
    assert(valid(camera),'proof camera unavailable')
    local p=camera:GetCameraLocation();local r=camera:GetCameraRotation();local fov=camera:GetFOVAngle()
    local yaw=r.Yaw*math.pi/180;local pitch=r.Pitch*math.pi/180
    return {camera={p.X,p.Y,p.Z,r.Pitch,r.Yaw,r.Roll,fov},actors={{1,p.X+300*math.cos(pitch)*math.cos(yaw),p.Y+300*math.cos(pitch)*math.sin(yaw),p.Z+300*math.sin(pitch),35,35}}}
end
function M.create(owner)
    local s={closed=false,ready=false,owned={},actors={},hidden={},hiddenUI={}}
    local c,saved;local templates={};local presentations={};local legacyTemplate
    local function transform(value)
        local r,p,z=value.Rotation,value.Translation,value.Scale3D
        for _,n in ipairs({r.X,r.Y,r.Z,r.W,p.X,p.Y,p.Z,z.X,z.Y,z.Z})do assert(finite(n),'invalid native skin transform')end
        return {Rotation={X=r.X,Y=r.Y,Z=r.Z,W=r.W},Translation={X=p.X,Y=p.Y,Z=p.Z},Scale3D={X=z.X,Y=z.Y,Z=z.Z}}
    end
    local function primitiveData(struct)
        local values={};if not struct or not struct.Data then return values end
        local function add(v)
            if type(v)~='number'then v=v:get()end
            assert(finite(v) and #values<32,'native primitive data budget exceeded');values[#values+1]=v
        end
        local data=struct.Data
        if type(data)=='table' and not data.ForEach then for _,v in ipairs(data)do add(v)end
        else data:ForEach(function(_,v)add(v)end)end
        return values
    end
    local function template(actor)
        if actor.bOnEnemyTeam~=true or actor.bHidden then return end
        local profile=str(actor:GetCharacterProfileName());if profile=='' or templates[profile]then return end
        local mathAPI=StaticFindObject('/Script/Engine.Default__KismetMathLibrary')
        local actorTransform=actor:GetTransform();local scale=actor:GetActorScale3D()
        local rotation=actor:K2_GetActorRotation()
        assert(finite(rotation.Pitch) and finite(rotation.Yaw) and finite(rotation.Roll),'native template rotation unavailable')
        local capsule=actor.CapsuleComponent;local radius,half=capsule:GetScaledCapsuleRadius(),capsule:GetScaledCapsuleHalfHeight()
        assert(finite(radius) and radius>0 and finite(half) and half>=radius)
        local animated=false
        each(actor:K2_GetComponentsByClass(StaticFindObject('/Script/Engine.SkeletalMeshComponent')),function(component)
            if valid(component) and component:IsVisible() and not component.bHiddenInGame and valid(component.SkeletalMesh)then animated=true end
        end)
        if animated then return end -- bone poses are not present in this format
        local parts={}
        each(actor:K2_GetComponentsByClass(StaticFindObject('/Script/Engine.StaticMeshComponent')),function(component)
            if valid(component) and component:IsVisible() and not component.bHiddenInGame and valid(component.StaticMesh)then
                assert(#parts<32,'native skin component budget exceeded')
                local part={mesh=component.StaticMesh,relative=transform(mathAPI:MakeRelativeTransform(component:K2_GetComponentToWorld(),actorTransform)),materials={}}
                local count=component:GetNumMaterials();assert(finite(count) and count>=0 and count<=16)
                for index=0,count-1 do local material=component:GetMaterial(index);if valid(material)then part.materials[index]=material end end
                -- Native skin materials may read per-component parameters. A
                -- material reference alone loses these values and renders black.
                local internal=primitiveData(component.CustomPrimitiveDataInternal)
                part.primitiveData=#internal>0 and internal or primitiveData(component.CustomPrimitiveData)
                local depth=component.bRenderCustomDepth;local stencil=component.CustomDepthStencilValue;local mask=component.CustomDepthStencilWriteMask
                if type(depth)=='boolean'then part.depth=depth end
                if finite(stencil) and stencil%1==0 and stencil>=0 and stencil<=255 then part.stencil=stencil end
                if finite(mask) and mask%1==0 and mask>=0 and mask<=255 then part.mask=mask end
                parts[#parts+1]=part
            end
        end)
        if #parts==0 then return end
        local health
        if valid(actor.Healthbar)then
            health={source=actor.Healthbar,relative=transform(mathAPI:MakeRelativeTransform(actor.Healthbar:K2_GetComponentToWorld(),actorTransform))}
        end
        templates[profile]={profile=profile,rotation={rotation.Pitch,rotation.Yaw,rotation.Roll},parts=parts,health=health,radius=radius,half=half,scale={X=scale.X,Y=scale.Y,Z=scale.Z}}
    end
    local function originalWorld()return c and valid(owner) and same(owner:GetWorld(),c.world) and valid(c.controller) and same(c.controller:GetWorld(),c.world)end
    local function destroy(actor)
        if valid(actor) and c and same(actor:GetWorld(),c.world) then actor:K2_DestroyActor() end
    end
    function s.close()
        if s.closed then return end;s.ready=false;s.closed=true
        local function attempt(fn) pcall(fn) end
        if saved and originalWorld() then
            attempt(function()if valid(saved.target) and same(saved.target:GetWorld(),c.world) then c.controller:SetViewTargetWithBlend(saved.target,0,0,0,false)end end)
            attempt(function()c.controller.bAutoManageActiveCameraTarget=saved.auto end)
            attempt(function()c.controller.bShouldPerformFullTickWhenPaused=saved.tick end)
            if saved.move then attempt(function()c.controller:SetIgnoreMoveInput(false)end)end
            if saved.look then attempt(function()c.controller:SetIgnoreLookInput(false)end)end
        end
        for _,h in pairs(s.hidden)do attempt(function()if valid(h.actor) and same(h.actor:GetWorld(),c.world)then h.actor:SetActorHiddenInGame(h.value)end end)end
        for _,h in ipairs(s.hiddenUI)do attempt(function()if valid(h.widget) and same(h.widget:GetWorld(),c.world)then h.widget:SetVisibility(h.value)end end)end
        for _,actor in pairs(s.owned)do attempt(function()destroy(actor)end)end
        s.owned={};s.actors={};s.hidden={};s.hiddenUI={};templates={};presentations={};legacyTemplate=nil
        -- Never unpause a new challenge or another world after an external transition.
        if saved and saved.changedPause and originalWorld() then
            local ok=pcall(function()inspect(owner,c.identity,c)end)
            if ok then attempt(function()c.api:SetGamePaused(owner,saved.paused)end)end
        end
    end
    local function verify()
        assert(s.ready and not s.closed,'replay scene not active')
        local nextContext=inspect(owner,c.identity,c)
        assert(same(nextContext.world,c.world) and same(nextContext.controller,c.controller) and same(nextContext.scenario,c.scenario),'replay context changed')
        assert(c.api:IsGamePaused(owner) and valid(s.camera),'replay pause lost')
        assert(same(c.controller:GetViewTarget(),s.camera),'replay camera ownership lost')
        return true
    end
    function s.verify()
        local ok,err=pcall(verify)
        if not ok then s.close();error(err) end
        return true
    end
    local function spawn(name)
        assert(name=='CameraActor' or name=='StaticMeshActor','unsupported replay visual')
        local cls=StaticFindObject('/Script/Engine.'..name);assert(valid(cls),'replay visual class unavailable')
        local t={Rotation={X=0,Y=0,Z=0,W=1},Translation={X=0,Y=0,Z=0},Scale3D={X=1,Y=1,Z=1}}
        local actor=c.api:BeginDeferredActorSpawnFromClass(owner,cls,t,1,nil)
        assert(valid(actor) and same(actor:GetWorld(),c.world),'replay visual spawn failed')
        s.owned[actor:GetAddress()]=actor
        actor:SetActorEnableCollision(false);actor:SetActorTickEnabled(false)
        c.api:FinishSpawningActor(actor,t)
        return actor
    end
    function s.bind(meta)
        assert(not s.closed and not saved,'replay scene cannot be rebound')
        c=inspect(owner,meta)
        local ok,err=pcall(function()
            local pc=c.controller;local target=pc:GetViewTarget();assert(valid(target),'original camera unavailable')
            saved={target=target,auto=pc.bAutoManageActiveCameraTarget,tick=pc.bShouldPerformFullTickWhenPaused,paused=c.api:IsGamePaused(owner)}
            if not saved.paused then saved.changedPause=true;assert(c.api:SetGamePaused(owner,true),'replay pause rejected')end
            pc:SetIgnoreMoveInput(true);saved.move=true;pc:SetIgnoreLookInput(true);saved.look=true
            pc.bAutoManageActiveCameraTarget=false;pc.bShouldPerformFullTickWhenPaused=true
            -- The game's session/score/countdown widgets refer to the paused
            -- live run. Replay HUD supplies its own recorded values instead.
            each(FindAllOf('PlayerUI_C') or {},function(widget)
                if valid(widget) and same(widget:GetWorld(),c.world) and same(widget:GetOwningPlayer(),pc) then
                    assert(#s.hiddenUI<16,'replay HUD budget exceeded')
                    s.hiddenUI[#s.hiddenUI+1]={widget=widget,value=widget:GetVisibility()};widget:SetVisibility(1)
                end
            end)
            local count=0;local skinErrorReported=false;local candidates,skinErrors=0,0
            each(c.gameState:GetCharacters(),function(actor)
                assert(valid(actor) and same(actor:GetWorld(),c.world),'invalid live character')
                if actor.bOnEnemyTeam==true and not actor.bHidden then candidates=candidates+1 end
                local skinOK,skinError=pcall(template,actor)
                if not skinOK then skinErrors=skinErrors+1 end
                if not skinOK and not skinErrorReported then skinErrorReported=true;print('[AimMod] Native replay skin template unavailable: '..tostring(skinError))end
                local id=actor:GetAddress();if not s.hidden[id] then
                    count=count+1;assert(count<=256,'live character budget exceeded')
                    local hidden=actor.bHidden;assert(type(hidden)=='boolean','actor visibility unavailable')
                    s.hidden[id]={actor=actor,value=hidden};actor:SetActorHiddenInGame(true)
                end
            end)
            local templateCount,partCount,healthCount=0,0,0
            for _,skin in pairs(templates)do
                templateCount=templateCount+1;partCount=partCount+#skin.parts;if skin.health then healthCount=healthCount+1 end
                legacyTemplate=skin
            end
            if templateCount~=1 then legacyTemplate=nil end
            -- Old files have positions but no profile/orientation. An exact
            -- same-scenario preflight plus one usable local profile permits a
            -- cosmetic local-skin fallback; never guess between profiles.
            print(string.format('[AimMod] Replay skin templates: candidates=%d profiles=%d parts=%d health=%d errors=%d legacyLocalSkin=%s',candidates,templateCount,partCount,healthCount,skinErrors,tostring(legacyTemplate~=nil)))
            s.camera=spawn('CameraActor');assert(valid(s.camera.CameraComponent),'replay camera component unavailable')
            s.camera.CameraComponent.bConstrainAspectRatio=false
            pc:SetViewTargetWithBlend(s.camera,0,0,0,false)
            s.ready=true;s.verify()
        end)
        if not ok then s.close();error(err)end
        return true
    end
    -- presented: the native presenter owns the view and target locations this
    -- frame (it applies them every engine frame); only spawn, style, rotate.
    function s.frame(frame,presented)
        local ok,err=pcall(function()
            s.verify();local camera=frame.camera
            assert(type(camera)=='table' and #camera==7 and type(frame.actors)=='table' and #frame.actors<=128,'invalid replay frame')
            for _,n in ipairs(camera)do assert(finite(n),'invalid replay camera')end
            assert(camera[7]>1 and camera[7]<179,'invalid replay FOV')
            local seen={}
            local appearance={}
            local partBudget=0
            for _,value in ipairs(frame.appearance or {})do
                local r=value.rotation
                if type(value.profile)=='string' and type(r)=='table' and #r==3 and finite(r[1]) and finite(r[2]) and finite(r[3])then
                    appearance[value.id]=value
                end
            end
            for _,a in ipairs(frame.actors)do
                assert(#a==6,'invalid replay actor');for _,n in ipairs(a)do assert(finite(n),'invalid replay actor value')end
                assert(a[1]>0 and a[1]%1==0 and not seen[a[1]] and a[5]>0 and a[6]>=a[5],'invalid replay actor geometry');seen[a[1]]=true
                local metadata=appearance[a[1]]
                local skin=metadata and templates[metadata.profile] or (not metadata and legacyTemplate or nil)
                if skin then partBudget=partBudget+#skin.parts end
            end
            assert(partBudget<=512,'native replay skin component budget exceeded')
            if not presented then
                s.camera:K2_SetActorLocationAndRotation(vector(camera[1],camera[2],camera[3]),{Pitch=camera[4],Yaw=camera[5],Roll=camera[6]},false,{},true)
                s.camera.CameraComponent:SetFieldOfView(camera[7])
            end
            for _,a in ipairs(frame.actors)do
                local proxy=s.actors[a[1]]
                local metadata=appearance[a[1]];local skin=metadata and templates[metadata.profile] or (not metadata and legacyTemplate or nil)
                local old=presentations[a[1]]
                if valid(proxy) and ((old and old.skin)~=skin)then s.owned[proxy:GetAddress()]=nil;destroy(proxy);proxy=nil end
                local fresh=not valid(proxy)
                if not valid(proxy)then
                    proxy=spawn('StaticMeshActor');s.actors[a[1]]=proxy;s.proxyVersion=(s.proxyVersion or 0)+1;local component=proxy.StaticMeshComponent
                    component:SetMobility(2);component:SetCollisionEnabled(0);component:SetSimulatePhysics(false);component:SetComponentTickEnabled(false)
                    if skin then
                        -- Copy native configured components onto an inert owned
                        -- root. Never instantiate a gameplay character or modify
                        -- the source bot, its material instances or its health.
                        for _,part in ipairs(skin.parts)do
                            assert(valid(part.mesh),'native replay skin mesh expired')
                            for _,material in pairs(part.materials)do assert(valid(material),'native replay skin material expired')end
                            local copy=proxy:AddComponentByClass(StaticFindObject('/Script/Engine.StaticMeshComponent'),false,part.relative,false)
                            assert(valid(copy),'native replay skin component unavailable')
                            copy:SetMobility(2);copy:SetCollisionEnabled(0);copy:SetSimulatePhysics(false);copy:SetComponentTickEnabled(false)
                            assert(copy:SetStaticMesh(part.mesh),'native replay skin mesh unavailable')
                            for index,material in pairs(part.materials)do copy:SetMaterial(index,material)end
                            for index,value in ipairs(part.primitiveData)do copy:SetCustomPrimitiveDataFloat(index-1,value)end
                            if part.stencil then copy:SetCustomDepthStencilValue(part.stencil)end
                            if part.mask then copy:SetCustomDepthStencilWriteMask(part.mask)end
                            if part.depth~=nil then copy:SetRenderCustomDepth(part.depth)end
                        end
                    else assert(component:SetStaticMesh(StaticFindObject('/Engine/BasicShapes/Sphere.Sphere')),'replay target mesh unavailable')end
                end
                presentations[a[1]]={actor=proxy,skin=skin,profile=skin and skin.profile,health=skin and skin.health,localSkinFallback=not metadata and skin~=nil}
                if skin then
                    local r=metadata and metadata.rotation or skin.rotation
                    if presented and not fresh then proxy:K2_SetActorRotation({Pitch=r[1],Yaw=r[2],Roll=r[3]},true)
                    else proxy:K2_SetActorLocationAndRotation(vector(a[2],a[3],a[4]),{Pitch=r[1],Yaw=r[2],Roll=r[3]},false,{},true)end
                    proxy:SetActorScale3D(vector(skin.scale.X*a[5]/skin.radius,skin.scale.Y*a[5]/skin.radius,skin.scale.Z*a[6]/skin.half))
                else
                    if not (presented and not fresh) then proxy:K2_SetActorLocation(vector(a[2],a[3],a[4]),false,{},true)end
                    proxy:SetActorScale3D(vector(a[5]/50,a[5]/50,a[6]/50))
                end
            end
            for id,proxy in pairs(s.actors)do if not seen[id]then s.owned[proxy:GetAddress()]=nil;destroy(proxy);s.actors[id]=nil;presentations[id]=nil;s.proxyVersion=(s.proxyVersion or 0)+1 end end
        end)
        if not ok then s.close();error(err)end
        return true
    end
    -- Render-rate pose between published frames: camera and target locations
    -- only, on proxies this scene already owns. Never spawns or destroys.
    function s.pose(camera,moves)
        local ok,err=pcall(function()
            assert(type(camera)=='table' and #camera==7,'invalid replay pose')
            for _,n in ipairs(camera)do assert(finite(n),'invalid replay pose')end
            assert(camera[7]>1 and camera[7]<179,'invalid replay FOV')
            assert(valid(s.camera),'replay camera unavailable')
            s.camera:K2_SetActorLocationAndRotation(vector(camera[1],camera[2],camera[3]),{Pitch=camera[4],Yaw=camera[5],Roll=camera[6]},false,{},true)
            s.camera.CameraComponent:SetFieldOfView(camera[7])
            for _,m in ipairs(moves or {})do
                local proxy=s.actors[m[1]]
                if valid(proxy) and finite(m[2]) and finite(m[3]) and finite(m[4]) then proxy:K2_SetActorLocation(vector(m[2],m[3],m[4]),false,{},true)end
            end
        end)
        if not ok then s.close();error(err)end
        return true
    end
    -- Object paths of the replay-owned camera and target proxies, for the
    -- native presenter (which moves nothing else).
    function s.proxies()
        local function path(o)local ok,name=pcall(function()return o:GetFullName()end);return ok and type(name)=='string' and name:match('^%S+%s+(%S.*)$') or nil end
        local lines={'AIMMOD_PROXIES_1'}
        local camera=valid(s.camera) and path(s.camera)
        if camera then lines[#lines+1]='camera\t'..camera end
        local ids={};for id in pairs(s.actors)do ids[#ids+1]=id end;table.sort(ids)
        for _,id in ipairs(ids)do local p=valid(s.actors[id]) and path(s.actors[id]);if p then lines[#lines+1]='actor\t'..id..'\t'..p end end
        return table.concat(lines,'\n')..'\n'
    end
    function s.isReady()local ok=pcall(s.verify);if not ok and s.ready then s.close()end;return ok end
    function s.presentation(id)return presentations[id]end
    return s
end
return M
