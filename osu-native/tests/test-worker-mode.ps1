[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $AimModExecutable
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$executable = (Resolve-Path -LiteralPath $AimModExecutable).Path
if (-not [System.IO.File]::Exists($executable)) {
    throw "AimMod executable is missing: $AimModExecutable"
}

$requests = @(
    @{ id = '11111111-1111-1111-1111-111111111111'; protocolVersion = 1; command = 'hello' },
    @{ id = '22222222-2222-2222-2222-222222222222'; protocolVersion = 1; command = 'shutdown' }
)
$inputLines = (($requests | ForEach-Object { $_ | ConvertTo-Json -Compress }) -join "`n") + "`n"

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $executable
$startInfo.ArgumentList.Add('--worker')
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo
try {
    if (-not $process.Start()) {
        throw 'The AimMod worker process did not start.'
    }
    $process.StandardInput.Write($inputLines)
    $process.StandardInput.Close()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()

    if (-not $process.WaitForExit(15000)) {
        $process.Kill($true)
        throw 'The AimMod worker did not exit within 15 seconds.'
    }

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) {
        throw "Worker exited with $($process.ExitCode): $stderr"
    }

    $lines = @($stdout -split "`r?`n" | Where-Object { $_.Length -gt 0 })
    if ($lines.Count -ne 2) {
        throw "Worker stdout was not protocol-only: $stdout"
    }

    $responses = @($lines | ForEach-Object { $_ | ConvertFrom-Json })
    for ($index = 0; $index -lt $requests.Count; $index++) {
        if ($responses[$index].id -ne $requests[$index].id -or $responses[$index].success -ne $true) {
            throw "Worker protocol smoke test failed: $stdout"
        }
    }
}
finally {
    if (-not $process.HasExited) {
        $process.Kill($true)
    }
    $process.Dispose()
}

Write-Host 'Single-apphost worker mode passed.'
