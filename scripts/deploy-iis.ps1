<#
.SYNOPSIS
    Configures IIS to host a published SignalLedger build (in-process, AlwaysRunning, preloaded).

.DESCRIPTION
    Run on the target Windows Server as Administrator after `scripts/publish.ps1` has produced the artifact.
    The script is idempotent: re-running it converges the application pool, site/application, preload,
    Application Initialization, environment variables and folder permissions to the requested values.
    Use -WhatIf to preview every change without applying it.

    It does NOT install Windows features or the .NET Hosting Bundle, and it never touches the SQLite file
    other than granting the application pool identity access to its directory.

    Prerequisites (see docs/IIS_DEPLOYMENT.md):
      - IIS with the "Application Initialization" feature (Web-AppInit)
      - .NET 10 ASP.NET Core Hosting Bundle (AspNetCoreModuleV2)
      - The WebAdministration PowerShell module (installed with IIS management tools)

.EXAMPLE
    .\deploy-iis.ps1 -SiteName SignalLedger -PhysicalPath C:\SignalLedger\app -DataDirectory C:\SignalLedger\data `
        -LogDirectory C:\SignalLedger\logs -SourcePath .\artifacts\publish -HostName signalledger.example.org -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z0-9 ._-]{1,64}$')][string]$SiteName,
    [ValidatePattern('^[A-Za-z0-9._-]{1,64}$')][string]$AppPoolName = 'SignalLedger',
    [Parameter(Mandatory = $true)][string]$PhysicalPath,
    [Parameter(Mandatory = $true)][string]$DataDirectory,
    [string]$LogDirectory = '',
    # Optional: published output to copy into PhysicalPath (app_offline.htm is used during the copy).
    [string]$SourcePath = '',
    # Optional: host the app as /<ApplicationAlias> under an existing site instead of creating a site.
    [ValidatePattern('^$|^[A-Za-z0-9._-]{1,64}$')][string]$ApplicationAlias = '',
    [string]$HostName = '',
    [ValidateRange(1, 65535)][int]$Port = 80,
    [ValidateSet('ApplicationPoolIdentity', 'NetworkService', 'LocalService')][string]$IdentityType = 'ApplicationPoolIdentity',
    [ValidateSet('Production', 'Staging', 'Development')][string]$AspNetCoreEnvironment = 'Production',
    [string]$WarmupPath = '/health',
    # Disable the default 29-hour periodic recycle (recommended for the background download worker).
    [switch]$DisablePeriodicRecycle,
    # Extra app settings as environment variables, e.g. @{ 'SignalLedger__PublicDemoMode' = 'false' }.
    [hashtable]$EnvironmentVariables = @{}
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script from an elevated (Administrator) PowerShell session.' }
}

function Assert-Prerequisites {
    Import-Module WebAdministration -ErrorAction Stop
    $ancm = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
    if (-not (Test-Path -LiteralPath $ancm)) { throw "ASP.NET Core Module V2 not found ($ancm). Install the .NET 10 Hosting Bundle." }
    $appInit = Join-Path $env:windir 'System32\inetsrv\warmup.dll'
    if (-not (Test-Path -LiteralPath $appInit)) { throw 'IIS Application Initialization is not installed (Install-WindowsFeature Web-AppInit).' }
}

function Get-FullPath([string]$Path) { [System.IO.Path]::GetFullPath($Path) }

function Set-AppPool {
    $poolPath = "IIS:\AppPools\$AppPoolName"
    if (-not (Test-Path $poolPath)) {
        if ($PSCmdlet.ShouldProcess($AppPoolName, 'Create application pool')) { New-WebAppPool -Name $AppPoolName | Out-Null }
    }
    $settings = [ordered]@{
        'managedRuntimeVersion'                 = ''              # No Managed Code (ASP.NET Core runs in-process via ANCM)
        'managedPipelineMode'                   = 'Integrated'
        'startMode'                             = 'AlwaysRunning'
        'autoStart'                             = $true
        'processModel.idleTimeout'              = [TimeSpan]::Zero
        'processModel.identityType'             = $IdentityType
        'processModel.loadUserProfile'          = $true
        # Only one worker process may own the SQLite database and the job queue at a time.
        'recycling.disallowOverlappingRotation' = $true
        'processModel.maxProcesses'             = 1
    }
    if ($DisablePeriodicRecycle) { $settings['recycling.periodicRestart.time'] = [TimeSpan]::Zero }
    foreach ($name in $settings.Keys) {
        if ($PSCmdlet.ShouldProcess("$AppPoolName/$name", "Set to '$($settings[$name])'")) { Set-ItemProperty -Path $poolPath -Name $name -Value $settings[$name] }
    }

    $variables = @{ 'ASPNETCORE_ENVIRONMENT' = $AspNetCoreEnvironment; 'SignalLedger__DataDirectory' = $DataDirectory }
    foreach ($key in $EnvironmentVariables.Keys) { $variables[[string]$key] = [string]$EnvironmentVariables[$key] }
    $filter = "system.applicationHost/applicationPools/add[@name='$AppPoolName']/environmentVariables"
    foreach ($key in $variables.Keys) {
        if ($PSCmdlet.ShouldProcess("$AppPoolName env:$key", 'Set environment variable')) {
            Remove-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter -Name '.' -AtElement @{ name = $key } -ErrorAction SilentlyContinue
            Add-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter -Name '.' -Value @{ name = $key; value = $variables[$key] }
        }
    }
}

function Set-SiteOrApplication {
    if ($ApplicationAlias) {
        if (-not (Test-Path "IIS:\Sites\$SiteName")) { throw "Site '$SiteName' does not exist; create it first or omit -ApplicationAlias." }
        $appPath = "IIS:\Sites\$SiteName\$ApplicationAlias"
        if (-not (Get-WebApplication -Site $SiteName -Name $ApplicationAlias)) {
            if ($PSCmdlet.ShouldProcess("$SiteName/$ApplicationAlias", 'Create application')) { New-WebApplication -Site $SiteName -Name $ApplicationAlias -PhysicalPath $PhysicalPath -ApplicationPool $AppPoolName | Out-Null }
        }
        if ($PSCmdlet.ShouldProcess($appPath, 'Set physical path, pool and preloadEnabled')) {
            Set-ItemProperty -Path $appPath -Name physicalPath -Value $PhysicalPath
            Set-ItemProperty -Path $appPath -Name applicationPool -Value $AppPoolName
            Set-ItemProperty -Path $appPath -Name preloadEnabled -Value $true
        }
        return "$SiteName/$ApplicationAlias"
    }

    $sitePath = "IIS:\Sites\$SiteName"
    if (-not (Test-Path $sitePath)) {
        if ($PSCmdlet.ShouldProcess($SiteName, "Create site on port $Port")) {
            New-Website -Name $SiteName -PhysicalPath $PhysicalPath -ApplicationPool $AppPoolName -Port $Port -HostHeader $HostName | Out-Null
        }
    }
    elseif (-not (Get-WebBinding -Name $SiteName -Protocol http -Port $Port -HostHeader $HostName)) {
        if ($PSCmdlet.ShouldProcess($SiteName, "Add http binding *:${Port}:$HostName")) { New-WebBinding -Name $SiteName -Protocol http -Port $Port -HostHeader $HostName }
    }
    if ($PSCmdlet.ShouldProcess($sitePath, 'Set physical path, pool, serverAutoStart and preloadEnabled')) {
        Set-ItemProperty -Path $sitePath -Name physicalPath -Value $PhysicalPath
        Set-ItemProperty -Path $sitePath -Name applicationPool -Value $AppPoolName
        Set-ItemProperty -Path $sitePath -Name serverAutoStart -Value $true
        Set-ItemProperty -Path $sitePath -Name applicationDefaults.preloadEnabled -Value $true
    }
    return $SiteName
}

function Set-ApplicationInitialization([string]$Location) {
    # Stored in applicationHost.config so web.config remains valid where the feature is absent.
    $filter = 'system.webServer/applicationInitialization'
    if ($PSCmdlet.ShouldProcess($Location, "Application Initialization warm-up $WarmupPath")) {
        Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $Location -Filter $filter -Name doAppInitAfterRestart -Value $true
        Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $Location -Filter $filter -Name skipManagedModules -Value $false
        Clear-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $Location -Filter "$filter/*" -ErrorAction SilentlyContinue
        Add-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $Location -Filter $filter -Name '.' -Value @{ initializationPage = $WarmupPath }
    }
}

function Grant-Access([string]$Path, [string]$Rights) {
    $account = if ($IdentityType -eq 'ApplicationPoolIdentity') { "IIS AppPool\$AppPoolName" } elseif ($IdentityType -eq 'NetworkService') { 'NT AUTHORITY\NETWORK SERVICE' } else { 'NT AUTHORITY\LOCAL SERVICE' }
    if ($PSCmdlet.ShouldProcess($Path, "Grant $account $Rights")) {
        & icacls.exe $Path /grant "${account}:(OI)(CI)$Rights" /T /C /Q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "icacls failed for $Path" }
    }
}

function Copy-Release {
    if (-not $SourcePath) { return }
    $source = Get-FullPath $SourcePath
    foreach ($required in 'SignalLedger.Api.dll', 'web.config', 'wwwroot\index.html') {
        if (-not (Test-Path -LiteralPath (Join-Path $source $required))) { throw "SourcePath is not a SignalLedger publish folder (missing $required)." }
    }
    $resolvedData = Get-FullPath $DataDirectory
    if ($resolvedData.StartsWith((Get-FullPath $PhysicalPath), [StringComparison]::OrdinalIgnoreCase)) { throw 'DataDirectory must not be inside PhysicalPath; redeployments replace that folder.' }
    $offline = Join-Path $PhysicalPath 'app_offline.htm'
    if ($PSCmdlet.ShouldProcess($PhysicalPath, "Copy release from $source (app_offline during copy)")) {
        Set-Content -LiteralPath $offline -Value '<!doctype html><title>SignalLedger</title><p>Updating SignalLedger. Please retry in a minute.</p>' -Encoding UTF8
        try {
            & robocopy.exe $source $PhysicalPath /MIR /XF app_offline.htm /XD logs /NFL /NDL /NJH /NJS /NP | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }
        }
        finally { Remove-Item -LiteralPath $offline -Force -ErrorAction SilentlyContinue }
    }
}

Assert-Administrator
Assert-Prerequisites

$PhysicalPath = Get-FullPath $PhysicalPath
$DataDirectory = Get-FullPath $DataDirectory
if (-not $LogDirectory) { $LogDirectory = Join-Path (Split-Path $DataDirectory -Parent) 'logs' }
$LogDirectory = Get-FullPath $LogDirectory
if ($DataDirectory.StartsWith($PhysicalPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'DataDirectory must be outside PhysicalPath (and outside wwwroot).' }

foreach ($directory in $PhysicalPath, $DataDirectory, $LogDirectory) {
    if (-not (Test-Path -LiteralPath $directory) -and $PSCmdlet.ShouldProcess($directory, 'Create directory')) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
}

Set-AppPool
Copy-Release
$location = Set-SiteOrApplication
Set-ApplicationInitialization -Location $location
Grant-Access -Path $PhysicalPath -Rights 'RX'
Grant-Access -Path $DataDirectory -Rights 'M'
Grant-Access -Path $LogDirectory -Rights 'M'

Write-Host "SignalLedger IIS configuration applied to '$location' (pool '$AppPoolName')."
Write-Host "Next: start the pool if it is stopped (Start-WebAppPool -Name $AppPoolName) and check $WarmupPath."
