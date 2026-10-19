# Builds bin\QfEdgeHub.dll with the C# compiler that ships with .NET Framework 4.x - no Visual Studio,
# no MSBuild project, no NuGet. Run from the repo root on any Windows PC or server:
#   powershell -ExecutionPolicy Bypass -File build.ps1
# Output: bin\QfEdgeHub.dll + the vendored DLLs from lib\ copied beside it.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found - .NET Framework 4.x is required' }
New-Item -ItemType Directory -Force bin | Out-Null
$refs = @('System.dll','System.Core.dll','System.Data.dll','System.Web.dll','System.Configuration.dll','Microsoft.CSharp.dll','System.Xml.dll')
$libs = Get-ChildItem lib\*.dll | ForEach-Object { $_.FullName }
$src = Get-ChildItem src\*.cs | ForEach-Object { $_.FullName }
$args = @('/nologo','/target:library','/optimize+','/langversion:5','/warn:3','/out:bin\QfEdgeHub.dll')
foreach ($r in $refs) { $args += "/r:$r" }
foreach ($l in $libs) { $args += "/r:$l" }
$args += $src
& $csc @args
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
Copy-Item lib\*.dll bin\ -Force
'built ' + (Get-Item bin\QfEdgeHub.dll).VersionInfo.FileVersion + ' -> ' + (Resolve-Path bin\QfEdgeHub.dll)
Get-ChildItem bin\*.dll | ForEach-Object { '  ' + $_.Name + '  ' + ([Reflection.AssemblyName]::GetAssemblyName($_.FullName)).Version }
