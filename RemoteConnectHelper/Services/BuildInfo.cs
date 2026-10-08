using System.Reflection;

namespace RemoteConnectHelper.Services;

/// <summary>版本与构建时间（构建时间由 csproj 的 InjectBuildTime target 在每次编译时注入）。</summary>
public static class BuildInfo
{
    public const string Version = "1.0.0";

    private static string? _buildTime;

    /// <summary>构建时间“yyyy-MM-dd HH:mm”；注入缺失时回退到程序集 InformationalVersion。</summary>
    public static string BuildTime
    {
        get
        {
            if (_buildTime != null) return _buildTime;
            var asm = Assembly.GetExecutingAssembly();
            var md = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
                        .FirstOrDefault(a => a.Key == "BuildTime");
            _buildTime = string.IsNullOrWhiteSpace(md?.Value)
                ? asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? Version
                : md!.Value!;
            return _buildTime;
        }
    }
}
