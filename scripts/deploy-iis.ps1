[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '..\artifacts\publish'),
    [string]$SiteRoot = 'C:\sites\SignalLedger',
    [string]$DataDirectory = 'C:\ProgramData\SignalLedger\data',
    [string]$SiteName = 'SignalLedger',
    [string]$AppPoolName = 'SignalLedger',
    [int]$HttpPort = 8080,
    [string]$HostName = '',
    [int]$HttpsPort = 443,
    [string]$CertificateThumbprint = '',
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$appcmd = Join-Path $env:SystemRoot 'System32\inetsrv\appcmd.exe'
$publish = [IO.Path]::GetFullPath($PublishDirectory); $siteRootFull = [IO.Path]::GetFullPath($SiteRoot); $dataFull = [IO.Path]::GetFullPath($DataDirectory)
foreach ($file in 'web.config','SignalLedger.Api.dll','wwwroot\index.html') { if (-not (Test-Path -LiteralPath (Join-Path $publish $file))) { throw "Publish artifact is missing $file. Run scripts\publish-iis.ps1 first." } }
if ($siteRootFull -in @('C:\','C:\sites') -or -not $siteRootFull.StartsWith('C:\sites\', [StringComparison]::OrdinalIgnoreCase)) { throw "SiteRoot must be a dedicated directory below C:\sites: $siteRootFull" }
if (-not $dataFull.StartsWith('C:\ProgramData\SignalLedger\', [StringComparison]::OrdinalIgnoreCase)) { throw "DataDirectory must stay below C:\ProgramData\SignalLedger: $dataFull" }
if ($ValidateOnly) { Write-Host "Deployment inputs validated: $publish"; return }
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run deploy-iis.ps1 from an elevated PowerShell session.' }
if (-not (Test-Path -LiteralPath $appcmd)) { throw 'IIS is not installed (appcmd.exe is missing).' }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'; $release = Join-Path $siteRootFull "releases\$stamp"; $backup = Join-Path $siteRootFull "backups\$stamp"
if (-not $PSCmdlet.ShouldProcess($SiteName, "Deploy release $release")) { return }
New-Item -ItemType Directory -Path $release,$dataFull,$backup -Force | Out-Null
Copy-Item -Path (Join-Path $publish '*') -Destination $release -Recurse -Force
$settingsPath = Join-Path $release 'appsettings.Production.json'
$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
$settings.SignalLedger.DataDirectory = $dataFull
$settings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $settingsPath -Encoding utf8
$database = Join-Path $dataFull 'signalledger.db'
if (Test-Path -LiteralPath $database) { Copy-Item -LiteralPath $database -Destination (Join-Path $backup 'signalledger.db') -Force }

$poolIdentity = "IIS AppPool\$AppPoolName"
& icacls.exe $release /inheritance:r /grant:r "${poolIdentity}:(OI)(CI)(RX)" 'Administrators:(OI)(CI)(F)' /T | Out-Null
& icacls.exe $dataFull /grant:r "${poolIdentity}:(OI)(CI)(M)" 'Administrators:(OI)(CI)(F)' /T | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Failed to configure IIS filesystem ACLs.' }

$previousData = $env:SignalLedger__DataDirectory; $previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
try {
    $env:SignalLedger__DataDirectory = $dataFull; $env:ASPNETCORE_ENVIRONMENT = 'Production'
    & dotnet (Join-Path $release 'SignalLedger.Api.dll') --migrate-only
    if ($LASTEXITCODE -ne 0) { throw 'Database migration failed; the site was not switched.' }
}
finally { $env:SignalLedger__DataDirectory = $previousData; $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment }

$pool = & $appcmd list apppool /name:$AppPoolName
if (-not $pool) { & $appcmd add apppool /name:$AppPoolName | Out-Null }
& $appcmd set apppool $AppPoolName /managedRuntimeVersion:"" /managedPipelineMode:Integrated /processModel.identityType:ApplicationPoolIdentity | Out-Null
$site = & $appcmd list site /name:$SiteName
$binding = "*:${HttpPort}:$HostName"
if (-not $site) { & $appcmd add site /name:$SiteName /physicalPath:$release /bindings:"http/$binding" | Out-Null }
else { & $appcmd set vdir "$SiteName/" /physicalPath:$release | Out-Null }
& $appcmd set app "$SiteName/" /applicationPool:$AppPoolName | Out-Null

if ($CertificateThumbprint) {
    $certificate = Get-Item -LiteralPath "Cert:\LocalMachine\My\$CertificateThumbprint" -ErrorAction Stop
    & $appcmd set site $SiteName "/+bindings.[protocol='https',bindingInformation='*:${HttpsPort}:$HostName']" | Out-Null
    $endpoint = if ($HostName) { "hostnameport=${HostName}:$HttpsPort" } else { "ipport=0.0.0.0:$HttpsPort" }
    & netsh.exe http delete sslcert $endpoint 2>$null | Out-Null
    & netsh.exe http add sslcert $endpoint "certhash=$($certificate.Thumbprint)" "appid={7d25cc27-8c83-4b36-b43a-711575f40895}" certstorename=MY | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Failed to bind the HTTPS certificate.' }
}

& $appcmd start apppool /apppool.name:$AppPoolName | Out-Null
& $appcmd start site /site.name:$SiteName | Out-Null
$headers = if ($HostName) { @{ Host = $HostName } } else { @{} }
$health = Invoke-RestMethod -Uri "http://127.0.0.1:$HttpPort/api/health" -Headers $headers -TimeoutSec 15
if ($health.status -ne 'healthy') { throw 'Deployed site health contract failed.' }
Write-Host "Deployed $SiteName to $release. Database backup: $backup"
