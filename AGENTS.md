# Rest Mind agent guidelines

Project-specific rules. The global `~/Code/AGENTS.md` still applies; this file adds to it.

## Cut a release after every merge to `main`

This app is not run from a checkout. It runs on a Windows machine that only ever receives
published binaries, so **a merged PR changes nothing for the user until a release is built**.
Treat the release as part of merging, not as a separate task to be asked about.

So: immediately after merging a PR to `main`, from the updated main checkout, do all of this
without waiting to be asked.

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

### Version numbers

Bump the **minor** version for a behaviour change the user would notice (a new rule about when
breaks happen, a new command), and the **patch** version for a fix that changes nothing about
how it behaves. There is no automated versioning; read `gh release list` for the last one.

### Release notes

Write them for a parent installing this on a family computer, not for a developer. Say what
changes about when the machine gets blocked. Keep the install steps in every release, since
that is what someone lands on when they follow the download link.

### Do not hardcode the version in the README

Link to `releases/latest` and describe the asset generically. A README that names
`v1.0.0` goes stale the moment the next release ships.

## Verify against the real behaviour, not the plan

Windows-only code (`RestMind.Service`, `RestMind.Agent`, `RestMind.Ctl`) compiles on macOS but
cannot run there, so a green build says very little about it. Anything genuinely testable belongs
in `RestMind.Core`, which has no Windows dependencies and is covered by the xUnit suite.

When documenting a config file or an output format, generate it and read it rather than
describing what the code looks like it should produce.
