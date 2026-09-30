-- Opt-in transaction coordinator. The adapter owns UObject resolution and deep
-- map snapshots; this module never cancels sessions, unpauses, or touches uploads.
local M={}
local function finite(n) return type(n)=='number' and n==n and math.abs(n)<1e12 end
local function identity(s)
    return type(s)=='table' and s.worldId~=nil and s.controllerId~=nil and s.scenarioId~=nil and s.repositoryId~=nil
end
local function same(a,b)
    return identity(a) and identity(b) and a.worldId==b.worldId and a.controllerId==b.controllerId
        and a.scenarioId==b.scenarioId and a.repositoryId==b.repositoryId
end
function M.validate(s)
    if not identity(s) then return false,'context-unavailable' end
    if s.paused~=true then return false,'game-not-paused' end
    if s.active~=false or s.challenge~=false or s.captureActive~=false then return false,'session-active' end
    if s.editor~=false or s.loading~=false or s.downloading~=false or s.mapLoading~=false then return false,'session-transition' end
    if not finite(s.queueSeconds) or s.queueSeconds>0 then return false,'challenge-queued' end
    return true
end
local function mapValid(s)
    return type(s)=='table' and type(s.mapName)=='string' and #s.mapName>0 and #s.mapName<=1024
        and not s.mapName:find('[%z\1-\31\\/:]') and s.mapName~='.' and s.mapName~='..'
        and finite(s.mapScale) and s.mapScale>0
end
local function mapMatches(a,b)
    return mapValid(a) and mapValid(b) and a.mapName==b.mapName and math.abs(a.mapScale-b.mapScale)<=1e-6*math.max(1,math.abs(b.mapScale))
end
function M.new(adapter)
    assert(type(adapter.read)=='function' and type(adapter.load)=='function' and type(adapter.restore)=='function'
        and type(adapter.save)=='function' and type(adapter.clock)=='function','map transition adapter incomplete')
    local tx={phase='idle',reason=nil}; local original,saved,wanted,deadline,proved
    local function fail(reason) tx.phase='failed'; tx.reason=reason; return false,reason end
    local function observe(repositoryId,success)
        if tx.phase~='loading' and tx.phase~='restoring' then return end
        if repositoryId~=original.repositoryId then return end
        proved=success==true
        if not proved then tx.reason='map-parse-failed' end
    end
    tx.observe=observe
    function tx.begin(target)
        if tx.phase~='idle' and tx.phase~='closed' then return false,'transaction-busy' end
        if not mapValid(target) then return false,'recorded-map-unavailable' end
        local s=adapter.read(); local ok,reason=M.validate(s)
        if not ok then return false,reason end
        if not mapValid(s) then return false,'original-map-unavailable' end
        if adapter.observerReady~=true then return false,'parse-observer-unavailable' end
        -- save() must deep copy map data before LoadMapByName mutates its fields.
        saved=adapter.save(s); if saved==nil then return false,'restore-snapshot-unavailable' end
        original={}; for key,value in pairs(s) do original[key]=value end
        wanted={mapName=target.mapName,mapScale=target.mapScale,fingerprint=target.fingerprint}
        proved=nil; tx.reason=nil; tx.phase='loading'; deadline=adapter.clock()+10
        local loaded=pcall(adapter.load,wanted)
        if not loaded then return fail('map-load-error') end
        return tx.poll()
    end
    function tx.poll()
        if tx.phase~='loading' and tx.phase~='restoring' and tx.phase~='ready' then return false,tx.reason end
        local s=adapter.read()
        if not same(s,original) then return fail('context-changed') end
        local ok,reason=M.validate(s)
        if not ok then return fail(reason) end
        if tx.phase=='ready' then
            if not mapMatches(s,wanted) then return fail('map-changed') end
            return true
        end
        if proved==false then return fail('map-parse-failed') end
        if adapter.clock()>deadline then return fail('map-load-timeout') end
        local expected=tx.phase=='restoring' and original or wanted
        if proved==true and mapMatches(s,expected) and s.geometryReady==true then
            if expected.fingerprint and s.fingerprint~=expected.fingerprint then return fail('map-fingerprint-mismatch') end
            tx.phase=tx.phase=='restoring' and 'closed' or 'ready'; return true
        end
        return false,'map-loading'
    end
    function tx.close()
        if tx.phase=='idle' or tx.phase=='closed' then return true end
        local s=adapter.read()
        -- Never cancel new genuine activity or restore through a stale world.
        if not same(s,original) then return fail('context-changed') end
        local ok,reason=M.validate(s); if not ok then return fail(reason) end
        proved=nil; tx.phase='restoring'; tx.reason=nil; deadline=adapter.clock()+10
        if not pcall(adapter.restore,saved) then return fail('map-restore-error') end
        return tx.poll()
    end
    return tx
end

-- Installed UE4SS 527a483b LuaMod.cpp post-hook signature puts original return
-- value SECOND, ahead of ordinary arguments. No callback return overrides it.
-- https://github.com/UE4SS-RE/RE-UE4SS/blob/527a483b/UE4SS/src/Mod/LuaMod.cpp
function M.observeParser(callback)
    local path='/Script/GameSkillsTrainer.KovaakMapCreatorRepository:LoadMapFromLines'
    local fn=StaticFindObject(path)
    if not fn or not fn:IsValid() then return nil end
    local pre,post=RegisterHook(path,function() return nil end,function(context,result)
        pcall(function()
            local repository=context:get()
            if repository and repository:IsValid() then callback(repository:GetAddress(),result:get()==true) end
        end)
        return nil
    end)
    return function() UnregisterHook(path,pre,post) end
end
return M
