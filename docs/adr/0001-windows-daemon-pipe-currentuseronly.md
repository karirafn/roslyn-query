# 0001. Windows daemon client uses PipeOptions.CurrentUserOnly with an explicit server owner

**Date:** 2026-10-09
**Status:** Accepted

## Context
Daemon pipe names are derived deterministically from the solution path, so any
local user can pre-create a pipe at the expected name. Because the server binds
with maxNumberOfServerInstances = 1, the impostor's pipe wins and the legitimate
server fails to bind; the client would then connect to the impostor and could
receive spoofed query results. The Unix client already defends against this with
PipeOptions.CurrentUserOnly, but the Windows client was deliberately excluded.

The exclusion had a real basis: on Windows the client-side CurrentUserOnly check
(.NET 10) compares the connected pipe's owner SID against
WindowsIdentity.GetCurrent().Owner — the token's *default owner*, which is
BUILTIN\Administrators in an elevated process and the user SID otherwise — so a
client and daemon running at different elevation levels fail the check. (Upstream
dotnet/runtime#123903 argues this should compare .User; it is unfixed as of
.NET 10, so we design against .Owner.)

## Decision
Enable PipeOptions.CurrentUserOnly on the Windows client too, and make the daemon
server set the created pipe's owner explicitly to WindowsIdentity.GetCurrent().Owner
(the same SID the client check reads), granting the DACL rule to that same SID.
This mirrors the .NET runtime's own CurrentUserOnly server descriptor and makes the
client/server agreement on the compared SID explicit rather than relying on Windows
owner-defaulting (which previously made it work by coincidence).

We accept that a same-user *cross-elevation* connection is refused, because
DaemonClient.TryExecuteAsync catches the resulting UnauthorizedAccessException and
returns daemon-unavailable, so the CLI silently falls back to a direct in-process
run — correct result, lost speed-up only. We reject (a) a client-only change that
leaves the SID agreement implicit and breakable by a future CreatePipeServer edit,
and (b) manual peer validation via GetNamedPipeServerProcessId, which is far more
Windows-only P/Invoke for a rare convenience case the fallback already covers.

## Consequences
- Easier: an impostor pipe owned by another user is refused on both platforms; the
  spoofing vector is closed.
- Easier: client and server key off the same explicit owner SID, documented in code,
  so the invariant survives refactoring of CreatePipeServer.
- Harder: same-user cross-elevation daemon reuse no longer works; those invocations
  fall back to direct runs (slower, still correct). Revisit if dotnet/runtime#123903
  ships a .User-based check, which would also require re-keying the server owner.
- The security-relevant behavior is Windows pipe-owner semantics and is not
  reproducible on Linux CI; it is covered by documented manual verification, with the
  cross-platform regression suite guarding only that same-user round-trips still work.
