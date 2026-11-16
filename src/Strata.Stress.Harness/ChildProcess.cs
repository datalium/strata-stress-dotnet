using System.Diagnostics;
using System.Reflection;

namespace Datalium.Strata.Stress.Harness;

/// <summary>
/// Starts the scenario's own binary again as a child process (SCENARIOS.md, "Running a scenario": a mode word before
/// the options). Nobody runs a mode by hand: the scenario starts it, and the child inherits the console and the
/// environment, so its progress lines show and it finds the same services.
/// </summary>
public static class ChildProcess
{
    /// <summary>
    /// Starts this binary with <paramref name="args"/>: a mode word first, then the options. With
    /// <paramref name="redirectStandardOutput"/>, the child's standard output comes to the parent
    /// (<see cref="Process.StandardOutput"/>), for a child that reports as it goes; its standard error still
    /// goes to the console.
    /// </summary>
    public static Process StartSelf(IEnumerable<string> args, bool redirectStandardOutput = false)
    {
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("the process's own path is unknown");
        var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = redirectStandardOutput };

        // Run as `dotnet scenario.dll` rather than through its apphost: the child needs the dll again.
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("the scenario's assembly is unknown"));
        }

        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        return Process.Start(start) ?? throw new InvalidOperationException($"could not start {host}");
    }
}
