# ServiceLauncher

A small, standalone, self-contained launcher that can bring services back
up remotely, from a plain authenticated URL - no VPN, no remote desktop
session required - or locally, from a GUI with a tray icon, when you're
actually at the machine. Built for the specific problem of a grid (or any
other service) living on a drive that's deliberately *not* mounted at
boot (a VHDX kept offline so it's safe to back up), plus other,
unrelated services that need the same kind of "start me from my phone"
trigger.

This is **not** part of the OpenSim-Confluence repo and doesn't assume
anything OpenSim-specific beyond the example config - it's a generic
process launcher. It happens to ship with an example `services.json`
tuned for Casperia-Dev because that's the concrete case it was built for.

## Why this exists, and why it's not "just a .bat file with a scheduled task"

The thing you'd want to remotely trigger is normally reachable *through*
the service itself (a web UI, in this case). But if the service isn't
running yet, there's nothing to click "start" on - so the trigger has to
live somewhere that can come up independently, before the real service
does. Concretely, this deliberately:

- **Lives outside any VHDX/offline volume**, so it can start at boot
  even when the drive the actual services live on isn't mounted yet.
- **Mounts that volume itself when needed** (`Mount-DiskImage`, no Hyper-V
  role required) before trying to launch anything that depends on it.
- **Never auto-mounts at boot on its own** - only when a launch is
  actually requested. Mounting ahead of time would defeat the entire
  reason the volume is kept offline (so it's always safe to back up the
  VHDX file without worrying about in-flight writes).

## Setup

1. Build a self-contained single-file exe:
   ```
   dotnet publish -c Release
   ```
   Output lands in
   `bin\Release\net8.0-windows\win-x64\publish\ServiceLauncher.exe` - one
   file, no separate .NET runtime install needed on the target machine.

2. Copy `ServiceLauncher.exe` to a folder **outside any volume that
   needs mounting first** - e.g. `C:\ServiceLauncher\`.

3. Copy `services.example.json` next to it, rename to `services.json`,
   and edit it for your actual setup: exe paths, working directories,
   ports, and which services should auto-launch which others. See the
   comments in `Models.cs` for what each `Liveness.Type` means and when
   to use it.

4. Run `ServiceLauncher.exe` once by hand first, to confirm it starts
   cleanly and generates `token.txt` (a random access token - back this
   up somewhere private, e.g. a password manager; anyone with it can
   trigger launches on this machine). The log line it prints on startup
   gives you the exact URL to bookmark. Run this way (interactively,
   not via Task Scheduler) it also opens the GUI - see below.

5. Register it to start at boot **without needing anyone logged in**:
   Task Scheduler → Create Task → Trigger: "At startup" → Action: run
   `ServiceLauncher.exe` → check "Run whether user is logged on or not."
   This is the one piece that has to be Windows-specific for this
   particular build (`HttpListener`, `Mount-DiskImage`, and
   `Process.Start` all assume Windows) - see the porting note below for
   other platforms. Running this way is headless (no interactive
   desktop session to show a window in), so no GUI appears - only the
   HTTP listener and crash monitor run.

6. Forward the port you configured (`ListenPort`, default 8090) on your
   router to this machine. This is the one step that puts the endpoint
   on the open internet - the token is what keeps it from being an open
   door, not obscurity, so treat that file like a password. Note this
   requires the Task Scheduler task from step 5 to run elevated
   (Administrator) - binding a port to all interfaces (rather than just
   `127.0.0.1`) needs it on Windows. Run without elevation and
   ServiceLauncher falls back to loopback-only automatically (the log
   says so) - the GUI still works locally, but nothing outside the
   machine can reach it.

## GUI

Launching `ServiceLauncher.exe` interactively (not via the headless Task
Scheduler task) opens a window: one row per configured service with its
live status and a Launch button, plus Launch All. The HTTP listener
keeps running underneath it either way - the GUI is an additional local
control surface, not a replacement for remote triggering.

- **If it's already running headless** (the Task Scheduler copy already
  owns the port), running the exe again doesn't compete with it - the
  second copy becomes a lightweight GUI client of the first, talking to
  its API over loopback instead of trying to start its own listener.
- **Tray icon** - closing the window (or minimizing it) hides it to the
  system tray instead of exiting; only the tray menu's "Exit" actually
  stops the process. The icon itself is status-colored (green = every
  configured service is up, red = none are, yellow = a mix), so you can
  tell at a glance without opening the window.
- **F12** opens a structured editor for `services.json` (add/edit/remove
  services and volumes) instead of hand-editing the JSON file. Saving
  writes the file but doesn't hot-reload the running instance - restart
  ServiceLauncher afterward for changes to take effect.
- **Check for Updates** (tray menu) is manual-only - it never runs on
  its own, on a timer, or at startup. It checks this repo's GitHub
  Releases and only reports what it finds; it never downloads or
  installs anything.

## Using it

- `GET /launch?service=all&token=...` - launches every configured
  service (each one skipped if it's already up).
- `GET /launch?service=<id>&token=...` - launches one specific service
  (and whatever it auto-launches).
- `GET /status?token=...` - reports what's currently running, no launch
  side effect. Handy to check from your phone before deciding to launch
  anything.
- `GET /api/launch?...` / `GET /api/status?...` - same as above, JSON
  instead of HTML. What the GUI itself calls; also handy if you're
  scripting against this rather than viewing it in a browser.
- `GET /launch?service=discover&token=...` (or `/api/launch?...`) -
  scans for and launches any region not already covered by an explicit
  `Services` entry - see Region discovery below. `service=all` already
  includes this; use `discover` on its own only if you want *just* newly
  found regions without touching the explicitly configured ones.

### Region discovery

If your grid lets avatars order new regions from an in-world store, new
region folders can appear under `Simulators\` at any time - a static
`services.json` list can't know about those ahead of time. Set
`RegionDiscovery` in `services.json` to have ServiceLauncher scan for
them itself, mirroring what the grid's own control batch script's
"Discover & Launch Store-Ordered Regions" option does:

```json
"RegionDiscovery": {
  "Enabled": true,
  "SimulatorsDirectory": "S:\\PATH\\TO\\YOUR\\OpenSim\\Simulators",
  "ExePath": "S:\\PATH\\TO\\YOUR\\OpenSim\\OpenSim.exe",
  "WorkingDirectory": "S:\\PATH\\TO\\YOUR\\OpenSim",
  "RequiresVolume": "servers",
  "StartupTimeoutSeconds": 60
}
```

Any subfolder of `SimulatorsDirectory` containing an `OpenSim.ini` that
isn't already referenced by one of your explicit `Services` entries (by
its `-inifile=Simulators\<folder>\OpenSim.ini` argument - nothing extra
to keep in sync) gets launched with `-inifile=Simulators\<folder>\
OpenSim.ini -background=true`. Since a freshly discovered region's port
isn't known ahead of time the way an explicitly configured one's is,
discovered regions get "process"-type liveness (disambiguated by their
own `-inifile=` argument) rather than "http" - no need to go find the
port. Two things explicit services get that discovered ones don't:
they're not watched by crash auto-restart, and they don't show up in
`/status` between discovery runs (both need an explicit `services.json`
entry, same as before).

## Porting to Linux / Docker / a VM host

The volume-mount and process-launch pieces
(`VolumeManager.cs`/`ServiceOrchestrator.StartProcess`) are the only
genuinely Windows-specific parts. The overall shape - a tiny always-on
listener outside your service's own storage, an authenticated trigger,
pluggable liveness checks, chained auto-launch with delays - carries
over directly to systemd (`systemctl start`), Docker (`docker start` /
`docker compose up`), or a VM host's own API (start a guest). Swap
`VolumeManager`'s `Mount-DiskImage` call for whatever your platform's
equivalent is (`mount`, `docker volume`, attaching a cloud disk, etc.)
and the rest of the design - config-driven services, HTTP trigger,
liveness checks, chained auto-launch - needs no other changes.

## Design notes worth knowing before you edit this

- **Liveness checks matter more than they look.** Every OpenSim.exe
  region instance shares the exact same exe name and path - a plain
  "is a process named OpenSim.exe running" check can't tell Sandbox
  apart from Welcome_Center apart from a Store-ordered region. Use
  `"http"` liveness (a raw TCP connect to each region's own port)
  wherever a service has a distinguishable port; reserve `"process"`
  for genuinely singleton programs.
- **"process" liveness works across a 32/64-bit mismatch on purpose.**
  It resolves a running process's exe path via `QueryFullProcessImageName`
  (`LivenessChecker.QueryImagePath`), not `Process.MainModule` - the
  latter throws ("Unable to enumerate the process modules") when a
  64-bit ServiceLauncher inspects a 32-bit process, confirmed live
  against a real 32-bit MajorBBS (`wgsappgo.exe`). Silently falling back
  to MainModule here would make "process" liveness permanently report
  such a service as down even while it's genuinely running.
- **Everything is idempotent.** Hitting `/launch` again for something
  already running just confirms it's up rather than starting a second
  copy - a stale bookmark or a double-tap on a slow phone connection
  can't cause harm.
- **A service that never comes up doesn't cascade.** If a service's own
  startup timeout expires, its `AutoLaunch` chain is skipped rather than
  launching things that depend on something that isn't actually there.
- **The access token uses a constant-time comparison**
  (`CryptographicOperations.FixedTimeEquals`), not `==` - this endpoint
  is meant to be reachable from the open internet, so a naive string
  comparison's timing behavior is a real, not hypothetical, attack
  surface here.
- **Crash auto-restart is opt-in, per service** (`AutoRestart` in
  `services.json`: `Enabled`, `RestartAttempts`, `CrashConfirmSeconds`,
  `AttemptDelaySeconds`) and runs headless the same as the HTTP listener
  - it's watching whether or not the GUI is open. It only reacts to a
  service it has already seen come up at least once; it won't chase
  something that was never actually running. Deliberately does **not**
  include a machine-reboot escalation stage the way its MBBSLauncher
  inspiration does - that's reasonable for a single program on its own
  machine, but this host runs several independent services (e.g. a
  Robust server plus multiple regions), and rebooting because one of
  them crashed would take all the others down with it.
