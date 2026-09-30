local loop,files,scans=nil,{},0
local function valid(t)t=t or{};t.IsValid=function()return true end;return t end
local widget=valid({LoadingThrobber=valid({GetVisibility=function()return 2 end}),GetFullName=function()return'Synthetic leaderboard'end,CurrentLeaderboardName='Synthetic scenario',FriendsOnlyCheckbox=valid({IsChecked=function()return true end}),CurrentLeaderboardPlayers={{PlayerName='Synthetic friend',Score=700,rank=2,ScoreInvalid=false},{PlayerName='Invalid',Score=999,rank=1,ScoreInvalid=true},{PlayerName='NanRank',Score=5,rank=0/0,ScoreInvalid=false},{PlayerName='InfRank',Score=5,rank=1/0,ScoreInvalid=false},{PlayerName='FractionRank',Score=5,rank=2.5,ScoreInvalid=false}}})
StaticFindObject=function()return valid({UWorksSteamIDToString=function()return '76561198000000001'end})end
LoopInGameThreadWithDelay=function(ms,fn)assert(ms==1000);loop=fn end
FindAllOf=function(name)assert(name=='LeaderboardsWidget');scans=scans+1;return{widget}end
os.getenv=function()return'synthetic'end;os.remove=function(p)files[p]=nil end;os.rename=function(a,b)files[b]=files[a];files[a]=nil end
io.open=function(p)return{write=function(_,s)files[p]=s;return true end,close=function()end}end
local M=dofile('../ue4ss/AimModNativeUI/Scripts/LiveOpponents.lua');M.start();M.start();loop()
local output=files['synthetic/AimMod/KovaaksNative/live-opponents.json'];assert(output:find('"source":"friends"',1,true));assert(output:find('"score":700',1,true));assert(not output:find('Invalid',1,true));assert(not output:find('Rank',1,true),'non-finite or fractional ranks never reach JSON');for i=1,9 do loop()end;assert(scans==1);loop();assert(scans==2)
assert(output:find('"steamId":"76561198000000001"',1,true))
print('PASS 7 observed opponent checks')
