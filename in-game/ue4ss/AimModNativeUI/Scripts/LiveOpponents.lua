-- Read already populated native leaderboard rows; never request scores or modify them.
local M={};local started=false;local widget;local scans=0;local steamConverter
local function valid(o)local ok,v=pcall(function()return o and o:IsValid()end);return ok and v end
local function text(v)return type(v)=='string'and v or v:ToString()end
local function quote(s)return '"'..s:gsub('[%z\1-\31\\"]',function(c)return string.format('\\u%04x',string.byte(c))end)..'"'end
function M.start()
 if started then return end;started=true
 pcall(function()steamConverter=StaticFindObject('/Script/GameSkillsTrainer.Default__WorksStatics')end)
 LoopInGameThreadWithDelay(1000,function()
  pcall(function()
   scans=scans-1;if scans<=0 then scans=10;widget=nil;for _,candidate in ipairs(FindAllOf('LeaderboardsWidget')or{})do if valid(candidate)and not candidate:GetFullName():find('Default__',1,true)then widget=candidate;break end end end
   if not valid(widget)then return end
   -- Do not label the previous list with a newly requested scenario while the
   -- widget is fetching its replacement rows.
   if not valid(widget.LoadingThrobber)then return end
   local loading=widget.LoadingThrobber:GetVisibility();if loading==0 or loading==3 or loading==4 then return end
   local scenario=text(widget.CurrentLeaderboardName);if #scenario<1 or #scenario>512 then return end
   local source='leaderboard';if valid(widget.FriendsOnlyCheckbox)and widget.FriendsOnlyCheckbox:IsChecked()then source='friends'end
   local rows={};local count=0
   local function add(row)
    count=count+1;if count>128 then return end
    if not pcall(function()return row.Score end)then row=row:get()end
    local name=text(row.PlayerName);local score,rank=row.Score,row.rank
    if row.ScoreInvalid or type(score)~='number'or score~=score or math.abs(score)>=1e12 or type(rank)~='number'or rank<1 or #name<1 or #name>256 then return end
    local steam=''
    pcall(function()
     if valid(steamConverter)then
      local id=text(steamConverter:UWorksSteamIDToString(row.SteamID))
      if #id==17 and id:match('^%d+$') and id~='00000000000000000'then steam=',"steamId":'..quote(id)end
     end
    end)
    rows[#rows+1]='{"name":'..quote(name)..',"score":'..tostring(score)..',"rank":'..tostring(rank)..steam..'}'
   end
   local players=widget.CurrentLeaderboardPlayers
   if type(players)=='table'and players.ForEach==nil then for i=1,math.min(#players,128)do add(players[i])end else players:ForEach(function(_,row)if count<128 then add(row:get())end end)end
   local path=(os.getenv('LOCALAPPDATA')or'')..'/AimMod/KovaaksNative/live-opponents.json';local file=io.open(path..'.next','wb')
   if file then local ok=file:write('{"scenario":'..quote(scenario)..',"source":'..quote(source)..',"rows":['..table.concat(rows,',')..']}');file:close();if ok then os.remove(path);os.rename(path..'.next',path)end end
  end)
 end)
end
return M
