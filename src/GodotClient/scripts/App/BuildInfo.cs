using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Godot;

namespace HexTactics.Client.App;

/// <summary>Version information shown in the HUD and written to logs.</summary>
public static class BuildInfo
{
    /// <summary>Product version, e.g. "0.1.0" (from Directory.Build.props).</summary>
    public static string Version { get; }

    /// <summary>Short Git commit the assembly was built from, or "unknown".</summary>
    public static string Commit { get; }

    static BuildInfo()
    {
        var info = typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = info.IndexOf('+');
        Version = plus >= 0 ? info[..plus] : info;
        var commit = plus >= 0 ? info[(plus + 1)..] : string.Empty;
        Commit = commit.Length >= 7 ? commit[..7] : (commit.Length > 0 ? commit : "unknown");
    }

    public static string EngineVersion => (string)Engine.GetVersionInfo()["string"];

    public static string DotNetVersion => RuntimeInformation.FrameworkDescription;

    public static string Title => $"HexTactics v{Version}";

    public static string Full => $"HexTactics v{Version} ({Commit}) · Godot {EngineVersion} · {DotNetVersion}";

    public static string ProjectVersion => (string)ProjectSettings.GetSetting("application/config/version", "?");

    public static bool VersionsMatch => string.Equals(ProjectVersion, Version, StringComparison.Ordinal);
}
