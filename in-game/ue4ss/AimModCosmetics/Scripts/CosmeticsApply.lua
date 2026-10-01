-- Prototype: apply curated catalog items, and only inside AimMod modes.
-- Disabled unless config.txt sets enabled=1 and cosmetics=1.
--
-- Scope (CosmeticsScope.decide), checked every tick:
--  * match: AimMod avatars and your own weapon/arms
--  * spectate: AimMod avatars only
--  * lobby, normal scenarios, challenges, benchmarks, editor: nothing, and
--    anything applied earlier is restored to the game's own material.
--
-- What it touches: only material slots, through a new dynamic instance
-- parented on the slot's current material (team colours and the game's
-- parameters are inherited). It never calls mesh, visibility, collision or
-- physics functions, never touches hitboxes, the capsule or ShotOrigin, and
-- loads no asset: pak items stay disabled until the signed pak verifier exists.
local Util = require('CosmeticsUtil')
local R = require('CosmeticsReflect')
local Scope = require('CosmeticsScope')
local Catalog = require('CosmeticsCatalog')
local M = {}

local valid, try = R.valid, R.try

local function defaultMarker()
    local root = os.getenv('LOCALAPPDATA')
    if not root or root == '' then return nil end
    local file = io.open(root .. '/AimMod/KovaaksNative/aimmod-session.txt', 'rb')
    if not file then return nil end
    local ok, text = pcall(function() return file:read(1025) end)
    pcall(function() file:close() end)
    return ok and text or nil
end

local function address(o)
    if not valid(o) then return nil end
    return try(function() return o:GetAddress() end) or R.name(o)
end

function M.new(config, log, deps)
    deps = deps or {}
    local now = deps.now or os.time
    local readMarker = deps.readMarker or defaultMarker
    local self = {}
    local warned = {}
    local function warnOnce(key, text)
        if warned[key] then return end
        warned[key] = true
        log(text)
    end

    local byId, errors = Catalog.index(deps.items)
    for _, e in ipairs(errors) do log('catalog: ' .. e) end
    -- No pak is verified yet: pak items resolve to nil.
    local options = {allowDrafts = config.allow_drafts, verifiedPaks = {}}
    local function pick(id, part)
        if id == '' then return nil end
        local item, reason = Catalog.resolve(byId, id, options)
        if not item then log(('%s item %s unavailable: %s'):format(part, id, reason)) return nil end
        return item
    end
    local avatarItem = pick(config.avatar_item, 'avatar')
    local weaponItem = pick(config.weapon_item, 'weapon')
    if avatarItem and not Util.contains(avatarItem.parts, 'body') then log('avatar item has no body part'); avatarItem = nil end
    if weaponItem and not (Util.contains(weaponItem.parts, 'weapon') or Util.contains(weaponItem.parts, 'arms')) then
        log('weapon item has no weapon or arms part'); weaponItem = nil
    end

    local applied = {}  -- {comp, index, original, mid, scope}
    local ours = {}     -- dynamic instance address -> true
    local lastReason

    local function restore(scope)
        local keep = {}
        for _, e in ipairs(applied) do
            if scope == nil or e.scope == scope then
                local current = valid(e.comp) and try(function() return e.comp:GetMaterial(e.index) end)
                if current and address(current) == address(e.mid) and valid(e.original) then
                    try(function() e.comp:SetMaterial(e.index, e.original) end)
                end
                ours[address(e.mid) or ''] = nil
            else
                keep[#keep + 1] = e
            end
        end
        applied = keep
    end

    local function hasAll(material, item)
        local vectors, scalars = {}, {}
        for _, n in ipairs(R.parameterNames(material, 'Vector')) do vectors[n] = true end
        for _, n in ipairs(R.parameterNames(material, 'Scalar')) do scalars[n] = true end
        for name in pairs(item.vector or {}) do if not vectors[name] then return false, name end end
        for name in pairs(item.scalar or {}) do if not scalars[name] then return false, name end end
        return true
    end

    local function applyItem(comp, item, owner, scope)
        if not valid(comp) or R.short(comp) == 'ShotOrigin' then return end
        local lib = StaticFindObject('/Script/Engine.Default__KismetMaterialLibrary')
        if not valid(lib) then return end
        local count = try(function() return comp:GetNumMaterials() end, 0) or 0
        for index = 0, math.min(count, 16) - 1 do
            local material = try(function() return comp:GetMaterial(index) end)
            if valid(material) and not ours[address(material)] then
                local ok, missing = hasAll(material, item)
                if not ok then
                    warnOnce(item.id .. R.name(material), ('%s does not fit %s (no parameter %s)'):format(item.id, R.name(material), missing))
                else
                    local mid = try(function() return lib:CreateDynamicMaterialInstance(owner, material, FName('AimModCosmetic_' .. item.id), 0) end)
                    if valid(mid) then
                        for name, c in pairs(item.vector or {}) do try(function() mid:SetVectorParameterValue(FName(name), c) end) end
                        for name, v in pairs(item.scalar or {}) do try(function() mid:SetScalarParameterValue(FName(name), v) end) end
                        try(function() comp:SetMaterial(index, mid) end)
                        ours[address(mid)] = true
                        applied[#applied + 1] = {comp=comp, index=index, original=material, mid=mid, scope=scope}
                    end
                end
            end
        end
    end

    local function avatars(localPawn)
        if not avatarItem then return end
        local freeModels, freeSkins = R.freeLooks()
        for _, actor in ipairs(R.characters(64)) do
            local info = {
                profile = R.str(try(function() return actor:GetCharacterProfileName() end, '')),
                playerControlled = try(function() return actor:IsPlayerControlled() end, true) ~= false,
                isLocal = R.sameObject(actor, localPawn),
            }
            if Util.isAvatarTarget(info) then
                local model, skin = R.look(actor)
                if Util.isFreeLook(model, skin, freeModels, freeSkins) and Util.contains(avatarItem.models, model) then
                    local mesh = try(function() return actor.Mesh end)
                    if R.isA(mesh, '/Script/Engine.SkeletalMeshComponent') then applyItem(mesh, avatarItem, actor, 'avatar') end
                end
            end
        end
    end

    local function ownWeapon(localPawn)
        if not weaponItem then return end
        local view = try(function() return localPawn.ViewModel_Native end)
        if not valid(view) then return end
        if weaponItem.weapons then
            local current = R.str(try(function() return view:GetSelectedWeaponModelName() end, ''))
            if not Util.contains(weaponItem.weapons, current) then return end
        end
        if Util.contains(weaponItem.parts, 'weapon') then
            applyItem(try(function() return view:GetSelectWeaponMesh() end), weaponItem, view, 'local')
        end
        if Util.contains(weaponItem.parts, 'arms') then
            applyItem(try(function() return view:GetFPSPlayerSkeletalMeshComponent() end), weaponItem, view, 'local')
        end
    end

    function self.decision()
        local state = R.gameState()
        local marker = Scope.parseMarker(readMarker())
        return Scope.decide({marker=marker, now=now(), scenario=state.scenario, inChallenge=state.inChallenge,
            benchmark=state.benchmark, editor=state.editor, loading=state.loading})
    end

    function self.tick()
        -- Forget components the game destroyed (avatars leave, levels change).
        local live = {}
        for _, e in ipairs(applied) do
            if valid(e.comp) then live[#live + 1] = e else ours[address(e.mid) or ''] = nil end
        end
        applied = live
        local decision = self.decision()
        if decision.reason ~= lastReason then lastReason = decision.reason; log('scope: ' .. decision.reason) end
        if not decision.avatars then restore('avatar') end
        if not decision.localPlayer then restore('local') end
        if not decision.avatars and not decision.localPlayer then return end
        local localPawn = R.localPawn()
        if localPawn == nil then return end   -- fail closed
        if decision.avatars then avatars(localPawn) end
        if decision.localPlayer then ownWeapon(localPawn) end
    end

    function self.applied() return #applied end
    return self
end

function M.start(config, log)
    local applier = M.new(config, log)
    local lastError
    LoopInGameThreadWithDelay(config.interval_ms, function()
        local ok, err = pcall(applier.tick)
        if not ok and err ~= lastError then lastError = err; log('cosmetics: ' .. tostring(err)) end
    end)
    log(('cosmetics prototype on: avatar_item=%s weapon_item=%s drafts=%s'):format(
        config.avatar_item ~= '' and config.avatar_item or '(none)', config.weapon_item ~= '' and config.weapon_item or '(none)',
        tostring(config.allow_drafts)))
end

return M
