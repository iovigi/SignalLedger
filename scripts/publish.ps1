[CmdletBinding()]
param(
    [string]$OutputDirectory = '',
    [switch]$SkipTestsForCi
)

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 turns any native stderr line (e.g. Vite's chunk-size warning) into a terminating
# error under 'Stop' when output is redirected. Native tools are judged by their exit code instead.
function Invoke-Native([string]$Failure, [scriptblock]$Command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command 2>&1 | ForEach-Object { "$_" } }
    finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw $Failure }
}
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts\publish' }
$webRoot = Join-Path $repoRoot 'src\signal-ledger-web'
$apiProject = Join-Path $repoRoot 'src\SignalLedger.Api\SignalLedger.Api.csproj'
$solution = Join-Path $repoRoot 'SignalLedger.slnx'
$publishRoot = [System.IO.Path]::GetFullPath($OutputDirectory)

if (-not $publishRoot.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must stay inside the repository: $repoRoot"
}

Write-Host 'Installing locked frontend dependencies...'
Push-Location $webRoot
try {
    Invoke-Native 'npm ci failed.' { npm ci }
    Invoke-Native 'Frontend lint failed.' { npm run lint }
    Invoke-Native 'Frontend build failed.' { npm run build }
}
finally { Pop-Location }

if (-not $SkipTestsForCi) {
    Write-Host 'Running backend tests...'
    Invoke-Native 'Backend tests failed.' { dotnet test $solution -c Release }
}
else {
    Write-Warning 'Backend tests were skipped via -SkipTestsForCi. This switch is intended only when the same tests already passed in the current CI job.'
}

if (Test-Path -LiteralPath $publishRoot) {
    $resolved = [System.IO.Path]::GetFullPath($publishRoot)
    if ($resolved -eq $repoRoot -or -not $resolved.StartsWith((Join-Path $repoRoot 'artifacts'), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean unsafe publish directory: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

Write-Host "Publishing API to $publishRoot..."
Invoke-Native 'dotnet publish failed.' { dotnet publish $apiProject -c Release --no-restore -o $publishRoot }

$publishedWwwroot = Join-Path $publishRoot 'wwwroot'
if (Test-Path -LiteralPath $publishedWwwroot) { Remove-Item -LiteralPath $publishedWwwroot -Recurse -Force }
New-Item -ItemType Directory -Path $publishedWwwroot -Force | Out-Null
Copy-Item -Path (Join-Path $webRoot 'dist\*') -Destination $publishedWwwroot -Recurse -Force

$requiredFiles = @(
    (Join-Path $publishRoot 'SignalLedger.Api.dll'),
    (Join-Path $publishRoot 'web.config'),
    (Join-Path $publishedWwwroot 'index.html')
)
foreach ($required in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Published output is missing required file: $required" }
}
if (-not (Get-ChildItem -Path (Join-Path $publishedWwwroot 'assets') -File -ErrorAction SilentlyContinue | Where-Object Extension -In '.js', '.css')) {
    throw 'Published output contains no JavaScript/CSS assets.'
}

Write-Host 'Validating IIS web.config...'
[xml]$webConfig = Get-Content -LiteralPath (Join-Path $publishRoot 'web.config') -Raw
$handler = $webConfig.SelectSingleNode("//system.webServer/handlers/add[@name='aspNetCore']")
$module = $webConfig.SelectSingleNode('//system.webServer/aspNetCore')
if ($null -eq $handler -or $handler.modules -ne 'AspNetCoreModuleV2') { throw 'web.config must register the AspNetCoreModuleV2 handler.' }
if ($null -eq $module -or $module.hostingModel -ne 'inprocess' -or $module.processPath -ne 'dotnet' -or $module.arguments -ne '.\SignalLedger.Api.dll' -or $module.stdoutLogEnabled -ne 'false') {
    throw 'web.config aspNetCore element must use dotnet .\SignalLedger.Api.dll, in-process hosting and disabled stdout logging.'
}
foreach ($forbidden in 'signalledger.db', 'signalledger.db-wal', 'signalledger.db-shm') {
    if (Get-ChildItem -LiteralPath $publishRoot -Recurse -Filter $forbidden -ErrorAction SilentlyContinue) { throw "Published output must not contain a database file ($forbidden)." }
}
Write-Host 'Statically validating scripts\deploy-iis.ps1...'
$parseErrors = $null
$null = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot 'scripts\deploy-iis.ps1'), [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw "deploy-iis.ps1 has syntax errors: $($parseErrors | ForEach-Object { $_.Message })" }

Write-Host 'Starting published artifact for health verification...'
$smokeData = Join-Path $repoRoot 'artifacts\smoke-data'
New-Item -ItemType Directory -Path $smokeData -Force | Out-Null
$previousDataDirectory = $env:SignalLedger__DataDirectory
$env:SignalLedger__DataDirectory = $smokeData
$port = 51837
$stdout = Join-Path $repoRoot 'artifacts\publish-smoke.out.log'
$stderr = Join-Path $repoRoot 'artifacts\publish-smoke.err.log'
$process = $null
try {
    $process = Start-Process -FilePath 'dotnet' -ArgumentList @((Join-Path $publishRoot 'SignalLedger.Api.dll'), '--urls', "http://127.0.0.1:$port") -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $health = $null
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try { $health = Invoke-RestMethod -Uri "http://127.0.0.1:$port/api/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
    }
    if ($null -eq $health -or $health.status -ne 'healthy') {
        $errorLog = if (Test-Path $stderr) { Get-Content -LiteralPath $stderr -Raw } else { '' }
        throw "Published health endpoint did not return the expected contract. $errorLog"
    }

    $liveness = Invoke-RestMethod -Uri "http://127.0.0.1:$port/health" -TimeoutSec 5
    if ($liveness.status -ne 'healthy') { throw '/health (IIS warm-up endpoint) did not return status healthy.' }

    $missingStatus = 0; $missingType = ''
    try { $null = Invoke-WebRequest -Uri "http://127.0.0.1:$port/api/does-not-exist" -UseBasicParsing -TimeoutSec 5 }
    catch {
        $missingStatus = [int]$_.Exception.Response.StatusCode
        $missingType = [string]$_.Exception.Response.Content.Headers.ContentType
        if (-not $missingType) { $missingType = [string]$_.Exception.Response.ContentType }
    }
    if ($missingStatus -ne 404 -or $missingType -notmatch 'json') {
        throw "Unknown /api route must return a JSON 404; got status '$missingStatus' and content type '$missingType'."
    }

    $deepLink = Invoke-WebRequest -Uri "http://127.0.0.1:$port/methodology" -UseBasicParsing -TimeoutSec 5
    if ($deepLink.StatusCode -ne 200 -or $deepLink.Content -notmatch 'id="root"') {
        throw 'SPA deep link /methodology did not return index.html.'
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    $env:SignalLedger__DataDirectory = $previousDataDirectory
}

Write-Host "SignalLedger artifact verified: $publishRoot"
