-- Where cosmetics may take effect. Pure: no Unreal calls, fully testable.
--
-- Policy: cosmetics apply only inside AimMod's own modes. Normal scenarios,
-- challenges, benchmarks, ranked and the scenario editor always look exactly
-- like the base game. Every check below must pass; anything unknown is "off".
--
-- Two independent signals must agree:
--  1. The AimMod service's session marker (aimmod-session.txt), refreshed
--     while the player is in an AimMod lobby, match or spectate session.
--  2. The game's own state: the current scenario is an AimMod generated match
--     scenario (MatchScenario.cs: "AimMod Match - <name> - <8 hex>"), played
--     in freeplay, and the game is not in a challenge, benchmark, editor or
--     loading phase.
local M = {}

M.MaxMarkerAge = 120   -- seconds; the service rewrites the marker every 30 s
M.modes = {lobby=true, match=true, spectate=true}

-- Same shape as MatchScenario.FilePattern without the .sce extension.
function M.isMatchScenario(name)
    if type(name) ~= 'string' then return false end
    local middle, key = name:match('^AimMod Match %- (.+) %- ([0-9a-f]+)$')
    return middle ~= nil and #key == 8 and #middle <= 120
end

-- Marker format (written by the service, at most 1 KiB):
--   v=1
--   mode=match|spectate|lobby
--   scenario=AimMod Match - <name> - <8 hex>
--   expires=<unix seconds>
function M.parseMarker(text)
    if type(text) ~= 'string' or #text == 0 or #text > 1024 then return nil, 'marker missing or too large' end
    local fields = {}
    for line in text:gmatch('[^\r\n]+') do
        local k, v = line:match('^([%a]+)=(.*)$')
        if k then fields[k] = v end
    end
    if fields.v ~= '1' then return nil, 'unsupported marker version' end
    if not M.modes[fields.mode or ''] then return nil, 'unknown mode' end
    local expires = tonumber(fields.expires or '')
    if not expires or expires ~= math.floor(expires) then return nil, 'invalid expiry' end
    return {mode=fields.mode, scenario=fields.scenario or '', expires=expires}
end

-- state = {marker=<parsed or nil>, now=<unix>, scenario=<current name or nil>,
--          inChallenge, benchmark, editor, loading (booleans, nil = unknown)}
-- Returns {avatars=bool, localPlayer=bool, reason=string}.
function M.decide(state)
    local off = function(reason) return {avatars=false, localPlayer=false, reason=reason} end
    if type(state) ~= 'table' then return off('no state') end
    local marker = state.marker
    if type(marker) ~= 'table' then return off('no AimMod session') end
    if type(state.now) ~= 'number' then return off('no clock') end
    if marker.expires <= state.now then return off('AimMod session expired') end
    if marker.expires > state.now + M.MaxMarkerAge then return off('AimMod session marker out of range') end
    -- Unknown game state counts as "in a normal scenario".
    if state.inChallenge ~= false then return off('challenge or unknown challenge state') end
    if state.benchmark ~= false then return off('benchmark or unknown benchmark state') end
    if state.editor ~= false then return off('scenario editor or unknown editor state') end
    if state.loading ~= false then return off('scenario loading') end
    if not M.isMatchScenario(state.scenario) then return off('not an AimMod match scenario') end
    if state.scenario ~= marker.scenario then return off('scenario differs from the AimMod session') end
    if marker.mode == 'match' then return {avatars=true, localPlayer=true, reason='AimMod match'} end
    if marker.mode == 'spectate' then return {avatars=true, localPlayer=false, reason='AimMod spectating'} end
    -- Lobby: catalog picker and preview only, nothing in the world.
    return off('AimMod lobby: preview only')
end

return M
