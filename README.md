# ServiceLauncher

A small, standalone, self-contained launcher that can bring services back
up remotely, from a plain authenticated URL - no VPN, no remote desktop
session required. Built for the specific problem of a grid (or any other
service) living on a drive that's deliberately *not* mounted at boot (a
VHDX kept offline so it's safe to back up), plus other, unrelated
services that need the same kind of "start me from my phone" trigger.

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
   Output lands in `bin\Release\net8.0\win-x64\publish\ServiceLauncher.exe`
   - one file, no separate .NET runtime install needed on the target
   machine.

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
   gives you the exact URL to bookmark.

5. Register it to start at boot **without needing anyone logged in**:
   Task Scheduler → Create Task → Trigger: "At startup" → Action: run
   `ServiceLauncher.exe` → check "Run whether user is logged on or not."
   This is the one piece that has to be Windows-specific for this
   particular build (`HttpListener`, `Mount-DiskImage`, and
   `Process.Start` all assume Windows) - see the porting note below for
   other platforms.

6. Forward the port you configured (`ListenPort`, default 8090) on your
   router to this machine. This is the one step that puts the endpoint
   on the open internet - the token is what keeps it from being an open
   door, not obscurity, so treat that file like a password.

## Using it

- `GET /launch?service=all&token=...` - launches every configured
  service (each one skipped if it's already up).
- `GET /launch?service=<id>&token=...` - launches one specific service
  (and whatever it auto-launches).
- `GET /status?token=...` - reports what's currently running, no launch
  side effect. Handy to check from your phone before deciding to launch
  anything.

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
