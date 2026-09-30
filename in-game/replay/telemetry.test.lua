package.preload.LiveScore=function()return {read=function()return {}end}end
-- Synthetic UI event routes. No native gameplay functions are callable here.
local callbacks,delayed,lines={},{},{}
local clock=1
local function valid(t)t.IsValid=function()return true end;return t end
local helpers=valid({GetTimeSeconds=function()return clock end})
local player=valid({GetFullName=function()return 'SyntheticController' end})
local ownWorld=valid({GetAddress=function()return 1 end})
local foreignWorld=valid({GetAddress=function()return 2 end})
helpers.GetGameState=function(_,context)return context=='foreign' and foreignWorld or ownWorld end
StaticFindObject=function(path)if path:find('GameplayStatics',1,true)then return helpers end;return valid({})end
FindAllOf=function()return {player}end
RegisterHook=function(path,_,post)callbacks[path]=post end
ExecuteInGameThreadWithDelay=function(_,fn)delayed[#delayed+1]=fn end
io.open=function()return {write=function(_,line)lines[#lines+1]=line end,close=function()end}end
os.getenv=function()return 'test-private-root' end
local starts=0
package.preload.ReplayCapture=function()return {start=function()starts=starts+1 end}end
local telemetry=dofile('../ue4ss/AimModNativeUI/Scripts/Telemetry.lua');telemetry.start()
local function param(v)return {get=function()return v end}end
local function scenario()return param({ToString=function()return 'Synthetic scenario' end})end
local function event(name,... )callbacks['/Script/GameSkillsTrainer.AnalyticsManager:'..name]({},...)end
local checks=0;local function check(v,msg)assert(v,msg);checks=checks+1 end
telemetry.start();check(starts==1,'telemetry and capture hook registration are idempotent')
event('OnChallengeStarted',scenario());local first=telemetry.state().id
check(telemetry.state().active,'normal play route starts capture')
check(telemetry.state().startEvent=='started','normal event source retained privately')
for n=1,40 do clock=1+n/100;event('OnChallengeReplayed',scenario());event('OnChallengeStarted',scenario()) end
check(telemetry.state().id==first and telemetry.state().startedAt==1,'repeated start notifications preserve identity and duration')
check(telemetry.state().startNotifications==81,'duplicate notifications counted privately')
clock=2;event('OnChallengeRestarted',scenario(),param(59))
check(telemetry.state().id==first,'restart notification alone preserves actual attempt')
telemetry.confirmAttempt(true,0);local second=telemetry.state().id
check(telemetry.state().active and second~=first,'confirmed native restart creates new attempt without Started')
check(telemetry.state().startEvent=='restarted','restart event source retained privately')
clock=3;telemetry.confirmAttempt(false,1)
check(telemetry.state().startedAt==2,'native timer establishes completed-history duration')
local metric=callbacks['/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Send_ShotsHit']
metric({},param(0));clock=4;metric({},param(1))
check(telemetry.state().hitStamp==4 and telemetry.state().hitDelta==1,'confirmed hit increment records native clock')
clock=5;metric({},param(1));check(telemetry.state().hitStamp==4,'duplicate metric does not replay a hit')
local scoreEvent=callbacks['/Script/KovaaKFramework.ScenarioStateReceiver:Send_ChallengeScore']
scoreEvent({},param({ChallengeName='Different scenario',Score=999}))
check(telemetry.state().score==nil,'unrelated native score broadcast ignored')
scoreEvent({},param({ChallengeName='Synthetic scenario',Score=42,ShotsHit=2,ShotsFired=4,KillCount=1,DamageDone=8}))
check(telemetry.state().score==42 and telemetry.state().shots==4,'native score broadcast captured without invoking scoring')
scoreEvent({},param({ChallengeName='Synthetic scenario',Score=0/0}))
check(telemetry.state().score==42,'invalid score broadcast preserves last valid score')
local computed=callbacks['/Script/GameSkillsTrainer.StatsManager:CalculateScore']
check(computed({},param({ChallengeName='Synthetic scenario',Score=43,DamageDone=0.0123}),param(player))==nil,'native score observer never replaces original return value')
check(telemetry.state().score==43 and telemetry.state().damage==0.0123,'original native computation captured including fractional damage')
computed({},param({ChallengeName='Synthetic scenario',Score=999}),param('foreign'))
check(telemetry.state().score==43,'foreign world computation cannot contaminate recording')
clock=62;event('OnChallengeCompleted',scenario(),param(123))
check(telemetry.state().score==123,'authoritative final score retained after completion')
check(not telemetry.state().active and #delayed==1,'native completion ends one attempt')
event('OnChallengeCompleted',scenario(),param(123));check(#delayed==1,'duplicate completion ignored')
delayed[1]();check(#lines==1 and lines[1]:find(second,1,true),'only completed attempt appended to local history')
clock=63;event('OnChallengeReplayed',scenario());local third=telemetry.state().id
check(telemetry.state().active and third~=second,'result replay route independently starts capture')
check(telemetry.state().startEvent=='replayed','replay event source retained privately')
event('OnChallengeQuit',scenario(),param(59));check(not telemetry.state().active and #delayed==1,'quit interrupts without completed score')
print('PASS '..checks..' telemetry lifecycle checks')
