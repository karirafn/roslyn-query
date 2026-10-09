---
status: accepted
---

# 0002. Behavioral cold-start integration test against the built apphost

**Date:** 2026-10-09

## Context
The daemon-stdio hang is invisible to the in-process pipe integration tests
(`DaemonIntegrationTests`), which never launch the real binary. A regression test
must run the actual apphost cold and observe that a piped stdout reaches EOF while
the daemon survives — the exact failure mode shape — and must not be able to pass
by the daemon dying.

## Decision
Add `ColdStartIntegrationTests` that launch the built apphost against a static
minimal fixture `.slnx` (a zero-package-reference `net10.0` classlib copied to the
test output), read stdout to EOF under a ~30s deadline, and assert the fixture's
PID file names a still-live process before stopping the daemon. An `AppHostLocator`
resolves the apphost-vs-`dotnet <dll>` launch form, mirroring the production
`Environment.ProcessPath` resolution. The CI build runs on an
`[ubuntu-latest, windows-latest]` matrix so both spawn paths are exercised.

## Consequences
The hang is caught on both OSes in CI. The fixture layout and the apphost output
path become test dependencies; the locator skips (not fails) when the binary is
absent, so the suite degrades gracefully if the output layout changes. CI duration
roughly doubles.
