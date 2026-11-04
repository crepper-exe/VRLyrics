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
/// Generates the <c>vrlyrics.vrmanifest</c> file and interacts with both <c>IVRApplications</c> (when SteamVR is running)
/// and SteamVR's on-disk configuration (<c>appconfig.json</c> and <c>vrappconfig/vrlyrics.overlay.app.vrappconfig</c>
/// resolved via <c>%LocalAppData%\openvr\openvrpaths.vrpath</c>) so auto-launch works reliably even when configured offline.
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
            ["arguments_windows"] = launchArguments,
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
    /// Prefers writing next to <c>VRLyrics.exe</c>; falls back to <c>%LocalAppData%\VRLyrics</c> if read-only.
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
    /// Resolves SteamVR's <c>config</c> directory (e.g., <c>D:\steam\config</c>) from <c>%LocalAppData%\openvr\openvrpaths.vrpath</c>.
    /// </summary>
    public static string? TryGetSteamVrConfigDirectory(string? customConfigDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(customConfigDirectory))
        {
            try
            {
                Directory.CreateDirectory(customConfigDirectory);
                return Path.GetFullPath(customConfigDirectory);
            }
            catch
            {
                return null;
            }
        }

        try
        {
            var vrpathFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "openvr",
                "openvrpaths.vrpath");

            if (!File.Exists(vrpathFile))
                return null;

            var node = JsonNode.Parse(File.ReadAllText(vrpathFile));
            var configArr = node?["config"] as JsonArray;
            if (configArr is null || configArr.Count == 0)
                return null;

            foreach (var item in configArr)
            {
                var raw = item?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(raw) && Directory.Exists(raw))
                    return Path.GetFullPath(raw);
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Directly synchronizes <c>appconfig.json</c> and <c>vrappconfig/vrlyrics.overlay.app.vrappconfig</c>
    /// inside SteamVR's config folder so that SteamVR auto-launch works even if configured while SteamVR is closed.
    /// </summary>
    public static (bool IsInstalled, bool IsAutoLaunch, string EffectiveManifestPath) SyncSteamVrConfigFilesDirectly(
        string manifestPath,
        bool registerManifest,
        bool enableAutoStart,
        string? customConfigDirectory = null)
    {
        var fullManifest = Path.GetFullPath(manifestPath);
        var configDir = TryGetSteamVrConfigDirectory(customConfigDirectory);
        if (string.IsNullOrWhiteSpace(configDir))
            return (false, false, fullManifest);

        try
        {
            var appConfigPath = Path.Combine(configDir, "appconfig.json");
            JsonObject rootObj;
            if (File.Exists(appConfigPath))
            {
                try
                {
                    rootObj = JsonNode.Parse(File.ReadAllText(appConfigPath)) as JsonObject ?? new JsonObject();
                }
                catch
                {
                    rootObj = new JsonObject();
                }
            }
            else
            {
                rootObj = new JsonObject();
            }

            if (rootObj["manifest_paths"] is not JsonArray pathsArray)
            {
                pathsArray = new JsonArray();
                rootObj["manifest_paths"] = pathsArray;
            }

            // If we are running from a transient dev/test build (\bin\Debug\ or \bin\Release\) and appconfig.json
            // already points to a published dist\...\vrlyrics.vrmanifest that exists on disk, preserve the dist path.
            var isTransientBuildPath = customConfigDirectory is null &&
                (fullManifest.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) ||
                 fullManifest.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase));

            string? existingDistManifest = null;
            for (var i = pathsArray.Count - 1; i >= 0; i--)
            {
                var p = pathsArray[i]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(p)) continue;

                if (Path.GetFileName(p).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                {
                    if (isTransientBuildPath &&
                        File.Exists(p) &&
                        !p.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) &&
                        !p.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase))
                    {
                        existingDistManifest = Path.GetFullPath(p);
                    }
                    pathsArray.RemoveAt(i);
                }
            }

            var targetManifestToRegister = existingDistManifest ?? fullManifest;
            if (registerManifest)
            {
                pathsArray.Add(targetManifestToRegister);
            }

            File.WriteAllText(
                appConfigPath,
                rootObj.ToJsonString(PrettyJson),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // Write vrappconfig/vrlyrics.overlay.app.vrappconfig
            var vrAppConfigDir = Path.Combine(configDir, "vrappconfig");
            Directory.CreateDirectory(vrAppConfigDir);
            var vrAppConfigPath = Path.Combine(vrAppConfigDir, $"{AppKey}.vrappconfig");

            var lastLaunchTime = "0";
            if (File.Exists(vrAppConfigPath))
            {
                try
                {
                    var existingCfg = JsonNode.Parse(File.ReadAllText(vrAppConfigPath)) as JsonObject;
                    if (existingCfg?["last_launch_time"]?.GetValue<string>() is { } lt && !string.IsNullOrWhiteSpace(lt))
                        lastLaunchTime = lt;
                }
                catch { }
            }

            var autoLaunchVal = registerManifest && enableAutoStart;
            var vrAppCfgObj = new JsonObject
            {
                ["autolaunch"] = autoLaunchVal,
                ["last_launch_time"] = lastLaunchTime
            };

            File.WriteAllText(
                vrAppConfigPath,
                vrAppCfgObj.ToJsonString(PrettyJson),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return (registerManifest, autoLaunchVal, targetManifestToRegister);
        }
        catch
        {
            return (false, false, fullManifest);
        }
    }

    /// <summary>
    /// Reads the current installation and auto-launch state directly from SteamVR's config files on disk.
    /// </summary>
    public static (bool IsInstalled, bool IsAutoLaunch, string? RegisteredManifestPath) QuerySteamVrConfigFilesDirectly(
        string? customConfigDirectory = null)
    {
        var configDir = TryGetSteamVrConfigDirectory(customConfigDirectory);
        if (string.IsNullOrWhiteSpace(configDir))
            return (false, false, null);

        try
        {
            var appConfigPath = Path.Combine(configDir, "appconfig.json");
            string? registeredManifest = null;
            if (File.Exists(appConfigPath))
            {
                var rootObj = JsonNode.Parse(File.ReadAllText(appConfigPath)) as JsonObject;
                if (rootObj?["manifest_paths"] is JsonArray arr)
                {
                    foreach (var item in arr)
                    {
                        var p = item?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(p) &&
                            Path.GetFileName(p).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase) &&
                            File.Exists(p))
                        {
                            registeredManifest = Path.GetFullPath(p);
                            break;
                        }
                    }
                }
            }

            var vrAppConfigPath = Path.Combine(configDir, "vrappconfig", $"{AppKey}.vrappconfig");
            var autoLaunch = false;
            if (File.Exists(vrAppConfigPath))
            {
                var cfgObj = JsonNode.Parse(File.ReadAllText(vrAppConfigPath)) as JsonObject;
                if (cfgObj?["autolaunch"] is JsonValue val && val.TryGetValue<bool>(out var b))
                    autoLaunch = b;
            }

            return (registeredManifest is not null, registeredManifest is not null && autoLaunch, registeredManifest);
        }
        catch
        {
            return (false, false, null);
        }
    }

    /// <summary>
    /// Registers the manifest with SteamVR, identifies the running process, and applies <paramref name="enableAutoStart"/>.
    /// Synchronizes both <c>IVRApplications</c> (if SteamVR is connected) and SteamVR's on-disk <c>appconfig.json</c> + <c>.vrappconfig</c>.
    /// </summary>
    public static SteamVrManifestStatus ApplyRegistration(
        CVRApplications? applications,
        bool enableAutoStart,
        string? targetDirectory = null,
        string? customExecutablePath = null,
        string? customSteamVrConfigDirectory = null)
    {
        var exePath = ResolveExecutablePath(customExecutablePath);
        var manifestPath = EnsureManifestFile(targetDirectory, exePath);

        // Only sync the global SteamVR config directory when not running an isolated temp-dir unit test
        // (unless customSteamVrConfigDirectory is explicitly passed).
        var shouldSyncDiskConfig = targetDirectory is null || customSteamVrConfigDirectory is not null;

        if (applications is null)
        {
            if (shouldSyncDiskConfig)
            {
                var (diskInstalled, diskAuto, effectivePath) = SyncSteamVrConfigFilesDirectly(
                    manifestPath,
                    registerManifest: true,
                    enableAutoStart: enableAutoStart,
                    customConfigDirectory: customSteamVrConfigDirectory);

                if (diskInstalled)
                {
                    return new SteamVrManifestStatus(
                        IsInstalled: true,
                        IsAutoLaunchEnabled: diskAuto,
                        ManifestPath: effectivePath,
                        ExecutablePath: exePath,
                        StatusMessage: diskAuto
                            ? "已写入 SteamVR 启动配置（随 SteamVR 自动启动：已开启）"
                            : "已写入 SteamVR 启动配置（随 SteamVR 自动启动：已关闭）");
                }
            }

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
            if (shouldSyncDiskConfig)
            {
                SyncSteamVrConfigFilesDirectly(
                    manifestPath,
                    registerManifest: true,
                    enableAutoStart: enableAutoStart,
                    customConfigDirectory: customSteamVrConfigDirectory);
            }

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

        if (shouldSyncDiskConfig)
        {
            var (diskInstalled, diskAuto, effectivePath) = SyncSteamVrConfigFilesDirectly(
                manifestPath,
                registerManifest: true,
                enableAutoStart: enableAutoStart,
                customConfigDirectory: customSteamVrConfigDirectory);

            installed |= diskInstalled;
            if (enableAutoStart)
                autoLaunch |= diskAuto;
            manifestPath = effectivePath;
        }

        var msg = autoErr == EVRApplicationError.None || autoLaunch == enableAutoStart
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
        string? customExecutablePath = null,
        string? customSteamVrConfigDirectory = null)
    {
        var exePath = ResolveExecutablePath(customExecutablePath);
        var manifestPath = EnsureManifestFile(targetDirectory, exePath);
        var shouldSyncDiskConfig = targetDirectory is null || customSteamVrConfigDirectory is not null;

        if (shouldSyncDiskConfig)
        {
            SyncSteamVrConfigFilesDirectly(
                manifestPath,
                registerManifest: false,
                enableAutoStart: false,
                customConfigDirectory: customSteamVrConfigDirectory);
        }

        if (applications is null)
        {
            return new SteamVrManifestStatus(
                IsInstalled: false,
                IsAutoLaunchEnabled: false,
                ManifestPath: manifestPath,
                ExecutablePath: exePath,
                StatusMessage: "已从 SteamVR 配置移除应用清单并关闭自动启动");
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

    /// <summary>Queries the current installation and auto-launch state from SteamVR or its on-disk config.</summary>
    public static SteamVrManifestStatus QueryStatus(
        CVRApplications? applications,
        bool configuredAutoStart,
        string? targetDirectory = null,
        string? customExecutablePath = null,
        string? customSteamVrConfigDirectory = null)
    {
        var exePath = ResolveExecutablePath(customExecutablePath);
        var manifestPath = EnsureManifestFile(targetDirectory, exePath);
        var shouldCheckDiskConfig = targetDirectory is null || customSteamVrConfigDirectory is not null;

        if (applications is null)
        {
            if (shouldCheckDiskConfig)
            {
                var (diskInstalled, diskAuto, registeredPath) = QuerySteamVrConfigFilesDirectly(customSteamVrConfigDirectory);
                if (diskInstalled)
                {
                    return new SteamVrManifestStatus(
                        IsInstalled: true,
                        IsAutoLaunchEnabled: diskAuto,
                        ManifestPath: registeredPath ?? manifestPath,
                        ExecutablePath: exePath,
                        StatusMessage: diskAuto
                            ? "SteamVR 配置已就绪 · 随 SteamVR 自动启动已开启"
                            : "SteamVR 配置已注册 · 随 SteamVR 自动启动未开启");
                }
            }

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
