# qf-edge-hub

QuickFlora's push hub for shop PCs. Epic **AB#3961** (move shop PCs to a push model), task **AB#3958**.
One small IIS application on the web server (WEB1AWS 172.30.0.60 in production; WEB1TEST on staging)
that holds one persistent SignalR connection per shop PC and pushes print jobs to it, so the
QuickFlora Print App (6.1.0+, repo `QuickFlora/QuickFloraPrintApp`) stops polling the database.

## Why

Query Store on the production SQL Server showed 84% of all queries (about 29.6 M a day) were the print
app asking "anything to print?" every few seconds from every shop PC, around the clock. Nothing was
broken; it was pure cost and lost headroom. With the hub, the PCs ask nothing: the hub tells them.

## What it does

```
Phone Order / POS pages  --INSERT-->  POSPrintRequest, POSPrintProcess, ... (unchanged tables)
                                            |
                 qf-edge-hub Dispatcher: ONE loop, 1 tick/second, 2 batched queries for ALL connected PCs
                                            |
          POSPrintProcess row Read=1 ----> push  Job{slno, PrintText, PrintText1, PrintText2, FileName}
          any *PrintRequest Active=1 ----> call existing PingPOSForPrinting once (builds the job), push Nudge
                                            |
                              SignalR group per CompanyID+DivisionID+DepartmentID+TerminalName
                                            |
                      Print App 6.1: writes the job to its SQLite edge queue, claims it with the
                      existing UpdateReadPOSForPrinting, prints, confirms with UpdatePOSForPrinting
```

- **Hub** (`src/EdgeHub.cs`): a PC connects to `<app>/signalr?c=&d=&p=&t=` and joins its terminal's
  group. Server-to-client messages: `Job`, `Nudge`, `Alert`. Client-to-server: `Hello`, `Claimed`.
- **Dispatcher** (`src/Dispatcher.cs`): the one-per-second loop. It only runs while at least one PC is
  connected and does **no database writes**. A job is pushed once and re-offered after 60 s if no PC
  claimed it, so a PC that vanished mid-push loses nothing (its long-poll fallback also still sees the row).
- **Cache pull** (`site/cache.ashx`, `src/CacheHandler.cs`): `?table=Companies&c=&d=&p=&since=` for the
  print app's EdgeSync. Returns shop-facing company columns only - no keys, passwords, bank or tax IDs.
- **Health** (`site/health.ashx`): connected terminals, tick counters, last error. Plain text.
- **Warm signal** (`site/signal.ashx`, `src/SignalHandler.cs`): `?type=warm&c=&d=&p=&t=` called by the print web
  service's `PrintDispatcher.SignalWarm` (repo `PrintWebServices`, appSetting `EdgeHubSignalUrl`). The hub pushes
  `Warm` to the terminal's group and the print app (6.1.1+) runs its existing warm-up (AB#3796) - a PC in push mode
  never polls, so the in-memory signal alone would never reach it. Reply `{"connections":N,"pushed":bool}`;
  0 connections means the PC is long-polling and the print web service's own signal serves it.

### Feed choice: dispatcher loop, not trigger -> Service Broker

AB#3958 named trigger -> Service Broker -> hub as the feed. The hub uses a single dispatcher loop instead:

| | trigger + Service Broker | dispatcher loop (chosen) |
|---|---|---|
| Schema change on prod | yes: 6 triggers, queue, service, contract, activation proc; `ALTER DATABASE ... ENABLE_BROKER` (Enterprise has `is_broker_enabled = 0`) - R4, Ved, maintenance window | **none** |
| Risk to the pages that write the tables | a failing trigger fails the INSERT (Phone Order save) | none - read-only |
| Latency | sub-second | <= 1 s (acceptance is "within 1 s") |
| DB load | ~0 when idle | 2 statements/second for the whole fleet, 0 when no PC is connected. Today: ~84 statements/second from polling |
| Rollback | drop triggers (R4 again) | stop the app pool |

The loop is the same pattern the staging print web service already runs for its long-poll
(`PrintDispatcher.vb`, 0.5 s tick), measured there at ~10 page reads and 0 ms CPU per terminal per check
on the existing indexes. The trigger path stays available later if sub-second matters.

## Build

No Visual Studio, no NuGet restore on the server. `build.ps1` compiles `src\*.cs` with the `csc.exe`
that ships with .NET Framework 4.x into `bin\QfEdgeHub.dll` and copies the vendored DLLs from `lib\`
(see `lib/README.md`: SignalR 2.4.3, Katana 4.2.3, Newtonsoft.Json 13.0.4 - all verified on nuget.org).
C# 5 syntax only (same rule as the print app). `csc /langversion:5` enforces it.

## Deploy (staging first; production only after a human PASS on the ADO case)

```powershell
powershell -ExecutionPolicy Bypass -File deploy\install-hub.ps1 -Branch main
```

Creates app pool `qf-edge-hub` (.NET 4.0 Integrated, AlwaysRunning) and the application `/edgehub`
under the server's existing site (no web.config or bin change to any existing site). The
`ConnectionString` is copied from the print web service's web.config **on the server** and is never
committed. Rollback: `Stop-WebAppPool qf-edge-hub` - every PC falls back to its long-poll on its own.

Change Card: Layer WEB · server WEB1AWS 172.30.0.60 (prod) / WEB1TEST (staging) · blast radius R3
(new IIS app only; if wrong, PCs fall back to polling) · downtime none · rollback stop the app pool.

## Not in this version

- Warm-up pushes. `WaitForPrintJobOrWarm` in the print web service keeps warm-up signals in its own
  memory (AB#3796); for a PC in hub mode they need a one-line call from `SignalWarm` to the hub. Follow-up.
- `InventoryItems` cache source (1,487 rows for the demo shop; wide table). Follow-up once Companies is proven.
- A real `rowversion` column on `Companies`/`InventoryItems` (schema change, Ved, R4). Until then the
  cache endpoint uses `BINARY_CHECKSUM`/`CHECKSUM_AGG` as the change fingerprint.
