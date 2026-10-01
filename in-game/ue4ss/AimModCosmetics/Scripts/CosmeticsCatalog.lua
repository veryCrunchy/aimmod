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
--   attach   accessories: bone or socket role; per-model names and offsets
--            are filled in from the probe.
--   draft    true until the parameter names are confirmed with the probe.
--            Drafts never appear in the picker and apply only for team tests
--            with allow_drafts=1.
local M = {}

M.version = 1

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

-- First curated set. Parameter names are placeholders until the probe lists
-- the real names on MI_PaintedMetal_Meso_TS1/TS2, the Endo materials and the
-- MI_* weapon materials; every item is a draft until then.
M.items = {
    {id='meso-tint-ember', version=1, kind='avatar_tint', name='Ember', models={'Meso'}, parts={'body'},
        vector={PrimaryColor={R=0.85, G=0.22, B=0.05, A=1}}, draft=true},
    {id='meso-tint-glacier', version=1, kind='avatar_tint', name='Glacier', models={'Meso'}, parts={'body'},
        vector={PrimaryColor={R=0.55, G=0.80, B=0.95, A=1}}, draft=true},
    {id='meso-tint-graphite', version=1, kind='avatar_tint', name='Graphite', models={'Meso'}, parts={'body'},
        vector={PrimaryColor={R=0.12, G=0.12, B=0.13, A=1}}, scalar={Roughness=0.6}, draft=true},
    {id='endo-tint-verdant', version=1, kind='avatar_tint', name='Verdant', models={'Endo'}, parts={'body'},
        vector={PrimaryColor={R=0.20, G=0.65, B=0.30, A=1}}, draft=true},
    {id='meso-pattern-stripes', version=1, kind='avatar_pattern', name='Racing stripes', models={'Meso'}, parts={'body'},
        pak={file='AimModCosmetics-1.pak', sha256=''}, draft=true},
    {id='weapon-finish-gunmetal', version=1, kind='weapon_finish', name='Gunmetal', parts={'weapon'},
        vector={PrimaryColor={R=0.30, G=0.32, B=0.35, A=1}}, scalar={Metallic=1.0}, draft=true},
    {id='weapon-finish-sand', version=1, kind='weapon_finish', name='Sand', parts={'weapon'},
        vector={PrimaryColor={R=0.76, G=0.66, B=0.48, A=1}}, draft=true},
    {id='weapon-finish-aimmod', version=1, kind='weapon_finish', name='AimMod colours', parts={'weapon', 'arms'},
        vector={PrimaryColor={R=0.98, G=0.45, B=0.10, A=1}}, draft=true},
    -- Proof accessories: rigid head items, no cloth or physics.
    {id='accessory-halo', version=1, kind='accessory', name='Halo', models={'Meso', 'Endo'}, parts={'body'},
        attach='head', pak={file='AimModCosmetics-1.pak', sha256=''}, draft=true},
    {id='accessory-visor', version=1, kind='accessory', name='Visor', models={'Meso', 'Endo'}, parts={'body'},
        attach='head', pak={file='AimModCosmetics-1.pak', sha256=''}, draft=true},
    {id='accessory-headband', version=1, kind='accessory', name='Headband', models={'Meso', 'Endo'}, parts={'body'},
        attach='head', pak={file='AimModCosmetics-1.pak', sha256=''}, draft=true},
}

local function finite(n, lo, hi) return type(n) == 'number' and n == n and n >= lo and n <= hi end

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
    if kind.needsPak and type(item.pak) ~= 'table' then return nil, item.id .. ': needs a pak' end
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
    if M.kinds[item.kind].needsPak and not (options.verifiedPaks and options.verifiedPaks[item.pak.file]) then
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
