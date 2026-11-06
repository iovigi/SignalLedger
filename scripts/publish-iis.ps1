[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\publish'),
    [switch]$SkipTestsForCi
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
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
    npm ci
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
    npm run lint
    if ($LASTEXITCODE -ne 0) { throw 'Frontend lint failed.' }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
}
finally { Pop-Location }

if (-not $SkipTestsForCi) {
    Write-Host 'Running backend tests...'
    dotnet test $solution -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Backend tests failed.' }
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
dotnet publish $apiProject -c Release --no-restore -o $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$publishedWwwroot = Join-Path $publishRoot 'wwwroot'
if (Test-Path -LiteralPath $publishedWwwroot) { Remove-Item -LiteralPath $publishedWwwroot -Recurse -Force }
New-Item -ItemType Directory -Path $publishedWwwroot -Force | Out-Null
Copy-Item -Path (Join-Path $webRoot 'dist\*') -Destination $publishedWwwroot -Recurse -Force

$requiredFiles = @(
    (Join-Path $publishRoot 'web.config'),
    (Join-Path $publishRoot 'SignalLedger.Api.dll'),
    (Join-Path $publishedWwwroot 'index.html')
)
foreach ($required in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Published output is missing required file: $required" }
}
if (-not (Get-ChildItem -Path (Join-Path $publishedWwwroot 'assets') -File -ErrorAction SilentlyContinue | Where-Object Extension -In '.js', '.css')) {
    throw 'Published output contains no JavaScript/CSS assets.'
}
if ((Get-Content -LiteralPath (Join-Path $publishRoot 'web.config') -Raw) -notmatch 'AspNetCoreModuleV2') {
    throw 'web.config is not configured for ASP.NET Core Module V2.'
}

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
}
finally {
    if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    $env:SignalLedger__DataDirectory = $previousDataDirectory
}

Write-Host "SignalLedger IIS artifact verified: $publishRoot"
