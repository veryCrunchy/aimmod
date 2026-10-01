package.path = '../Scripts/?.lua;' .. package.path
local Cat = require('CosmeticsCatalog')

-- The shipped catalog is structurally valid. Parameter items use only the
-- parameter names the probe found; pak items stay drafts until the pak ships.
local probed = {
    body = {vector={MetalPaint=true, TriangularPaint=true, RawMetal=true, Silicone=true}, scalar={Roughness=true, Metallic=true}},
    weapon = {vector={AccentColor=true, Emissive=true}, scalar={}},
}
local byId, errors = Cat.index()
assert(#errors == 0, table.concat(errors, '; '))
local count, ready = 0, 0
for _, item in pairs(byId) do
    count = count + 1
    if item.pak then assert(item.draft, item.id .. ': pak items stay drafts until the pak ships') end
    if not item.draft then
        ready = ready + 1
        for _, part in ipairs(item.parts) do
            for name in pairs(item.vector or {}) do assert(probed[part].vector[name], item.id .. ': ' .. name .. ' is not a probed ' .. part .. ' parameter') end
            for name in pairs(item.scalar or {}) do assert(probed[part].scalar[name], item.id .. ': ' .. name .. ' is not a probed ' .. part .. ' parameter') end
        end
    end
    for _, model in ipairs(item.models or {}) do
        assert(model == 'Meso' or model == 'Endo', item.id .. ': base models must be free Default-pack models')
    end
    for _, field in ipairs({'file', 'path', 'url', 'texture'}) do assert(item[field] == nil, item.id .. ' references ' .. field) end
end
assert(count == #Cat.items)
assert(ready >= 6 and #Cat.pickable(byId) == ready, 'parameter items are offered in the picker, drafts are not')

-- Resolution: unknown ids, drafts and unverified pak items fall back.
assert(Cat.resolve(byId, 'not-an-item') == nil)
assert(Cat.resolve(byId, '') == nil and Cat.resolve(byId, nil) == nil)
assert(Cat.resolve(byId, 'tint-mint').id == 'tint-mint', 'a shipped parameter item resolves')
local drafts = Cat.index({{id='d-1', version=1, kind='avatar_tint', models={'Meso'}, parts={'body'}, vector={MetalPaint={R=1, G=0, B=0, A=1}}, draft=true}})
assert(Cat.resolve(drafts, 'd-1') == nil, 'draft hidden by default')
assert(Cat.resolve(drafts, 'd-1', {allowDrafts=true}).id == 'd-1')
assert(Cat.resolve(byId, 'accessory-visor', {allowDrafts=true}) == nil, 'pak item needs a verified pak')
assert(Cat.resolve(byId, 'accessory-visor', {allowDrafts=true, verifiedPaks={['AimModCosmetics-1.pak']=true}}).id == 'accessory-visor')

-- Validation.
local good = {id='t-1', version=1, kind='avatar_tint', models={'Meso'}, parts={'body'}, vector={Tint={R=1, G=0, B=0, A=1}}}
assert(Cat.validate(good))
local function bad(change, why)
    local item = {}
    for k, v in pairs(good) do item[k] = v end
    for k, v in pairs(change) do item[k] = v end
    assert(not Cat.validate(item), why)
end
bad({id='Bad Id'}, 'id format')
bad({id='../x'}, 'id format')
bad({version=0}, 'version')
bad({kind='user_texture'}, 'unknown kind')
bad({parts={'weapon'}}, 'part not allowed for kind')
bad({models={}}, 'avatar items need models')
bad({vector={Tint={R=2, G=0, B=0, A=1}}}, 'colour range')
bad({vector={}}, 'no parameters')
bad({scalar={Rough=0 / 0}, vector={}}, 'NaN scalar')
assert(not Cat.validate({id='p', version=1, kind='accessory', models={'Meso'}, parts={'body'}}), 'pak kinds need a pak')
local _, dupErrors = Cat.index({good, good})
assert(#dupErrors == 1)
local dupIndex = Cat.index({good, good})
assert(dupIndex['t-1'] == nil, 'duplicate ids are dropped')
print('catalog.test.lua ok')
