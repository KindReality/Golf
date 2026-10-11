# Shared by both restart scripts. Stage and verify before stopping a working app.
function New-VerifiedApplicationStage([string]$Source, [string]$Application) {
    $manifestName = 'build-manifest.sha256'
    $manifest = [IO.File]::ReadAllText((Join-Path $Source $manifestName))
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    $stage = [IO.Path]::GetFullPath((Join-Path $temporaryRoot ('HomeTechnologies-Update-' + [Guid]::NewGuid().ToString('N'))))
    if ([IO.Path]::GetDirectoryName($stage) -ine $temporaryRoot) { throw 'Invalid update staging folder.' }
    [void][IO.Directory]::CreateDirectory($stage)
    $complete = $false
    try {
        $names = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($line in $manifest.Split([char]10)) {
            if (!$line.Trim()) { continue }
            $entry = $line.TrimEnd([char]13).Split([char]'|')
            if ($entry.Count -ne 2 -or $entry[0] -notmatch '^[A-Fa-f0-9]{64}$' -or
                [string]::IsNullOrWhiteSpace($entry[1]) -or [IO.Path]::IsPathRooted($entry[1]) -or $entry[1].Contains(':')) { throw 'Invalid build manifest.' }
            $relative = $entry[1]
            $inputPath = [IO.Path]::GetFullPath((Join-Path $Source $relative))
            $outputPath = [IO.Path]::GetFullPath((Join-Path $stage $relative))
            if (!$inputPath.StartsWith($Source.TrimEnd('\')+'\', [StringComparison]::OrdinalIgnoreCase) -or
                !$outputPath.StartsWith($stage+'\', [StringComparison]::OrdinalIgnoreCase) -or !$names.Add($relative)) { throw 'Invalid build manifest path.' }
            # Working configuration and media must never enter an update package.
            if ($relative -match '^(Media|logs|Configuration)(\\|/|$)' -or $relative -ieq 'appsettings.json' -or
                ($relative -ilike 'appsettings.*' -and $relative -notin @('appsettings.defaults.json','appsettings.environment.json'))) { throw 'Build manifest contains local user data.' }
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath))
            [IO.File]::Copy($inputPath, $outputPath, $false)
            if ((Get-FileSha256 -LiteralPath $outputPath) -ine $entry[0]) { throw "Build is still syncing: $relative. Existing app was not stopped." }
        }
        $restart = if ($Application -eq 'Controller') { 'Restart-Controller.ps1' } else { 'Restart-Experience.ps1' }
        foreach ($required in @(($Application+'.exe'), ($Application+'.dll'), ($Application+'.deps.json'), ($Application+'.runtimeconfig.json'), 'appsettings.defaults.json', $restart, 'Stage-ApplicationUpdate.ps1')) {
            if (!$names.Contains($required)) { throw "Build manifest is missing $required." }
        }
        if ($Application -eq 'Controller' -and !$names.Contains('appsettings.environment.json')) { throw 'Controller environment is missing.' }
        if ([IO.File]::ReadAllText((Join-Path $Source $manifestName)) -cne $manifest) { throw 'Build changed during staging. Existing app was not stopped.' }
        [IO.File]::WriteAllText((Join-Path $stage $manifestName), $manifest)
        $complete = $true
        return $stage
    } finally {
        if (!$complete -and [IO.Path]::GetDirectoryName($stage) -ieq $temporaryRoot -and (Test-Path -LiteralPath $stage)) {
            Remove-Item -LiteralPath $stage -Recurse -Force
        }
    }
}
