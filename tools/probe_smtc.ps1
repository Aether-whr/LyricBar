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
$PropT = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties, Windows.Media.Control, ContentType = WindowsRuntime]

$mgr = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) $MgrT
"manager ok: $($mgr -ne $null)"

$sessions = $mgr.GetSessions()
"sessions: $($sessions.Count)"

foreach ($s in $sessions) {
    $app = $s.SourceAppUserModelId
    $props = Await ($s.TryGetMediaPropertiesAsync()) $PropT
    $info = $s.GetPlaybackInfo()
    $tl = $s.GetTimelineProperties()
    "--- app=$app"
    "    title=$($props.Title) | artist=$($props.Artist) | album=$($props.AlbumTitle)"
    "    status=$($info.PlaybackStatus) | pos=$($tl.Position) | end=$($tl.EndTime)"
    "    controls: prev=$($info.Controls.IsPreviousEnabled) play=$($info.Controls.IsPlayPauseToggleEnabled) next=$($info.Controls.IsNextEnabled)"
}
