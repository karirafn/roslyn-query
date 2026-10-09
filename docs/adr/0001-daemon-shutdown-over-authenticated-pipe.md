# 0001 — Daemon shutdown over the authenticated pipe, kill-by-PID as guarded fallback

## Context
`daemon stop` must terminate the right process. A PID file with a reused PID previously let `stop` kill an unrelated process, because identity was matched by executable base name only — which matches any `dotnet` process under the `dotnet` host.

## Decision
Stop a daemon by sending a `--shutdown` verb over the existing authenticated named pipe (`CurrentUserOnly` on Unix, owner-SID ACL on Windows); the pipe name is derived from the solution path, so a successful round-trip proves identity. Fall back to `Process.Kill()` only when the pipe is unreachable, and only after verifying the live process's start time matches the start time recorded in the PID file. PID files record `pid` plus the process start-time ticks; an unverifiable process (start time unreadable or mismatched) is never killed (fail closed).

## Consequences
- Graceful shutdown lets the daemon clean up its own PID file and dispose its workspace instead of dying mid-reload.
- PID reuse can no longer cause a wrong-process kill.
- A daemon started by an older version uses a different pipe name (the hash algorithm changed from MD5 to truncated SHA256), so new `daemon stop` does not find it; it exits on its idle timeout. Accepted (see issue #92 upgrade note).
- A small protocol addition (`--shutdown`) must be intercepted in the server before the command dispatcher.
