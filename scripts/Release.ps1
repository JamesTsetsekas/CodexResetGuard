param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.2.0',
    [string]$OutDir = (Join-Path $PSScriptRoot '..\artifacts')
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$OutDir = [IO.Path]::GetFullPath($OutDir)
$assemblySource = Get-Content -LiteralPath (Join-Path $repoRoot 'source\Program.cs') -Raw
if (-not $assemblySource.Contains('AssemblyVersion("' + $Version + '.0")') -or -not $assemblySource.Contains('AssemblyFileVersion("' + $Version + '.0")')) { throw 'Release version does not match AssemblyVersion and AssemblyFileVersion.' }
if (-not $assemblySource.Contains('Version = "' + $Version + '"')) { throw 'Release version does not match Program.Version.' }
$manifestSource = Get-Content -LiteralPath (Join-Path $repoRoot 'source\app.manifest') -Raw
if (-not $manifestSource.Contains('version="' + $Version + '.0"')) { throw 'Release version does not match app.manifest.' }
$notesSource = Get-Content -LiteralPath (Join-Path $repoRoot 'docs\RELEASE-NOTES.md') -Raw
if (-not $notesSource.Contains('Reset Guard ' + $Version)) { throw 'docs/RELEASE-NOTES.md does not describe this version.' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$buildPath = Join-Path $OutDir 'build'
& (Join-Path $repoRoot 'source\Build.ps1') -OutDir $buildPath
# Windows PowerShell turns native stderr into terminating errors under 'Stop'; capture it first so failures are reported.
$ErrorActionPreference = 'Continue'
$testOutput = & (Join-Path $buildPath 'ResetGuard.Tests.exe') 2>&1 | ForEach-Object { "$_" }
$testExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($testExit -ne 0) { $testOutput | Write-Output; throw 'Release tests failed.' }
$smokePath = Join-Path $buildPath 'ui'
$smoke = Start-Process -FilePath (Join-Path $buildPath 'ResetGuard.exe') -ArgumentList @('--ui-smoke', ('"' + $smokePath + '"')) -WindowStyle Hidden -Wait -PassThru
if ($smoke.ExitCode -ne 0) { Get-Content -LiteralPath (Join-Path $smokePath 'ui-smoke-error.txt') -ErrorAction SilentlyContinue | Write-Output; throw 'Release UI smoke checks failed.' }
$packageName = 'ResetGuard-' + $Version
$stagingRoot = Join-Path $OutDir ('staging-' + [Guid]::NewGuid().ToString('N'))
$packagePath = Join-Path $stagingRoot $packageName
New-Item -ItemType Directory -Force -Path $packagePath | Out-Null
foreach ($fileName in @('ResetGuard.exe','ResetGuard.exe.config')) {
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
Remove-Item -LiteralPath $stagingRoot -Recurse -Force
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($zipPath)
[IO.File]::WriteAllText((Join-Path $OutDir 'SHA256SUMS.txt'), $zipHash + [Environment]::NewLine, $utf8NoBom)
$testOutput | Write-Output
Write-Output ('Packaged ' + $zipPath)
