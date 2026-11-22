using System.Reflection;

namespace LogLens.App;

internal static class AppInfo
{
    public const string Name = "LogLens";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}
