-- Run from this directory with a Lua 5.3/5.4 interpreter (or `nvim -l`).
package.path = '../Scripts/?.lua;./?.lua;' .. package.path
local mock = require('mock')
local world = require('world').build()
mock.install(world)
local probe = require('CosmeticsProbe')

local lines = probe.run()
local text = table.concat(lines, '\n')

-- Read-only: only getters may be called.
local readOnly = {
    IsValid=true, GetFullName=true, GetFName=true, GetClass=true, IsA=true, GetAddress=true,
    GetCharacterProfileName=true, IsPlayerControlled=true, K2_IsUsingMeshHitDetection=true,
    K2_GetComponentsByClass=true, GetAttachParent=true, GetAttachSocketName=true, IsVisible=true,
    GetCollisionEnabled=true, GetCollisionProfileName=true, GetAllSocketNames=true, GetNumMaterials=true,
    GetMaterial=true, GetMaterialSlotNames=true, IsLocalPlayerController=true, K2_GetPawn=true,
    GetSelectedWeaponModelName=true, GetSelectWeaponMesh=true, GetFPSPlayerSkeletalMeshComponent=true,
}
for _, call in ipairs(mock.calls) do assert(readOnly[call], 'probe called non-getter ' .. call) end

assert(text:find('CHARACTER FPSCharacter_C /Game/Map.Map:PersistentLevel.Player [FPSCharacter_C]  <LOCAL PLAYER>', 1, true))
assert(text:find('profile="AimMod Meso McCree"', 1, true))
assert(text:find('CylHead [StaticMeshComponent]  <hitbox>', 1, true))
assert(text:find('collision=QueryOnly', 1, true))
assert(text:find('slot 0 "Body": MaterialInstanceConstant /Game/Test/MI_Body.MI_Body', 1, true))
assert(text:find('parent chain: Material /Game/Test/M_Base.M_Base', 1, true))
assert(text:find('Masks = Texture2D /Game/Test/T_Masks.T_Masks [override]', 1, true))
assert(text:find('BaseColor = (base default) [base]', 1, true), 'base material parameters are listed')
assert(text:find('Tint = (1.000, 0.000, 0.000, 1.000) [override]', 1, true))
assert(text:find('VIEWMODEL', 1, true) and text:find('selected weapon model: Pistol', 1, true))
assert(text:find('sockets/bones (2): head, hand_r', 1, true))
-- The viewmodel is logged for the local player only.
local _, views = text:gsub('VIEWMODEL', '')
assert(views == 1)
assert(text:find('look: model=Meso skin=McCree (from interface)', 1, true))
assert(text:find('look: model=AnimeGirl skin=Variant_1 (from interface)', 1, true))
assert(text:find('models: Endo, Meso', 1, true) and text:find('skins: McCree, Tracer', 1, true))
assert(probe.signature():find('Avatar', 1, true))
print('probe.test.lua ok')
