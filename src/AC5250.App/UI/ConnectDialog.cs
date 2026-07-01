using AC5250.Session;

namespace AC5250.UI;

public class ConnectDialog : Form
{
    private TextBox _hostBox = null!;
    private NumericUpDown _portBox = null!;
    private CheckBox _sslCheck = null!;
    private TextBox _deviceBox = null!;
    private ComboBox _sizeBox = null!;
    private Button _okButton = null!;
    private Button _cancelButton = null!;

    public ConnectionSettings Settings { get; private set; } = new();

    public ConnectDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Text = "Connect to AS/400";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(440, 360);
        BackColor = DarkTheme.Surface;
        ForeColor = DarkTheme.TextPrimary;
        Font = DarkTheme.UIFont;

        HandleCreated += (_, _) => MainForm.ApplyDarkTitleBar(this);

        // Title header
        var header = new Label
        {
            Text = "New Connection",
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            ForeColor = DarkTheme.TextPrimary,
            Location = new Point(24, 16),
            AutoSize = true,
        };
        Controls.Add(header);

        var subtitle = new Label
        {
            Text = "Enter the connection details for your IBM AS/400 system.",
            Font = DarkTheme.UIFont,
            ForeColor = DarkTheme.TextSecondary,
            Location = new Point(24, 44),
            AutoSize = true,
        };
        Controls.Add(subtitle);

        // Divider
        var divider = new Panel
        {
            Location = new Point(24, 68),
            Size = new Size(390, 1),
            BackColor = DarkTheme.BorderSubtle,
        };
        Controls.Add(divider);

        int y = 82;
        int labelLeft = 24;
        int controlLeft = 140;
        int controlWidth = 260;
        int rowHeight = 36;

        // Host
        AddLabel("Host", labelLeft, y);
        _hostBox = CreateTextBox(controlLeft, y, controlWidth);
        _hostBox.PlaceholderText = "hostname or IP address";
        y += rowHeight;

        // Port
        AddLabel("Port", labelLeft, y);
        _portBox = new NumericUpDown
        {
            Location = new Point(controlLeft, y),
            Size = new Size(90, 28),
            Minimum = 1,
            Maximum = 65535,
            Value = 23,
            BackColor = DarkTheme.Background,
            ForeColor = DarkTheme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = DarkTheme.UIFont,
        };
        Controls.Add(_portBox);
        y += rowHeight;

        // SSL
        AddLabel("Security", labelLeft, y);
        _sslCheck = new CheckBox
        {
            Location = new Point(controlLeft, y),
            Size = new Size(controlWidth, 24),
            Text = "Use SSL/TLS encryption",
            ForeColor = DarkTheme.TextPrimary,
            Font = DarkTheme.UIFont,
            FlatStyle = FlatStyle.Flat,
        };
        Controls.Add(_sslCheck);
        y += rowHeight;

        // Device Name
        AddLabel("Device Name", labelLeft, y);
        _deviceBox = CreateTextBox(controlLeft, y, controlWidth);
        _deviceBox.PlaceholderText = "auto-assign if blank";
        y += rowHeight;

        // Screen Size
        AddLabel("Screen Size", labelLeft, y);
        _sizeBox = new ComboBox
        {
            Location = new Point(controlLeft, y),
            Size = new Size(controlWidth, 28),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = DarkTheme.Background,
            ForeColor = DarkTheme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = DarkTheme.UIFont,
        };
        _sizeBox.Items.AddRange(new object[] { "24 x 80  (Standard)", "27 x 132  (Wide)" });
        _sizeBox.SelectedIndex = 0;
        Controls.Add(_sizeBox);
        y += rowHeight + 16;

        // Bottom divider
        var divider2 = new Panel
        {
            Location = new Point(24, y),
            Size = new Size(390, 1),
            BackColor = DarkTheme.BorderSubtle,
        };
        Controls.Add(divider2);
        y += 12;

        // Buttons
        _cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(240, y),
            Size = new Size(80, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = DarkTheme.SurfaceLighter,
            ForeColor = DarkTheme.TextPrimary,
            Font = DarkTheme.UIFont,
        };
        _cancelButton.FlatAppearance.BorderColor = DarkTheme.Border;
        Controls.Add(_cancelButton);

        _okButton = new Button
        {
            Text = "Connect",
            DialogResult = DialogResult.OK,
            Location = new Point(328, y),
            Size = new Size(86, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = DarkTheme.AccentDim,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        };
        _okButton.FlatAppearance.BorderColor = DarkTheme.Accent;
        _okButton.Click += OnOkClick;
        Controls.Add(_okButton);

        AcceptButton = _okButton;
        CancelButton = _cancelButton;
    }

    private Label AddLabel(string text, int x, int y)
    {
        var label = new Label
        {
            Text = text,
            Location = new Point(x, y + 4),
            AutoSize = true,
            ForeColor = DarkTheme.TextSecondary,
            Font = DarkTheme.UIFont,
        };
        Controls.Add(label);
        return label;
    }

    private TextBox CreateTextBox(int x, int y, int width)
    {
        var box = new TextBox
        {
            Location = new Point(x, y),
            Size = new Size(width, 28),
            BackColor = DarkTheme.Background,
            ForeColor = DarkTheme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = DarkTheme.UIFont,
        };
        Controls.Add(box);
        return box;
    }

    private void OnOkClick(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_hostBox.Text))
        {
            _hostBox.BackColor = Color.FromArgb(60, 30, 30);
            _hostBox.Focus();
            DialogResult = DialogResult.None;
            return;
        }

        Settings = new ConnectionSettings
        {
            HostName = _hostBox.Text.Trim(),
            Port = (int)_portBox.Value,
            UseSsl = _sslCheck.Checked,
            DeviceName = _deviceBox.Text.Trim(),
            ScreenSize = _sizeBox.SelectedIndex == 1 ? ScreenSize.Wide : ScreenSize.Normal,
        };
    }
}
