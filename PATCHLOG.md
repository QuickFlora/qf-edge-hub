# PATCHLOG - qf-edge-hub

`date · AB#xxxx · client (CompanyID) · WEB/DB · R-level · downtime · patched by · rollback`

- 2026-10-18 · AB#3958 · all shops (staging only: QuickfloraDemo / LENOVO-i5) · WEB (WEB1TEST, new IIS app /edgehub, pool qf-edge-hub) · R3 · none · Claude for Alex · Stop-WebAppPool qf-edge-hub
- 2026-10-19 · AB#3958 · all shops (staging only: QuickfloraDemo / LENOVO-i5) · WEB (WEB1TEST /edgehub, hub 1.1.0 warm signal endpoint) · R3 · none (pool recycle, PCs fell back and reconnected in 1 s) · Claude for Alex · copy back C:\WebApps\qf-edge-hub.20261019-0027.bak or Stop-WebAppPool qf-edge-hub
