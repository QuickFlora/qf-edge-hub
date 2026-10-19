# Installs or updates the qf-edge-hub IIS application on THIS server. Run as administrator (SSM runs as SYSTEM).
#   powershell -ExecutionPolicy Bypass -File install-hub.ps1 -Branch main -SiteName 'Default Web Site' -AppPath /edgehub
# What it does, in order:
#   1. downloads the branch zip from the public GitHub repo and builds bin\QfEdgeHub.dll with build.ps1
#   2. copies site\ + bin\ to C:\WebApps\qf-edge-hub (previous copy kept as qf-edge-hub.<timestamp>.bak)
#   3. fills ConnectionString in web.config from the print web service's own web.config (never printed)
#   4. creates app pool qf-edge-hub (.NET 4.0 Integrated, AlwaysRunning) and the IIS application
#   5. prints health.ashx
# Rollback: Stop-WebAppPool qf-edge-hub (shop PCs fall back to the long-poll by themselves), or
#   Remove-WebApplication -Site 'Default Web Site' -Name edgehub
param(
  [string]$Branch = 'main',
  [string]$SiteName = 'Default Web Site',
  [string]$AppPath = '/edgehub',
  [string]$PoolName = 'qf-edge-hub',
  [string]$Root = 'C:\WebApps\qf-edge-hub',
  [string]$PrintWsConfig = 'C:\WebApps\NewPOSQFPrintWS\web.config',
  [string]$PrintServiceUrl = '',
  [string]$PrintServiceHost = ''
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Import-Module WebAdministration
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$work = Join-Path $env:TEMP ("qf-edge-hub-" + $stamp)
New-Item -ItemType Directory -Force $work | Out-Null
$zip = Join-Path $work 'src.zip'
"1. download https://github.com/QuickFlora/qf-edge-hub/archive/refs/heads/$Branch.zip"
(New-Object Net.WebClient).DownloadFile("https://github.com/QuickFlora/qf-edge-hub/archive/refs/heads/$Branch.zip", $zip)
Expand-Archive $zip -DestinationPath $work -Force
$src = Get-ChildItem $work -Directory | Where-Object { $_.Name -like 'qf-edge-hub-*' } | Select-Object -First 1
"   source: " + $src.FullName
"   build:"
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $src.FullName 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

"2. copy to $Root"
if (Test-Path $Root) {
  $bak = "$Root.$stamp.bak"
  Copy-Item $Root $bak -Recurse -Force
  "   previous copy kept at $bak"
}
New-Item -ItemType Directory -Force $Root | Out-Null
New-Item -ItemType Directory -Force (Join-Path $Root 'bin') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $Root 'App_Data\Logs') | Out-Null
$oldCfg = Join-Path $Root 'web.config'
$oldConn = $null; $oldKey = $null; $oldPsUrl = $null; $oldPsHost = $null
if (Test-Path $oldCfg) {
  $x = [xml](Get-Content $oldCfg)
  $oldConn = ($x.configuration.appSettings.add | Where-Object { $_.key -eq 'ConnectionString' }).value
  $oldKey = ($x.configuration.appSettings.add | Where-Object { $_.key -eq 'HubKey' }).value
  $oldPsUrl = ($x.configuration.appSettings.add | Where-Object { $_.key -eq 'PrintServiceUrl' }).value
  $oldPsHost = ($x.configuration.appSettings.add | Where-Object { $_.key -eq 'PrintServiceHost' }).value
}
Copy-Item (Join-Path $src.FullName 'site\*') $Root -Recurse -Force
Copy-Item (Join-Path $src.FullName 'bin\*.dll') (Join-Path $Root 'bin') -Force

"3. connection string"
$cfg = [xml](Get-Content (Join-Path $Root 'web.config'))
$conn = $oldConn
if (-not $conn -or $conn -eq '__SET_ON_SERVER__') {
  $pw = [xml](Get-Content $PrintWsConfig)
  $conn = ($pw.configuration.appSettings.add | Where-Object { $_.key -eq 'ConnectionString' }).value
  "   taken from $PrintWsConfig"
} else { "   kept from previous install" }
if (-not $conn) { throw 'no ConnectionString found' }
$conn = $conn -replace 'Application Name=[^;]*', 'Application Name=qf-edge-hub'
($cfg.configuration.appSettings.add | Where-Object { $_.key -eq 'ConnectionString' }).value = $conn
if ($oldKey) { ($cfg.configuration.appSettings.add | Where-Object { $_.key -eq 'HubKey' }).value = $oldKey }
# the print web service is reached on the loopback with the site's own Host header: a server cannot
# reach its public DNS name from inside AWS (hairpin), seen on WEB1TEST 2026-10-18
if (-not $PrintServiceUrl) { $PrintServiceUrl = $oldPsUrl }
if (-not $PrintServiceHost) { $PrintServiceHost = $oldPsHost }
$site = Get-Website $SiteName
$hostName = ($site.bindings.Collection | Where-Object { $_.protocol -eq 'http' -and $_.bindingInformation -like '*:80:*' } | ForEach-Object { $_.bindingInformation.Split(':')[2] } | Where-Object { $_ } | Select-Object -Last 1)
if (-not $PrintServiceUrl) { $PrintServiceUrl = 'http://127.0.0.1/NewQFPrintWebService/QFPrintService.asmx' }
if (-not $PrintServiceHost -and $hostName) { $PrintServiceHost = $hostName }
($cfg.configuration.appSettings.add | Where-Object { $_.key -eq 'PrintServiceUrl' }).value = $PrintServiceUrl
($cfg.configuration.appSettings.add | Where-Object { $_.key -eq 'PrintServiceHost' }).value = $PrintServiceHost
$cfg.Save((Join-Path $Root 'web.config'))
"   PrintServiceUrl = $PrintServiceUrl  (Host: $PrintServiceHost)"

"4. IIS"
if (-not (Test-Path "IIS:\AppPools\$PoolName")) {
  New-WebAppPool $PoolName | Out-Null
  "   app pool $PoolName created"
}
Set-ItemProperty "IIS:\AppPools\$PoolName" managedRuntimeVersion 'v4.0'
Set-ItemProperty "IIS:\AppPools\$PoolName" managedPipelineMode 'Integrated'
Set-ItemProperty "IIS:\AppPools\$PoolName" startMode 'AlwaysRunning'
Set-ItemProperty "IIS:\AppPools\$PoolName" processModel.idleTimeout ([TimeSpan]::Zero)
Set-ItemProperty "IIS:\AppPools\$PoolName" recycling.periodicRestart.time ([TimeSpan]::Zero)
$appName = $AppPath.TrimStart('/')
$existing = Get-WebApplication -Site $SiteName -Name $appName -ErrorAction SilentlyContinue
if (-not $existing) {
  New-WebApplication -Site $SiteName -Name $appName -PhysicalPath $Root -ApplicationPool $PoolName | Out-Null
  "   application $SiteName$AppPath created -> $Root"
} else {
  Set-ItemProperty "IIS:\Sites\$SiteName\$appName" physicalPath $Root
  Set-ItemProperty "IIS:\Sites\$SiteName\$appName" applicationPool $PoolName
  "   application $SiteName$AppPath updated"
}
# the app pool identity writes App_Data\Logs
$acl = Get-Acl (Join-Path $Root 'App_Data')
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule("IIS AppPool\$PoolName", 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$acl.SetAccessRule($rule); Set-Acl (Join-Path $Root 'App_Data') $acl
Restart-WebAppPool $PoolName
Start-Sleep -Seconds 3

"5. health"
$url = "http://127.0.0.1$AppPath/health.ashx"
"   GET $url (Host: $hostName)"
try { (Invoke-WebRequest -UseBasicParsing $url -Headers @{Host=$hostName} -TimeoutSec 30).Content } catch { "   health failed: " + $_.Exception.Message }
"   from outside: https://$hostName$AppPath/health.ashx"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
"DONE"
