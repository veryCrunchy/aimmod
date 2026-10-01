-- AimMod owns its menu widgets; existing gameplay and submission functions are
-- neither invoked nor replaced by this UI integration.
-- API baseline: UE4SS v3.0.1-1125-g527a483b, KovaaK's 3.9.8, Unreal 4.26.
local required={'RegisterHook','StaticFindObject','StaticConstructObject','FindAllOf','FindFirstOf',
    'LoopInGameThreadWithDelay','ExecuteInGameThreadWithDelay','FName','FText'}
local missing={}
for _,name in ipairs(required) do if _G[name]==nil then missing[#missing+1]=name end end
if #missing>0 then
    -- Older UE4SS builds lack the game-thread scheduler. Fail closed instead of
    -- registering observers that would run game reads off the game thread.
    print('[AimMod] disabled: UE4SS API unavailable (' .. table.concat(missing, ', ') .. '); requires UE4SS v3.0.1-1125-g527a483b or newer with the game-thread scheduler\n')
    return
end
local Telemetry = require('Telemetry')
-- Telemetry.start also starts the idempotent replay capture observer.
local ok, reason = pcall(Telemetry.start)
if not ok then print('[AimMod] telemetry unavailable: ' .. tostring(reason) .. '\n') end
local replayOk, replayReason = pcall(function() require('ReplayCapture').start(Telemetry) end)
if not replayOk then print('[AimMod] replay capture unavailable: ' .. tostring(replayReason) .. '\n') end
local menuOk, menuReason = pcall(function() require('Menu').start() end)
if not menuOk then print('[AimMod] menu unavailable: ' .. tostring(menuReason) .. '\n') end
local discordOk, discordReason = pcall(function() require('DiscordPresence').start() end)
if not discordOk then print('[AimMod] Discord handoff unavailable: ' .. tostring(discordReason) .. '\n') end
