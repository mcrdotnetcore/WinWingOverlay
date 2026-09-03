namespace WinWingOverlay;

/// <summary>
/// The opacity sliders, opened from the tray menu. Changes apply live as you drag and are
/// written to the config when the window closes.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly OverlayConfig _config;
    private readonly Action _onChanged;

    private readonly TrackBar _overall = new();
    private readonly TrackBar _background = new();
    private readonly Label _overallValue = new();
    private readonly Label _backgroundValue = new();

    public SettingsForm(OverlayConfig config, Action onChanged)
    {
        _config = config;
        _onChanged = onChanged;

        Text = "Overlay opacity";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(360, 210);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;

        Controls.Add(MakeLabel("Everything", 14));
        Controls.Add(Configure(_overall, 15, 100, (int)Math.Round(config.Opacity * 100), 34));
        Controls.Add(Position(_overallValue, 300, 14));

        Controls.Add(MakeLabel("Background only", 92));
        Controls.Add(Configure(_background, 0, 100, (int)Math.Round(config.BackgroundOpacity * 100), 112));
        Controls.Add(Position(_backgroundValue, 300, 92));

        Controls.Add(new Label
        {
            Left = 14,
            Top = 152,
            Width = 332,
            Height = 32,
            ForeColor = SystemColors.GrayText,
            Text = "Background fades the dark panels only — outlines, text and live values stay solid."
        });

        var close = new Button { Text = "Close", Left = 270, Top = 176, Width = 76 };
        close.Click += (_, _) => Close();
        Controls.Add(close);
        AcceptButton = close;

        _overall.ValueChanged += (_, _) => Apply();
        _background.ValueChanged += (_, _) => Apply();

        UpdateValueLabels();
    }

    private static Label MakeLabel(string text, int top) => new()
    {
        Left = 14,
        Top = top,
        Width = 200,
        Text = text
    };

    private static Label Position(Label label, int left, int top)
    {
        label.Left = left;
        label.Top = top;
        label.Width = 46;
        label.TextAlign = ContentAlignment.TopRight;
        return label;
    }

    private static TrackBar Configure(TrackBar bar, int min, int max, int value, int top)
    {
        bar.Minimum = min;
        bar.Maximum = max;
        bar.Value = Math.Clamp(value, min, max);
        bar.TickFrequency = 10;
        bar.LargeChange = 10;
        bar.SmallChange = 1;
        bar.Left = 12;
        bar.Top = top;
        bar.Width = 334;
        return bar;
    }

    private void Apply()
    {
        _config.Opacity = _overall.Value / 100.0;
        _config.BackgroundOpacity = _background.Value / 100.0;
        UpdateValueLabels();
        _onChanged();
    }

    private void UpdateValueLabels()
    {
        _overallValue.Text = $"{_overall.Value}%";
        _backgroundValue.Text = $"{_background.Value}%";
    }

    /// <summary>Pull slider positions back from the config, e.g. after a mouse-wheel change.</summary>
    public void Sync()
    {
        _overall.Value = Math.Clamp((int)Math.Round(_config.Opacity * 100), _overall.Minimum, _overall.Maximum);
        _background.Value = Math.Clamp((int)Math.Round(_config.BackgroundOpacity * 100),
            _background.Minimum, _background.Maximum);
        UpdateValueLabels();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _config.Save();
        base.OnFormClosed(e);
    }
}
