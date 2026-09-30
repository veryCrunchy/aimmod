-- Presentation only: cached audio assets, never weapon hit/fire or scoring APIs.
local M={}
local function valid(o)local ok,v=pcall(function()return o:IsValid()end);return ok and v end
local function finite(n)return type(n)=='number' and n==n and math.abs(n)<1e12 end
local function integer(n)return finite(n) and n>=0 and n==math.floor(n)end
local function unwrap(v)if valid(v)then return v end;local ok,o=pcall(function()return v:get()end);return ok and o end
function M.create(owner)
    local s={closed=false};local previous,lastHits,lastSound,api,world,controller
    local sounds={};local soundIndex=0;local cooldown=0.06;local settings;local blocked=false
    pcall(function()
        local settingsAPI=StaticFindObject('/Script/GameSkillsTrainer.Default__MetaGameUserSettings')
        settings=settingsAPI:Get()
    end)
    -- Resolve once per scene, never scan all weapons/assets on replay ticks.
    pcall(function()
        assert(valid(owner));world=owner:GetWorld()
        api=StaticFindObject('/Script/Engine.Default__GameplayStatics')
        controller=api:GetPlayerController(owner,0)
        pcall(function()
            local instance=api:GetGameInstance(owner)
            local function inspect(manager)
                manager=unwrap(manager)
                if valid(manager) and manager:GetOuter():GetAddress()==instance:GetAddress()then
                    local scenario=manager:GetCurrentScenario()
                    if valid(scenario)then blocked=scenario:GetChallengeProfile().BlockHitSounds==true end
                end
            end
            local managers=FindAllOf('ScenarioManager') or {}
            if type(managers)=='table' and not managers.ForEach then for _,manager in ipairs(managers)do inspect(manager)end
            else managers:ForEach(function(_,manager)inspect(manager)end)end
        end)
        local character=controller.MyCharacter;assert(valid(character))
        local weapon=character:GetCurrentWeapon();assert(valid(weapon))
        local soundClass=StaticFindObject('/Script/Engine.SoundWave');assert(valid(soundClass))
        local function add(v)
            v=unwrap(v)
            if #sounds<16 and valid(v) and v:IsA(soundClass)then sounds[#sounds+1]=v end
        end
        local collection=weapon.CachedBodyHitSounds
        if type(collection)=='table' and type(collection.ForEach)~='function'then
            for _,v in ipairs(collection)do add(v)end
        else collection:ForEach(function(_,v)add(v)end)end
        -- Events do not identify headshots; never invent a head-hit sound.
        local configured=weapon.WeaponSettingsNative.HitSoundCooldown
        if finite(configured) and configured>=0 then cooldown=math.max(cooldown,math.min(configured,10))end
    end)
    local function play(time,speed)
        if blocked then return end
        if lastSound and (time-lastSound)/speed<cooldown then return end
        lastSound=time
        pcall(function()
            assert(valid(owner) and valid(world) and valid(api) and valid(controller))
            assert(owner:GetWorld():GetAddress()==world:GetAddress())
            assert(controller:GetWorld():GetAddress()==world:GetAddress())
            if #sounds==0 then return end
            soundIndex=soundIndex%#sounds+1;local sound=sounds[soundIndex]
            if not valid(sound)then return end
            if not valid(settings)then return end
            local volume,pitch
            do
                -- Native HitSound multiplies raw MasterVolume * HitVolume;
                -- HitPitch is also a direct multiplier, not percent or semitones.
                -- Dynamic damage/headshot pitch modifiers are not recorded yet.
                local master=settings:GetFloatUserSetting(19)
                local hit=settings:GetFloatUserSetting(26)
                pitch=settings:GetFloatUserSetting(27)
                if not finite(master) or not finite(hit) or not finite(pitch) or master<0 or hit<0 or pitch<=0 then return end
                volume=master*hit
                if not finite(volume) or volume<=0 then return end
            end
            -- Preserve the cached sound's class/mix. UI audio remains audible in
            -- the deliberately paused game world; never change global volume.
            api:PlaySound2D(owner,sound,volume,pitch,0,nil,controller,true)
        end)
    end
    function s.update(frame)
        if s.closed then return false end
        local t=type(frame)=='table' and frame.time
        if type(t)~='table' or not finite(t.time) or not integer(frame.transport)then
            previous=nil;lastHits=nil;lastSound=nil;return false
        end
        local p=previous
        previous={time=t.time,playing=t.playing==true,transport=frame.transport}
        local hit=frame.hit
        local known=type(hit)=='table' and finite(hit.time) and hit.time>=0 and integer(hit.hits) and integer(hit.delta) and hit.delta>0 and hit.delta<=hit.hits
        local speed=finite(t.speed) and math.max(0.25,math.min(t.speed,2)) or 1
        -- Transport generations change on load, pause, seek and speed commands.
        -- Rebaseline silently; no old hit may burst after any discontinuity.
        if not p or p.transport~=frame.transport or not p.playing or t.playing~=true or t.time<p.time or t.time-p.time>0.5*speed then
            lastHits=known and hit.hits or nil;lastSound=nil;return false
        end
        if not known then return false end
        if lastHits and hit.hits<=lastHits then return false end
        lastHits=hit.hits
        if hit.time<=p.time or hit.time>t.time then return false end
        play(t.time,speed)
        return true -- A confirmed crossed hit also drives the visual indicator.
    end
    function s.close()s.closed=true;previous=nil;lastHits=nil;sounds={};api=nil;controller=nil;world=nil;settings=nil end
    return s
end
return M
