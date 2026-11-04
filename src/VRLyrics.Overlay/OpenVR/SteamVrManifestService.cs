using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Valve.VR;
using VRLyrics.Overlay.Dashboard;

namespace VRLyrics.Overlay.OpenVR;

/// <summary>Result of a SteamVR <c>.vrmanifest</c> registration or autostart operation.</summary>
public sealed record SteamVrManifestStatus(
    bool IsInstalled,
    bool IsAutoLaunchEnabled,
    string ManifestPath,
    string ExecutablePath,
    string StatusMessage);

/// <summary>
/// Generates the <c>vrlyrics.vrmanifest</c> file and interacts with <c>IVRApplications</c> to register
/// VRLyrics with SteamVR and configure automatic startup when SteamVR launches.
/// </summary>
public static class SteamVrManifestService
{
    public const string AppKey = "vrlyrics.overlay.app";
    public const string ManifestFileName = "vrlyrics.vrmanifest";

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Resolves the path to <c>VRLyrics.exe</c> (preferring the actual apphost binary over <c>dotnet.exe</c>).
    /// </summary>
    public static string ResolveExecutablePath(string? overrideExecutablePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideExecutablePath))
            return Path.GetFullPath(overrideExecutablePath);

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            !Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(processPath).StartsWith("testhost", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(processPath);
        }

        foreach (var candidateName in new[] { "VRLyrics.exe", "VRLyrics.App.exe" })
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, candidateName);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "VRLyrics.exe"));
    }

    /// <summary>
    /// Builds the JSON content of a SteamVR <c>.vrmanifest</c> file for VRLyrics.
    /// </summary>
    public static string BuildManifestJson(
        string executablePath,
        string? imagePath = null,
        string launchArguments = "--minimized")
    {
        var appEntry = new JsonObject
        {
            ["app_key"] = AppKey,
            ["launch_type"] = "binary",
            ["binary_path_windows"] = executablePath,
            ["arguments"] = launchArguments,
            ["is_dashboard_overlay"] = true,
        };

        if (!string.IsNullOrWhiteSpace(imagePath))
            appEntry["image_path"] = imagePath;

        appEntry["strings"] = new JsonObject
        {
            ["zh_cn"] = new JsonObject
            {
                ["name"] = "VRLyrics 实时歌词叠加层",
                ["description"] = "在 SteamVR 中实时显示双语逐字卡拉OK同步歌词与仪表盘控制面板"
            },
            ["en_us"] = new JsonObject
            {
                ["name"] = "VRLyrics Overlay",
                ["description"] = "Real-time synchronized karaoke & bilingual lyrics overlay for SteamVR"
            }
        };

        var root = new JsonObject
        {
            ["source"] = "builtin",
            ["applications"] = new JsonArray(appEntry)
        };

        return root.ToJsonString(PrettyJson);
    }

    /// <summary>
    /// Writes <c>vrlyrics.vrmanifest</c> (and <c>dashboard_thumb.png</c>) to disk and returns the manifest's full path.
    /// Prefers writing next to <c>VRLyrics.App.exe</c>; falls back to <c>%LocalAppData%\VRLyrics</c> if read-only.
    /// </summary>
    public static string EnsureManifestFile(
        string? targetDirectory = null,
        string? customExecutablePath = null,
        string launchArguments = "--minimized")
    {
        var exePath = ResolveExecutablePath(customExecutablePath);
        var dir = !string.IsNullOrWhiteSpace(targetDirectory)
            ? targetDirectory
            : Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;

        try
        {
            Directory.CreateDirectory(dir);
            return WriteManifestAndIcon(dir, exePath, launchArguments);
        }
        catch
        {
            var fallbackDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VRLyrics");
            Directory.CreateDirectory(fallbackDir);
            return WriteManifestAndIcon(fallbackDir, exePath, launchArguments);
        }
    }

    private static string WriteManifestAndIcon(string dir, string exePath, string launchArguments)
    {
        var iconPath = Path.Combine(dir, "dashboard_thumb.png");
        try
        {
            if (!File.Exists(iconPath))
                File.WriteAllBytes(iconPath, DashboardPanelRenderer.RenderThumbnailPng());
        }
        catch
        {
            iconPath = null;
        }

        var manifestPath = Path.Combine(dir, ManifestFileName);
        var json = BuildManifestJson(exePath, iconPath, launchArguments);
        File.WriteAllText(manifestPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return Path.GetFullPath(manifestPath);
    }

    /// <summary>
    /// Registers the manifest with SteamVR, identifies the running process, and applies <paramref name="enableAutoStart"/>.
    /// Requires OpenVR to be initialized (<c>Valve.VR.OpenVR.Applications</c> non-null).
    /// </summary>
    public static SteamVrManifestStatus ApplyRegistration(
        CVRApplications? applications,
        bool enableAutoStart,
        string? targetDirectory = null,
        string? customExecutablePath = null)
    {
        var exePath = ResolveExecutablePath(customExecutablePath);
        var manifestPath = EnsureManifestFile(targetDirectory, exePath);

        if (applications is null)
        {
            return new SteamVrManifestStatus(
                IsInstalled: false,
                IsAutoLaunchEnabled: enableAutoStart,
                ManifestPath: manifestPath,
                ExecutablePath: exePath,
                StatusMessage: $"已生成 {ManifestFileName}（等待连接 SteamVR 后自动完成注册）");
        }

        var addErr = applications.AddApplicationManifest(manifestPath, bTemporary: false);
        if (addErr != EVRApplicationError.None)
        {
            return new SteamVrManifestStatus(
                IsInstalled: applications.IsApplicationInstalled(AppKey),
                IsAutoLaunchEnabled: applications.GetApplicationAutoLaunch(AppKey),
                ManifestPath: manifestPath,
                ExecutablePath: exePath,
                StatusMessage: $"注册 vrmanifest 失败: {addErr}");
        }

        try
        {
            applications.IdentifyApplication((uint)Environment.ProcessId, AppKey);
        }
        catch { }

        var autoErr = applications.SetApplicationAutoLaunch(AppKey, enableAutoStart);
        var installed = applications.IsApplicationInstalled(AppKey);
        var autoLaunch = applications.GetApplicationAutoLaunch(AppKey);

        var msg = autoErr == EVRApplicationError.None
            ? (autoLaunch ? "已注册到 SteamVR 并开启随 SteamVR 自动启动" : "已注册到 SteamVR（随 SteamVR 自动启动：已关闭）")
            : $"已注册 vrmanifest，但设置自启动返回: {autoErr}";

        return new SteamVrManifestStatus(
            IsInstalled: installed,
            IsAutoLaunchEnabled: autoLaunch,
            ManifestPath: manifestPath,
            ExecutablePath: exePath,
            StatusMessage: msg);
    }

    /// <summary>
    /// Unregisters <c>vrlyrics.vrmanifest</c> from SteamVR and disables auto-launch.
    /// </summary>
    public static SteamVrManifestStatus Unregister(
        CVRApplications? applications,
        string? targetDirectory = null,
        string? customExecutablePath = null)
    {
        var exePath = ResolveExecutablePath(customExecutablePath);
        var manifestPath = EnsureManifestFile(targetDirectory, exePath);

        if (applications is null)
        {
            return new SteamVrManifestStatus(
                IsInstalled: false,
                IsAutoLaunchEnabled: false,
                ManifestPath: manifestPath,
                ExecutablePath: exePath,
                StatusMessage: "当前未连接 SteamVR，已关闭本地自启动配置");
        }

        try { applications.SetApplicationAutoLaunch(AppKey, false); } catch { }
        var remErr = applications.RemoveApplicationManifest(manifestPath);
        var installed = applications.IsApplicationInstalled(AppKey);

        var msg = remErr == EVRApplicationError.None || !installed
            ? "已从 SteamVR 移除应用清单并取消自动启动"
            : $"移除 vrmanifest 返回: {remErr}";

        return new SteamVrManifestStatus(
            IsInstalled: installed,
            IsAutoLaunchEnabled: false,
            ManifestPath: manifestPath,
            ExecutablePath: exePath,
            StatusMessage: msg);
    }

    /// <summary>Queries the current installation and auto-launch state from SteamVR.</summary>
    public static SteamVrManifestStatus QueryStatus(
        CVRApplications? applications,
        bool configuredAutoStart,
        string? targetDirectory = null,
        string? customExecutablePath = null)
    {
        var exePath = ResolveExecutablePath(customExecutablePath);
        var manifestPath = EnsureManifestFile(targetDirectory, exePath);

        if (applications is null)
        {
            return new SteamVrManifestStatus(
                IsInstalled: false,
                IsAutoLaunchEnabled: configuredAutoStart,
                ManifestPath: manifestPath,
                ExecutablePath: exePath,
                StatusMessage: configuredAutoStart
                    ? "已预设随 SteamVR 自启动（连接 SteamVR 时自动生效）"
                    : "未连接 SteamVR（清单文件已就绪）");
        }

        var installed = applications.IsApplicationInstalled(AppKey);
        var autoLaunch = installed && applications.GetApplicationAutoLaunch(AppKey);

        var msg = installed
            ? (autoLaunch ? "SteamVR 已注册 · 随 SteamVR 自启动已开启" : "SteamVR 已注册 · 随 SteamVR 自启动未开启")
            : "尚未注册到 SteamVR";

        return new SteamVrManifestStatus(
            IsInstalled: installed,
            IsAutoLaunchEnabled: autoLaunch,
            ManifestPath: manifestPath,
            ExecutablePath: exePath,
            StatusMessage: msg);
    }
}
