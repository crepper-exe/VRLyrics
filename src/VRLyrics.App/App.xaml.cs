using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using VRLyrics.App.Services;

namespace VRLyrics.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\VRLyrics_SingleInstance_Mutex";
    private const string ActivateWindowEventName = @"Local\VRLyrics_ActivateWindow_Event";
    private const string YieldToElevatedEventName = @"Local\VRLyrics_YieldToElevated_Event";

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _activateEvent;
    private EventWaitHandle? _yieldToElevatedEvent;
    private VRLyricsRuntimeHost? _runtime;
    private TrayIconService? _trayIcon;
    private MainWindow? _mainWindow;
    private bool _exiting;
    private bool _elevationRetryInProgress;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
            ReportFatalErrorAndExit(args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                LogCrash("AppDomainUnhandledException", ex);
        };

        try
        {
            var startMinimized = e.Args.Any(a =>
                string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

            var selfTest = e.Args.Any(a =>
                string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase));

            var isElevatedRetry = e.Args.Any(a =>
                string.Equals(a, "--elevated-retry", StringComparison.OrdinalIgnoreCase));

            if (!selfTest)
            {
                _instanceMutex = new Mutex(false, SingleInstanceMutexName);
                try
                {
                    _ownsMutex = _instanceMutex.WaitOne(0, false);
                }
                catch (AbandonedMutexException)
                {
                    // Previous instance crashed while holding the mutex; we now own it cleanly.
                    _ownsMutex = true;
                }

                if (!_ownsMutex && Environment.IsPrivilegedProcess)
                {
                    // An elevated instance (e.g. auto-launched by an elevated SteamVR vrserver.exe,
                    // or relaunched with --elevated-retry) is starting while a non-elevated instance
                    // holds the mutex. Ask the non-elevated instance to yield so we can connect to OpenVR.
                    try
                    {
                        using var yieldEvt = EventWaitHandle.OpenExisting(YieldToElevatedEventName);
                        yieldEvt.Set();
                        _ownsMutex = _instanceMutex.WaitOne(3500, false);
                    }
                    catch (AbandonedMutexException)
                    {
                        _ownsMutex = true;
                    }
                    catch { }
                }

                if (!_ownsMutex)
                {
                    // If launched interactively (not --minimized), signal the running instance to show its window.
                    if (!startMinimized)
                    {
                        try
                        {
                            using var evt = EventWaitHandle.OpenExisting(ActivateWindowEventName);
                            evt.Set();
                        }
                        catch { }
                    }

                    _instanceMutex.Dispose();
                    _instanceMutex = null;
                    Shutdown(0);
                    return;
                }

                _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateWindowEventName);
                _ = Task.Run(ListenForActivationSignal);

                if (!Environment.IsPrivilegedProcess)
                {
                    _yieldToElevatedEvent = new EventWaitHandle(false, EventResetMode.AutoReset, YieldToElevatedEventName);
                    _ = Task.Run(ListenForYieldToElevatedSignal);
                }
            }

            _runtime = new VRLyricsRuntimeHost();
            _runtime.Overlay.VrQuitRequested += () =>
            {
                if (_runtime.Settings.Current.ExitWithSteamVr)
                    Dispatcher.BeginInvoke(ExitApplication);
            };
            _runtime.Overlay.VrElevationRequired += () =>
            {
                if (!Environment.IsPrivilegedProcess && !selfTest && !isElevatedRetry)
                    Dispatcher.BeginInvoke(HandleVrElevationRequiredAsync);
            };

            _trayIcon = new TrayIconService(
                _runtime,
                openMainWindow: () => Dispatcher.BeginInvoke(ShowSettingsWindow),
                exitApplication: () => Dispatcher.BeginInvoke(ExitApplication));

            _mainWindow = new MainWindow(
                _runtime,
                onMinimizeToTray: () => _trayIcon.ShowMinimizedHintOnce(),
                onRequestExit: ExitApplication);

            string? dumpDir = null;
            for (var i = 0; i < e.Args.Length - 1; i++)
            {
                if (string.Equals(e.Args[i], "--dump-ui", StringComparison.OrdinalIgnoreCase))
                    dumpDir = e.Args[i + 1];
            }

            // Show the window immediately so startup feels instant even if SMTC / OpenVR takes a moment
            if (!startMinimized)
                ShowSettingsWindow();

            await _runtime.StartAsync();

            if (selfTest)
            {
                await Task.Delay(500);
                await _mainWindow.RunUiSelfTestAndScreenshotsAsync(dumpDir);
                await Task.Delay(300);
                ExitApplication();
            }
        }
        catch (Exception ex)
        {
            LogCrash("OnStartup", ex);
            ReportFatalErrorAndExit(ex);
        }
    }

    private void ListenForActivationSignal()
    {
        while (_activateEvent is { } evt && !_exiting)
        {
            try
            {
                if (evt.WaitOne(1000) && !_exiting)
                    Dispatcher.BeginInvoke(ShowSettingsWindow);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private void ListenForYieldToElevatedSignal()
    {
        while (_yieldToElevatedEvent is { } evt && !_exiting)
        {
            try
            {
                if (evt.WaitOne(1000) && !_exiting)
                {
                    Dispatcher.BeginInvoke(ExitApplication);
                    break;
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private async void HandleVrElevationRequiredAsync()
    {
        if (_exiting || _elevationRetryInProgress || Environment.IsPrivilegedProcess)
            return;

        _elevationRetryInProgress = true;

        // Wait briefly in case SteamVR's own elevated auto-launch process is already starting up
        // and about to take over via YieldToElevatedEventName (which avoids any UAC prompt).
        await Task.Delay(2500);
        if (_exiting)
            return;

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return;

        var wasWindowVisible = _mainWindow?.IsVisible == true;
        var args = wasWindowVisible ? "--elevated-retry" : "--minimized --elevated-retry";

        try
        {
            // Release single-instance handles first so the newly elevated process can immediately acquire the mutex.
            ReleaseSingleInstanceHandles();

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas"
            };
            System.Diagnostics.Process.Start(psi);
            ExitApplication();
        }
        catch (Exception ex)
        {
            // User declined UAC or launch failed; continue running in desktop mode.
            LogCrash("HandleVrElevationRequiredAsync", ex);
        }
    }

    private void ShowSettingsWindow()
    {
        if (_mainWindow is null) return;
        if (!_mainWindow.IsVisible)
            _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VRLyrics");
            Directory.CreateDirectory(dir);
            var logPath = Path.Combine(dir, "crash.log");
            File.AppendAllText(logPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [{source}]\n{ex}\n\n");
        }
        catch { }
    }

    private void ReportFatalErrorAndExit(Exception ex)
    {
        if (_exiting) return;
        _exiting = true;

        try { _trayIcon?.Dispose(); } catch { }
        _trayIcon = null;

        ReleaseSingleInstanceHandles();

        try
        {
            System.Windows.MessageBox.Show(
                $"VRLyrics 启动或运行时遇到错误：\n\n{ex.Message}\n\n详细日志已保存至 %LocalAppData%\\VRLyrics\\crash.log",
                "VRLyrics 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch { }

        Environment.Exit(1);
    }

    private void ReleaseSingleInstanceHandles()
    {
        try
        {
            _activateEvent?.Set();
            _activateEvent?.Dispose();
        }
        catch { }
        _activateEvent = null;

        try
        {
            _yieldToElevatedEvent?.Set();
            _yieldToElevatedEvent?.Dispose();
        }
        catch { }
        _yieldToElevatedEvent = null;

        if (_instanceMutex is not null)
        {
            if (_ownsMutex)
            {
                try { _instanceMutex.ReleaseMutex(); } catch { }
                _ownsMutex = false;
            }
            try { _instanceMutex.Dispose(); } catch { }
            _instanceMutex = null;
        }
    }

    private async void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;

        if (_mainWindow is not null)
        {
            _mainWindow.AllowPermanentClose();
            _mainWindow.Close();
            _mainWindow = null;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;

        if (_runtime is not null)
        {
            await _runtime.DisposeAsync();
            _runtime = null;
        }

        ReleaseSingleInstanceHandles();
        Shutdown(0);
    }
}
