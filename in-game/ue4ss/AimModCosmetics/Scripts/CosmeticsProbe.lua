-- Read-only cosmetics probe. Logs character mesh components, their
-- materials and material parameter names, the first-person viewmodel and the
-- loaded skin data assets. It calls only getters and reads properties: it
-- never creates materials, sets meshes, changes visibility or collision, or
-- loads assets. Output goes to UE4SS.log and to the private file
-- %LOCALAPPDATA%/AimMod/KovaaksNative/cosmetics-probe.txt (never source
-- control: it names local assets and may include the player's profile).
local R = require('CosmeticsReflect')
local M = {}

local valid, each, str, try = R.valid, R.each, R.str, R.try
local MaxLines = 6000

local function writer()
    local lines = {}
    local self = {}
    function self.line(depth, text)
        if #lines >= MaxLines then return end
        lines[#lines + 1] = string.rep('  ', depth) .. text
        if #lines == MaxLines then lines[#lines + 1] = '... output truncated' end
    end
    function self.lines() return lines end
    return self
end

local collisionNames = {[0]='NoCollision', 'QueryOnly', 'PhysicsOnly', 'QueryAndPhysics'}

local function material(out, depth, mesh, index)
    local mat = try(function() return mesh:GetMaterial(index) end)
    local slotName = ''
    local names = try(function() return mesh:GetMaterialSlotNames() end)
    if names then
        local i = 0
        each(names, function(n) if i == index then slotName = str(n) end; i = i + 1 end, 64)
    end
    out.line(depth, ('slot %d %s: %s [%s]'):format(index, slotName ~= '' and ('"' .. slotName .. '"') or '', R.name(mat), R.className(mat)))
    if not valid(mat) then return end
    local chain = R.parentChain(mat)
    if #chain > 1 then out.line(depth + 1, 'parent chain: ' .. table.concat(chain, ' -> ', 2)) end
    for _, kind in ipairs({'Texture', 'Vector', 'Scalar'}) do
        local params = R.parameters(mat, kind)
        if #params == 0 then
            out.line(depth + 1, kind .. ' params: none readable')
        else
            out.line(depth + 1, kind .. ' params (' .. #params .. '):')
            for _, p in ipairs(params) do
                out.line(depth + 2, ('%s = %s [%s]'):format(p.name, p.value, p.source))
            end
        end
    end
end

local function component(out, depth, comp, note)
    local class = R.className(comp)
    local parent = try(function() return comp:GetAttachParent() end)
    local socket = str(try(function() return comp:GetAttachSocketName() end, ''))
    local visible = try(function() return comp:IsVisible() end, '?')
    local hidden = try(function() return comp.bHiddenInGame end, '?')
    local collision = try(function() return comp:GetCollisionEnabled() end)
    local profile = str(try(function() return comp:GetCollisionProfileName() end, ''))
    out.line(depth, ('%s [%s]%s'):format(R.short(comp), class, note and ('  <' .. note .. '>') or ''))
    out.line(depth + 1, ('attach: %s socket=%s visible=%s hiddenInGame=%s collision=%s profile=%s'):format(
        R.short(parent), socket ~= '' and socket or 'None', tostring(visible), tostring(hidden),
        collisionNames[collision] or tostring(collision), profile))
    if R.isA(comp, '/Script/Engine.SkinnedMeshComponent') then
        out.line(depth + 1, 'skeletal mesh: ' .. R.name(try(function() return comp.SkeletalMesh end)))
        if R.isA(comp, '/Script/Engine.SkeletalMeshComponent') then
            out.line(depth + 1, 'anim class: ' .. R.name(try(function() return comp.AnimClass end)))
            out.line(depth + 1, 'anim tick option: ' .. tostring(try(function() return comp.VisibilityBasedAnimTickOption end, '?')))
        end
        local sockets = try(function() return comp:GetAllSocketNames() end)
        if sockets then
            local names = {}
            local n = each(sockets, function(s) names[#names + 1] = str(s) end, 96)
            out.line(depth + 1, ('sockets/bones (%d): %s'):format(n, table.concat(names, ', ')))
        end
    elseif R.isA(comp, '/Script/Engine.StaticMeshComponent') then
        out.line(depth + 1, 'static mesh: ' .. R.name(try(function() return comp.StaticMesh end)))
    end
    local count = try(function() return comp:GetNumMaterials() end, 0) or 0
    for i = 0, math.min(count, 16) - 1 do material(out, depth + 1, comp, i) end
end

local function components(out, depth, actor, notes)
    local meshClass = R.class('/Script/Engine.MeshComponent')
    if not valid(meshClass) then out.line(depth, 'MeshComponent class unavailable'); return end
    local list = try(function() return actor:K2_GetComponentsByClass(meshClass) end)
    if not list then out.line(depth, 'components unreadable'); return end
    local n = each(list, function(comp)
        if valid(comp) then component(out, depth, comp, notes and notes[R.short(comp)]) end
    end, 64)
    out.line(depth, ('(%d mesh components)'):format(n))
end

-- Components the game uses for gameplay. Cosmetics must never modify them.
local characterNotes = {
    CollisionCylinder='gameplay: capsule', BodyBB='gameplay: bounding box', ProjBB='gameplay: projectile bounding box',
    CylHead='hitbox', CylBody='hitbox', CylTopSphere='hitbox', CylBottomSphere='hitbox',
    PCylHead='projectile hitbox', PCylBody='projectile hitbox', PCylTopSphere='projectile hitbox', PCylBottomSphere='projectile hitbox',
    SphereHead='hitbox', SphereBody='hitbox', PSphereHead='projectile hitbox', PSphereBody='projectile hitbox',
    CubeHead='hitbox', CubeBody='hitbox', PCubeHead='projectile hitbox', PCubeBody='projectile hitbox',
    CharacterMesh0='character mesh: materials only',
}
local viewNotes = {ShotOrigin='gameplay: shot origin, never modify', FPSPlayer='first-person arms'}

local function character(out, actor, localPawn)
    local isLocal = R.sameObject(actor, localPawn)
    local profile = str(try(function() return actor:GetCharacterProfileName() end, ''))
    out.line(0, ('CHARACTER %s [%s]%s'):format(R.name(actor), R.className(actor), isLocal and '  <LOCAL PLAYER>' or ''))
    out.line(1, ('profile="%s" playerControlled=%s meshHitDetection=%s'):format(profile,
        tostring(try(function() return actor:IsPlayerControlled() end, '?')),
        tostring(try(function() return actor:K2_IsUsingMeshHitDetection() end, '?'))))
    local model, skin, source = R.look(actor)
    out.line(1, ('look: model=%s skin=%s (from %s)'):format(tostring(model), tostring(skin), source))
    local mesh = try(function() return actor.Mesh end)
    out.line(1, 'ACharacter.Mesh: ' .. R.short(mesh))
    components(out, 1, actor, characterNotes)
    if isLocal then
        for _, field in ipairs({'ViewModel_Native', 'ViewModel'}) do
            local view = try(function() return actor[field] end)
            if valid(view) then
                out.line(1, ('VIEWMODEL %s via %s [%s]'):format(R.name(view), field, R.className(view)))
                out.line(2, 'selected weapon model: ' .. str(try(function() return view:GetSelectedWeaponModelName() end, '?')))
                out.line(2, 'selected weapon mesh: ' .. R.short(try(function() return view:GetSelectWeaponMesh() end)))
                out.line(2, 'arms mesh: ' .. R.short(try(function() return view:GetFPSPlayerSkeletalMeshComponent() end)))
                components(out, 2, view, viewNotes)
                break
            end
        end
    end
end

local function softPath(value)
    return try(function() return str(value:ToString()) end)
        or try(function() return str(value.ObjectPath.AssetPathName) end)
        or '<soft reference unreadable on this UE4SS build>'
end

local function sorted(set)
    local list = {}
    for k in pairs(set or {}) do list[#list + 1] = k end
    table.sort(list)
    return table.concat(list, ', ')
end

local function skins(out)
    local models, freeSkins = R.freeLooks()
    out.line(0, 'FREE DEFAULT PACKS ' .. (models and '' or '(not loaded)'))
    out.line(1, 'models: ' .. sorted(models))
    out.line(1, 'skins: ' .. sorted(freeSkins))
    out.line(0, 'SKIN DATA (loaded objects only)')
    for _, class in ipairs({'MetaSkeletalCharacterSkin', 'MetaStaticCharacterSkin'}) do
        each(FindAllOf(class) or {}, function(skin)
            if not valid(skin) or R.name(skin):find('Default__', 1, true) then return end
            out.line(1, ('%s name=%s model=%s'):format(R.name(skin), str(try(function() return skin.Name end, '?')),
                str(try(function() return skin.OwningCharacterModelName end, '?'))))
            if class == 'MetaSkeletalCharacterSkin' then
                out.line(2, 'skeletal mesh: ' .. softPath(try(function() return skin.SkeletalMesh end)))
                local mats = try(function() return skin.Materials end)
                if mats then each(mats, function(m) out.line(2, 'material: ' .. softPath(m)) end, 32) end
            end
        end, 64)
    end
    each(FindAllOf('MetaSkeletalCharacterModel') or {}, function(model)
        if not valid(model) or R.name(model):find('Default__', 1, true) then return end
        out.line(1, ('MODEL %s name=%s skeletal=%s meshHitDetection=%s headBone=%s'):format(R.name(model),
            str(try(function() return model.Name end, '?')),
            softPath(try(function() return model.SkeletalMesh end)),
            tostring(try(function() return model.bCanUseSkeletalMeshHitDetection end, '?')),
            str(try(function() return model.HeadShotBoneName end, '?'))))
    end, 64)
end

function M.signature()
    local parts = {}
    for _, actor in ipairs(R.characters(64)) do parts[#parts + 1] = R.name(actor) end
    table.sort(parts)
    return table.concat(parts, '|')
end

function M.run()
    local out = writer()
    out.line(0, 'AimModCosmetics probe ' .. os.date('!%Y-%m-%dT%H:%M:%SZ'))
    local localPawn = R.localPawn()
    local actors = R.characters(64)
    out.line(0, ('%d MetaCharacter actors; local pawn %s'):format(#actors, R.name(localPawn)))
    for _, actor in ipairs(actors) do
        local ok, reason = pcall(character, out, actor, localPawn)
        if not ok then out.line(1, 'probe error: ' .. tostring(reason)) end
    end
    local ok, reason = pcall(skins, out)
    if not ok then out.line(1, 'probe error: ' .. tostring(reason)) end
    return out.lines()
end

-- Private local output. The folder is created by the native service; when it
-- does not exist the probe logs to UE4SS.log only.
function M.save(lines)
    local root = os.getenv('LOCALAPPDATA')
    if not root or root == '' then return false end
    local folder = root .. '/AimMod/KovaaksNative'
    local probeFile = io.open(folder .. '/cosmetics-probe.txt', 'wb')
    if not probeFile then return false end
    local ok = pcall(function() probeFile:write(table.concat(lines, '\n'), '\n') end)
    pcall(function() probeFile:close() end)
    return ok
end

-- Dump once per distinct set of characters, bounded per session.
function M.start(config, log)
    local runs, last = 0, nil
    LoopInGameThreadWithDelay(2000, function()
        if runs >= config.probe_limit then return end
        local ok, err = pcall(function()
            local sig = M.signature()
            if sig == '' or sig == last then return end
            last = sig
            runs = runs + 1
            local lines = M.run()
            for _, line in ipairs(lines) do print('[AimModCosmetics] ' .. line .. '\n') end
            log(('probe %d/%d: %d lines%s'):format(runs, config.probe_limit, #lines,
                M.save(lines) and ', saved to %LOCALAPPDATA%/AimMod/KovaaksNative/cosmetics-probe.txt' or ''))
        end)
        if not ok then log('probe failed: ' .. tostring(err)) end
    end)
end

return M
