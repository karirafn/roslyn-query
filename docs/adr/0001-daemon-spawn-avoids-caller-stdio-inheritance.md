# 0001. Daemon spawn avoids caller stdio inheritance

**Date:** 2026-10-09
**Status:** Accepted

## Context
The first `roslyn-query` call that spawns the daemon inherited the caller's
stdout/stderr handles, so any caller waiting for EOF on those pipes (the
Claude Code Bash tool, `| cat`, CI capture) blocked until the daemon exited
(30-min idle). On Windows, redirecting the child's std slots is insufficient:
.NET calls `CreateProcess` with `bInheritHandles=TRUE`, leaking the caller's
inheritable pipe handle into the child regardless of redirection. `ShellExecute`
does not inherit handles.

## Decision
Spawn the daemon per-OS. On Windows use `UseShellExecute = true` with
`WindowStyle = Hidden` (no redirection, no handle inheritance). On Unix use
`UseShellExecute = false` with stdin/stdout/stderr redirected, and close the
parent's ends immediately after `Process.Start()` so the parent drops its handle
to the inherited pipe. Resolve the spawn `FileName` from `Environment.ProcessPath`
(prepending the entry-assembly path when the client runs under the `dotnet` host)
so the daemon is always the same binary as the client.

## Consequences
Cold-start calls reach stdout EOF as soon as the client exits while the daemon
keeps running. The spawn path now branches per-OS, so `BuildStartInfo` returns
a per-OS `ProcessStartInfo` and the Unix post-start close lives in `StartDaemon`'s
spawn step (a post-`Start()` action `ProcessStartInfo` cannot express). The daemon
must continue writing only to in-memory `StringWriter`s — a write to `Console`
on Unix would throw `IOException` after the close.
