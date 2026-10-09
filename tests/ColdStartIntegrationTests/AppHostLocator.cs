namespace roslyn_query.Tests.ColdStartIntegrationTests;

/// <summary>
/// Resolves the launch FileName and prefix args for the built roslyn-query client,
/// mirroring the production Environment.ProcessPath resolution used in DaemonProcess.BuildStartInfo.
/// </summary>
internal static class AppHostLocator
{
    private const string ApphostName = "roslyn-query";

    /// <summary>
    /// Attempts to locate the built client binary.
    /// </summary>
    /// <param name="fileName">The executable to launch.</param>
    /// <param name="prefixArgs">Arguments to prepend before the roslyn-query command args (empty for apphost).</param>
    /// <returns>True when the binary was located; false when it should be skipped.</returns>
    internal static bool TryLocate(out string fileName, out IReadOnlyList<string> prefixArgs)
    {
        string baseDir = AppContext.BaseDirectory;

        // Derive the build configuration from the test output path (Debug/Release).
        string configuration = DeriveConfiguration(baseDir);

        // Resolve relative to the test output: tests/bin/<config>/net10.0/ → ../../../../src/bin/<config>/net10.0/
        string srcBinDir = Path.GetFullPath(
            Path.Combine(baseDir, "..", "..", "..", "..", "src", "bin", configuration, "net10.0"));

        // Prefer the native apphost.
        string apphostName = OperatingSystem.IsWindows()
            ? ApphostName + ".exe"
            : ApphostName;

        string apphostPath = Path.Combine(srcBinDir, apphostName);
        if (File.Exists(apphostPath))
        {
            fileName = apphostPath;
            prefixArgs = [];
            return true;
        }

        // Fall back to dotnet <dll>.
        string dllPath = Path.Combine(srcBinDir, ApphostName + ".dll");
        if (File.Exists(dllPath))
        {
            // Use the current dotnet host, mirroring what production does when ProcessPath is dotnet.
            string dotnetHost = ResolveCurrentDotnetHost();
            fileName = dotnetHost;
            prefixArgs = [dllPath];
            return true;
        }

        fileName = string.Empty;
        prefixArgs = [];
        return false;
    }

    private static string DeriveConfiguration(string baseDir)
    {
        // The test output path ends with: bin/Debug/net10.0 or bin/Release/net10.0
        ReadOnlySpan<char> path = baseDir.AsSpan().TrimEnd(Path.DirectorySeparatorChar);

        // Walk up two segments: net10.0 → <config>
        int lastSep = path.LastIndexOf(Path.DirectorySeparatorChar);
        if (lastSep >= 0)
        {
            ReadOnlySpan<char> parent = path[..lastSep];
            int secondSep = parent.LastIndexOf(Path.DirectorySeparatorChar);
            if (secondSep >= 0)
            {
                string config = parent[(secondSep + 1)..].ToString();
                if (config.Length > 0)
                {
                    return config;
                }
            }
        }

        return "Debug";
    }

    private static string ResolveCurrentDotnetHost()
    {
        // When the test host itself runs under dotnet, Environment.ProcessPath is the dotnet host.
        string? processPath = Environment.ProcessPath;
        if (processPath is not null
            && string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        // Otherwise use the entry assembly's location to find dotnet on PATH.
        return "dotnet";
    }
}
