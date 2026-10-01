-- AimModCosmetics: read-only probe and a disabled-by-default prototype that
-- applies curated catalog items inside AimMod modes only. Visual only: no
-- gameplay, hitbox, scoring or input function is called or hooked, and no
-- player-supplied file is ever loaded.
-- API baseline: UE4SS v3.0.1-1152, KovaaK's 3.9.11, Unreal 4.26.
local function log(s) print('[AimModCosmetics] ' .. tostring(s) .. '\n') end

local required = {'StaticFindObject', 'FindAllOf', 'LoopInGameThreadWithDelay', 'FName'}
local missing = {}
for _, name in ipairs(required) do if _G[name] == nil then missing[#missing + 1] = name end end
if #missing > 0 then
    log('disabled: UE4SS API unavailable (' .. table.concat(missing, ', ') .. ')')
    return
end

local source = debug.getinfo(1, 'S').source:sub(2):gsub('\\', '/')
local root = source:match('^(.*)/Scripts/[^/]+$')
local Util = require('CosmeticsUtil')

local text = ''
local file = io.open(root .. '/config.txt', 'rb')
if file then
    text = file:read('a') or ''
    file:close()
end
local config = Util.parseConfig(text)
if not config.enabled then
    log('disabled (set enabled=1 in config.txt)')
    return
end

if config.probe then
    local ok, reason = pcall(function() require('CosmeticsProbe').start(config, log) end)
    log(ok and ('probe on (up to ' .. config.probe_limit .. ' dumps)') or ('probe unavailable: ' .. tostring(reason)))
end
if config.cosmetics then
    local ok, reason = pcall(function() require('CosmeticsApply').start(config, log) end)
    if not ok then log('cosmetics prototype unavailable: ' .. tostring(reason)) end
end
if not config.probe and not config.cosmetics then log('enabled, but probe=0 and cosmetics=0: nothing to do') end
