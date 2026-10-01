-- Pure helpers for AimModCosmetics. No Unreal calls: everything here is
-- testable with a plain Lua interpreter.
local M = {}

-- Every feature is off unless config.txt turns it on.
M.defaults = {
    enabled = false,       -- master switch
    probe = false,         -- read-only component/material logger
    probe_limit = 12,      -- probe dumps per game session
    cosmetics = false,     -- prototype: apply curated catalog items in AimMod matches
    allow_drafts = false,  -- team testing: allow catalog items marked draft
    avatar_item = '',      -- team testing: catalog id applied to every AimMod avatar
    weapon_item = '',      -- team testing: catalog id applied to your own weapon
    interval_ms = 1000,
}

local booleans = {enabled=true, probe=true, cosmetics=true, allow_drafts=true}
local integers = {probe_limit={1, 100}, interval_ms={250, 10000}}
local ids = {avatar_item=true, weapon_item=true}
local truthy = {['1']=true, ['true']=true, yes=true, on=true}

local function trim(s) return (s:gsub('^%s+', ''):gsub('%s+$', '')) end

-- key=value per line; '#' or ';' starts a comment line. Unknown keys,
-- out-of-range numbers and malformed ids are ignored, so a bad line never
-- enables anything. Item values are catalog ids only, never paths.
function M.parseConfig(text)
    local config = {}
    for k, v in pairs(M.defaults) do config[k] = v end
    for line in (text or ''):gmatch('[^\r\n]+') do
        line = trim(line)
        local first = line:sub(1, 1)
        if line ~= '' and first ~= '#' and first ~= ';' then
            local key, value = line:match('^([%w_]+)%s*=%s*(.-)$')
            if key and M.defaults[key] ~= nil then
                if booleans[key] then
                    config[key] = truthy[value:lower()] == true
                elseif integers[key] then
                    local n = tonumber(value)
                    local range = integers[key]
                    if n and n == math.floor(n) and n >= range[1] and n <= range[2] then config[key] = n end
                elseif ids[key] then
                    if value == '' or (value:match('^[a-z0-9][a-z0-9%-]*$') and #value <= 48) then config[key] = value end
                end
            end
        end
    end
    return config
end

-- Avatar items go only on free base looks: the model must be in the game's
-- Default character model pack, and the skin in the Default skin pack (or
-- the model's own default). A viewer-equipped DLC skin, or any look we cannot
-- identify, is left untouched.
function M.isFreeLook(model, skin, freeModels, freeSkins)
    if type(model) ~= 'string' or model == '' or model == 'None' then return false end
    if not freeModels or not freeModels[model] then return false end
    if skin == nil or skin == '' or skin == 'None' or skin == 'Default' then return true end
    return freeSkins ~= nil and freeSkins[skin] == true
end

-- Only AimMod remote-player avatars: the steam bridge spawns them with
-- character profiles named "AimMod <Model> [<Skin>]" (Avatars.cs). The
-- local pawn and scenario bots never match.
M.avatarPrefix = 'AimMod '
function M.isAvatarTarget(info)
    if type(info) ~= 'table' or info.isLocal or info.playerControlled then return false end
    return type(info.profile) == 'string' and info.profile:sub(1, #M.avatarPrefix) == M.avatarPrefix
end

function M.contains(list, value)
    for _, v in ipairs(list or {}) do if v == value then return true end end
    return false
end

return M
