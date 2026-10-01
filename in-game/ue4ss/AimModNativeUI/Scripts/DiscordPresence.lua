-- Hands KovaaK's own Discord presence to the AimMod worker and back.
-- KovaaK's publishes its presence itself from a background thread and exposes
-- one reflected switch for it (MetaGameUserSettings Get/SetDiscordRichPresence,
-- the "Discord Rich Presence" option in its settings menu). Turning it off makes
-- the game clear its activity and close its Discord connection; turning it on
-- reconnects. This module only flips that switch: nothing is hooked, and no
-- gameplay, scoring, input or upload function is called.
--
-- Files in %LOCALAPPDATA%/AimMod/KovaaksNative:
--   discord-takeover.tsv  worker -> game, refreshed every 2 s while the worker
--                         wants to publish; older than 6 s means "not wanted"
--   discord-game.tsv      game -> worker, refreshed every second: released |
--                         game | off | unavailable. The worker publishes only
--                         while it reads "released".
--   discord-restore.tsv   present while this module holds the switch off, so a
--                         crash is repaired on the next launch.
local M={}
local base=(os.getenv('LOCALAPPDATA') or '')..'/AimMod/KovaaksNative/'
local started,userSettings,held,state,written,failed=false,nil,nil,nil,-1,false
local class='/Script/GameSkillsTrainer.MetaGameUserSettings'
local function valid(o)local ok,v=pcall(function()return o and o:IsValid()end);return ok and v==true end
local function read(name,limit)
    local file=io.open(base..name,'rb');if not file then return nil end
    local ok,text=pcall(function()return file:read(limit)end);file:close()
    if ok then return text end
end
local function write(name,text)
    local file=io.open(base..name..'.next','wb');if not file then return false end
    local ok=file:write(text);file:close();if not ok then return false end
    os.remove(base..name);return os.rename(base..name..'.next',base..name)~=nil
end
local function requested(now)
    local text=read('discord-takeover.tsv',128);if not text then return false end
    local stamp=tonumber(text:match('^AIMMOD_DISCORD_TAKEOVER_1\t(%d+)\r?\n$'))
    return stamp~=nil and now-stamp<=6 and stamp-now<=2
end
-- The static accessors read GEngine's game user settings, so they are called
-- only once that live (non-default) settings object exists.
local function bind()
    if valid(userSettings) then return true end
    userSettings=nil
    if not valid(StaticFindObject(class..':GetDiscordRichPresence')) or not valid(StaticFindObject(class..':SetDiscordRichPresence')) then return false end
    for _,candidate in ipairs(FindAllOf('MetaGameUserSettings') or {}) do
        if valid(candidate) and not candidate:GetFullName():find('Default__',1,true) then userSettings=candidate;break end
    end
    return userSettings~=nil
end
local function publish(now,value)
    if value==state and now-written<1 then return end
    if write('discord-game.tsv','AIMMOD_DISCORD_GAME_1\t'..value..'\t'..now..'\n') then state=value;written=now end
end
function M.tick()
    local now=os.time()
    if not bind() then publish(now,'unavailable');return end
    local enabled=userSettings:GetDiscordRichPresence()==true
    if held==nil then held=read('discord-restore.tsv',64)~=nil end
    if requested(now) then
        if enabled then
            -- Record the hold before changing anything, so it can be undone.
            if not held then
                if not write('discord-restore.tsv','AIMMOD_DISCORD_RESTORE_1\n') then publish(now,'game');return end
                held=true
            end
            userSettings:SetDiscordRichPresence(false)
            enabled=userSettings:GetDiscordRichPresence()==true
        end
        -- Not held and off: the player turned KovaaK's presence off. Keep it off
        -- and let nobody publish.
        publish(now,enabled and 'game' or (held and 'released' or 'off'))
    else
        if held then
            if not enabled then userSettings:SetDiscordRichPresence(true);enabled=userSettings:GetDiscordRichPresence()==true end
            if enabled then os.remove(base..'discord-restore.tsv');held=false end
        end
        publish(now,enabled and 'game' or 'off')
    end
end
function M.start()
    if started then return end;started=true
    local waited=0
    LoopInGameThreadWithDelay(500,function()
        -- Give the engine time to create its user settings object.
        if waited<10 then waited=waited+1;return end
        local ok,reason=pcall(M.tick)
        if not ok and not failed then failed=true;print('[AimMod] Discord handoff unavailable: '..tostring(reason)..'\n') end
    end)
end
return M
