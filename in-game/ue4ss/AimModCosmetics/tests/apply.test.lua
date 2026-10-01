-- The catalog applier changes nothing outside AimMod modes, applies only
-- curated items inside them, and restores the game's materials on exit.
package.path = '../Scripts/?.lua;./?.lua;' .. package.path
local mock = require('mock')
local world = require('world').build()
mock.install(world)
local Util = require('CosmeticsUtil')
local Apply = require('CosmeticsApply')

local MATCH = 'AimMod Match - Tile Frenzy - Duel - 0a1b2c3d'
local clock = 1000
local markerText
local items = {
    {id='test-tint', version=1, kind='avatar_tint', models={'Meso'}, parts={'body'}, vector={Tint={R=0, G=1, B=0, A=1}}},
    {id='test-finish', version=1, kind='weapon_finish', parts={'weapon', 'arms'}, scalar={Roughness=0.2}},
    {id='test-misfit', version=1, kind='avatar_tint', models={'Meso'}, parts={'body'}, vector={NoSuchParam={R=0, G=0, B=0, A=1}}},
    {id='test-visor', version=1, kind='accessory', models={'Meso'}, parts={'body'}, pak={file='x.pak', sha256=''}},
}
local logs = {}
local function new(cfg)
    return Apply.new(Util.parseConfig(cfg), function(s) logs[#logs + 1] = s end,
        {items=items, now=function() return clock end, readMarker=function() return markerText end})
end
local function marker(mode, scenario)
    return ('v=1\nmode=%s\nscenario=%s\nexpires=%d\n'):format(mode, scenario or MATCH, clock + 60)
end

local mutating = {CreateDynamicMaterialInstance=true, SetMaterial=true, SetVectorParameterValue=true,
    SetScalarParameterValue=true, SetTextureParameterValue=true, ImportFileAsTexture2D=true, ImportBufferAsTexture2D=true,
    SetSkeletalMesh=true, SetStaticMesh=true, SetVisibility=true, SetHiddenInGame=true, SetCollisionEnabled=true,
    SetCollisionProfileName=true, SetRenderInMainPass=true, SetMasterPoseComponent=true, K2_DestroyComponent=true,
    AddComponentByClass=true, Montage_Play=true}
local function changes()
    local n = 0
    for _, call in ipairs(mock.calls) do if mutating[call] then n = n + 1 end end
    return n
end
local function originals()
    return {world.avatarMesh:GetMaterial(0), world.avatarMesh:GetMaterial(1), world.weaponMesh:GetMaterial(0),
        world.armsMesh:GetMaterial(0), world.playerMesh:GetMaterial(0), world.botMesh:GetMaterial(0),
        world.paidMesh:GetMaterial(0), world.shotOrigin:GetMaterial(0)}
end
local base = originals()
local applier = new('enabled=1\ncosmetics=1\navatar_item=test-tint\nweapon_item=test-finish\n')

-- 1. Normal scenario (even with a stale or forged session): nothing changes.
local function assertUntouched(why)
    mock.calls = {}
    applier.tick()
    assert(changes() == 0, why .. ': changed the game')
    local now = originals()
    for i = 1, #base do assert(now[i] == base[i], why .. ': material ' .. i .. ' changed') end
end
markerText = nil;                       world.game.scenario = 'Tile Frenzy'; assertUntouched('normal scenario, no session')
markerText = marker('match', 'Tile Frenzy'); assertUntouched('normal scenario named in a forged marker')
markerText = marker('match');           assertUntouched('session marker but normal scenario')
world.game.scenario = MATCH; world.game.inChallenge = true; assertUntouched('challenge in a match-named scenario')
world.game.inChallenge = false; world.game.benchmark = true; assertUntouched('benchmark')
world.game.benchmark = false; world.game.editor = true; assertUntouched('scenario editor')
world.game.editor = false; markerText = nil; assertUntouched('match scenario without an AimMod session')
markerText = marker('lobby'); assertUntouched('lobby is preview only')

-- 2. AimMod match: avatar gets the tint, own weapon and arms get the finish.
markerText = marker('match')
mock.calls = {}
world.midNames = {}
applier.tick()
local m = originals()
-- A named instance replaces an existing one of the same name and owner.
for i, a in ipairs(world.midNames) do
    for j = i + 1, #world.midNames do
        local b = world.midNames[j]
        assert(a.name == 'None' or not (a.owner == b.owner and a.name == b.name), 'material instance names collide under one owner')
    end
end
assert(#world.midNames >= 3, 'instances created')
assert(m[1] ~= base[1] and m[2] ~= base[2], 'avatar slots dressed')
assert(m[1].__params.Tint.G == 1 and m[1].Parent == base[1], 'tint on an instance parented on the game material')
assert(m[3] ~= base[3] and m[3].__params.Roughness == 0.2, 'own weapon finish')
assert(m[4] ~= base[4], 'own arms finish')
assert(m[5] == base[5], 'local character mesh untouched')
assert(m[6] == base[6], 'scenario bot untouched')
assert(m[7] == base[7], 'DLC look untouched')
assert(m[8] == base[8], 'ShotOrigin untouched')
for _, call in ipairs(mock.calls) do
    assert(call ~= 'ImportFileAsTexture2D' and call ~= 'SetSkeletalMesh' and call ~= 'SetCollisionEnabled', call)
end

-- Idempotent.
mock.calls = {}
applier.tick()
assert(changes() == 0, 'second tick re-applied')

-- 3. Spectating: own weapon restored, avatars stay dressed.
markerText = marker('spectate')
applier.tick()
assert(world.weaponMesh:GetMaterial(0) == base[3] and world.armsMesh:GetMaterial(0) == base[4], 'own items restored when spectating')
assert(world.avatarMesh:GetMaterial(0) ~= base[1])

-- 4. Leaving AimMod (back to a normal scenario): everything restored.
markerText = marker('match'); applier.tick()
world.game.scenario = 'Tile Frenzy'
applier.tick()
local after = originals()
for i = 1, #base do assert(after[i] == base[i], 'restored on exit: ' .. i) end
assertUntouched('after exit')

-- 5. Items that do not fit, drafts and pak items never apply.
world.game.scenario = MATCH; markerText = marker('match')
local misfit = new('cosmetics=1\navatar_item=test-misfit\n')
mock.calls = {}; misfit.tick()
assert(changes() == 0, 'item whose parameters the material lacks')
local pak = new('cosmetics=1\navatar_item=test-visor\n')
mock.calls = {}; pak.tick()
assert(changes() == 0, 'pak item without a verified pak')
local unknown = new('cosmetics=1\navatar_item=someone-elses-model\n')
mock.calls = {}; unknown.tick()
assert(changes() == 0, 'unknown catalog id')
local drafts = Apply.new(Util.parseConfig('cosmetics=1\navatar_item=meso-tint-ember\n'), function() end,
    {now=function() return clock end, readMarker=function() return markerText end})
mock.calls = {}; drafts.tick()
assert(changes() == 0, 'shipped drafts need allow_drafts')

-- 6. Fail closed without a local pawn.
world.all.MetaPlayerController = {}
local lonely = new('cosmetics=1\navatar_item=test-tint\n')
mock.calls = {}; lonely.tick()
assert(changes() == 0, 'no local pawn')
print('apply.test.lua ok')
