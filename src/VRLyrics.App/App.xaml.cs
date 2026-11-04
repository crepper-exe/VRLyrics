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

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _activateEvent;
    private VRLyricsRuntimeHost? _runtime;
    private TrayIconService? _trayIcon;
    private MainWindow? _mainWindow;
    private bool _exiting;

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

            if (!_ownsMutex)
            {
                // Signal the already-running healthy instance to show its settings window.
                try
                {
                    using var evt = EventWaitHandle.OpenExisting(ActivateWindowEventName);
                    evt.Set();
                }
                catch { }

                _instanceMutex.Dispose();
                _instanceMutex = null;
                Shutdown(0);
                return;
            }

            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateWindowEventName);
            _ = Task.Run(ListenForActivationSignal);

            _runtime = new VRLyricsRuntimeHost();
            _runtime.Overlay.VrQuitRequested += () =>
            {
                if (_runtime.Settings.Current.ExitWithSteamVr)
                    Dispatcher.BeginInvoke(ExitApplication);
            };
            _trayIcon = new TrayIconService(
                _runtime,
                openMainWindow: () => Dispatcher.BeginInvoke(ShowSettingsWindow),
                exitApplication: () => Dispatcher.BeginInvoke(ExitApplication));

            _mainWindow = new MainWindow(
                _runtime,
                onMinimizeToTray: () => _trayIcon.ShowMinimizedHintOnce(),
                onRequestExit: ExitApplication)
            {
                Icon = Imaging.CreateBitmapSourceFromHIcon(
                    _trayIcon.AppIcon.Handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions())
            };

            var startMinimized = e.Args.Any(a =>
                string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

            var selfTest = e.Args.Any(a =>
                string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase));

            // Show the window immediately so startup feels instant even if SMTC / OpenVR takes a moment
            if (!startMinimized)
                ShowSettingsWindow();

            await _runtime.StartAsync();

            if (selfTest)
            {
                await Task.Delay(1200);
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
