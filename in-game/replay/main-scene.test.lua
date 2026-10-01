local n=0
local function obj(t)n=n+1;local id=n;t=t or {};t.IsValid=function(self)return not self.dead end;t.GetAddress=function()return id end;return t end
local function noop()end
local world=obj();local activeWorld=world;local instance=obj();local challenge=false;local loading=false
local paused=false;local pauses=0;local spawned=0;local destroyed=0;local move=2;local look=0;local failSpawn=false
local target=obj({GetWorld=function()return world end})
local pc=obj({GetWorld=function()return world end,bAutoManageActiveCameraTarget=true,bShouldPerformFullTickWhenPaused=false,
    target=target,GetViewTarget=function(self)return self.target end,SetViewTargetWithBlend=function(self,t)self.target=t end,
    SetIgnoreMoveInput=function(_,v)move=move+(v and 1 or -1)end,SetIgnoreLookInput=function(_,v)look=look+(v and 1 or -1)end})
local menuVisible=true
local owner=obj({GetWorld=function()return activeWorld end,IsVisible=function()return menuVisible end})
local live=obj({bHidden=false,GetWorld=function()return world end,SetActorHiddenInGame=function(self,v)self.bHidden=v end})
local alreadyHidden=obj({bHidden=true,GetWorld=function()return world end,SetActorHiddenInGame=function(self,v)self.bHidden=v end})
local scenarioActive=false
local scenario=obj({IsActive=function()return scenarioActive end,IsInChallenge=function()return challenge end,GetName=function()return 'Synthetic scenario' end})
local elapsed,queue,clock=10,0,100
local manager=obj({GetChallengeTimeElapsed=function()return elapsed end,GetChallengeQueueTimeRemaining=function()return queue end,GetOuter=function()return instance end,IsInChallenge=function()return challenge end,IsScenarioLoading=function()return loading end,
    IsInScenarioEditor=function()return false end,IsCurrentlyInBenchmark=function()return false end,GetCurrentScenario=function()return scenario end})
local gs=obj({GetWorld=function()return world end,bMapLoading=false,bFullyLoaded=true,GetCurrentMapName=function()return 'Synthetic map' end,
    GetMapScale=function()return 1 end,GetCharacters=function()return {{get=function()return live end},alreadyHidden}end})
local api=obj({GetTimeSeconds=function()return clock end,GetGameInstance=function()return instance end,GetPlayerController=function()return pc end,GetGameState=function()return gs end,
    IsGamePaused=function()return paused end,SetGamePaused=function(_,_,value)paused=value;pauses=pauses+1;return true end,
    GetPlayerCameraManager=function()return obj({GetCameraLocation=function()return {X=0,Y=0,Z=10}end,GetCameraRotation=function()return {Pitch=0,Yaw=0,Roll=0}end,GetFOVAngle=function()return 110 end})end,
    BeginDeferredActorSpawnFromClass=function(_,context,class,transform,collision)
        assert(context==owner and collision==1 and paused)
        if failSpawn then error('synthetic spawn failure')end
        assert(class.name=='CameraActor' or class.name=='StaticMeshActor');spawned=spawned+1
        return obj({GetWorld=function()return world end,SetActorEnableCollision=function(_,v)assert(v==false)end,SetActorTickEnabled=noop,
            K2_SetActorLocationAndRotation=function(self,p,r)self.rotation=r end,K2_SetActorLocation=noop,SetActorScale3D=function(self,v)self.scale=v end,
            AddComponentByClass=function(self,class,manual,relative,deferred)
                assert(class.name=='StaticMeshComponent' and manual==false and deferred==false)
                local part=obj({relative=relative,SetMobility=noop,SetCollisionEnabled=noop,SetSimulatePhysics=noop,SetComponentTickEnabled=noop,
                    SetStaticMesh=function(self,mesh)self.mesh=mesh;return true end,SetMaterial=function(self,index,material)self.material=material end,
                    SetCustomPrimitiveDataFloat=function(self,index,value)self.data=self.data or {};self.data[index]=value end,
                    SetCustomDepthStencilValue=function(self,v)self.stencil=v end,SetCustomDepthStencilWriteMask=function(self,v)self.mask=v end,
                    SetRenderCustomDepth=function(self,v)self.depth=v end})
                self.parts=self.parts or {};self.parts[#self.parts+1]=part;return part
            end,
            K2_DestroyActor=function(self)assert(not self.dead);self.dead=true;destroyed=destroyed+1 end,
            CameraComponent=obj({SetFieldOfView=function(self,v)self.fov=v end}),
            StaticMeshComponent=obj({SetMobility=noop,SetCollisionEnabled=noop,SetSimulatePhysics=noop,SetComponentTickEnabled=noop,SetStaticMesh=function()return true end,SetMaterial=function(self,index,material)assert(index==0);self.material=material end})})
    end,FinishSpawningActor=noop})
StaticFindObject=function(path)if path:find('GameplayStatics',1,true)then return api end;return obj({name=path:match('%.([^%.]+)$')})end
local scans=0
local nativeUI=obj({visibility=4,GetWorld=function()return world end,GetOwningPlayer=function()return pc end,
    GetVisibility=function(self)return self.visibility end,SetVisibility=function(self,value)self.visibility=value end})
FindAllOf=function(name)scans=scans+1;if name=='PlayerUI_C' then return {nativeUI}end;assert(name=='ScenarioManager');return {manager}end
local M=dofile('../ue4ss/AimModNativeUI/Scripts/ReplayMainScene.lua')
local meta={scenario='Synthetic scenario',mapName='Synthetic map',mapScale=1}
local checks=0;local function check(v,msg)assert(v,msg);checks=checks+1 end
check(M.verify(owner,meta).mapName==meta.mapName and spawned==0 and pauses==0,'preflight is read only')
challenge=true;check(not pcall(M.verify,owner,meta) and pauses==0,'active challenge rejected before mutation');challenge=false
scenarioActive=true;check(not pcall(M.verify,owner,meta) and pauses==0,'active freeplay rejected before mutation');scenarioActive=false
gs.bFullyLoaded=false;check(not pcall(M.verify,owner,meta),'incomplete map rejected');gs.bFullyLoaded=true
loading=true;check(not pcall(M.verify,owner,meta),'scenario loading rejected');loading=false
check(not pcall(M.verify,owner,{scenario=meta.scenario,mapName='Other map',mapScale=1}),'mismatched map rejected')
check(not pcall(M.verify,owner,{}),'legacy missing metadata rejected')
local frame=M.proofFrame(owner);check(frame.actors[1][2]==300 and frame.camera[7]==110 and pauses==0,'proof uses current camera without mutation')
local s=M.create(owner);check(s.bind(meta) and paused and move==3 and look==1,'bind pauses and adds balanced locks')
check(pc.target==s.camera and live.bHidden and alreadyHidden.bHidden,'actual view target and reversible live visibility')
check(nativeUI.visibility==1,'live-run HUD hidden during replay')
check(s.frame(frame) and s.camera.CameraComponent.fov==110,'native camera FOV set')
local count=spawned;local previousScans=scans;s.frame(frame);check(spawned==count,'proxy reused')
check(scans==previousScans,'per-frame guards reuse validated manager without object scans')
s.frame({camera=frame.camera,actors={}});check(next(s.actors)==nil and destroyed==1,'despawn removes owned reference')
s.close();check(not paused and move==2 and look==0 and pc.target==target,'original pause camera and input counters restored')
check(pc.bAutoManageActiveCameraTarget and not pc.bShouldPerformFullTickWhenPaused and not live.bHidden and alreadyHidden.bHidden,'original flags restored exactly')
check(nativeUI.visibility==4,'live-run HUD restored to original visibility')
local done=destroyed;s.close();check(destroyed==done,'close idempotent')
failSpawn=true;s=M.create(owner);check(not pcall(s.bind,meta) and not paused and pc.target==target and move==2 and not live.bHidden,'partial bind failure rolls back');failSpawn=false
paused=true;s=M.create(owner);s.bind(meta);s.close();check(paused,'preexisting pause retained');paused=false
s=M.create(owner);s.bind(meta);challenge=true;check(not pcall(s.frame,frame) and s.closed and paused and pc.target==target,'challenge transition aborts and never unpauses');challenge=false;paused=false
s=M.create(owner);s.bind(meta);activeWorld=obj();check(not pcall(s.frame,frame) and s.closed and paused,'world transition aborts without unpausing new world');activeWorld=world
pc.target=target;move=2;look=0
s=M.create(owner);s.bind(meta);owner.dead=true;s.close();owner.dead=false
check(move==2 and look==0 and pc.target==target,'input and camera handed back when the pause widget was destroyed mid-world');paused=false
pc.target=target;move=2;look=0
challenge=true;scenarioActive=true;paused=true
local priorPauses=pauses
check(M.verify(owner,meta).mapName==meta.mapName and pauses==priorPauses,'paused active challenge preflight does not cancel or change pause')
menuVisible=false;check(not pcall(M.verify,owner,meta),'paused active challenge requires visible menu for entry');menuVisible=true
s=M.create(owner);s.bind(meta);menuVisible=false
check(s.frame(frame) and challenge and scenarioActive and pauses==priorPauses,'frozen challenge supports actual viewport without changing lifecycle')
s.close();menuVisible=true
check(paused and challenge and scenarioActive and elapsed==10 and clock==100 and pc.target==target and move==2 and look==0,'exit preserves original paused run and timer')
s=M.create(owner);s.bind(meta);elapsed=10.1
check(not pcall(s.frame,frame) and s.closed and paused,'challenge timer advancement fails closed');elapsed=10
s=M.create(owner);s.bind(meta);elapsed=10.00005
check(pcall(s.frame,frame) and not s.closed,'sub-tolerance timer read variation retains paused replay')
elapsed=10.00011
local driftOk,driftError=pcall(s.frame,frame)
check(not driftOk and tostring(driftError):find('replay challenge elapsed changed',1,true) and s.closed and paused,'cumulative timer movement checked against original snapshot');elapsed=10
s=M.create(owner);s.bind(meta);queue=.01
local queueOk,queueError=pcall(s.frame,frame)
check(not queueOk and tostring(queueError):find('replay challenge queue changed',1,true) and s.closed and paused,'queue changes have distinct fail-closed diagnostic');queue=0
s=M.create(owner);s.bind(meta);clock=100.001
local clockOk,clockError=pcall(s.frame,frame)
check(not clockOk and tostring(clockError):find('replay world clock advanced',1,true) and s.closed and paused,'world time advancement fails closed even if challenge counters frozen');clock=100
s=M.create(owner);s.bind(meta);paused=false
check(not pcall(s.frame,frame) and s.closed and pc.target==target,'external resume closes replay immediately')
paused=true;s=M.create(owner);s.bind(meta);challenge=false;scenarioActive=false
check(not pcall(s.frame,frame) and s.closed and paused,'completion during replay closes without resuming')
scenarioActive=true;s=M.create(owner);s.bind(meta);s.frame(frame);s.close()
check(paused and scenarioActive and pc.target==target,'paused freeplay also preserves its original state')
elapsed=0/0;check(not pcall(M.verify,owner,meta),'unreadable active timer fails closed');elapsed=10;scenarioActive=false
local skin=obj();local mesh=obj();local skinReads=0
local localTransform={Translation={X=0,Y=0,Z=4},Rotation={X=0,Y=0,Z=0,W=1},Scale3D={X=1,Y=1,Z=1}}
local oldFind=StaticFindObject
StaticFindObject=function(path)if path:find('KismetMathLibrary',1,true)then return obj({MakeRelativeTransform=function(_,a)return a end})end;return oldFind(path)end
live.bOnEnemyTeam=true;live.bHidden=false
live.GetCharacterProfileName=function()return 'Synthetic bot' end
live.GetTransform=function()return localTransform end;live.GetActorScale3D=function()return {X=1,Y=1,Z=1}end
live.K2_GetActorRotation=function()return {Pitch=4,Yaw=5,Roll=6}end
live.CapsuleComponent=obj({GetScaledCapsuleRadius=function()return 35 end,GetScaledCapsuleHalfHeight=function()return 35 end})
local component=obj({StaticMesh=mesh,IsVisible=function()return true end,bHiddenInGame=false,GetNumMaterials=function()return 1 end,
    GetMaterial=function()skinReads=skinReads+1;return skin end,K2_GetComponentToWorld=function()return localTransform end})
component.CustomPrimitiveData={Data={.1,.2,.3}}
component.CustomPrimitiveDataInternal={Data={ForEach=function(_,fn)fn(0,{get=function()return .7 end});fn(1,{get=function()return .8 end})end}}
component.bRenderCustomDepth=true;component.CustomDepthStencilValue=5;component.CustomDepthStencilWriteMask=1
live.K2_GetComponentsByClass=function()return {component}end
live.Healthbar=obj({K2_GetComponentToWorld=function()return localTransform end})
local skinFrame={camera=frame.camera,actors=frame.actors,appearance={{id=1,profile='Synthetic bot',rotation={10,20,30}}}}
s=M.create(owner);s.bind(meta);s.frame(skinFrame)
check(s.actors[1].parts[1].mesh==mesh and s.actors[1].parts[1].material==skin,'exact profile native mesh and material copied to owned component')
check(s.actors[1].rotation.Yaw==20 and s.actors[1].rotation.Roll==30,'recorded actor orientation applied')
check(s.presentation(1).health.source==live.Healthbar,'native healthbar source tied to matched target profile')
check(s.actors[1].parts[1].relative.Translation.Z==4,'native component relative offset retained')
check(s.actors[1].parts[1].data[0]==.7 and s.actors[1].parts[1].data[1]==.8 and s.actors[1].parts[1].data[2]==nil,'native internal primitive color parameters preserved without guessed defaults')
check(s.actors[1].parts[1].depth==true and s.actors[1].parts[1].stencil==5 and s.actors[1].parts[1].mask==1,'native custom depth stencil style preserved')
local priorReads=skinReads;s.frame(skinFrame);check(skinReads==priorReads,'native skin cached outside frame loop')
s.frame(frame);check(s.actors[1].parts[1].mesh==mesh and s.presentation(1).health.source==live.Healthbar and s.presentation(1).localSkinFallback,'one local profile supplies old recording cosmetic skin and native healthbar')
check(s.actors[1].rotation.Pitch==4 and s.actors[1].rotation.Yaw==5,'legacy cosmetic skin retains source orientation without inventing recorded motion');s.close()
local unknown={camera=frame.camera,actors=frame.actors,appearance={{id=1,profile='Other bot',rotation={0,0,0}}}}
s=M.create(owner);s.bind(meta);s.frame(unknown);check(s.actors[1].parts==nil,'mismatched profile never borrows another bot skin');s.close()
local second=obj({bOnEnemyTeam=true,bHidden=false,GetWorld=function()return world end,SetActorHiddenInGame=function(self,v)self.bHidden=v end,
    GetCharacterProfileName=function()return 'Second bot' end,GetTransform=live.GetTransform,GetActorScale3D=live.GetActorScale3D,
    K2_GetActorRotation=live.K2_GetActorRotation,CapsuleComponent=live.CapsuleComponent,K2_GetComponentsByClass=live.K2_GetComponentsByClass,Healthbar=live.Healthbar})
gs.GetCharacters=function()return {live,second}end
s=M.create(owner);s.bind(meta);s.frame(frame);check(s.actors[1].parts==nil and s.presentation(1).health==nil,'multiple local profiles never guessed for old recordings');s.close()
s=M.create(owner);s.bind(meta);mesh.dead=true
local meshOk,meshError=pcall(s.frame,skinFrame)
check(not meshOk and tostring(meshError):find('native replay skin mesh expired',1,true) and s.closed,'expired cached mesh closes scene before passing invalid UObject to native setter');mesh.dead=false
s=M.create(owner);s.bind(meta);skin.dead=true
local materialOk,materialError=pcall(s.frame,skinFrame)
check(not materialOk and tostring(materialError):find('native replay skin material expired',1,true) and s.closed,'expired cached material closes scene before native setter');skin.dead=false
print('PASS '..checks..' main viewport replay checks')
