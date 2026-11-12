using System.Reflection;

namespace Inkling;

internal static class AppInfo
{
    public const string SourceUrl = "https://github.com/AtharvGogia/Inkling";

    /// <summary>"0.4.0", from the assembly's informational version.</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
}
