-- AimMod owns its menu widgets; existing gameplay and submission functions are
-- neither invoked nor replaced by this UI integration.
local Menu = require('Menu')
local Telemetry = require('Telemetry')
local ok, reason = pcall(Telemetry.start)
if not ok then print('[AimMod] telemetry unavailable: ' .. tostring(reason) .. '\n') end
local replayOk, replayReason = pcall(function() require('ReplayCapture').start(Telemetry) end)
if not replayOk then print('[AimMod] replay capture unavailable: ' .. tostring(replayReason) .. '\n') end
Menu.start()
