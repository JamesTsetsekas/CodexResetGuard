param([string]$OutDir = (Join-Path $PSScriptRoot 'build'))
$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '.NET Framework 4.8 and its C# compiler are required.' }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$sourceFiles = @('Models.cs','Policy.cs','StateStore.cs','ResetEngine.cs','CodexClient.cs','ModernControls.cs','GuardForm.cs','TrayApplication.cs','Program.cs','UiSmoke.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$references = @('/r:System.dll','/r:System.Core.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll')
$applicationPath = Join-Path $OutDir 'CodexResetGuard.exe'
$compilerArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+','/warn:4','/utf8output',('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),('/out:' + $applicationPath)) + $references + $sourceFiles
& $compilerPath @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
$iconPath = Join-Path $OutDir 'app.ico'
$iconProcess = Start-Process -FilePath $applicationPath -ArgumentList @('--make-icon', ('"' + $iconPath + '"')) -WindowStyle Hidden -Wait -PassThru
if ($iconProcess.ExitCode -ne 0) { throw 'Icon generation failed.' }
& $compilerPath @compilerArgs ('/win32icon:' + $iconPath)
if ($LASTEXITCODE -ne 0) { throw 'Application compilation with icon failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CodexResetGuard.exe.config') -Destination $OutDir -Force
$testPath = Join-Path $OutDir 'CodexResetGuard.Tests.exe'
$testArgs = @('/nologo','/target:exe','/platform:x64','/optimize+','/warn:4','/utf8output','/main:CodexResetGuard.TestsMain',('/out:' + $testPath)) + $references + $sourceFiles + (Join-Path $PSScriptRoot 'Tests.cs')
& $compilerPath @testArgs
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
Write-Output ('Built ' + $applicationPath)
