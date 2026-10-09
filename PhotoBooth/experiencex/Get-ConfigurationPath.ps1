function Get-ExperienceXConfigurationPath {
    $localRoot=[Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if([string]::IsNullOrWhiteSpace($localRoot)){throw 'The local application data folder is unavailable.'}
    Join-Path $localRoot 'Home Technologies\ExperienceX\Configuration\appsettings.json'
}
