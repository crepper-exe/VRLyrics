using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SkiaSharp;
using VRLyrics.App.Interop;
using VRLyrics.Overlay.Rendering;

namespace VRLyrics.App;

public partial class ColorPickerWindow : Window
{
    private static readonly string[] PresetSwatches =
    [
        "#FF7CF7FF", "#FF00C2FF", "#FF38BDF8", "#FF60A5FA", "#FF818CF8", "#FFC084FC",
        "#FFFF85C0", "#FFF43F5E", "#FFFFB84D", "#FFFACC15", "#FF4ADE80", "#FF2DD4BF",
        "#FFFFFFFF", "#FFE2E8F0", "#FF94A3B8", "#E8090D16", "#380A0F1A", "#00000000"
    ];

    private bool _suppressEvents;

    public ColorPickerWindow(string title, string initialHex)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Win11BackdropHelper.ApplyFluentDarkWindowFrame(this);

        LblTargetName.Text = title;
        SelectedColorHex = initialHex;

        BuildSwatchButtons();
        SetFromHex(initialHex);
    }

    public string SelectedColorHex { get; private set; }

    private void BuildSwatchButtons()
    {
        foreach (var hex in PresetSwatches)
        {
            var sk = ColorParseHelper.Parse(hex, SKColors.White);
            var btn = new System.Windows.Controls.Button
            {
                Width = 34,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 8),
                ToolTip = hex,
                Tag = hex,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(sk.Alpha, sk.Red, sk.Green, sk.Blue)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                BorderThickness = new Thickness(1)
            };
            btn.Click += (_, _) => SetFromHex(hex);
            SwatchWrapPanel.Children.Add(btn);
        }
    }

    private void SetFromHex(string hex)
    {
        var sk = ColorParseHelper.Parse(hex, SKColors.White);
        _suppressEvents = true;
        try
        {
            SldR.Value = sk.Red;
            SldG.Value = sk.Green;
            SldB.Value = sk.Blue;
            SldA.Value = sk.Alpha;
            UpdatePreviewAndText(sk.Alpha, sk.Red, sk.Green, sk.Blue, updateTextBox: true);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void OnChannelSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || !IsLoaded) return;
        _suppressEvents = true;
        try
        {
            var r = (byte)Math.Round(SldR.Value);
            var g = (byte)Math.Round(SldG.Value);
            var b = (byte)Math.Round(SldB.Value);
            var a = (byte)Math.Round(SldA.Value);
            UpdatePreviewAndText(a, r, g, b, updateTextBox: true);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void OnHexTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents || !IsLoaded) return;
        var raw = TxtHex.Text?.Trim() ?? "";
        if (!raw.StartsWith('#')) raw = "#" + raw;
        if (raw.Length is not (7 or 9)) return;

        var sk = ColorParseHelper.Parse(raw, SKColors.White);
        _suppressEvents = true;
        try
        {
            SldR.Value = sk.Red;
            SldG.Value = sk.Green;
            SldB.Value = sk.Blue;
            SldA.Value = sk.Alpha;
            UpdatePreviewAndText(sk.Alpha, sk.Red, sk.Green, sk.Blue, updateTextBox: false);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void UpdatePreviewAndText(byte a, byte r, byte g, byte b, bool updateTextBox)
    {
        LblR.Text = r.ToString();
        LblG.Text = g.ToString();
        LblB.Text = b.ToString();
        LblA.Text = a.ToString();

        var hex = $"#{a:X2}{r:X2}{g:X2}{b:X2}";
        SelectedColorHex = hex;
        if (updateTextBox)
            TxtHex.Text = hex;

        PreviewColorBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
