<#
.SYNOPSIS
Manual driver for a running AimModSteam: connects to \\.\pipe\aimmod-steam-v1,
sends commands and prints every event (ids shortened, names hidden). For
testing without the native service; the pipe takes one client at a time.

.DESCRIPTION
The lobby stays open after this script exits (Ctrl+C is fine): the bridge
only leaves when told to (-Leave), when the game closes, or on kick.

.EXAMPLE
.\Test-AimModSteamPipe.ps1                          # hello + friends summary, then listen
.\Test-AimModSteamPipe.ps1 -Create                  # friends-only lobby + Steam overlay invite dialog
.\Test-AimModSteamPipe.ps1 -Create -InviteName Bob  # also invite the friend whose name contains "Bob"
.\Test-AimModSteamPipe.ps1 -Join <lobby id>         # join a lobby
.\Test-AimModSteamPipe.ps1 -Leave                   # leave the current lobby
#>
param(
    [switch]$Create,
    [string]$InviteName,
    [string]$Join,
    [switch]$Leave,
    [int]$Seconds = 0  # 0 = until Ctrl+C
)
$ErrorActionPreference = 'Stop'
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'aimmod-steam-v1', [System.IO.Pipes.PipeDirection]::InOut)
try { $pipe.Connect(5000) } catch { throw 'AimModSteam is not running (start KovaaK''s with AimModSteam installed and wait for the main menu).' }
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
        if ($n -le 0) { throw 'pipe closed (game exited?)' }
        $read += $n
    }
    return , $buffer
}
function Clean([string]$text) {
    $text = [regex]::Replace($text, '"(\d{13,16})(\d{4})"', '"...$2"')
    return [regex]::Replace($text, '"(name|fromName)":"[^"]*"', '"$1":"<name>"')
}

Send @{ cmd = 'hello' }
Send @{ cmd = 'friends.list' }
if ($Leave) { Send @{ cmd = 'lobby.leave' } }
if ($Create) { Send @{ cmd = 'lobby.create'; privacy = 'friends'; maxMembers = 4; data = @{ 'aimmod.mode' = 'ghost-demo' } } }
if ($Join) { Send @{ cmd = 'lobby.join'; lobby = $Join } }

$deadline = if ($Seconds -gt 0) { (Get-Date).AddSeconds($Seconds) } else { [datetime]::MaxValue }
$invited = $false
$friends = @()
while ((Get-Date) -lt $deadline) {
    $length = [BitConverter]::ToUInt32((ReadExact 4), 0)
    $text = [Text.Encoding]::UTF8.GetString((ReadExact ([int]$length)))
    $event = $text | ConvertFrom-Json
    if ($event.ev -eq 'friends') {
        $friends = @($event.friends)
        Write-Host "friends: $($friends.Count) total, $(@($friends | Where-Object playing).Count) playing KovaaK's, $(@($friends | Where-Object aimmod).Count) with AimMod"
        continue
    }
    if ($event.ev -in 'avatar', 'p2p.message') { continue }
    Write-Host (Clean $text)
    if ($Leave -and $event.ev -eq 'lobby.left') { break }
    if ($Create -and -not $invited -and $event.ev -eq 'lobby.updated') {
        $invited = $true
        if ($InviteName) {
            $match = @($friends | Where-Object { $_.name -like "*$InviteName*" })
            if ($match.Count -eq 1) { Send @{ cmd = 'lobby.invite'; friend = $match[0].peer }; Write-Host "invited the friend matching '$InviteName' (Steam chat invite)" }
            else { Write-Host "$($match.Count) friends match '$InviteName'; opening the overlay instead"; Send @{ cmd = 'lobby.invite' } }
        }
        else { Send @{ cmd = 'lobby.invite' }; Write-Host 'Steam overlay invite dialog opened in the game (switch to KovaaK''s to pick your friend).' }
    }
}
$pipe.Dispose()
