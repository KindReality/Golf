$ErrorActionPreference='Stop'
$compiler=Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\fxc.exe" |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $compiler) { throw 'Install a Windows SDK with fxc to rebuild shaders. Normal builds use the checked-in .cso files.' }
foreach ($stage in @(@('vs_5_0','VSMain','VideoVS.cso'),@('ps_5_0','PSMain','VideoPS.cso'))) {
    & $compiler.FullName /nologo /T $stage[0] /E $stage[1] /Fo (Join-Path $PSScriptRoot ('Shaders\'+$stage[2])) (Join-Path $PSScriptRoot 'Shaders\Video.hlsl')
    if ($LASTEXITCODE -ne 0) { throw 'Shader compilation failed.' }
}
& $compiler.FullName /nologo /T ps_5_0 /E PSMain /Fo (Join-Path $PSScriptRoot 'Shaders\CalibrationPS.cso') (Join-Path $PSScriptRoot 'Shaders\Calibration.hlsl')
if ($LASTEXITCODE -ne 0) {throw 'Calibration shader compilation failed.'}
