param([string[]]$Names = @('Camera','Experience','ExperienceX'))
$ErrorActionPreference = 'Stop'
if (-not ('PhotoBoothCloseWindows' -as [type])) {
    Add-Type @'
using System; using System.Runtime.InteropServices;
public static class PhotoBoothCloseWindows {
 public delegate bool Callback(IntPtr hwnd,IntPtr context);
 [DllImport("user32.dll")] public static extern bool EnumWindows(Callback callback,IntPtr context);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint id);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr w,IntPtr l);
}
'@
}
foreach ($application in @(Get-Process -Name $Names -ErrorAction SilentlyContinue)) {
    $applicationId = $application.Id
    [void][PhotoBoothCloseWindows]::EnumWindows({ param($hwnd,$context)
        [uint32]$ownerId=0
        [void][PhotoBoothCloseWindows]::GetWindowThreadProcessId($hwnd,[ref]$ownerId)
        if ($ownerId -eq $applicationId) { [void][PhotoBoothCloseWindows]::PostMessage($hwnd,0x10,0,0) }
        return $true
    },0)
    if (-not $application.WaitForExit(10000)) { throw "$($application.ProcessName) did not exit gracefully. Build cancelled." }
}
