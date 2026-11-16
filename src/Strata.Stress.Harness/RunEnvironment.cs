using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Datalium.Strata.Graph;

namespace Datalium.Strata.Stress.Harness;

/// <summary>
/// The machine and the versions a run used (SCENARIOS.md, "Output"): what RESULTS.md has to name next to every
/// number. No host name and no user name: the JSON is committed to a public repository.
/// </summary>
public sealed record RunEnvironment(
    [property: JsonPropertyName("engine")] string Engine,
    [property: JsonPropertyName("strata_version")] string StrataVersion,
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("os")] string Os,
    [property: JsonPropertyName("cpu")] string Cpu,
    [property: JsonPropertyName("logical_cores")] int LogicalCores,
    [property: JsonPropertyName("memory_bytes")] long MemoryBytes,
    [property: JsonPropertyName("gc")] string Gc,
    [property: JsonPropertyName("build")] string Build)
{
    public static RunEnvironment Capture() => new(
        Engine: "dotnet",
        StrataVersion: StrataVersionOf(typeof(StateGraph<>).Assembly),
        Runtime: RuntimeInformation.FrameworkDescription,
        Os: RuntimeInformation.OSDescription,
        Cpu: CpuName(),
        LogicalCores: Environment.ProcessorCount,
        MemoryBytes: GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        Gc: $"{(GCSettings.IsServerGC ? "server" : "workstation")}, {GCSettings.LatencyMode}",
        Build: Debugger.IsAttached ? "debugger attached" : IsOptimized() ? "release" : "debug");

    // The informational version is "0.9.0-preview.2+<commit>": the commit is the package's, not ours to report.
    private static string StrataVersionOf(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static bool IsOptimized() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<DebuggableAttribute>() is not { IsJITOptimizerDisabled: true };

    private static string CpuName()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (key?.GetValue("ProcessorNameString") is string name)
                {
                    return name.Trim();
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                var line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
                if (line?.Split(':', 2) is [_, var name])
                {
                    return name.Trim();
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                using var sysctl = Process.Start(new ProcessStartInfo("sysctl", "-n machdep.cpu.brand_string") { RedirectStandardOutput = true });
                if (sysctl is not null)
                {
                    var name = sysctl.StandardOutput.ReadToEnd().Trim();
                    sysctl.WaitForExit();
                    if (name.Length > 0)
                    {
                        return name;
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Fall through: an unknown CPU is reported as such, not guessed.
        }

        return $"unknown ({RuntimeInformation.ProcessArchitecture})";
    }
}
