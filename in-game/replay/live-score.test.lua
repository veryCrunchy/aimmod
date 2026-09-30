local checks, scans, now = 0, 0, 100
local function check(value, label) assert(value, label); checks = checks + 1 end
local function object(id) return {IsValid=function() return true end,GetAddress=function() return id end} end
local instance, other = object(1), object(2)
local score, result, spm = 12.5, 0, 300
local receiver=object(3)
receiver.GetOuter=function() return instance end
receiver.Get_Score_ValueElse=function(_,value,status)
    assert(type(value)=='table' and type(status)=='table' and value==status)
    value.OutValue=score; status.Result=result
end
receiver.Get_ScorePerMinute_ValueElse=function(_,value,status) value.OutValue=spm;status.Result=0 end
local wrong=object(4);wrong.GetOuter=function()return other end
wrong.Get_Score_ValueElse=function() error('wrong game instance read') end
FindAllOf=function(name) assert(name=='PerformanceIndicatorsStateReceiver');scans=scans+1;return{wrong,receiver}end
os.time=function()return now end
local M=dofile('../ue4ss/AimModNativeUI/Scripts/LiveScore.lua')
local value=M.read(instance)
check(value.score==12.5 and value.scorePerMinute==300,'owned getters with named primitive OUT arguments')
receiver.Get_Score_ValueElse=function(_,value,status)
    -- Current runtime stores both primitive OUTs in the first argument table.
    value.OutValue=score; value.Result=result
end
check(M.read(instance).score==12.5,'current runtime first-table OUT behavior supported')
M.read(instance);check(scans==1,'receiver cached')
score=0;check(M.read(instance).score==0,'known zero retained')
score=-5;check(M.read(instance).score==-5,'native penalties retained')
result=1;score=0;check(M.read(instance).score==nil,'Else initialized zero is not measured')
result=0;score=0/0;check(M.read(instance).score==nil,'NaN rejected')
score=math.huge;check(M.read(instance).score==nil,'infinity rejected')
receiver.Get_Score_ValueElse=function()error('unavailable getter')end
check(M.read(instance).score==nil and M.read(instance).scorePerMinute==300,'independent getter failure')
receiver.IsValid=function()return false end
check(M.read(instance).score==nil,'invalid cached receiver does not leak old value')
local before=scans;M.read(instance);check(scans==before,'missing receiver scans throttled')
check(M.read(nil).score==nil,'missing instance safe')
print('Live score: '..checks..' checks passed')
