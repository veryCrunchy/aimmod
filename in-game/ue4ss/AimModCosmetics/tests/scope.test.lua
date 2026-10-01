-- Scope policy: cosmetics only inside AimMod modes, never in normal
-- scenarios, challenges, benchmarks, ranked or the editor.
package.path = '../Scripts/?.lua;' .. package.path
local S = require('CosmeticsScope')

local match = 'AimMod Match - Tile Frenzy - Duel - 0a1b2c3d'
assert(S.isMatchScenario(match))
assert(S.isMatchScenario('AimMod Match - x - deadbeef'))
for _, name in ipairs({'Tile Frenzy', 'AimMod Match - Tile Frenzy', 'AimMod Match - Tile - 0A1B2C3D',
    'AimMod Match - Tile - 0a1b2c3', 'AimMod Match - Tile - 0a1b2c3d9', 'aimmod match - t - 0a1b2c3d',
    'VT AimMod Match - Tile - 0a1b2c3d', 'AimMod Match -  - 0a1b2c3d x', '', 42}) do
    assert(not S.isMatchScenario(name), 'not a match scenario: ' .. tostring(name))
end

local function marker(mode, expires, scenario)
    return ('v=1\nmode=%s\nscenario=%s\nexpires=%d\n'):format(mode, scenario or match, expires)
end
assert(S.parseMarker(marker('match', 1000)).mode == 'match')
assert(S.parseMarker(nil) == nil and S.parseMarker('') == nil)
assert(S.parseMarker('v=2\nmode=match\nexpires=1000') == nil, 'unknown version')
assert(S.parseMarker('v=1\nmode=ranked\nexpires=1000') == nil, 'unknown mode')
assert(S.parseMarker('v=1\nmode=match\nexpires=soon') == nil)
assert(S.parseMarker(string.rep('x', 2000)) == nil, 'oversized marker')

local now = 1000
local NIL = {}
local function state(over)
    local s = {marker=S.parseMarker(marker('match', now + 60)), now=now, scenario=match,
        inChallenge=false, benchmark=false, editor=false, loading=false}
    for k, v in pairs(over or {}) do if v == NIL then s[k] = nil else s[k] = v end end
    return s
end
local function off(over, why)
    local d = S.decide(state(over))
    assert(not d.avatars and not d.localPlayer, 'must be off: ' .. why .. ' (' .. d.reason .. ')')
end

-- The one fully on case.
local on = S.decide(state())
assert(on.avatars and on.localPlayer, on.reason)

-- Normal scenarios, challenges, ranked, benchmarks, editor: always off.
off({scenario='Tile Frenzy'}, 'normal scenario even with an AimMod session')
off({inChallenge=true}, 'challenge (leaderboard/ranked) even in a match scenario')
off({inChallenge=NIL}, 'unknown challenge state')
off({benchmark=true}, 'benchmark')
off({benchmark=NIL}, 'unknown benchmark state')
off({editor=true}, 'scenario editor')
off({loading=true}, 'loading')
off({scenario=NIL}, 'unknown scenario')
-- Without the AimMod service session, a match-named scenario is not enough.
off({marker=false}, 'no session')
off({marker=S.parseMarker(marker('match', now))}, 'expired session')
off({marker=S.parseMarker(marker('match', now + 3600))}, 'marker too far in the future')
off({marker=S.parseMarker(marker('match', now + 60, 'AimMod Match - Other - 0a1b2c3e'))}, 'different match')
off({now=NIL}, 'no clock')
off({marker=S.parseMarker(marker('lobby', now + 60))}, 'lobby is preview only')
assert(not S.decide(nil).avatars)

-- Spectating: other players' avatars only, never the local player's own items.
local spectate = S.decide(state({marker=S.parseMarker(marker('spectate', now + 60))}))
assert(spectate.avatars and not spectate.localPlayer)
print('scope.test.lua ok')
