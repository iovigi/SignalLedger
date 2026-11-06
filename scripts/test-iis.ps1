[CmdletBinding()]
param(
    [string]$BaseUrl = 'https://localhost',
    [string]$SiteName = 'SignalLedger',
    [string]$AppPoolName = 'SignalLedger',
    [switch]$SkipCertificateCheck
)

$ErrorActionPreference = 'Stop'
$appcmd = Join-Path $env:SystemRoot 'System32\inetsrv\appcmd.exe'
if (-not (Test-Path -LiteralPath $appcmd)) { throw 'IIS appcmd.exe is missing.' }
$invokeOptions = @{ SkipHttpErrorCheck = $true; TimeoutSec = 30 }
if ($SkipCertificateCheck) { $invokeOptions.SkipCertificateCheck = $true }

function Invoke-SignalLedger([string]$Path, [string]$Method = 'GET', [string]$Body = '') {
    $options = $invokeOptions.Clone()
    $options.Uri = "$($BaseUrl.TrimEnd('/'))$Path"
    $options.Method = $Method
    if ($Body) { $options.Body = $Body; $options.ContentType = 'application/json' }
    Invoke-WebRequest @options
}

$health = Invoke-SignalLedger '/api/health'
if ($health.StatusCode -ne 200 -or ($health.Content | ConvertFrom-Json).status -ne 'healthy') { throw 'Health contract failed.' }

$deepRoute = Invoke-SignalLedger '/methodology'
if ($deepRoute.StatusCode -ne 200 -or $deepRoute.Content -notmatch '<div id="root">') { throw 'SPA deep-route fallback failed.' }

$missingApi = Invoke-SignalLedger '/api/does-not-exist'
if ($missingApi.StatusCode -ne 404 -or $missingApi.Headers.'Content-Type' -notmatch 'application/problem\+json') { throw 'Unknown API route did not return JSON Problem Details.' }

$sync = Invoke-SignalLedger '/api/market-data/sync?symbol=QQQ&from=2024-01-01&to=2024-02-01' 'POST'
if ($sync.StatusCode -ne 403) { throw 'Public provider sync was not forbidden.' }

$request = @{
    symbol = 'SYNTH'; source = 'Synthetic'; startDate = '2018-01-01'; endDate = '2024-12-31'; initialCapital = 1000
    strategy = @{ type = 'SMA'; period = 30 }
    execution = @{ commissionPercentPerSide = 0.2; slippageBpsPerSide = 5; liquidateAtEnd = $false; cashYieldMode = 'None' }
} | ConvertTo-Json -Depth 5
$created = Invoke-SignalLedger '/api/backtests' 'POST' $request
if ($created.StatusCode -ne 201) { throw "Backtest creation failed with HTTP $($created.StatusCode)." }
$before = $created.Content | ConvertFrom-Json

& $appcmd recycle apppool /apppool.name:$AppPoolName | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not recycle IIS app pool $AppPoolName." }
$ready = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    Start-Sleep -Milliseconds 500
    try { if ((Invoke-SignalLedger '/api/health').StatusCode -eq 200) { $ready = $true; break } } catch { }
}
if (-not $ready) { throw 'Site did not recover after app-pool recycle.' }

$afterResponse = Invoke-SignalLedger "/api/backtests/$($before.id)"
if ($afterResponse.StatusCode -ne 200) { throw 'Stored backtest was unavailable after recycle.' }
$after = $afterResponse.Content | ConvertFrom-Json
if ($after.dataFingerprint -ne $before.dataFingerprint -or $after.strategy.finalEquity -ne $before.strategy.finalEquity) {
    throw 'Backtest result changed after IIS recycle.'
}

$site = & $appcmd list site /name:$SiteName
if (-not $site -or $site -notmatch 'Started') { throw "IIS site $SiteName is not started." }
Write-Host "IIS acceptance passed for $BaseUrl; run $($before.id) survived recycle reproducibly."
