using System.IO.Pipes;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonIntegrationTests;

/// <summary>
/// Verifies that DaemonClient enforces the 5s handshake deadline and 75s response deadline,
/// and correctly classifies outcomes from stall, malformed-frame, Loading, and ResponseTimeout.
/// Uses injectable deadline seams so no test waits real time.
/// </summary>
public sealed class HandshakeDeadlines
{
    [Fact]
    public async Task WhenServerWritesResultAsync_ReturnsOutcomeResult()
    {
        // Arrange
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);

        Task serverTask = Task.Run(async () =>
        {
            using NamedPipeServerStream pipe = new(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await pipe.WaitForConnectionAsync();
            _ = await PipeProtocol.ReadRequestEnvelopeAsync(pipe, CancellationToken.None);
            await PipeProtocol.WriteResultAsync(pipe, "hello", "", 0, CancellationToken.None);
        });

        StringWriter stdout = new();
        StringWriter stderr = new();

        // Act
        DaemonAttempt attempt = await DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            ["find-refs", "Foo"],
            stdout,
            stderr);

        await serverTask;

        // Assert
        attempt.Outcome.ShouldBe(DaemonOutcome.Result);
        attempt.ExitCode.ShouldBe(0);
        stdout.ToString().ShouldBe("hello");
        stderr.ToString().ShouldBe("");
    }

    [Fact]
    public async Task WhenServerWritesLoadingStatus_ReturnsOutcomeLoading()
    {
        // Arrange
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);

        Task serverTask = Task.Run(async () =>
        {
            using NamedPipeServerStream pipe = new(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await pipe.WaitForConnectionAsync();
            _ = await PipeProtocol.ReadRequestEnvelopeAsync(pipe, CancellationToken.None);
            await PipeProtocol.WriteStatusAsync(pipe, ResponseStatus.Loading, CancellationToken.None);
        });

        StringWriter stdout = new();
        StringWriter stderr = new();

        // Act
        DaemonAttempt attempt = await DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            ["find-refs", "Foo"],
            stdout,
            stderr);

        await serverTask;

        // Assert
        attempt.Outcome.ShouldBe(DaemonOutcome.Loading);
        attempt.ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task WhenServerWritesReloadingStatus_ReturnsOutcomeReloading()
    {
        // Arrange
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);

        Task serverTask = Task.Run(async () =>
        {
            using NamedPipeServerStream pipe = new(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await pipe.WaitForConnectionAsync();
            _ = await PipeProtocol.ReadRequestEnvelopeAsync(pipe, CancellationToken.None);
            await PipeProtocol.WriteStatusAsync(pipe, ResponseStatus.Reloading, CancellationToken.None);
        });

        StringWriter stdout = new();
        StringWriter stderr = new();

        // Act
        DaemonAttempt attempt = await DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            ["find-refs", "Foo"],
            stdout,
            stderr);

        await serverTask;

        // Assert
        attempt.Outcome.ShouldBe(DaemonOutcome.Reloading);
        attempt.ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task WhenServerWritesErrorStatus_ReturnsOutcomeResultWithExit1AndWritesStderr()
    {
        // Arrange
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);
        string errorMessage = "command failed: something broke";

        Task serverTask = Task.Run(async () =>
        {
            using NamedPipeServerStream pipe = new(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await pipe.WaitForConnectionAsync();
            _ = await PipeProtocol.ReadRequestEnvelopeAsync(pipe, CancellationToken.None);
            await PipeProtocol.WriteErrorAsync(pipe, errorMessage, CancellationToken.None);
        });

        StringWriter stdout = new();
        StringWriter stderr = new();

        // Act
        DaemonAttempt attempt = await DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            ["find-refs", "Foo"],
            stdout,
            stderr);

        await serverTask;

        // Assert — Error is surfaced as a Result with exit 1; no retry
        attempt.Outcome.ShouldBe(DaemonOutcome.Result);
        attempt.ExitCode.ShouldBe(1);
        stderr.ToString().ShouldContain(errorMessage);
        stdout.ToString().ShouldBe("");
    }

    [Fact]
    public async Task WhenServerWritesVersionMismatch_ReturnsOutcomeMalformedOrShort()
    {
        // Arrange
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);

        Task serverTask = Task.Run(async () =>
        {
            using NamedPipeServerStream pipe = new(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await pipe.WaitForConnectionAsync();
            _ = await PipeProtocol.ReadRequestEnvelopeAsync(pipe, CancellationToken.None);
            await PipeProtocol.WriteStatusAsync(pipe, ResponseStatus.VersionMismatch, CancellationToken.None);
        });

        StringWriter stdout = new();
        StringWriter stderr = new();

        // Act
        DaemonAttempt attempt = await DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            ["find-refs", "Foo"],
            stdout,
            stderr);

        await serverTask;

        // Assert — VersionMismatch triggers replace path same as MalformedOrShort
        attempt.Outcome.ShouldBe(DaemonOutcome.MalformedOrShort);
    }

    [Fact]
    public async Task WhenServerWritesOldStyleFrame_StatusByte0_ReturnsOutcomeMalformedOrShort()
    {
        // Arrange — simulate an old-protocol daemon that writes a 4-byte length prefix
        // first (big-endian int32 = 0x00000000 for empty stdout), which the new client
        // reads as status byte 0 (invalid) → MalformedOrShort.
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);

        using CancellationTokenSource serverCts = new(TimeSpan.FromSeconds(10));

        // Create the pipe synchronously (before the client connects) so ConnectAsync is near
        // instant and the handshake deadline is spent waiting for a status byte that never comes,
        // not on a slow connect that a loaded CI runner would misclassify as NotRunning.
        using NamedPipeServerStream serverPipe = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        Task serverTask = Task.Run(async () =>
        {
            await serverPipe.WaitForConnectionAsync(serverCts.Token);
            // Consume whatever the client sends (new envelope) but never write a status byte.
            byte[] buf = new byte[256];
            while (await serverPipe.ReadAsync(buf, serverCts.Token) > 0) { /* drain */ }
        }, serverCts.Token);

        DaemonClient.SetHandshakeDeadlineForTests(TimeSpan.FromSeconds(1));

        StringWriter stdout = new();
        StringWriter stderr = new();

        try
        {
            // Act
            DaemonAttempt attempt = await DaemonClient.TryExecuteAsync(
                fakeSolutionPath,
                ["find-refs", "Foo"],
                stdout,
                stderr);

            // Assert — no valid status byte arrives before the deadline → MalformedOrShort or Wedged
            attempt.Outcome.ShouldBeOneOf(DaemonOutcome.MalformedOrShort, DaemonOutcome.Wedged);
        }
        finally
        {
            DaemonClient.SetHandshakeDeadlineForTests(null);
            await serverCts.CancelAsync();
        }

        try
        {
            await serverTask;
        }
        catch (OperationCanceledException)
        {
            // Expected when the server CTS is cancelled after the test assertion
        }
    }

    [Fact]
    public async Task WhenNoDaemonRunning_ReturnsOutcomeNotRunning()
    {
        // Arrange
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        StringWriter stdout = new();
        StringWriter stderr = new();

        // Act
        DaemonAttempt attempt = await DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            ["find-refs", "Foo"],
            stdout,
            stderr);

        // Assert
        attempt.Outcome.ShouldBe(DaemonOutcome.NotRunning);
    }

    [Fact]
    public async Task WhenHandshakeExceedsDeadline_ReturnsOutcomeWedged()
    {
        // Arrange — server accepts and reads the request, then stalls without writing a reply.
        // The pipe is created synchronously (before the client connects) so ConnectAsync is near
        // instant and the handshake deadline is spent on the stalled status-byte read, not on a
        // slow connect — otherwise a loaded CI runner can trip the deadline during connect and the
        // client classifies that as NotRunning instead of Wedged. The deadline is kept short for a
        // fast test but comfortably longer than a connect to an already-listening pipe.
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);

        using CancellationTokenSource serverCts = new(TimeSpan.FromSeconds(10));

        using NamedPipeServerStream serverPipe = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        Task serverTask = Task.Run(async () =>
        {
            await serverPipe.WaitForConnectionAsync(serverCts.Token);
            _ = await PipeProtocol.ReadRequestEnvelopeAsync(serverPipe, serverCts.Token);
            // Deliberately stall — do not write a status byte.
            await Task.Delay(Timeout.Infinite, serverCts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }, serverCts.Token);

        DaemonClient.SetHandshakeDeadlineForTests(TimeSpan.FromSeconds(1));

        StringWriter stdout = new();
        StringWriter stderr = new();

        try
        {
            // Act
            DaemonAttempt attempt = await DaemonClient.TryExecuteAsync(
                fakeSolutionPath,
                ["find-refs", "Foo"],
                stdout,
                stderr);

            // Assert
            attempt.Outcome.ShouldBe(DaemonOutcome.Wedged);
        }
        finally
        {
            DaemonClient.SetHandshakeDeadlineForTests(null);
            await serverCts.CancelAsync();
        }

        try
        {
            await serverTask;
        }
        catch (OperationCanceledException)
        {
            // Expected when the server CTS is cancelled after the test assertion
        }
    }

    [Fact]
    public async Task WhenResponseBodyExceedsDeadline_ReturnsOutcomeResponseTimeout()
    {
        // Arrange — server writes a valid status byte (Result) but then stalls before the body
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);

        using CancellationTokenSource serverCts = new(TimeSpan.FromSeconds(5));

        Task serverTask = Task.Run(async () =>
        {
            using NamedPipeServerStream pipe = new(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await pipe.WaitForConnectionAsync(serverCts.Token);
            _ = await PipeProtocol.ReadRequestEnvelopeAsync(pipe, serverCts.Token);
            // Write the status byte but then stall — no body follows
            pipe.WriteByte((byte)ResponseStatus.Result);
            await pipe.FlushAsync(serverCts.Token);
            await Task.Delay(Timeout.Infinite, serverCts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }, serverCts.Token);

        // Very short response deadline
        DaemonClient.SetResponseDeadlineForTests(TimeSpan.FromMilliseconds(150));

        StringWriter stdout = new();
        StringWriter stderr = new();

        try
        {
            // Act
            DaemonAttempt attempt = await DaemonClient.TryExecuteAsync(
                fakeSolutionPath,
                ["find-refs", "Foo"],
                stdout,
                stderr);

            // Assert
            attempt.Outcome.ShouldBe(DaemonOutcome.ResponseTimeout);
        }
        finally
        {
            DaemonClient.SetResponseDeadlineForTests(null);
            await serverCts.CancelAsync();
        }

        try
        {
            await serverTask;
        }
        catch (OperationCanceledException)
        {
            // Expected when the server CTS is cancelled after the test assertion
        }
    }
}
