-- The curated AimMod cosmetics catalog. Made by the AimMod team and shipped
-- with the AimMod install; players pick items by id and lobbies share ids,
-- never files. There is no path for player-supplied textures or models.
--
-- Item fields:
--   id       lowercase id shared in lobbies, stable across versions
--   version  integer, bumped when the look changes
--   kind     see M.kinds
--   name     label for the picker
--   models   base character models the item fits (Default-pack models only)
--   weapons  weapon model names the item fits (weapon kinds; nil = any)
--   parts    'body' (avatar ACharacter.Mesh), 'weapon' (own selected
--            viewmodel weapon), 'arms' (own first-person arms)
--   vector / scalar  material parameter values set on a dynamic instance
--            parented on the slot's existing material
--   pak      {file=..., sha256=...} for items whose assets ship in an AimMod
--            pak (textures, meshes, animations). Usable only when the pak's
--            size and SHA-256 match the catalog manifest shipped with the
--            install (AimModCore, phase 2).
--   attach   accessories: role ('head', 'neck' or 'spine'), and for
--            accessories built from game meshes a fit: a bone name part, an
--            anchor ('bone', 'top' or 'crown'), an offset and a size in cm
--            (forward, right, up)
--   mesh / material  accessory assets: an AimMod pak's, or the curated game
--            and engine assets (see gameAsset)
--   draft    true while the item is not ready (its pak has not shipped).
--            Drafts never appear in the picker and apply only for team tests
--            with allow_drafts=1.
local M = {}

M.version = 6

M.kinds = {
    avatar_tint = {parts={body=true}, needsPak=false},
    avatar_pattern = {parts={body=true}, needsPak=true},
    weapon_finish = {parts={weapon=true, arms=true}, needsPak=false},
    weapon_pattern = {parts={weapon=true, arms=true}, needsPak=true},
    accessory = {parts={body=true}, needsPak=true},
    weapon_model = {parts={weapon=true}, needsPak=true},
    reload_animation = {parts={arms=true}, needsPak=true},
    player_model = {parts={body=true}, needsPak=true},
}

-- First curated set: material parameters only, no pak. The names come from
-- the probe: the Meso and Endo body and head slots share MM_BaseDummy
-- (vectors MetalPaint, TriangularPaint, RawMetal, Silicone; scalars
-- Roughness, Metallic) and weapons share M_SingleAssetMaster (vectors
-- AccentColor, Emissive). Colours are linear. A weapon's emissive is never
-- brighter than the game's own orange accent. Pak items stay drafts until the
-- pak ships. catalog.json is the shipped copy (catalog.test.cjs keeps them equal).
local function rgba(r, g, b, a) return {R=r, G=g, B=b, A=a or 1} end
M.items = {
    {id='tint-mint', version=1, kind='avatar_tint', name='AimMod Mint', models={'Meso', 'Endo'}, parts={'body'},
        vector={MetalPaint=rgba(0.02, 0.6, 0.3), TriangularPaint=rgba(0.86, 0.9, 0.88), RawMetal=rgba(0.3, 0.34, 0.32), Silicone=rgba(0.02, 0.025, 0.022)},
        scalar={Roughness=0.35, Metallic=0.1}},
    {id='tint-carbon', version=1, kind='avatar_tint', name='Carbon', models={'Meso', 'Endo'}, parts={'body'},
        vector={MetalPaint=rgba(0.012, 0.013, 0.015), TriangularPaint=rgba(0.06, 0.065, 0.07), RawMetal=rgba(0.1, 0.11, 0.12), Silicone=rgba(0.005, 0.005, 0.006)},
        scalar={Roughness=0.6, Metallic=0.2}},
    {id='tint-ivory', version=1, kind='avatar_tint', name='Ivory', models={'Meso', 'Endo'}, parts={'body'},
        vector={MetalPaint=rgba(0.78, 0.74, 0.64), TriangularPaint=rgba(0.3, 0.27, 0.22), RawMetal=rgba(0.55, 0.52, 0.46), Silicone=rgba(0.05, 0.045, 0.04)},
        scalar={Roughness=0.45, Metallic=0}},
    {id='tint-crimson', version=1, kind='avatar_tint', name='Crimson', models={'Meso', 'Endo'}, parts={'body'},
        vector={MetalPaint=rgba(0.02, 0.02, 0.022), TriangularPaint=rgba(0.55, 0.02, 0.035), RawMetal=rgba(0.35, 0.33, 0.33), Silicone=rgba(0.01, 0.01, 0.01)},
        scalar={Roughness=0.4, Metallic=0.1}},
    {id='tint-gold', version=1, kind='avatar_tint', name='Gold', models={'Meso', 'Endo'}, parts={'body'},
        vector={MetalPaint=rgba(0.85, 0.58, 0.18), TriangularPaint=rgba(0.05, 0.045, 0.04), RawMetal=rgba(0.95, 0.75, 0.4), Silicone=rgba(0.02, 0.018, 0.015)},
        scalar={Roughness=0.25, Metallic=0.9}},
    {id='tint-chrome', version=1, kind='avatar_tint', name='Chrome', models={'Meso', 'Endo'}, parts={'body'},
        vector={MetalPaint=rgba(0.8, 0.82, 0.85), TriangularPaint=rgba(0.3, 0.32, 0.36), RawMetal=rgba(0.9, 0.92, 0.95), Silicone=rgba(0.02, 0.02, 0.022)},
        scalar={Roughness=0.12, Metallic=1}},
    {id='finish-mint', version=1, kind='weapon_finish', name='AimMod Mint', parts={'weapon'},
        vector={AccentColor=rgba(0.02, 0.78, 0.36, 0), Emissive=rgba(0.02, 0.62, 0.29, 0)}},
    {id='finish-crimson', version=1, kind='weapon_finish', name='Crimson', parts={'weapon'},
        vector={AccentColor=rgba(0.85, 0.02, 0.06, 0), Emissive=rgba(0.85, 0.02, 0.06, 0)}},
    {id='finish-gold', version=1, kind='weapon_finish', name='Gold', parts={'weapon'},
        vector={AccentColor=rgba(1, 0.62, 0.12, 0), Emissive=rgba(0.85, 0.5, 0.08, 0)}},
    {id='finish-ice', version=1, kind='weapon_finish', name='Ice', parts={'weapon'},
        vector={AccentColor=rgba(0.2, 0.62, 1, 0), Emissive=rgba(0.16, 0.5, 0.85, 0)}},
    {id='finish-violet', version=1, kind='weapon_finish', name='Violet', parts={'weapon'},
        vector={AccentColor=rgba(0.45, 0.15, 1, 0), Emissive=rgba(0.45, 0.15, 1, 0)}},
    {id='finish-ghost', version=1, kind='weapon_finish', name='Ghost', parts={'weapon'},
        vector={AccentColor=rgba(0.75, 0.78, 0.8, 0), Emissive=rgba(0.55, 0.58, 0.6, 0)}},
    {id='meso-pattern-stripes', version=1, kind='avatar_pattern', name='Racing stripes', models={'Meso'}, parts={'body'},
        pak={file='AimModCosmetics-1.pak', sha256=''}, draft=true},
    -- Accessories from AimMod runtime meshes (.amsh, manifest-pinned) and the free Meso
    -- material, tinted through its probed parameters and fitted to any rig (no pak).
    {id='accessory-halo', version=4, kind='accessory', name='Halo', models={'Meso', 'Endo'}, parts={'body'},
        shape='halo.amsh', material='/Game/Materials/Masters/Environment/MM_Glow.MM_Glow',
        vector={MetalPaint=rgba(0.95, 0.72, 0.28), TriangularPaint=rgba(0.95, 0.72, 0.28), RawMetal=rgba(0.95, 0.72, 0.28), Silicone=rgba(0.95, 0.72, 0.28)}, scalar={Roughness=0.2, Metallic=0.8},
        attach={role='head', fit={bone='Head', anchor='top', offset={0, 0, 3}, size={23.5, 23.5, 1.0}, keepAxes=true}}},
    {id='accessory-visor', version=3, kind='accessory', name='Visor', models={'Meso', 'Endo'}, parts={'body'},
        shape='visor.amsh', material='/MapCreator/Materials/MM_G_Basic.MM_G_Basic',
        vector={MetalPaint=rgba(0.04, 0.55, 0.42), TriangularPaint=rgba(0.04, 0.55, 0.42), RawMetal=rgba(0.04, 0.55, 0.42), Silicone=rgba(0.04, 0.55, 0.42)}, scalar={Roughness=0.15, Metallic=0.6},
        attach={role='head', fit={bone='Head', anchor='crown', offset={0, 0, 0}, size={9.8, 25.0, 4.5}, keepAxes=true}}},
    {id='accessory-collar', version=4, kind='accessory', name='Collar', models={'Meso', 'Endo'}, parts={'body'},
        shape='collar.amsh', material='/MapCreator/Materials/MM_G_Basic.MM_G_Basic',
        vector={MetalPaint=rgba(0.03, 0.032, 0.035), TriangularPaint=rgba(0.03, 0.032, 0.035), RawMetal=rgba(0.03, 0.032, 0.035), Silicone=rgba(0.03, 0.032, 0.035)}, scalar={Roughness=0.45, Metallic=0.4},
        attach={role='neck', fit={bone='Neck', anchor='chin', offset={0, 0, -2}, size={23.6, 23.6, 4.4}, keepAxes=true}}},
}

local function finite(n, lo, hi) return type(n) == 'number' and n == n and n >= lo and n <= hi end

-- Curated game and engine assets an accessory may use without a pak (the
-- same allow-list as AimModCore's IsGameAccessoryAsset).
local gameMeshes = {'/Engine/BasicShapes/', '/Game/Art/StaticMeshes/KMC/Brushes/'}
local gameMaterials = {'/Game/Materials/Instances/Characters/S_Meso/Base/MI_PaintedMetal_', '/Game/Materials/Instances/Characters/S_Endo/Base/MI_PaintedMetal_'}
local flatMaterials = {['/MapCreator/Materials/MM_G_Basic.MM_G_Basic']=true, ['/Engine/BasicShapes/BasicShapeMaterial.BasicShapeMaterial']=true,
    ['/Game/Materials/Masters/Environment/MM_Glow.MM_Glow']=true, ['/MapCreator/Materials/DefaultManipulationMaterial.DefaultManipulationMaterial']=true}
local function gameAsset(path, roots)
    if type(path) ~= 'string' or #path > 200 or path:find('..', 1, true) or path:find('//', 1, true) or path:find('[^%w_/%.%-]') then return false end
    if roots == gameMaterials and flatMaterials[path] then return true end
    for _, root in ipairs(roots) do
        if #path > #root and path:sub(1, #root) == root and not path:find('/', #root + 1, true) then return true end
    end
    return false
end

-- Pak kinds need a verified pak, except accessories fitted from game meshes.
function M.needsPak(item)
    local kind = M.kinds[item.kind]
    if not kind or not kind.needsPak then return false end
    local fit = type(item.attach) == 'table' and item.attach.fit
    return not (item.kind == 'accessory' and item.pak == nil and type(fit) == 'table'
        and (gameAsset(item.mesh, gameMeshes) or (item.mesh == nil and type(item.shape) == 'string' and item.shape:match('^[a-z0-9][a-z0-9%-]*%.amsh$') ~= nil))
        and gameAsset(item.material, gameMaterials))
end

-- Structural check of one item. Returns true or nil, reason.
function M.validate(item)
    if type(item) ~= 'table' then return nil, 'not a table' end
    if type(item.id) ~= 'string' or not item.id:match('^[a-z0-9][a-z0-9%-]*$') or #item.id > 48 then return nil, 'bad id' end
    if not finite(item.version, 1, 100000) or item.version ~= math.floor(item.version) then return nil, item.id .. ': bad version' end
    local kind = M.kinds[item.kind]
    if not kind then return nil, item.id .. ': unknown kind' end
    if type(item.parts) ~= 'table' or #item.parts == 0 then return nil, item.id .. ': no parts' end
    for _, part in ipairs(item.parts) do
        if not kind.parts[part] then return nil, item.id .. ': part ' .. tostring(part) .. ' not allowed for ' .. item.kind end
    end
    if kind.parts.body and not kind.parts.weapon and (type(item.models) ~= 'table' or #item.models == 0) then
        return nil, item.id .. ': avatar items need base models'
    end
    for name, c in pairs(item.vector or {}) do
        if type(name) ~= 'string' or type(c) ~= 'table' then return nil, item.id .. ': bad vector' end
        for _, k in ipairs({'R', 'G', 'B', 'A'}) do
            if not finite(c[k], 0, 1) then return nil, item.id .. ': vector ' .. name .. ' out of range' end
        end
    end
    for name, v in pairs(item.scalar or {}) do
        if type(name) ~= 'string' or not finite(v, -10, 10) then return nil, item.id .. ': bad scalar ' .. tostring(name) end
    end
    if M.needsPak(item) and type(item.pak) ~= 'table' then return nil, item.id .. ': needs a pak' end
    if not kind.needsPak and next(item.vector or {}) == nil and next(item.scalar or {}) == nil then
        return nil, item.id .. ': no parameters'
    end
    return true
end

-- Index of valid items by id. Duplicate ids invalidate both entries.
function M.index(items)
    local byId, errors, dup = {}, {}, {}
    for _, item in ipairs(items or M.items) do
        local ok, reason = M.validate(item)
        if not ok then errors[#errors + 1] = reason
        elseif byId[item.id] or dup[item.id] then dup[item.id] = true; byId[item.id] = nil; errors[#errors + 1] = item.id .. ': duplicate id'
        else byId[item.id] = item end
    end
    return byId, errors
end

-- Resolve an id a player picked or a lobby sent. Unknown ids, drafts (unless
-- team testing) and pak items whose pak does not match the manifest resolve
-- to nil, and the
-- caller falls back to the base look.
function M.resolve(byId, id, options)
    options = options or {}
    if type(id) ~= 'string' or id == '' then return nil, 'no item' end
    local item = byId[id]
    if not item then return nil, 'not in the catalog: ' .. id end
    if item.draft and not options.allowDrafts then return nil, id .. ' is a draft' end
    if M.needsPak(item) and not (options.verifiedPaks and options.verifiedPaks[item.pak.file]) then
        return nil, id .. ' needs an AimMod pak matching the manifest'
    end
    return item
end

-- Items shown in the picker: valid, not drafts.
function M.pickable(byId)
    local list = {}
    for _, item in pairs(byId) do if not item.draft then list[#list + 1] = item end end
    table.sort(list, function(a, b) return a.id < b.id end)
    return list
end

return M
