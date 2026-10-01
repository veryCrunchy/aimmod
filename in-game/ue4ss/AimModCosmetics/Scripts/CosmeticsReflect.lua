-- Read-only reflection helpers shared by the probe and the prototype.
-- Every function here only reads properties or calls pure getters.
local M = {}

function M.valid(o)
    local ok, v = pcall(function() return o:IsValid() end)
    return ok and v == true
end
local valid = M.valid

function M.unwrap(o)
    if valid(o) then return o end
    local ok, v = pcall(function() return o:get() end)
    if ok then return v end
    return nil
end

-- Iterate a UE4SS TArray or a plain Lua table, stopping after `limit`.
function M.each(a, fn, limit)
    limit = limit or 256
    local n = 0
    local ok, f = pcall(function() return a.ForEach end)
    if ok and type(f) == 'function' then
        a:ForEach(function(_, v)
            n = n + 1
            if n <= limit then fn(M.unwrap(v)) end
        end)
    elseif type(a) == 'table' then
        for _, v in ipairs(a) do
            n = n + 1
            if n > limit then break end
            fn(M.unwrap(v))
        end
    end
    return n
end

function M.str(s)
    if s == nil then return '' end
    if type(s) == 'string' then return s end
    local ok, v = pcall(function() return s:ToString() end)
    if ok and type(v) == 'string' then return v end
    return tostring(s)
end

function M.name(o)
    if not valid(o) then return '<none>' end
    local ok, v = pcall(function() return o:GetFullName() end)
    return ok and M.str(v) or '<unnamed>'
end

function M.short(o)
    if not valid(o) then return '<none>' end
    local ok, v = pcall(function() return o:GetFName():ToString() end)
    return ok and M.str(v) or '<unnamed>'
end

function M.className(o)
    if not valid(o) then return '<none>' end
    local ok, v = pcall(function() return o:GetClass():GetFName():ToString() end)
    return ok and M.str(v) or '<unknown>'
end

local classes = {}
function M.class(path)
    local c = classes[path]
    if valid(c) then return c end
    c = StaticFindObject(path)
    classes[path] = c
    return c
end

function M.isA(o, path)
    local c = M.class(path)
    if not valid(o) or not valid(c) then return false end
    local ok, v = pcall(function() return o:IsA(c) end)
    return ok and v == true
end

function M.try(fn, fallback)
    local ok, v = pcall(fn)
    if ok then return v end
    return fallback
end

-- Parameter names from one material's own overrides (MaterialInstance) or,
-- for a base Material, from its cooked CachedExpressionData. Static arrays
-- are not readable on every UE4SS build, so that branch is best-effort.
-- kind: 'Texture', 'Vector' or 'Scalar'.
local runtimeIndex = {Scalar=0, Vector=1, Texture=2}
local function ownParameters(material, kind, out, seen)
    if M.isA(material, '/Script/Engine.MaterialInstance') then
        local list = M.try(function() return material[kind .. 'ParameterValues'] end)
        if list then
            M.each(list, function(entry)
                local n = M.try(function() return M.str(entry.ParameterInfo.Name) end)
                if n and n ~= '' and not seen[n] then
                    seen[n] = true
                    local value
                    if kind == 'Texture' then value = M.name(M.try(function() return entry.ParameterValue end))
                    elseif kind == 'Vector' then
                        value = M.try(function()
                            local c = entry.ParameterValue
                            return ('(%.3f, %.3f, %.3f, %.3f)'):format(c.R, c.G, c.B, c.A)
                        end, '?')
                    else value = tostring(M.try(function() return entry.ParameterValue end, '?')) end
                    out[#out + 1] = {name=n, value=value, source='override'}
                end
            end, 128)
        end
    elseif M.isA(material, '/Script/Engine.Material') then
        M.try(function()
            local entries = material.CachedExpressionData.Parameters.RuntimeEntries
            local entry = entries[runtimeIndex[kind] + 1]
            M.each(entry.ParameterInfos, function(info)
                local n = M.str(info.Name)
                if n ~= '' and not seen[n] then
                    seen[n] = true
                    out[#out + 1] = {name=n, value='(base default)', source='base'}
                end
            end, 128)
        end)
    end
end

-- Walk instance -> parent -> ... -> base material (bounded).
function M.parameters(material, kind)
    local out, seen = {}, {}
    local current = material
    for _ = 1, 8 do
        if not valid(current) then break end
        ownParameters(current, kind, out, seen)
        if not M.isA(current, '/Script/Engine.MaterialInstance') then break end
        current = M.try(function() return current.Parent end)
    end
    return out
end

function M.parentChain(material)
    local chain = {}
    local current = material
    for _ = 1, 8 do
        if not valid(current) then break end
        chain[#chain + 1] = M.name(current)
        if not M.isA(current, '/Script/Engine.MaterialInstance') then break end
        current = M.try(function() return current.Parent end)
    end
    return chain
end

function M.parameterNames(material, kind)
    local names = {}
    for _, p in ipairs(M.parameters(material, kind)) do names[#names + 1] = p.name end
    return names
end

-- The local player's pawn, from the player controller (same source the
-- steam bridge uses). Never mutated.
function M.localPawn()
    local pawn
    M.each(FindAllOf('MetaPlayerController') or {}, function(pc)
        if pawn or not valid(pc) then return end
        local isLocal = M.try(function() return pc:IsLocalPlayerController() end, false)
        if isLocal then
            pawn = M.try(function() return pc.MyCharacter end)
            if not valid(pawn) then pawn = M.try(function() return pc:K2_GetPawn() end) end
        end
    end, 16)
    if valid(pawn) then return pawn end
    return nil
end

function M.sameObject(a, b)
    if not valid(a) or not valid(b) then return false end
    local ok, v = pcall(function() return a:GetAddress() == b:GetAddress() end)
    if ok then return v end
    return M.name(a) == M.name(b)
end

-- The look a character currently has: the ICharacterModelInterface getters,
-- else the loaded character profile. Returns model, skin, source.
local interfacePath = '/Script/GameSkillsTrainer.CharacterModelInterface:'
function M.look(actor)
    local fm, fs = StaticFindObject(interfacePath .. 'GetCharacterModelName'), StaticFindObject(interfacePath .. 'GetCharacterSkinName')
    if valid(fm) and valid(fs) then
        local model = M.try(function() return M.str(fm(actor)) end)
        local skin = M.try(function() return M.str(fs(actor)) end)
        if model and model ~= '' then return model, skin or '', 'interface' end
    end
    local profile = M.try(function() return actor.mCharacterProfileNative end)
    if profile then
        local model = M.try(function() return M.str(profile.CharacterModel) end)
        local skin = M.try(function() return M.str(profile.CharacterSkin) end)
        if model and model ~= '' then return model, skin or '', 'profile' end
    end
    return nil, nil, 'unreadable'
end

-- Names in the game's free Default packs (asset paths from the 3.9.11 dump).
-- Returns nil when a pack is not loaded, so callers fail closed.
local packs = '/Game/FirstPersonBP/Blueprints/Bodies/Characters/'
M.defaultModelPack = packs .. 'CharacterModelPacks/Default_CharacterModelPack.Default_CharacterModelPack'
M.defaultSkinPack = packs .. 'CharacterSkinPacks/Default_CharacterSkinPack.Default_CharacterSkinPack'
function M.freeLooks()
    local modelPack, skinPack = StaticFindObject(M.defaultModelPack), StaticFindObject(M.defaultSkinPack)
    if not valid(modelPack) or not valid(skinPack) then return nil end
    local models, skins = {}, {}
    M.each(M.try(function() return modelPack.Models end) or {}, function(asset)
        local n = M.try(function() return M.str(asset.CharacterModel.Name) end)
        if n and n ~= '' then models[n] = true end
    end, 64)
    M.each(M.try(function() return skinPack.Skins end) or {}, function(asset)
        local n = M.try(function() return M.str(asset.CharacterSkin.Name) end)
        if n and n ~= '' then skins[n] = true end
    end, 128)
    if next(models) == nil then return nil end
    return models, skins
end

-- Scenario state for the scope gate, read from the one live ScenarioManager.
-- Any missing or ambiguous value stays nil, which the gate treats as "off".
function M.gameState()
    local state = {}
    local manager
    local count = 0
    M.each(FindAllOf('ScenarioManager') or {}, function(o)
        if valid(o) and not M.name(o):find('Default__', 1, true) then count = count + 1; manager = o end
    end, 8)
    if count ~= 1 then return state end
    local function bool(fn)
        local v = M.try(fn)
        if v == true or v == false then return v end
        return nil
    end
    local scenario = M.try(function() return manager:GetCurrentScenario() end)
    if not valid(scenario) then return state end
    state.scenario = M.try(function() return M.str(scenario:GetName()) end)
    local managerChallenge = bool(function() return manager:IsInChallenge() end)
    local scenarioChallenge = bool(function() return scenario:IsInChallenge() end)
    if managerChallenge == false and scenarioChallenge == false then state.inChallenge = false
    elseif managerChallenge or scenarioChallenge then state.inChallenge = true end
    state.benchmark = bool(function() return manager:IsCurrentlyInBenchmark() end)
    state.editor = bool(function() return manager:IsInScenarioEditor() end)
    state.loading = bool(function() return manager:IsScenarioLoading() end)
    return state
end

-- Live MetaCharacter instances (no CDOs or archetypes).
function M.characters(limit)
    local list = {}
    M.each(FindAllOf('MetaCharacter') or {}, function(actor)
        if valid(actor) and not M.name(actor):find('Default__', 1, true) then list[#list + 1] = actor end
    end, limit or 64)
    return list
end

return M
