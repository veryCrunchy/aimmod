-- Read the game's computed indicator state without invoking scoring/gameplay.
local M = {}
local receiver, instanceId, nextScan = nil, nil, 0
local function valid(object)
    local ok, value = pcall(function() return object and object:IsValid() end)
    return ok and value
end
local function owned(object, id)
    local ok, value = pcall(function()
        return valid(object) and object:GetOuter():GetAddress() == id
    end)
    return ok and value
end
local function readValue(object, method)
    -- UE4SS LuaUObject.cpp (527a483b): primitive OUT parameters are tables
    -- populated by parameter name, not return values or positional entries.
    -- This runtime's primitive OUT branch retains stack index 1 between OUTs.
    -- Sharing a table preserves both named outputs whether that branch consumes
    -- the argument or not; separate tables can leave Result in the first one.
    local value = {}
    local result = value
    local ok = pcall(function() object[method](object, value, result) end)
    local number = value.OutValue
    if ok and result.Result == 0 and type(number) == 'number'
        and number == number and math.abs(number) < 1e12 then return number, 'available' end
    if not ok then return nil, 'getter-unavailable' end
    if result.Result == 1 then return nil, 'no-native-value' end
    return nil, 'invalid-native-value', 'value:'..type(value.OutValue)..';result:'..type(result.Result)
end
function M.read(instance)
    if not valid(instance) then return {scoreStatus='instance-unavailable'} end
    local ok, id = pcall(function() return instance:GetAddress() end)
    if not ok then return {scoreStatus='instance-unavailable'} end
    if instanceId ~= id then receiver = nil; instanceId = id; nextScan = 0 end
    if not owned(receiver, id) then
        receiver = nil
        local now = os.time()
        if now < nextScan then return {scoreStatus='receiver-unavailable'} end
        nextScan = now + 5
        local found, candidates = pcall(FindAllOf, 'PerformanceIndicatorsStateReceiver')
        if found then
            for _, candidate in ipairs(candidates or {}) do
                if owned(candidate, id) then receiver = candidate; break end
            end
        end
    end
    if not receiver then return {scoreStatus='receiver-unavailable'} end
    -- Never carry a prior getter value through Else or an unavailable receiver.
    local score, status, diagnostic = readValue(receiver, 'Get_Score_ValueElse')
    return {score = score, scoreStatus = status, scoreDiagnostic = diagnostic,
        scorePerMinute = readValue(receiver, 'Get_ScorePerMinute_ValueElse')}
end
return M
