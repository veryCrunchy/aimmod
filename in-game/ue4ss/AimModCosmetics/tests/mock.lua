-- Minimal synthetic UE4SS object model for tests. Objects record every
-- method call so tests can assert which engine functions were used.
local M = {calls = {}}

local classes = {}
local function class(path, parent)
    local c = {path = path, parent = parent}
    classes[path] = c
    return c
end
local Object = class('/Script/CoreUObject.Object')
local Material = class('/Script/Engine.Material', Object)
local MaterialInstance = class('/Script/Engine.MaterialInstance', Object)
local MIC = class('/Script/Engine.MaterialInstanceConstant', MaterialInstance)
local MID = class('/Script/Engine.MaterialInstanceDynamic', MaterialInstance)
local MeshComponent = class('/Script/Engine.MeshComponent', Object)
local Skinned = class('/Script/Engine.SkinnedMeshComponent', MeshComponent)
local Skeletal = class('/Script/Engine.SkeletalMeshComponent', Skinned)
local StaticMeshComponent = class('/Script/Engine.StaticMeshComponent', MeshComponent)
local Texture = class('/Script/Engine.Texture2D', Object)
M.classes = {Material=Material, MIC=MIC, MID=MID, Skeletal=Skeletal, Static=StaticMeshComponent, Texture=Texture}

local nextAddress = 1000
local function isA(obj, c)
    local k = obj.__class
    while k do if k == c then return true end; k = k.parent end
    return false
end

-- Wrap a TArray-like list.
function M.array(items)
    return {ForEach = function(self, fn) for i, v in ipairs(items) do fn(i, {get = function() return v end}) end end}
end

local function fname(s) return {ToString = function() return s end} end
M.fname = fname

function M.object(c, name, fields, methods)
    nextAddress = nextAddress + 1
    local obj = {__class = c, __name = name, __address = nextAddress}
    for k, v in pairs(fields or {}) do obj[k] = v end
    local base = {
        IsValid = function() return true end,
        GetFullName = function() return c.path:match('[^.]+$') .. ' ' .. name end,
        GetFName = function() return fname(name:match('[^.:]+$')) end,
        GetClass = function() return {GetFName = function() return fname(c.path:match('[^.]+$')) end} end,
        IsA = function(_, k) return isA(obj, k) end,
        GetAddress = function() return obj.__address end,
    }
    for k, v in pairs(methods or {}) do base[k] = v end
    return setmetatable(obj, {__index = function(_, key)
        local fn = base[key]
        if fn == nil then return nil end
        return function(_, ...)
            M.calls[#M.calls + 1] = key
            return fn(obj, ...)
        end
    end})
end

function M.install(world)
    M.calls = {}
    _G.StaticFindObject = function(path)
        if classes[path] then
            local c = classes[path]
            return setmetatable({}, {__index = function(_, k)
                if k == 'IsValid' then return function() return true end end
                if k == '__class_ref' then return c end
            end, __eq = function() return false end, __class = c})
        end
        return world.find and world.find(path)
    end
    _G.FindAllOf = function(name) return world.all[name] or {} end
    _G.FName = function(s) return s end
end

-- StaticFindObject returns a proxy; resolve proxies for IsA.
local rawIsA = isA
isA = function(obj, k)
    if type(k) == 'table' and getmetatable(k) and getmetatable(k).__class then k = getmetatable(k).__class end
    return rawIsA(obj, k)
end

return M
