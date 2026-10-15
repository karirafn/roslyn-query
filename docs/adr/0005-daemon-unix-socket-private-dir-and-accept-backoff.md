# 0005. Daemon Unix socket lives in a per-user private directory; accept loop backs off and exits on repeated bind failure

**Date:** 2026-10-15
**Status:** Accepted

## Context

On Unix the daemon's socket previously lived at a predictable path in the shared
temp directory (`Path.GetTempPath()/CoreFxPipe_roslyn-query-<hash>`). Another local
user could pre-create a file there; `/tmp`'s sticky bit blocks the runtime from
unlinking a foreign-owned file, so `Bind` throws `SocketException` inside
`new NamedPipeServerStream`. The accept loop's general `catch (Exception)` swallowed
that and retried with no delay, spinning at 100% CPU until the idle timeout; a
respawned daemon hit the same squatted path. `PipeOptions.CurrentUserOnly` on .NET 10
checks the peer euid only *after* accept and does not chmod the socket, so the socket
file's directory is the real access control.

.NET 10's `PipeStream.GetPipePath` uses a rooted pipe name verbatim as the Unix
domain socket path (validated only for invalid path chars / trailing separator),
bypassing the `CoreFxPipe_` prefix. The BCL exposes no API to read a path's owner uid.
`sun_path` is ~108 bytes on Linux, ~104 on macOS; macOS's `/var/folders/.../T/`
TMPDIR base plus a username-bearing directory plus the prefixed 32-hex hash exceeds
104 bytes.

## Decision

Derive the Unix socket as a rooted path inside a per-user private directory:
`$XDG_RUNTIME_DIR/roslyn-query` when set (the XDG spec guarantees it is user-owned,
0700, local), else `<TMPDIR>/rq-<user8>` where `<user8>` is the first 8 hex of
SHA256(username). The directory is created 0700, a symlink is refused (pre- and
post-create), and a pre-existing directory looser than 0700 is rejected as a
squatting signal. The socket filename is the bare 32-hex solution-path hash with no
`roslyn-query-` prefix and no extension, keeping the worst-case path at ~86 bytes
(TMPDIR) / ~66 bytes (XDG). The client and server share one `DerivePipeName`, so both
compute the identical path; only the server prepares the directory. Windows pipe
naming (`roslyn-query-<hash>`) and the owner-SID ACL are unchanged.

The accept loop separates two failure classes: a create/bind failure increments a
consecutive-failure counter, waits a bounded backoff (500 ms), and breaks the loop
after 5 consecutive failures so the `finally` block cleans up the PID file and the
client falls back to a direct in-process run; a successful accept resets the counter.
A peer-rejection `UnauthorizedAccessException` from `WaitForConnectionAsync` is caught
separately, does not count toward the exit threshold, and does not back off — a
rejected cross-uid peer is the security model working, not a daemon fault, and must
not be a DoS lever.

## Consequences

- Socket squatting by another user is prevented: a foreign user cannot create a file
  in our 0700 directory, so bind no longer fails on a squatted path.
- The hot-spin is gone: bind failure degrades to a quick bounded fallback instead of
  100% CPU until idle timeout.
- Exit via `break` (not `Environment.Exit`) keeps cleanup and testability.
- Ownership is verified structurally (XDG guarantee + symlink refusal + 0700-mode
  rejection), not by a uid syscall — the BCL has no uid getter, so a foreign-owned
  TMPDIR directory with exactly 0700 mode is the residual gap, accepted as the same
  bound the PID-file hardening already takes.
- A daemon started by an older version uses the old shared-temp socket path, so a new
  client does not find it; it exits on idle timeout (same upgrade behavior as the
  MD5->SHA256 pipe-name change in ADR 0001).
- `$XDG_RUNTIME_DIR` absence in some containers/CI falls back to the still-private
  TMPDIR directory.
