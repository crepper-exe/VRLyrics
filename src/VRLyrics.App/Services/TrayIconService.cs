using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VRLyrics.Core.Models;

namespace VRLyrics.App.Services;

/// <summary>
/// Manages the Windows system tray (<see cref="NotifyIcon"/>) icon and quick-action context menu.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint hIcon);

    private readonly VRLyricsRuntimeHost _runtime;
    private readonly Action _openMainWindow;
    private readonly Action _exitApplication;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _modeHeadItem;
    private readonly ToolStripMenuItem _modeWorldItem;
    private readonly ToolStripMenuItem _modeLeftHandItem;
    private readonly ToolStripMenuItem _modeRightHandItem;
    private readonly ToolStripMenuItem _allowWorldGrabItem;
    private readonly ToolStripMenuItem _srcAutoItem;
    private readonly ToolStripMenuItem _srcNetEaseItem;
    private readonly ToolStripMenuItem _srcQqMusicItem;
    private readonly ToolStripMenuItem _srcLrclibItem;
    private readonly ToolStripMenuItem _srcLocalItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly nint _hIcon;
    private readonly Icon _icon;
    private bool _balloonShown;

    public TrayIconService(VRLyricsRuntimeHost runtime, Action openMainWindow, Action exitApplication)
    {
        _runtime = runtime;
        _openMainWindow = openMainWindow;
        _exitApplication = exitApplication;

        (_icon, _hIcon) = CreateVrLyricsIcon();

        _menu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("打开 VRLyrics 设置窗口")
        {
            Font = new Font(_menu.Font, FontStyle.Bold)
        };
        openItem.Click += (_, _) => _openMainWindow();

        _enabledItem = new ToolStripMenuItem("显示 VR 歌词叠加层")
        {
            CheckOnClick = false
        };
        _enabledItem.Click += (_, _) =>
            _runtime.Settings.Update(s => s with { Enabled = !s.Enabled });

        var modeMenu = new ToolStripMenuItem("显示位置模式");
        _modeHeadItem = CreateModeItem("头部跟随 (视野下方)", OverlayAttachmentMode.HeadLocked);
        _modeWorldItem = CreateModeItem("空间固定 (手柄可拖拽)", OverlayAttachmentMode.WorldLocked);
        _modeLeftHandItem = CreateModeItem("左手腕跟随", OverlayAttachmentMode.LeftHand);
        _modeRightHandItem = CreateModeItem("右手腕跟随", OverlayAttachmentMode.RightHand);

        var recenterWorldItem = new ToolStripMenuItem("📍 将空间歌词移至当前视野正前方");
        recenterWorldItem.Click += (_, _) => _runtime.RecenterWorldOverlayToHmd();

        _allowWorldGrabItem = new ToolStripMenuItem("✋ 允许 VR 手柄抓取拖动空间歌词")
        {
            CheckOnClick = false
        };
        _allowWorldGrabItem.Click += (_, _) =>
            _runtime.Settings.Update(s => s with { EnableWorldControllerGrab = !s.EnableWorldControllerGrab });

        modeMenu.DropDownItems.AddRange([
            _modeHeadItem,
            _modeWorldItem,
            _modeLeftHandItem,
            _modeRightHandItem,
            new ToolStripSeparator(),
            recenterWorldItem,
            _allowWorldGrabItem
        ]);

        var offsetMenu = new ToolStripMenuItem("歌词时间偏移微调");
        var offsetMinus = new ToolStripMenuItem("歌词提前 0.2s (-200ms)");
        offsetMinus.Click += (_, _) => _runtime.Settings.Update(s => s with { GlobalOffsetMs = s.GlobalOffsetMs - 200 });
        var offsetPlus = new ToolStripMenuItem("歌词延后 0.2s (+200ms)");
        offsetPlus.Click += (_, _) => _runtime.Settings.Update(s => s with { GlobalOffsetMs = s.GlobalOffsetMs + 200 });
        var offsetZero = new ToolStripMenuItem("重置偏移 (0ms)");
        offsetZero.Click += (_, _) => _runtime.Settings.Update(s => s with { GlobalOffsetMs = 0 });
        offsetMenu.DropDownItems.AddRange([offsetMinus, offsetPlus, new ToolStripSeparator(), offsetZero]);

        var sourceMenu = new ToolStripMenuItem("歌词源偏好");
        _srcAutoItem = CreateSourceItem("⚡ 自动优选 (Auto)", "Auto");
        _srcNetEaseItem = CreateSourceItem("🎵 网易云音乐优先", "NetEase");
        _srcQqMusicItem = CreateSourceItem("🎶 QQ音乐优先", "QQMusic");
        _srcLrclibItem = CreateSourceItem("🌐 LRCLIB 国际曲库优先", "LRCLIB");
        _srcLocalItem = CreateSourceItem("📁 本地 LRC 目录优先", "LocalLRC");
        sourceMenu.DropDownItems.AddRange([_srcAutoItem, _srcNetEaseItem, _srcQqMusicItem, _srcLrclibItem, _srcLocalItem]);

        var reloadItem = new ToolStripMenuItem("重新获取当前歌曲歌词");
        reloadItem.Click += (_, _) => _runtime.ReloadCurrentLyrics(bypassCache: true);

        _autoStartItem = new ToolStripMenuItem("随 SteamVR 自动启动")
        {
            CheckOnClick = false
        };
        _autoStartItem.Click += (_, _) =>
            _runtime.SetAutoStartWithSteamVr(!_runtime.Settings.Current.AutoStartWithSteamVr);

        var exitItem = new ToolStripMenuItem("退出 VRLyrics");
        exitItem.Click += (_, _) => _exitApplication();

        _menu.Items.AddRange(
        [
            openItem,
            new ToolStripSeparator(),
            _enabledItem,
            modeMenu,
            offsetMenu,
            sourceMenu,
            reloadItem,
            new ToolStripSeparator(),
            _autoStartItem,
            new ToolStripSeparator(),
            exitItem
        ]);

        _menu.Opening += (_, _) => SyncMenuChecks();

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "VRLyrics - SteamVR 实时歌词叠加层",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => _openMainWindow();

        _runtime.Settings.SettingsChanged += _ => SyncMenuChecks();
        SyncMenuChecks();
    }

    public Icon AppIcon => _icon;

    public void ShowMinimizedHintOnce()
    {
        if (_balloonShown) return;
        _balloonShown = true;
        _notifyIcon.ShowBalloonTip(
            2500,
            "VRLyrics 仍在后台运行",
            "双击系统托盘图标可重新打开设置窗口；右键托盘图标可快速调节或退出。",
            ToolTipIcon.Info);
    }

    private ToolStripMenuItem CreateModeItem(string title, OverlayAttachmentMode mode)
    {
        var item = new ToolStripMenuItem(title);
        item.Click += (_, _) => _runtime.SwitchAttachmentMode(mode);
        return item;
    }

    private ToolStripMenuItem CreateSourceItem(string title, string provider)
    {
        var item = new ToolStripMenuItem(title);
        item.Click += (_, _) => _runtime.SwitchLyricsProvider(provider);
        return item;
    }

    private void SyncMenuChecks()
    {
        var cfg = _runtime.Settings.Current;
        _enabledItem.Checked = cfg.Enabled;
        _modeHeadItem.Checked = cfg.AttachmentMode == OverlayAttachmentMode.HeadLocked;
        _modeWorldItem.Checked = cfg.AttachmentMode == OverlayAttachmentMode.WorldLocked;
        _modeLeftHandItem.Checked = cfg.AttachmentMode == OverlayAttachmentMode.LeftHand;
        _modeRightHandItem.Checked = cfg.AttachmentMode == OverlayAttachmentMode.RightHand;
        _allowWorldGrabItem.Checked = cfg.EnableWorldControllerGrab;

        var pref = cfg.PreferredLyricsProvider ?? "Auto";
        _srcNetEaseItem.Checked = string.Equals(pref, "NetEase", StringComparison.OrdinalIgnoreCase);
        _srcQqMusicItem.Checked = string.Equals(pref, "QQMusic", StringComparison.OrdinalIgnoreCase);
        _srcLrclibItem.Checked = string.Equals(pref, "LRCLIB", StringComparison.OrdinalIgnoreCase);
        _srcLocalItem.Checked = string.Equals(pref, "LocalLRC", StringComparison.OrdinalIgnoreCase);
        _srcAutoItem.Checked = !_srcNetEaseItem.Checked
                               && !_srcQqMusicItem.Checked
                               && !_srcLrclibItem.Checked
                               && !_srcLocalItem.Checked;
        _autoStartItem.Checked = cfg.AutoStartWithSteamVr;
    }

    private static (Icon Icon, nint HIcon) CreateVrLyricsIcon()
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Dark rounded badge background
            using var bgBrush = new LinearGradientBrush(
                new Rectangle(0, 0, 64, 64),
                Color.FromArgb(255, 16, 24, 42),
                Color.FromArgb(255, 9, 13, 24),
                45f);
            using var bgPath = CreateRoundedRect(new RectangleF(2, 2, 60, 60), 14f);
            g.FillPath(bgBrush, bgPath);

            using var borderPen = new Pen(Color.FromArgb(220, 0, 210, 255), 2.5f);
            g.DrawPath(borderPen, bgPath);

            // Cyan glowing lyric bars inside visor
            using var sungBrush = new SolidBrush(Color.FromArgb(255, 96, 244, 255));
            using var unsungBrush = new SolidBrush(Color.FromArgb(190, 215, 228, 242));

            // Top line (karaoke half-filled)
            g.FillRectangle(sungBrush, new RectangleF(12, 20, 24, 7));
            g.FillRectangle(unsungBrush, new RectangleF(36, 20, 16, 7));

            // Bottom translation line
            g.FillRectangle(unsungBrush, new RectangleF(16, 35, 32, 5));
        }

        var hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        return (icon, hIcon);
    }

    private static GraphicsPath CreateRoundedRect(RectangleF rect, float radius)
    {
        var d = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
        if (_hIcon != 0)
            DestroyIcon(_hIcon);
    }
}
