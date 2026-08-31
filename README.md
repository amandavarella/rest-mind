# Rest Mind

Enforced screen breaks for a Windows machine. A repeating work/break cycle (50 minutes on, 10
minutes off by default) blocks the computer completely during the break, with a countdown on
screen. A parent password ends a break early; without it, the break has to be waited out.

Built for one household: a parent developing on macOS, a child using Windows 10/11 from a
standard (non-administrator) account.

## How the blocking actually holds

The design assumption is that anything running in the child's session can be killed, so nothing
important lives there.

- **`RestMind.Service`** runs as LocalSystem and owns the clock. A standard user cannot stop,
  reconfigure, or uninstall it.
- **`RestMind.Agent`** runs in the child's session and only draws what the service tells it to.
  It has no timer of its own.

So killing the agent doesn't shorten a break: the deadline lives in the service, the watchdog
brings the overlay back within about five seconds, and the countdown resumes where it was.

While a break is running the agent also swallows Alt+Tab, Alt+F4, Alt+Esc, Ctrl+Esc, the
Windows keys, and Alt+Space, re-asserts itself on top twice a second, and covers every monitor.
The service disables Task Manager for the duration and re-enables it afterwards.

**What it deliberately does not try to beat:** Ctrl+Alt+Del reaches the secure desktop, which no
standard-user program can intercept. From there she can sign out or reboot. That doesn't help
her: break time is wall-clock and persisted, so a reboot mid-break resumes the remaining time at
the next sign-in, and time spent powered off counts as resting anyway.

Work time is measured differently on purpose. It accrues only while the service is actually
running, so a machine that sat switched off for three hours doesn't demand a break the moment it
boots.

## Installing on the Windows machine

The quickest path is the prebuilt release, which needs nothing installed on the target machine:

1. Download
   [`RestMind-win-x64-v1.0.0.zip`](https://github.com/amandavarella/rest-mind/releases/latest)
   and extract it.
2. Open PowerShell **as Administrator** and `cd` into the extracted `RestMind` folder.
3. Run `powershell -ExecutionPolicy Bypass -File .\install.ps1`.

To build it yourself instead, on the Mac:

```bash
./scripts/publish.sh
```

That runs the tests and writes self-contained Windows binaries to `scripts/publish/` (no .NET
install needed on the target). Copy the whole `scripts/` folder to the Windows machine, then
from an **elevated** PowerShell prompt:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

It installs to `C:\Program Files\RestMind`, creates `C:\ProgramData\RestMind` locked down so only
administrators can write it, prompts for the parent password, registers the service to start at
boot, and registers the agent to start at every sign-in.

For this to mean anything, **your daughter's account must be a standard user, not an
administrator**. Check with `net localgroup Administrators`; if her account is listed, remove it
and make sure you have a separate admin account first.

To ship a newer build later, publish again, copy `scripts/` over, and run `.\update.ps1` as
administrator. It preserves the schedule, the password, and an in-progress break.

## Day-to-day use

All from an ordinary prompt (each command asks for the parent password):

```powershell
RestMind.Ctl status                 # what it is doing right now
RestMind.Ctl unlock                 # end the current break
RestMind.Ctl pause --minutes 30     # suspend enforcement for a while
RestMind.Ctl resume                 # cancel a pause
RestMind.Ctl break                  # start a break immediately
RestMind.Ctl show-config            # print the schedule
RestMind.Ctl set-password           # change the password (needs an admin prompt)
```

There is also a "Parent unlock" box on the overlay itself, which does the same thing as
`unlock`.

A pause always ends into a fresh, full work period, so it can never drop her straight into a
break.

## Changing the schedule

Edit `C:\ProgramData\RestMind\config.json` from an administrator editor. The service notices the
change within a second or two and applies it; no restart or reload command needed.

```json
{
  "version": 1,
  "schedule": {
    "days": [
      {
        "day": "Monday",
        "enabled": true,
        "activeStart": "07:00:00",
        "activeEnd": "21:00:00",
        "workMinutes": 50,
        "breakMinutes": 10
      }
    ]
  },
  "preBreakWarningMinutes": 2,
  "passwordHash": "210000.<salt>.<hash>"
}
```

Leave `passwordHash` alone; change it with `RestMind.Ctl set-password` rather than by hand.

Each day of the week gets its own entry. Outside `activeStart`–`activeEnd` nothing is enforced,
and `"enabled": false` turns a day off entirely. If `activeEnd` is earlier than `activeStart` the
window wraps past midnight. A break that has already started always runs to completion, even if
the window closes underneath it, so working right up to the edge is not a way to skip it.

`preBreakWarningMinutes` controls the tray balloon that warns her to save her work; set it to `0`
to turn the warning off.

### School hours

Breaks never happen during the school day, **Monday to Friday between 08:30 and 15:10**. Within
those hours the machine behaves as though Rest Mind were not installed, so a break can't cover
the screen during a lesson.

This sits on top of the active window rather than replacing it. On a weekday the default 07:00
to 21:00 window therefore enforces breaks from 07:00 to 08:30 and again from 15:10 to 21:00; at
weekends the whole window is enforced.

Two details worth knowing. A break already running when the school day starts is **cancelled**,
not paused, unlike the active-window boundary where a break runs to completion. And the school
day doesn't eat into a work period: the afternoon always starts from a full 50 minutes rather
than resuming whatever was left over at 08:30.

These times are currently fixed in code, in `src/RestMind.Core/SchoolHours.cs`, rather than being
part of `config.json`.

## Repository layout

| Project | What it is |
|---|---|
| `src/RestMind.Core` | The scheduling engine, config, password hashing, pipe protocol. No Windows APIs, so it runs and tests on macOS. |
| `src/RestMind.Core.Tests` | xUnit suite covering the cycle, active hours, reboot resume, and pause behaviour. |
| `src/RestMind.Service` | The Windows service: clock, named-pipe server, agent watchdog, Task Manager policy. |
| `src/RestMind.Agent` | WPF overlay and tray icon for the child's session. |
| `src/RestMind.Ctl` | The parent's command line. |

The service and agent talk over a local named pipe (`\\.\pipe\restmind`) using newline-delimited
JSON. Any signed-in user may open the pipe, which they must, since the overlay runs as the
child. Authority comes from the password checked inside the service, never from who is allowed
to connect.

## Development

```bash
dotnet test          # the Core suite, runs on macOS
dotnet build         # all five projects compile on macOS via EnableWindowsTargeting
```

The Windows projects compile but cannot run on macOS. Everything with interesting logic lives in
`RestMind.Core` specifically so it can be tested before it ever reaches the target machine.

### Checking it on Windows

The unit tests cover the scheduling logic. These are the things only a real machine can confirm:

1. **Coverage** — with a short cycle configured, the overlay covers every monitor and stays on
   top over a full-screen game or video.
2. **Keys** — Alt+Tab, Alt+F4, the Windows key, Alt+Esc and Ctrl+Esc do nothing during a break.
3. **Killing the agent** — end `RestMind.Agent.exe` mid-break; it should return within about five
   seconds with the countdown continuing, not restarted.
4. **Reboot mid-break** — restart during a break; after sign-in the overlay returns with roughly
   the remaining time.
5. **Password** — the correct password ends the break, a wrong one is refused.
6. **Task Manager** — disabled during a break, available again afterwards. Then kill the service
   mid-break (`taskkill /F`) and confirm Task Manager still comes back, since the service clears
   the policy on its next start.
7. **Standard user limits** — from her account, `sc stop RestMind` is denied, `config.json` cannot
   be saved, and the `RestMindAgent` run key cannot be deleted.

Logs are at `C:\ProgramData\RestMind\logs\restmind-<date>.log`.

## Uninstalling

From an elevated prompt:

```powershell
.\uninstall.ps1              # removes everything
.\uninstall.ps1 -KeepData    # keeps the schedule and password for a reinstall
```
