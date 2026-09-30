-- Opt-in diagnostic observer. Never call score functions or modify hook values.
-- Counters cover reflected entry points, not every native HTTP/Steam call.
-- Start before a proof and compare snapshots within the same Lua module lifetime.
local M = {}
local started = false
local limit = 1000000000
local events = {
    {'challenge_completed', '/Script/GameSkillsTrainer.AnalyticsManager:OnChallengeCompleted'},
    {'challenge_broadcast', '/Script/GameSkillsTrainer.ScenarioManager:BroadcastChallengeCompleted'},
    {'stats_saved', '/Script/GameSkillsTrainer.MetaCharacter:SaveStatsAndResetAllAccuracy'},
    {'damage_notified', '/Script/GameSkillsTrainer.ScenarioManager:NotifyDamageDealt'},
    {'death_notified', '/Script/GameSkillsTrainer.ScenarioManager:NotifyCharacterDeath'},
    {'kill_notified', '/Script/GameSkillsTrainer.ScenarioManager:NotifyPlayerKillCredit'},
    {'experiments_upload', '/Script/GameSkillsTrainer.ExperimentsUploadLeaderboardScoreNode:UploadLeaderboardScoreNode'},
    {'steam_upload_node', '/Script/UWorksCore.CoreUploadLeaderboardScoreNode:UploadLeaderboardScoreNode'},
    {'steam_upload_minimal', '/Script/UWorksCore.UWorksInterfaceCoreUserStats:UploadLeaderboardScoreMinimal'},
    {'steam_upload_request', '/Script/UWorksCore.UWorksInterfaceCoreUserStats:UploadLeaderboardScore'},
    {'steam_upload_input', '/Script/UWorksCore.UWorksRequestCoreUploadLeaderboardScore:SetInput'}
}
local counters = {}
for _, event in ipairs(events) do counters[event[1]] = {count=0, available=false, saturated=false} end

function M.start()
    if started then return end
    started = true
    for _, event in ipairs(events) do
        local counter = counters[event[1]]
        local path = event[2]
        local ok, available = pcall(function()
            local fn = StaticFindObject(path)
            if not fn or not fn:IsValid() then return false end
            RegisterHook(path, function() return nil end, function()
                -- Deliberately do not accept/read/dereference arguments or results.
                if counter.count < limit then counter.count = counter.count + 1
                else counter.saturated = true end
                return nil
            end)
            return true
        end)
        counter.available = ok and available == true
    end
end

function M.snapshot()
    local snapshot = {format=1, started=started, counters={}}
    local records = {}
    for _, event in ipairs(events) do
        local key = event[1]
        local counter = counters[key]
        snapshot.counters[key] = {count=counter.count, available=counter.available, saturated=counter.saturated}
        records[#records+1] = '"' .. key .. '":{"count":' .. tostring(counter.count)
            .. ',"available":' .. tostring(counter.available) .. ',"saturated":' .. tostring(counter.saturated) .. '}'
    end
    local body = '{"format":1,"started":' .. tostring(started) .. ',"counters":{' .. table.concat(records, ',') .. '}}\n'
    -- Explicit snapshots only; no file I/O, timers, or scheduling inside hooks.
    local ok, saved = pcall(function()
        local folder = os.getenv('LOCALAPPDATA')
        if not folder or folder == '' or #body > 8192 then return false end
        local file = io.open(folder .. '/AimMod/KovaaksNative/native-replay-safety.json', 'wb')
        if not file then return false end
        local writeOk, written = pcall(file.write, file, body)
        local closeOk, closed = pcall(file.close, file)
        return writeOk and written ~= nil and closeOk and closed ~= nil
    end)
    return snapshot, ok and saved == true
end
return M
