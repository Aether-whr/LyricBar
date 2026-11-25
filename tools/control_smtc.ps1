# 播放/暂停切换 + 跳过，用来在没有界面点击的情况下驱动播放器（调试/验证用）。
#   .\control_smtc.ps1 play
#   .\control_smtc.ps1 next
#   .\control_smtc.ps1 prev
param([string]$Action = 'play')

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
})[0]

function Await($op, [Type]$rt) {
    $m = $asTaskGeneric.MakeGenericMethod($rt)
    $task = $m.Invoke($null, @($op))
    if (-not $task.Wait(8000)) { throw 'timeout' }
    $task.Result
}

$MgrT = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime]
$mgr = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) $MgrT
$s = $mgr.GetCurrentSession()
if ($null -eq $s) { throw 'no session' }

switch ($Action) {
    'next' { Await ($s.TrySkipNextAsync()) ([bool]) | Out-Null; "next ok" }
    'prev' { Await ($s.TrySkipPreviousAsync()) ([bool]) | Out-Null; "prev ok" }
    default { Await ($s.TryTogglePlayPauseAsync()) ([bool]) | Out-Null; "toggle ok" }
}
