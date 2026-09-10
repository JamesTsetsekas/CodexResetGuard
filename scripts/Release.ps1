param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.1.0',
    [string]$OutDir = (Join-Path $PSScriptRoot '..\artifacts')
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$OutDir = [IO.Path]::GetFullPath($OutDir)
$assemblySource = Get-Content -LiteralPath (Join-Path $repoRoot 'source\Program.cs') -Raw
if (-not $assemblySource.Contains('AssemblyVersion("' + $Version + '.0")')) { throw 'Release version does not match AssemblyVersion.' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$buildPath = Join-Path $OutDir 'build'
& (Join-Path $repoRoot 'source\Build.ps1') -OutDir $buildPath
$testOutput = & (Join-Path $buildPath 'CodexResetGuard.Tests.exe') 2>&1
if ($LASTEXITCODE -ne 0) { $testOutput | Write-Output; throw 'Release tests failed.' }
$packageName = 'CodexResetGuard-' + $Version
$stagingRoot = Join-Path $OutDir ('staging-' + [Guid]::NewGuid().ToString('N'))
$packagePath = Join-Path $stagingRoot $packageName
New-Item -ItemType Directory -Force -Path $packagePath | Out-Null
foreach ($fileName in @('CodexResetGuard.exe','CodexResetGuard.exe.config')) {
    Copy-Item -LiteralPath (Join-Path $buildPath $fileName) -Destination $packagePath
}
foreach ($fileName in @('README.md','LICENSE','CONTRIBUTING.md','SECURITY.md')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $fileName) -Destination $packagePath
}
$sourceDestination = Join-Path $packagePath 'source'
New-Item -ItemType Directory -Force -Path $sourceDestination | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repoRoot 'source') -File | Where-Object { $_.Name -match '\.(cs|ps1|manifest|config)$' } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $sourceDestination
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination $packagePath -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts') -Destination $packagePath -Recurse
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllLines((Join-Path $packagePath 'VALIDATION.txt'), [string[]]$testOutput, $utf8NoBom)
$manifest = Get-ChildItem -LiteralPath $packagePath -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relativePath = $_.FullName.Substring($packagePath.Length + 1).Replace('\', '/')
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $relativePath
}
[IO.File]::WriteAllLines((Join-Path $packagePath 'SHA256SUMS.txt'), [string[]]$manifest, $utf8NoBom)
$zipPath = Join-Path $OutDir ($packageName + '-windows-x64.zip')
Compress-Archive -LiteralPath $packagePath -DestinationPath $zipPath -Force
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($zipPath)
[IO.File]::WriteAllText((Join-Path $OutDir 'SHA256SUMS.txt'), $zipHash + [Environment]::NewLine, $utf8NoBom)
$testOutput | Write-Output
Write-Output ('Packaged ' + $zipPath)
