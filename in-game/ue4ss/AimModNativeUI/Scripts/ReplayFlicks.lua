local M={}
local function finite(n)return type(n)=='number' and n==n and math.abs(n)<1e9 end
function M.parse(data,id)
    if type(data)~='string' or #data>16000000 or data:sub(-1)~='\n' then return {} end
    local found,duration,body=data:match('^AIMMOD_FLICKS_1\t([%w_-]+)\t([^\n]+)\n(.*)$')
    duration=tonumber(duration)
    if found~=id or not finite(duration) or duration<=0 then return {} end
    local result={}
    for line in body:gmatch('([^\n]+)\n')do
        local a,b,e,r,i=line:match('^([^\t]+)\t([^\t]+)\t([^\t]+)\t([^\t]+)\t([^\t]+)$')
        a,b,e=tonumber(a),tonumber(b),tonumber(e)
        if not finite(a) or not finite(b) or not finite(e) or a<0 or b<a or b>duration+.01 or #result>=2000 then return {} end
        local function points(raw)
            local out={}
            for pair in (raw or ''):gmatch('[^;]+')do
                local x,y=pair:match('^([^,]+),([^,]+)$');x,y=tonumber(x),tonumber(y)
                if not finite(x) or not finite(y) or #out>=65 then return nil end
                out[#out+1]={x,y}
            end
            return #out>=2 and out or nil
        end
        r,i=points(r),points(i)
        if not r or not i then return {} end
        result[#result+1]={time=a,finish=b,efficiency=e,recorded=r,ideal=i}
    end
    return result,duration
end
function M.create(id,make,place,text,canvas,button,send)
    local file=io.open((os.getenv('LOCALAPPDATA') or '')..'/AimMod/KovaaksNative/replay-flicks.tsv','rb')
    local paths,duration={}
    if file then local data=file:read(16000001);file:close();paths,duration=M.parse(data,id)end
    local panel=make('CanvasPanel');panel:SetVisibility(2);place(canvas,panel,24,120,340,240)
    -- A backdrop keeps the path readable over bright maps.
    local backdrop=make('Border');backdrop:SetVisibility(3);backdrop:SetPadding({Left=0,Top=0,Right=0,Bottom=0});backdrop:SetBrushColor({R=.009,G=.024,B=.018,A=.88});place(panel,backdrop,-12,-10,364,260)
    local caption=text('',14);place(panel,caption,0,0,340,24)
    local legend=text('Recorded: green   Ideal: amber',11);place(panel,legend,0,218,340,20)
    local lines={}
    for k=1,128 do local line=make('Border');line:SetVisibility(2);line:SetPadding({Left=0,Top=0,Right=0,Bottom=0});place(panel,line,0,0,1,2);lines[k]=line end
    local enabled=true;local previous
    button('Flick paths',716,136,function()enabled=not enabled;previous=nil;panel:SetVisibility(2)end)
    -- Markers use the HUD's registered button routes and its existing scrubber coordinates.
    local bins={}
    local marker={normal={R=.62,G=.38,B=.06,A=1},hover={R=1,G=.72,B=.2,A=1},pressed={R=.42,G=.25,B=.04,A=1},disabled={R=.2,G=.14,B=.05,A=.55}}
    for index,f in ipairs(paths)do
        local bin=math.floor(f.time/duration*124)
        if not bins[bin]then
            bins[bin]=true
            button('',25+bin*8,6,function()send('flick-seek',index-1)end,false,66,12,marker)
        end
    end
    return function(state)
        local selected
        if enabled and not state.playing then
            for _,f in ipairs(paths)do if state.time>=f.time-.001 and state.time<=f.finish+.1 then selected=f end end
        end
        if selected==previous then return end
        previous=selected;panel:SetVisibility(selected and 3 or 2)
        if not selected then return end
        caption:SetText(FText(string.format('Flick efficiency: %.0f%%',selected.efficiency*100)))
        local extent=1
        for _,points in ipairs({selected.recorded,selected.ideal})do for _,p in ipairs(points)do extent=math.max(extent,math.abs(p[1]),math.abs(p[2]))end end
        local scale=85/extent;local k=0
        for series,points in ipairs({selected.recorded,selected.ideal})do
            for j=2,#points do
                k=k+1;local line=lines[k];local a,b=points[j-1],points[j]
                local dx,dy=(b[1]-a[1])*scale,-(b[2]-a[2])*scale
                local slot=line.Slot;slot:SetOffsets({Left=165+a[1]*scale,Top=120-a[2]*scale,Right=math.sqrt(dx*dx+dy*dy),Bottom=2})
                line:SetRenderTransformPivot({X=0,Y=.5});line:SetRenderTransformAngle(math.atan(dy,dx)*180/math.pi)
                line:SetBrushColor(series==1 and {R=.04,G=.82,B=.49,A=1} or {R=1,G=.65,B=.15,A=1});line:SetVisibility(3)
            end
        end
        for j=k+1,#lines do lines[j]:SetVisibility(2)end
    end
end
return M
