<#
.SYNOPSIS
Manual check of a running AimModSteam: connects to \\.\pipe\aimmod-steam-v1,
sends commands and prints every event. For testing without the native service
(close the service first, since the pipe takes one client at a time).

.EXAMPLE
.\Test-AimModSteamPipe.ps1                       # hello + friends list, then listen
.\Test-AimModSteamPipe.ps1 -Create               # also create a friends-only lobby and open the invite overlay
.\Test-AimModSteamPipe.ps1 -Join <lobby id>      # join a lobby (for example from a join.requested event)
#>
param(
    [switch]$Create,
    [string]$Join,
    [int]$Seconds = 120
)
$ErrorActionPreference = 'Stop'
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'aimmod-steam-v1', [System.IO.Pipes.PipeDirection]::InOut)
$pipe.Connect(5000)
$script:next = 1

function Send([hashtable]$command) {
    $command['v'] = 1
    $command['id'] = $script:next++
    $bytes = [Text.Encoding]::UTF8.GetBytes(($command | ConvertTo-Json -Compress -Depth 5))
    $pipe.Write([BitConverter]::GetBytes([uint32]$bytes.Length), 0, 4)
    $pipe.Write($bytes, 0, $bytes.Length)
    $pipe.Flush()
}
function ReadExact([int]$count) {
    $buffer = New-Object byte[] $count
    $read = 0
    while ($read -lt $count) {
        $n = $pipe.Read($buffer, $read, $count - $read)
        if ($n -le 0) { throw 'pipe closed' }
        $read += $n
    }
    return , $buffer
}
function Redact([string]$text) { return [regex]::Replace($text, '"(\d{13})(\d{4})"', '"...$2"') }

Send @{ cmd = 'hello' }
Send @{ cmd = 'friends.list' }
if ($Create) { Send @{ cmd = 'lobby.create'; privacy = 'friends'; maxMembers = 4; data = @{ 'aimmod.mode' = 'score-race' } } }
if ($Join) { Send @{ cmd = 'lobby.join'; lobby = $Join } }

$deadline = (Get-Date).AddSeconds($Seconds)
$invited = $false
while ((Get-Date) -lt $deadline) {
    $length = [BitConverter]::ToUInt32((ReadExact 4), 0)
    $text = [Text.Encoding]::UTF8.GetString((ReadExact ([int]$length)))
    $event = $text | ConvertFrom-Json
    if ($event.ev -eq 'friends') { Write-Host "friends: $($event.friends.Count) ($(@($event.friends | Where-Object aimmod).Count) with AimMod)"; continue }
    if ($event.ev -eq 'avatar') { Write-Host "avatar: $($event.w)x$($event.h)"; continue }
    Write-Host (Redact $text)
    if ($Create -and -not $invited -and $event.ev -eq 'lobby.updated') { Send @{ cmd = 'lobby.invite' }; $invited = $true }
}
Send @{ cmd = 'lobby.leave' }
Start-Sleep -Milliseconds 300
$pipe.Dispose()
