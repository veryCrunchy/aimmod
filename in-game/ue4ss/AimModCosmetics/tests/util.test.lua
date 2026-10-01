-- Run from this directory with a Lua 5.3/5.4 interpreter (or `nvim -l`).
package.path = '../Scripts/?.lua;' .. package.path
local U = require('CosmeticsUtil')

-- Config: everything off by default; bad values never enable anything.
local d = U.parseConfig('')
assert(d.enabled == false and d.probe == false and d.cosmetics == false and d.allow_drafts == false)
assert(d.avatar_item == '' and d.weapon_item == '' and d.probe_limit == 12)
assert(d.texture == nil and d.texture_param == nil, 'no file or texture settings exist')
local c = U.parseConfig('# comment\n; other\nenabled = yes\nprobe=1\ncosmetics=maybe\nprobe_limit=0\ninterval_ms=500\nunknown=1\navatar_item = meso-tint-ember \r\n')
assert(c.enabled == true and c.probe == true and c.cosmetics == false)
assert(c.probe_limit == 12, 'out-of-range numbers keep the default')
assert(c.interval_ms == 500 and c.unknown == nil and c.avatar_item == 'meso-tint-ember')
assert(U.parseConfig('enabled=1.5').enabled == false)
-- Item settings take catalog ids only, never paths.
assert(U.parseConfig('avatar_item=C:/skins/me.png').avatar_item == '')
assert(U.parseConfig('weapon_item=../x').weapon_item == '')
assert(U.parseConfig('weapon_item=Weapon-Finish').weapon_item == '')
assert(U.parseConfig('texture=me.png').texture == nil)

-- Free looks only: Default-pack models and skins.
local models, skins = {Meso=true, Endo=true}, {McCree=true}
assert(U.isFreeLook('Meso', 'McCree', models, skins))
assert(U.isFreeLook('Endo', 'Default', models, skins) and U.isFreeLook('Endo', '', models, skins))
assert(not U.isFreeLook('AnimeGirl', 'Default', models, skins), 'DLC model rejected')
assert(not U.isFreeLook('Meso', 'Variant_1', models, skins), 'unknown skin rejected')
assert(not U.isFreeLook('Meso', 'McCree', nil, nil), 'packs unavailable fails closed')
assert(not U.isFreeLook(nil, 'McCree', models, skins) and not U.isFreeLook('None', '', models, skins))

-- Targets: AimMod avatars only.
assert(U.isAvatarTarget({profile='AimMod Meso McCree', playerControlled=false, isLocal=false}))
assert(not U.isAvatarTarget({profile='AimMod Meso McCree', playerControlled=true, isLocal=false}))
assert(not U.isAvatarTarget({profile='AimMod Endo', playerControlled=false, isLocal=true}))
assert(not U.isAvatarTarget({profile='Tile Frenzy Bot', playerControlled=false, isLocal=false}))
assert(not U.isAvatarTarget({profile='AimModX', playerControlled=false, isLocal=false}))
assert(not U.isAvatarTarget(nil))
print('util.test.lua ok')
