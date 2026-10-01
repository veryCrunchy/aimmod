-- AimModModesProbe: read-only feasibility probe for AimMod game modes.
--
-- It answers, on the live game, the questions in in-game/docs/game-modes.md
-- section 9 that the reflection dumps cannot settle:
--   1. Which classes and functions the mode framework needs exist at runtime.
--   2. Which gameplay functions fire through reflection (RegisterHook sees
--      them) and which are called natively (the hook count stays 0).
--   3. What characters, bots, weapons, abilities and map objects a scenario
--      actually has, and how health, damage and kill counters move.
--
-- Read-only rules:
--   * Never calls a setter, spawner, damage, weapon, ability or scoring
--     function. Calls only reflected getters (Get*, Is*, K2_Is*) and reads
--     reflected properties.
--   * Hooks only count calls. They never read, change or return arguments.
--   * Writes nothing except log lines (UE4SS.log), capped by a line budget.
--   * Prints no player names or Steam ids.
local TAG = '[AimModModesProbe] '
local budget = 6000
local function log(text)
    if budget <= 0 then return end
    budget = budget - 1
    print(TAG .. text .. '\n')
    if budget == 0 then print(TAG .. 'line budget exhausted; further output suppressed\n') end
end

for _, name in ipairs({'RegisterHook', 'StaticFindObject', 'FindAllOf', 'LoopInGameThreadWithDelay'}) do
    if _G[name] == nil then
        print(TAG .. 'disabled: UE4SS global ' .. name .. ' is missing\n')
        return
    end
end

local function valid(o) local ok, v = pcall(function() return o:IsValid() end); return ok and v == true end
local function unwrap(o) if valid(o) then return o end; local ok, v = pcall(function() return o:get() end); return ok and v or nil end
local function same(a, b) return valid(a) and valid(b) and a:GetAddress() == b:GetAddress() end
local function str(s)
    if s == nil then return '' end
    if type(s) == 'string' then return s end
    local ok, v = pcall(function() return s:ToString() end)
    return ok and v or tostring(s)
end
local function each(a, fn)
    if a == nil then return end
    local ok, f = pcall(function() return a.ForEach end)
    if ok and type(f) == 'function' then a:ForEach(function(_, v) fn(unwrap(v)) end)
    elseif type(a) == 'table' then for _, v in ipairs(a) do fn(unwrap(v)) end end
end
local function try(fn, ...) local ok, v = pcall(fn, ...); if ok then return v end; return nil end
local function num(v) if type(v) ~= 'number' or v ~= v then return 'n/a' end; return string.format('%.2f', v) end
local function short(o) local n = try(function() return o:GetFName():ToString() end); return n or '?' end

-- 1. Inventory -------------------------------------------------------------
-- Every class and function the framework plans to use (game-modes.md 4.3).
local inventory = {
    classes = {
        '/Script/GameSkillsTrainer.MetaCharacter', '/Script/GameSkillsTrainer.TheMetaAIController',
        '/Script/GameSkillsTrainer.WeaponHandler', '/Script/GameSkillsTrainer.WeaponParentActor',
        '/Script/GameSkillsTrainer.AbilityHandler', '/Script/GameSkillsTrainer.AbilityParentActor',
        '/Script/GameSkillsTrainer.MovementAbility', '/Script/GameSkillsTrainer.WeaponAbility',
        '/Script/GameSkillsTrainer.MeleeAbility', '/Script/GameSkillsTrainer.RecallAbility',
        '/Script/GameSkillsTrainer.SprintAbility', '/Script/GameSkillsTrainer.MetaProjectile',
        '/Script/GameSkillsTrainer.MetaCharacterMovementComponent', '/Script/GameSkillsTrainer.PlaybackComponent',
        '/Script/GameSkillsTrainer.MapCreatorSpawnPoint', '/Script/GameSkillsTrainer.MapCreatorSpawnVolume',
        '/Script/GameSkillsTrainer.MapCreatorJumpPad', '/Script/GameSkillsTrainer.MapCreatorTeleporter',
        '/Script/GameSkillsTrainer.MapCreatorHurtKill', '/Script/GameSkillsTrainer.MapCreatorWaypoint',
        '/Script/GameSkillsTrainer.MapCreatorTextLabel', '/Script/GameSkillsTrainer.MapCreatorWater',
        '/Script/GameSkillsTrainer.KovaakMapCreatorRepository', '/Script/GameSkillsTrainer.Duels',
        '/Script/GameSkillsTrainer.TargetMarker', '/Script/CableComponent.CableActor',
        '/Script/Engine.StaticMeshActor', '/Game/FirstPersonBP/Blueprints/Bodies/FPSCharacter.FPSCharacter_C',
    },
    functions = {
        -- remote avatars
        'MetaCharacter:UpdateClientLocAndRot', 'MetaCharacter:UpdateVisibility', 'MetaCharacter:SetTeam',
        'MetaCharacter:OverrideTeam', 'MetaCharacter:LoadCharacterProfile', 'MetaCharacter:OverrideInvulnerable',
        'MetaCharacter:SetSkipHandleDamage', 'TheMetaAIController:Spawn', 'TheMetaAIController:ClearMovementInputs',
        'TheMetaAIController:StopAiming', 'TheMetaAIController:SetUseWeapons', 'TheMetaAIController:RemoveSelf',
        'PlaybackComponent:StartPlayback', 'PlaybackComponent:IsWorldPositionPlayback',
        -- damage, health, death
        'MetaCharacter:TakeDamageFunc', 'MetaCharacter:HandleDamage', 'MetaCharacter:HandleHeal',
        'MetaCharacter:HandleLifesteal', 'MetaCharacter:SetHealth', 'MetaCharacter:Death',
        'MetaCharacter:Respawn', 'MetaCharacter:BeginRespawn', 'MetaCharacter:FinishRespawn',
        'MetaCharacter:RegenHealthFromKill', 'MetaCharacter:OnKillScored', 'MetaCharacter:SetLives',
        'MetaCharacter:ShouldTakeDamageFromAttacker', 'MetaCharacter:GetCurrentHealth',
        -- movement, teleport, freeze
        'MetaCharacter:TeleportDelayed', 'MetaCharacter:StunMe', 'MetaCharacter:ApplyFlatKnockback',
        'MetaCharacter:ApplyKnockback', 'MetaCharacter:IsMovementInputBlocked', 'MetaCharacter:IsAttackInputBlocked',
        -- weapons and abilities
        'WeaponHandler:SetWeaponProfileByString', 'WeaponHandler:ChangeWeapon', 'WeaponHandler:RefillAllAmmo',
        'WeaponHandler:GetSelectableWeapons', 'WeaponParentActor:SetCurrentAmmo', 'WeaponParentActor:Send_ShotHit',
        'WeaponParentActor:Send_ShotFired', 'WeaponParentActor:Send_ShotMissed', 'WeaponParentActor:DamageEnemy',
        'WeaponParentActor:DamageHitscanTargetNative', 'WeaponParentActor:ApplyEffects',
        'AbilityHandler:PressAbility', 'AbilityHandler:UnpressAbility', 'AbilityHandler:LoadAbilities',
        -- scenario
        'ScenarioManager:NotifyDamageDealt', 'ScenarioManager:NotifyCharacterDeath',
        'ScenarioManager:NotifyPlayerKillCredit', 'ScenarioManager:Import_CharacterProfile',
        'ScenarioManager:InitializeScenario', 'MetaGameState:GetCharacters',
        'KovaakMapCreatorRepository:GetSpawnPointsForTeam', 'KovaakMapCreatorRepository:GetWaypoints',
        'KovFunctionLibrary:TeamCheck', 'MetaGameplayStatics:RemoveAllBots',
        'FeatureFlagLibrary:IsDuelsEnabled', 'FeatureFlagLibrary:IsCharacterPlaybacksEnabled',
    },
    engine = {
        '/Script/Engine.Character:LaunchCharacter', '/Script/Engine.Actor:K2_TeleportTo',
        '/Script/Engine.Actor:K2_SetActorLocationAndRotation', '/Script/Engine.Controller:SetIgnoreMoveInput',
        '/Script/Engine.Controller:SetIgnoreLookInput', '/Script/Engine.CharacterMovementComponent:SetMovementMode',
        '/Script/Engine.CharacterMovementComponent:DisableMovement', '/Script/Engine.KismetSystemLibrary:LineTraceSingle',
        '/Script/Engine.GameplayStatics:BeginDeferredActorSpawnFromClass', '/Script/Engine.DataTableFunctionLibrary:GetDataTableRowNames',
    },
}

local function inventoryReport()
    local found, missing = 0, {}
    local function check(path)
        if valid(try(StaticFindObject, path)) then found = found + 1 else missing[#missing + 1] = path end
    end
    for _, p in ipairs(inventory.classes) do check(p) end
    for _, f in ipairs(inventory.functions) do check('/Script/GameSkillsTrainer.' .. f) end
    for _, p in ipairs(inventory.engine) do check(p) end
    log('inventory: ' .. found .. ' present, ' .. #missing .. ' missing')
    for _, p in ipairs(missing) do log('inventory missing: ' .. p) end
end

-- 2. Reflection reachability: counter-only hooks ----------------------------
-- A count above 0 means the game calls the function through ProcessEvent, so
-- AimModCore can observe it with a UFunction hook. A count that stays 0 while
-- the event clearly happens in game (you shot a bot, took damage, died) means
-- the call is native and the framework must poll state instead (doc 5.2).
local hooks = {
    'WeaponParentActor:Send_ShotFired', 'WeaponParentActor:Send_ShotHit', 'WeaponParentActor:Send_ShotMissed',
    'WeaponParentActor:DamageEnemy', 'WeaponParentActor:DamageHitscanTargetNative', 'WeaponParentActor:ApplyEffects',
    'WeaponParentActor:HitscanShot', 'WeaponParentActor:ProjectileShot', 'WeaponParentActor:KilledMob',
    'MetaCharacter:TakeDamageFunc', 'MetaCharacter:HandleDamage', 'MetaCharacter:HandleHeal',
    'MetaCharacter:HandleLifesteal', 'MetaCharacter:SetHealth', 'MetaCharacter:Death', 'MetaCharacter:OnCharacterKilled',
    'MetaCharacter:BeginRespawn', 'MetaCharacter:Respawn', 'MetaCharacter:FinishRespawn', 'MetaCharacter:K2_FinishRespawn',
    'MetaCharacter:RegenHealthFromKill', 'MetaCharacter:OnKillScored', 'MetaCharacter:GotKillCredit',
    'MetaCharacter:UpdateClientLocAndRot', 'MetaCharacter:StunMe', 'MetaCharacter:ApplyKnockback',
    'MetaCharacter:ApplyFlatKnockback', 'MetaCharacter:TeleportDelayed', 'MetaCharacter:LoadCharacterProfile',
    'WeaponHandler:ChangeWeapon', 'WeaponHandler:SetWeaponProfileByString', 'WeaponHandler:RefillAllAmmo',
    'AbilityHandler:PressAbility', 'AbilityHandler:UnpressAbility', 'AbilityParentActor:UseAbility',
    'AbilityParentActor:AbilityPressed', 'MovementAbility:HandleTimelineUpdate',
    'ScenarioManager:NotifyDamageDealt', 'ScenarioManager:NotifyCharacterDeath', 'ScenarioManager:NotifyPlayerKillCredit',
    'TheMetaAIController:Spawn', 'TheMetaAIController:CharWasKilled', 'PlaybackComponent:StartPlayback',
}
local counts, reported = {}, {}
local function registerHooks()
    local bound = 0
    for _, f in ipairs(hooks) do
        local path = '/Script/GameSkillsTrainer.' .. f
        counts[f] = -1
        local ok = pcall(function()
            assert(valid(StaticFindObject(path)))
            RegisterHook(path, function() return nil end, function()
                -- Count only. Do not read, dereference or replace anything.
                if counts[f] < 1000000000 then counts[f] = counts[f] + 1 end
                return nil
            end)
        end)
        if ok then counts[f] = 0; bound = bound + 1 end
    end
    log('hooks: ' .. bound .. '/' .. #hooks .. ' bound (count-only)')
    for _, f in ipairs(hooks) do if counts[f] < 0 then log('hook unavailable: ' .. f) end end
end
local function hookReport(force)
    local parts = {}
    for _, f in ipairs(hooks) do
        local c = counts[f]
        if c and c >= 0 and (force or c ~= reported[f]) then parts[#parts + 1] = f .. '=' .. c; reported[f] = c end
    end
    if #parts > 0 then log('hook counts: ' .. table.concat(parts, ' ')) end
end

-- 3. World snapshot ---------------------------------------------------------
local api = nil
local worldKey, characterState = nil, {}

local function context()
    api = api or try(StaticFindObject, '/Script/Engine.Default__GameplayStatics')
    if not valid(api) then return nil end
    local pc = nil
    each(FindAllOf('MetaPlayerController'), function(o)
        if not pc and valid(o) and not str(try(function() return o:GetFullName() end)):find('Default__', 1, true) then pc = o end
    end)
    if not valid(pc) then return nil end
    local gs = try(function() return api:GetGameState(pc) end)
    if not valid(gs) then return nil end
    return pc, gs
end

local function describeAbilities(character)
    local handler = try(function() return character.AbilityHandler end)
    if not valid(handler) then return 'none' end
    local out = {}
    each(try(function() return handler.Abilities end), function(a)
        if valid(a) and #out < 8 then
            out[#out + 1] = short(a) .. '{name=' .. str(try(function() return a.AbilityName end))
                .. ' type=' .. tostring(try(function() return a.AbilityType end))
                .. ' charges=' .. num(try(function() return a.CurrentCharges end)) .. '/' .. num(try(function() return a.MaxCharges end)) .. '}'
        end
    end)
    return #out > 0 and table.concat(out, ' ') or 'none'
end

local function describeWeapons(character)
    local handler = try(function() return character.WeaponHandler end)
    if not valid(handler) then return 'none' end
    local out = {}
    each(try(function() return handler.NativeWeapons end), function(w)
        if valid(w) and #out < 8 then
            out[#out + 1] = '{' .. str(try(function() return w:GetWeaponProfileName() end))
                .. ' ammo=' .. tostring(try(function() return w.CurrentAmmo end))
                .. ' fired=' .. tostring(try(function() return w.ShotsFiredThisSession end))
                .. ' hit=' .. tostring(try(function() return w.ShotsHitThisSession end))
                .. ' dmg=' .. num(try(function() return w.DamageDoneThisSession end)) .. '}'
        end
    end)
    return 'current=' .. tostring(try(function() return handler.CurrentWeaponNum_ end))
        .. ' count=' .. tostring(try(function() return handler:GetWeaponCount() end)) .. ' ' .. table.concat(out, ' ')
end

local function describeCharacter(c, pc)
    local isPlayer = same(try(function() return pc.MyCharacter end), c)
    local ai = try(function() return c.MyAiController end)
    local profile = try(function() return c.mCharacterProfileNative end)
    local function p(field) return profile and try(function() return profile[field] end) end
    return (isPlayer and 'player ' or (valid(ai) and 'bot ' or 'other '))
        .. short(c) .. ' class=' .. short(try(function() return c:GetClass() end))
        .. ' profile=' .. str(try(function() return c.CharacterProfileName end))
        .. (valid(ai) and (' botProfile=' .. str(try(function() return ai.MyProfileName end))) or '')
        .. ' team=' .. tostring(try(function() return c.Team end)) .. ' enemy=' .. tostring(try(function() return c.bOnEnemyTeam end))
        .. ' hp=' .. num(try(function() return c:GetCurrentHealth() end)) .. '/' .. num(p('maxHealth'))
        .. ' lives=' .. tostring(try(function() return c.Lives end))
        .. ' model=' .. str(try(function() return c:GetEffectiveCharacterModel() end))
        .. ' skin=' .. str(try(function() return c:GetEffectiveCharacterSkin() end))
        .. ' meshHits=' .. tostring(try(function() return c:K2_IsUsingMeshHitDetection() end))
        .. ' invuln=' .. tostring(try(function() return c:IsInvulnerable() end))
        .. ' teamDamageBlocked=' .. tostring(try(function() return c:IsTeamDamageBlocked() end))
        .. ' lifesteal=' .. num(p('LifeStealPercent')) .. ' hpOnKill=' .. num(try(function() return c:GetHealthRegainedOnKill() end))
        .. ' regen=' .. num(try(function() return c:GetHealthRegenPerSecond() end))
        .. ' respawn=' .. num(try(function() return c:GetMinRespawnDelay() end)) .. '-' .. num(try(function() return c:GetMaxRespawnDelay() end))
        .. ' tp=' .. tostring(try(function() return c:IsUsingThirdPersonCamera() end))
end

local function worldReport(pc, gs)
    local count = 0
    each(try(function() return gs:GetCharacters() end), function(c)
        if valid(c) and count < 24 then
            count = count + 1
            log('character: ' .. describeCharacter(c, pc))
            log('  weapons: ' .. describeWeapons(c))
            log('  abilities: ' .. describeAbilities(c))
        end
    end)
    log('characters: ' .. count)
    for _, name in ipairs({'MapCreatorSpawnPoint', 'MapCreatorJumpPad', 'MapCreatorTeleporter', 'MapCreatorHurtKill',
                           'MapCreatorWaypoint', 'MapCreatorTextLabel', 'MapCreatorWater', 'TheMetaTrainerTarget',
                           'TargetMarker', 'MetaProjectile', 'TriggerBox'}) do
        local n, masks = 0, {}
        each(FindAllOf(name), function(o)
            if valid(o) and same(try(function() return o:GetWorld() end), try(function() return gs:GetWorld() end)) then
                n = n + 1
                if name == 'MapCreatorSpawnPoint' and #masks < 32 then
                    masks[#masks + 1] = short(try(function() return o:GetClass() end)) .. ':' .. tostring(try(function() return o.TeamMask end))
                end
            end
        end)
        log('map objects: ' .. name .. '=' .. n .. (#masks > 0 and (' teamMasks=' .. table.concat(masks, ',')) or ''))
    end
    -- Map-creator prop and game-object rows: candidates for objective markers.
    local tables = try(StaticFindObject, '/Script/Engine.Default__DataTableFunctionLibrary')
    for _, path in ipairs({'/Game/MapCreator/KovaakMapCreatorPropTable.KovaakMapCreatorPropTable',
                           '/Game/MapCreator/KovaaKMapCreatorGameObjectTable.KovaaKMapCreatorGameObjectTable'}) do
        local dt = try(StaticFindObject, path)
        if valid(tables) and valid(dt) then
            local rows = {}
            local ok = pcall(function() tables:GetDataTableRowNames(dt, rows) end)
            local names = {}
            for i, r in ipairs(rows) do if i <= 200 then names[#names + 1] = str(r) end end
            log('data table ' .. short(dt) .. ': ' .. (ok and (#rows .. ' rows: ' .. table.concat(names, ',')) or 'unreadable'))
        else
            log('data table not loaded: ' .. path)
        end
    end
    local models = {}
    each(FindAllOf('MetaSkeletalCharacterModelDataAsset'), function(o) if valid(o) and #models < 40 then models[#models + 1] = short(o) end end)
    log('character models loaded: ' .. table.concat(models, ','))
end

-- Changes only: health, damage taken/done, kills, deaths, lives, team.
local function deltaReport(pc, gs)
    each(try(function() return gs:GetCharacters() end), function(c)
        if not valid(c) then return end
        local key = tostring(c:GetAddress())
        local now = {
            hp = try(function() return c:GetCurrentHealth() end), taken = try(function() return c.DamageTaken end),
            done = try(function() return c.DamageDone end), kills = try(function() return c.KillCount end),
            deaths = try(function() return c.DeathCount end), lives = try(function() return c.Lives end),
            team = try(function() return c.Team end), hidden = try(function() return c.bHidden end),
        }
        local before = characterState[key]
        characterState[key] = now
        if not before then return end
        local changed = false
        for k, v in pairs(now) do if v ~= before[k] then changed = true end end
        if changed then
            local isPlayer = same(try(function() return pc.MyCharacter end), c)
            log('delta ' .. (isPlayer and 'player ' or 'bot ') .. short(c) .. ' hp=' .. num(before.hp) .. '->' .. num(now.hp)
                .. ' taken=' .. num(now.taken) .. ' done=' .. num(now.done) .. ' kills=' .. tostring(now.kills)
                .. ' deaths=' .. tostring(now.deaths) .. ' lives=' .. tostring(now.lives) .. ' team=' .. tostring(now.team)
                .. ' hidden=' .. tostring(now.hidden))
        end
    end)
end

-- Driver ------------------------------------------------------------------
log('start (read-only). See in-game/docs/game-modes.md, "Live probe".')
inventoryReport()
registerHooks()
local ticks = 0
LoopInGameThreadWithDelay(500, function()
    local ok, err = pcall(function()
        ticks = ticks + 1
        local pc, gs = context()
        if not pc then return end
        local world = try(function() return gs:GetWorld() end)
        local key = world and (tostring(world:GetAddress()) .. ':' .. str(try(function() return gs:GetCurrentMapName() end))) or nil
        local ready = try(function() return gs.bFullyLoaded end) == true and try(function() return gs.bMapLoading end) == false
        if key and ready and key ~= worldKey then
            worldKey = key; characterState = {}
            log('world: map=' .. str(try(function() return gs:GetCurrentMapName() end)) .. ' scale=' .. num(try(function() return gs:GetMapScale() end)))
            worldReport(pc, gs)
            hookReport(true)
        elseif ready then
            deltaReport(pc, gs)
            if ticks % 4 == 0 then hookReport(false) end
        end
    end)
    if not ok then log('tick error: ' .. tostring(err)) end
end)
