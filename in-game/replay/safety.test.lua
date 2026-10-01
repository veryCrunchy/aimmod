local hooks, writes = {}, {}
local registrationCount = 0
StaticFindObject = function(path)
    return {IsValid=function() return not path:find('ExperimentsUpload', 1, true) end}
end
RegisterHook = function(path, pre, post)
    registrationCount = registrationCount + 1
    hooks[path] = {pre=pre, post=post}
end
local oldGetenv = os.getenv
os.getenv = function(key) assert(key == 'LOCALAPPDATA'); return 'synthetic-local' end
io.open = function(path, mode)
    assert(path == 'synthetic-local/AimMod/KovaaksNative/native-replay-safety.json' and mode == 'wb')
    return {write=function(_, body) writes[#writes+1]=body; return true end, close=function() return true end}
end
local probe = dofile('../ue4ss/AimModNativeUI/Scripts/ReplaySafetyProbe.lua')
assert(registrationCount == 0 and #writes == 0, 'requiring module must not enable probe')
probe.start(); probe.start()
assert(registrationCount == 13, 'idempotent start registers only available hooks')
local before, saved = probe.snapshot()
assert(saved and before.started and not before.counters.experiments_upload.available)
local sentinel = setmetatable({}, {
    __index=function() error('hook read an argument') end,
    __newindex=function() error('hook changed an argument') end,
    __tostring=function() error('hook converted an argument') end
})
for _, hook in pairs(hooks) do
    assert(hook.pre(sentinel, sentinel, 42, 'private-value') == nil)
    assert(hook.post(sentinel, sentinel, 42, 'private-value') == nil)
end
assert(#writes == 1, 'hooks must not perform file I/O')
local after = probe.snapshot()
local count = 0
for key, value in pairs(after.counters) do
    assert(before.counters[key].count == 0, 'snapshots must not alias live counters')
    assert(value.count == (value.available and 1 or 0))
    count = count + value.count
end
assert(count == 13 and #writes[2] < 8192 and not writes[2]:find('private-value',1,true))
after.counters.stats_saved.count = 999
local fresh = probe.snapshot(); assert(fresh.counters.stats_saved.count == 1, 'snapshot mutation must not change counters')
io.open = function() return nil end
local retained, failed = probe.snapshot(); assert(not failed and retained.counters.stats_saved.count == 1)
os.getenv = oldGetenv
print('Replay safety probe checks passed: opt-in, stable counters, missing hook, nil returns, opaque unchanged arguments, bounded snapshots, I/O failure.')
