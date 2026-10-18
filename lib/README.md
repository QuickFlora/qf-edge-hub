# Vendored DLLs (no NuGet restore on the server)

All from nuget.org, copied out of the .nupkg `lib\net45` (Owin: `lib\net40`) folders. `build.ps1`
copies them to `bin\` next to `QfEdgeHub.dll`.

| DLL | NuGet package | Version |
|---|---|---|
| Microsoft.AspNet.SignalR.Core.dll | Microsoft.AspNet.SignalR.Core | 2.4.3 |
| Microsoft.AspNet.SignalR.SystemWeb.dll | Microsoft.AspNet.SignalR.SystemWeb | 2.4.3 |
| Microsoft.Owin.dll | Microsoft.Owin | 4.2.3 |
| Microsoft.Owin.Host.SystemWeb.dll | Microsoft.Owin.Host.SystemWeb | 4.2.3 |
| Microsoft.Owin.Security.dll | Microsoft.Owin.Security | 4.2.3 |
| Owin.dll | Owin | 1.0.0 |
| Newtonsoft.Json.dll | Newtonsoft.Json | 13.0.4 |

Verified on nuget.org 2026-10-18 (`https://api.nuget.org/v3-flatcontainer/<id>/index.json`).
