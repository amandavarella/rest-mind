# Agent Guidelines

Guidelines for AI agents working in this codebase. The global `~/Code/AGENTS.md` still applies;
this file adds to it and wins where the two disagree.

## Steps to run before starting

- Read `README.md`. It documents the behaviour from the parent's point of view and is what users
  actually read.
- `dotnet` is **not on `PATH`** on this machine. It lives in `~/.dotnet`, installed by Microsoft's
  script rather than Homebrew (the cask needs an interactive sudo password and fails in an agent
  session). Prefix commands with `export PATH="$HOME/.dotnet:$PATH"`.

## Commands

```bash
export PATH="$HOME/.dotnet:$PATH"

dotnet build                      # all five projects; compiles on macOS
dotnet test                       # the RestMind.Core suite

# A single test class or method, by substring:
dotnet test --filter "FullyQualifiedName~SchoolHoursTests"
dotnet test --filter "FullyQualifiedName~BreakSchedulerTests.FirstTick_StartsAFullWorkPeriod"

./scripts/publish.sh              # runs the tests, then builds win-x64 binaries for release
```

`scripts/publish.sh` adds `~/.dotnet` to `PATH` itself, so it works without the export.

## What can and cannot be verified here

Development happens on macOS; the app runs on Windows. This shapes everything.

- **`RestMind.Core` targets `net8.0` and must stay free of Windows APIs.** It holds the scheduling
  engine, config, password hashing, and the pipe message types, and it is the only project with
  tests. Anything with logic worth trusting belongs here.
- **`RestMind.Service`, `RestMind.Agent`, and `RestMind.Ctl` target `net8.0-windows`.** They build
  on macOS only because of `<EnableWindowsTargeting>true</EnableWindowsTargeting>`, and they cannot
  run here. A green build says almost nothing about them.
- **Never claim Windows behaviour was verified.** P/Invoke, the keyboard hook, service
  registration, and the registry policy can only be confirmed on the target machine. Say what
  still needs checking there rather than implying it works.

When documenting a config file or an output format, generate it and read it back rather than
describing what the code looks like it should produce.

## Architecture

The design assumption is that **anything running in the child's session can be killed**, so
nothing important lives there.

- **`RestMind.Service`** runs as LocalSystem, owns the work/break clock, and is the only component
  with authority. It persists state, toggles the Task Manager policy, serves the named pipe, and
  relaunches the agent.
- **`RestMind.Agent`** runs in the child's session and renders whatever status arrives over the
  pipe. It has no timer and makes no decisions.
- **`RestMind.Ctl`** is the parent's CLI, a short-lived pipe client.

The consequence to preserve in any change: **killing the agent must never shorten a break.** The
deadline lives in the service and is persisted, so a killed or crashed overlay comes back with the
countdown intact.

### Invariants that are easy to break

- **Work time and break time are measured differently, on purpose.** Break time is wall-clock
  (`BreakEndsAtUtc`), so a reboot cannot shorten a break and time powered off still counts as
  rest. Work time accrues only between consecutive work ticks, so a machine that sat switched off
  does not demand a break the moment it boots.
- **`SchedulerState.AccruingWork` gates that accrual.** Only a tick whose predecessor also ended
  in a work period may credit elapsed time. This is what stops the tick that ends a break, a
  pause, or an out-of-hours stretch from immediately eating into the fresh work period. Any new
  early-return path in `Tick` must leave the flag false.
- **A break runs to completion at the active-hours boundary, but is cancelled at the school-hours
  boundary.** These differ deliberately: finishing the break stops "work up to the edge of the
  window" from being a way to skip it, but that reasoning does not apply to a lesson starting.
- **Authority comes from the password checked inside the service, never from the pipe ACL.** Any
  signed-in user may open the pipe, which they must, since the overlay runs as the child. Adding a
  state-changing command means adding a password check in `Worker.HandleCommand`.
- **The Task Manager policy must always revert.** `PolicyEnforcer` clears it on break end, on
  shutdown, and defensively on startup. Leaving a machine with Task Manager permanently disabled
  is a far worse failure than an escapable break.
- **`StateStore.Load` clears `LastTickUtc` and `AccruingWork`**, so downtime between service runs
  is never credited as screen time.

### Testing the scheduler

`BreakScheduler` takes an injected `IClock`, so tests drive time by hand through `TestClock`.
`SchedulerHarness.Run` ticks in realistic small steps, which matters because a single large jump
is clamped to `MaxTickCreditSeconds` and would not accrue the work you expect.

Tests must sit outside school hours (Monday to Friday, 08:30 to 15:10) unless they are exercising
that rule, or they will pass for the wrong reason. `BreakSchedulerTests` is based at a Monday
afternoon for exactly this reason.

## Cut a release after every merge to `main`

This app is not run from a checkout. It runs on a Windows machine that only ever receives
published binaries, so **a merged PR changes nothing for the user until a release is built.**
Treat the release as part of merging, not as a separate task to be asked about.

**When to skip it:** if the merge changes nothing that ships, no release is needed. The bundle
contains the three executables, the three `.ps1` scripts, and `README.md`. A change to
`AGENTS.md`, to tests, or to `scripts/publish.sh` alone ships nothing, so it does not earn a 70MB
release.

Otherwise, from the updated main checkout:

```bash
git checkout main && git pull          # release exactly what is on main
./scripts/publish.sh                   # runs the tests, then builds win-x64 binaries
```

`publish.sh` fails the build if the tests fail. Do not publish a release from a red test run.

Then assemble the bundle. The layout matters: `install.ps1` defaults to looking for the binaries
in a `publish` folder beside itself, so it must be laid out exactly this way.

```
RestMind/
  install.ps1
  uninstall.ps1
  update.ps1
  README.md
  publish/          <- everything scripts/publish.sh produced
```

Zip that `RestMind` folder as `RestMind-win-x64-v<version>.zip` and attach it:

```bash
gh release create v<version> <path-to-zip> --title "Rest Mind v<version>" --notes "..."
```

Build the bundle in the scratchpad directory, not in the repo, and delete it afterwards. It is
roughly 160MB unzipped and 70MB zipped, and `publish/` is gitignored for a reason.

Confirm the binaries really contain the change rather than trusting the build, for example
`strings scripts/publish/RestMind.Core.dll | grep SchoolHours`.

### Version numbers

Bump the **minor** version for a behaviour change the user would notice (a new rule about when
breaks happen, a new command), and the **patch** version for a fix that changes nothing about how
it behaves. There is no automated versioning; read `gh release list` for the last one.

### Release notes

Write them for a parent installing this on a family computer, not for a developer. Say what
changes about when the machine gets blocked. Always include the install steps, since that is what
someone lands on when they follow the download link, and point an existing install at
`update.ps1` rather than `install.ps1`, because update preserves the schedule, the password, and
a break in progress.

### Do not hardcode the version in the README

Link to `releases/latest` and describe the asset generically. A README that names `v1.0.0` goes
stale the moment the next release ships.

## Keeping documentation up to date

`README.md` is written for the parent using the app, not for a developer. Update it in the same
change whenever you alter **when the machine gets blocked**, the config file format, the
`RestMind.Ctl` commands, or the install and update steps.

The README is also shipped inside the release bundle, so a README change on its own is still a
reason to cut a release.

## GitHub

The repository is `amandavarella/rest-mind` under the **personal** account. The `gh` wrapper picks
the account from the origin URL, so never run `gh auth switch`. When creating a repository, set
the origin remote before creating it, otherwise the wrapper cannot tell which account to use.

Run merges from the main checkout, not from inside a worktree, or the local branch deletion fails
with `cannot delete branch ... used by worktree`. If that happens, remove the worktree first, then
delete the local and remote branches.
