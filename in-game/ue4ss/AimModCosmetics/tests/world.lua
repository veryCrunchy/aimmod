-- Synthetic scene: a local player with a viewmodel, a scenario bot, an AimMod
-- avatar, an avatar showing a DLC look, a scenario manager and the material
-- library. All names are invented; no game asset paths beyond class names.
local mock = require('mock')
local C = mock.classes
local W = {}

function W.build()
    local base = mock.object(C.Material, '/Game/Test/M_Base.M_Base', {
        CachedExpressionData = {Parameters = {RuntimeEntries = {
            {ParameterInfos = mock.array({{Name = mock.fname('Roughness')}})},
            {ParameterInfos = mock.array({{Name = mock.fname('Tint')}})},
            {ParameterInfos = mock.array({{Name = mock.fname('BaseColor')}, {Name = mock.fname('Masks')}})},
        }}},
    })
    local function mic(name)
        return mock.object(C.MIC, name, {
            Parent = base,
            TextureParameterValues = mock.array({{ParameterInfo = {Name = mock.fname('Masks')},
                ParameterValue = mock.object(C.Texture, '/Game/Test/T_Masks.T_Masks')}}),
            VectorParameterValues = mock.array({{ParameterInfo = {Name = mock.fname('Tint')},
                ParameterValue = {R = 1, G = 0, B = 0, A = 1}}}),
            ScalarParameterValues = mock.array({}),
        })
    end

    local mids = 0
    local function makeMid(parent)
        mids = mids + 1
        local params = {}
        return mock.object(C.MID, '/Engine/Transient.MID_' .. mids, {Parent = parent,
            TextureParameterValues = mock.array({}), VectorParameterValues = mock.array({}), ScalarParameterValues = mock.array({}),
            __params = params}, {
            SetTextureParameterValue = function(_, n, t) params[n] = t end,
            SetVectorParameterValue = function(_, n, v) params[n] = v end,
            SetScalarParameterValue = function(_, n, v) params[n] = v end,
            K2_GetTextureParameterValue = function(_, n) return params[n] end,
        })
    end

    -- A mesh component with swappable material slots.
    local function meshComponent(class, name, slots)
        return mock.object(class, name, {SkeletalMesh = mock.object(C.Texture, '/Game/Test/SK.SK'), bHiddenInGame = false, __slots = slots}, {
            GetNumMaterials = function() return #slots end,
            GetMaterial = function(_, i) return slots[i + 1] end,
            SetMaterial = function(_, i, m) slots[i + 1] = m end,
            GetMaterialSlotNames = function() return mock.array({mock.fname('Body'), mock.fname('Head')}) end,
            GetAttachParent = function() return nil end,
            GetAttachSocketName = function() return mock.fname('None') end,
            IsVisible = function() return true end,
            GetCollisionEnabled = function() return 0 end,
            GetCollisionProfileName = function() return mock.fname('CharacterMesh') end,
            GetAllSocketNames = function() return mock.array({mock.fname('head'), mock.fname('hand_r')}) end,
        })
    end

    local function character(name, profile, controlled, model, skin)
        local mesh = meshComponent(C.Skeletal, 'CharacterMesh0', {mic('/Game/Test/MI_Body.MI_Body'), mic('/Game/Test/MI_Head.MI_Head')})
        local hitbox = mock.object(C.Static, 'CylHead', {StaticMesh = mock.object(C.Texture, '/Game/Test/Cyl.Cyl')}, {
            GetNumMaterials = function() return 0 end, IsVisible = function() return false end,
            GetCollisionEnabled = function() return 1 end,
        })
        local actor = mock.object({path = '/Game/Test.FPSCharacter_C', parent = nil}, '/Game/Map.Map:PersistentLevel.' .. name,
            {Mesh = mesh, __model = model or 'Meso', __skin = skin or 'McCree'}, {
            GetCharacterProfileName = function() return profile end,
            IsPlayerControlled = function() return controlled end,
            K2_IsUsingMeshHitDetection = function() return false end,
            K2_GetComponentsByClass = function() return mock.array({mesh, hitbox}) end,
        })
        return actor, mesh
    end

    local player, playerMesh = character('Player', 'Local Profile', true)
    local bot, botMesh = character('Bot', 'Scenario Bot', false)
    local avatar, avatarMesh = character('Avatar', 'AimMod Meso McCree', false)
    -- An avatar showing a viewer-equipped DLC skin: must never be altered.
    local paid, paidMesh = character('Paid', 'AimMod Meso Tracer', false, 'AnimeGirl', 'Variant_1')

    local weaponMesh = meshComponent(C.Skeletal, 'LawBringer', {mic('/Game/Test/MI_Gun.MI_Gun')})
    local armsMesh = meshComponent(C.Skeletal, 'FPSPlayer', {mic('/Game/Test/MI_Arms.MI_Arms')})
    local shotOrigin = meshComponent(C.Static, 'ShotOrigin', {mic('/Game/Test/MI_Shot.MI_Shot')})
    local view = mock.object({path = '/Game/Test.FPSPlayer_WeaponComponent_C'}, '/Game/Map.Map:PersistentLevel.View', {}, {
        GetSelectedWeaponModelName = function() return mock.fname('Pistol') end,
        GetSelectWeaponMesh = function() return weaponMesh end,
        GetFPSPlayerSkeletalMeshComponent = function() return armsMesh end,
        K2_GetComponentsByClass = function() return mock.array({weaponMesh, armsMesh, shotOrigin}) end,
    })
    player.ViewModel_Native = view
    local pc = mock.object({path = '/Game/Test.PC_C'}, '/Game/Map.Map:PersistentLevel.PC', {MyCharacter = player}, {
        IsLocalPlayerController = function() return true end,
    })

    local self = {
        midNames = {},
        all = {MetaCharacter = {player, bot, avatar, paid}, MetaPlayerController = {pc}},
        player = player, bot = bot, avatar = avatar, paid = paid,
        playerMesh = playerMesh, botMesh = botMesh, avatarMesh = avatarMesh, paidMesh = paidMesh,
        weaponMesh = weaponMesh, armsMesh = armsMesh, shotOrigin = shotOrigin,
        packsLoaded = true,
        -- Scenario state the tests change.
        game = {scenario = 'Tile Frenzy', inChallenge = false, benchmark = false, editor = false, loading = false},
    }

    local scenario = mock.object({path = '/Script/GameSkillsTrainer.Scenario'}, '/Engine/Transient.Scenario', {}, {
        GetName = function() return mock.fname(self.game.scenario) end,
        IsInChallenge = function() return self.game.inChallenge end,
    })
    local manager = mock.object({path = '/Script/GameSkillsTrainer.ScenarioManager'}, '/Engine/Transient.ScenarioManager_1', {}, {
        GetCurrentScenario = function() return scenario end,
        IsInChallenge = function() return self.game.inChallenge end,
        IsCurrentlyInBenchmark = function() return self.game.benchmark end,
        IsInScenarioEditor = function() return self.game.editor end,
        IsScenarioLoading = function() return self.game.loading end,
    })
    self.all.ScenarioManager = {manager}

    local materialLib = mock.object({path = '/Script/Engine.KismetMaterialLibrary'}, '/Script/Engine.Default__KismetMaterialLibrary', {}, {
        CreateDynamicMaterialInstance = function(_, owner, parent, name)
            self.midNames[#self.midNames + 1] = {owner = owner, name = name}
            return makeMid(parent)
        end,
    })

    local function getter(field)
        return setmetatable({IsValid = function() return true end}, {__call = function(_, actor) return actor[field] end})
    end
    local Object = {path = '/Script/CoreUObject.Object'}
    local modelPack = mock.object(Object, 'Default_CharacterModelPack', {Models = mock.array({
        {CharacterModel = {Name = mock.fname('Meso')}}, {CharacterModel = {Name = mock.fname('Endo')}}})})
    local skinPack = mock.object(Object, 'Default_CharacterSkinPack', {Skins = mock.array({
        {CharacterSkin = {Name = mock.fname('McCree')}}, {CharacterSkin = {Name = mock.fname('Tracer')}}})})
    self.find = function(path)
        if path:find('CharacterModelInterface:GetCharacterModelName', 1, true) then return getter('__model') end
        if path:find('CharacterModelInterface:GetCharacterSkinName', 1, true) then return getter('__skin') end
        if self.packsLoaded and path:find('Default_CharacterModelPack', 1, true) then return modelPack end
        if self.packsLoaded and path:find('Default_CharacterSkinPack', 1, true) then return skinPack end
        if path == '/Script/Engine.Default__KismetMaterialLibrary' then return materialLib end
        return nil
    end
    return self
end

return W
