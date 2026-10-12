---
status: accepted
---

# 0004. Daemon availability protocol: status discriminator, versioned handshake, concurrent serving

**Date:** 2026-10-12

## Context

The client had a single signal — "got a complete response or not" — so a missing, loading, busy, reloading, crashed, or wedged daemon all collapsed into "unavailable". The recovery that followed (a 15s poll, then an in-process `RunDirect` full workspace load) duplicated the daemon's own load, serialized parallel agents behind a single-instance pipe, and could still exceed an agent's Bash timeout. Several concrete defects compounded this: the pipe opened only after the workspace finished loading, so a long load triggered `RunDirect` alongside the daemon's own load; a command that threw dropped the connection with no response, and the client retried the failing command for 15s; the client read the response with no deadline, so a wedged daemon blocked every client.

## Decision

Replace the single-bit signal with an explicit status protocol, concurrent serving, and bounded client deadlines with a documented retry exit code.

- **Status discriminator byte leads every response frame.** `ResponseStatus` is a single byte (`Result=1`, `Loading=2`, `Reloading=3`, `Error=4`, `VersionMismatch=5`; `0` reserved invalid). It is the first wire element so a cold daemon can answer `Loading` before its workspace exists, and it doubles as the version-skew detector: an old daemon's response begins with the high byte of a big-endian stdout-length int32, which is `0x00` for any sub-16 MB payload — an invalid status the client classifies as "malformed/short reply" and replaces. Values start at `1` so a zero or truncated write from a dead daemon is always invalid.
- **Versioned request handshake.** The request frame carries a raw leading `protocolVersion` int plus the client's informational version. On mismatch the daemon replies `VersionMismatch` and exits; the client cold-starts a replacement. This keeps one pipe name per solution (a version in the pipe name would orphan the old daemon holding a workspace and break `daemon stop`).
- **Concurrent serving.** The accept loop uses `MaxAllowedServerInstances` and hands each connection to its own task, capturing the immutable `Solution` snapshot at accept. Parallel queries share one snapshot's compilation cache. The 30-minute idle timeout fires only when no request has arrived for the interval *and* no request is in flight.
- **Pipe opens before the workspace load; warm-up before Ready.** A background task runs the load and then `GetCompilationAsync` for every project; requests arriving before that completes get `Loading`. This keeps the first real query from paying a cold `GetCompilationAsync` inside the 60s query timeout.
- **Bounded client deadlines terminating in exit 75.** The handshake must complete within 5s (else the daemon is wedged — kill after `IsDaemonProcess` + `IsRecordedDaemon`, then cold-start); the response read has a 75s deadline (else exit 75 `daemon did not respond; retry`); a cumulative loading wait of ~90s exits 75 `daemon still loading; retry - load continues in background`. Exit 75 (`EX_TEMPFAIL`, BSD `sysexits.h`) is the public "transient — retry" contract. `RunDirect` runs only when the spawned daemon process is confirmed not alive; liveness is read from the spawned `Process.HasExited`, never from a not-yet-written PID file.

See [ADR 0002](0002-cold-start-behavioral-integration-test.md) for the real-apphost integration harness these paths are proved against, and [ADR 0003](0003-daemon-spawn-avoids-caller-stdio-inheritance.md) for the spawn mechanics this builds on.

## Considered Options

- **Stay serial with a longer connect wait, or a "busy" reply** — rejected; parallel agents still queue behind a slow query, and a busy client can only wait.
- **Client waits indefinitely while loading** — rejected; collides with the 2-minute Bash timeout, reproducing a silent hang.
- **Keep the `RunDirect` fallback after a timeout** — rejected; doubles CPU and memory during the load.
- **Serve immediately and warm compilations in the background** — rejected; queries can still hit the 60s timeout mid-warm-up and report failure instead of retry.
- **Version in the pipe name** — rejected; the old daemon lingers holding a workspace, and per-solution PID files break `daemon stop`.

## Consequences

The wire is backward-incompatible by design: an old daemon left running after a `dotnet tool update` is detected via its malformed reply (or the handshake deadline) and replaced automatically. Concurrent queries raise peak memory, accepted because they share the snapshot's compilation cache. `list-projects` on a cold daemon now waits for full compilation warm-up — it is the load-confirmation call. Killing a PID on handshake failure could target an unrelated process after PID reuse, mitigated by the `IsDaemonProcess` exe-name check composed with the `IsRecordedDaemon` start-time check, both fail-closed.
