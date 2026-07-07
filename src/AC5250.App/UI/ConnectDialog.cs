using AC5250.Session;

namespace AC5250.UI;

public class ConnectDialog : Form
{
    private TextBox _sessionNameBox = null!;
    private TextBox _hostBox = null!;
    private NumericUpDown _portBox = null!;
    private CheckBox _sslCheck = null!;
    private TextBox _deviceBox = null!;
    private ComboBox _sizeBox = null!;
    private ComboBox _savedBox = null!;
    private Button _saveButton = null!;
    private Button _deleteButton = null!;
    private Button _okButton = null!;
    private Button _cancelButton = null!;

    private List<ConnectionSettings> _saved = new();
    private bool _loadingSaved;

    public ConnectionSettings Settings { get; private set; } = new();

    // When true this dialog edits a saved connection (persists to ConnectionStore on OK)
    // instead of returning settings to connect. Used by Manage Saved Connections.
    private readonly bool _saveOnly;

    public ConnectDialog(ConnectionSettings? preset = null, bool saveOnly = false)
    {
        _saveOnly = saveOnly;
        InitializeComponent();
        if (preset != null) FillFrom(preset);
    }

    private void InitializeComponent()
    {
        Text = "Connect to AS/400";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = DarkTheme.Surface;
        ForeColor = DarkTheme.TextPrimary;
        Font = DarkTheme.UIFont;

        HandleCreated += (_, _) => MainForm.ApplyDarkTitleBar(this);

        const int labelLeft = 24;
        const int controlLeft = 150;
        const int controlWidth = 264;
        const int rowHeight = 36;
        const int contentRight = controlLeft + controlWidth; // 414

        // Title header
        Controls.Add(new Label
        {
            Text = "New Connection",
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            ForeColor = DarkTheme.TextPrimary,
            Location = new Point(labelLeft, 16),
            AutoSize = true,
        });
        Controls.Add(new Label
        {
            Text = "Enter the connection details for your IBM AS/400 system.",
            Font = DarkTheme.UIFont,
            ForeColor = DarkTheme.TextSecondary,
            Location = new Point(labelLeft, 44),
            AutoSize = true,
        });
        Controls.Add(new Panel
        {
            Location = new Point(labelLeft, 68),
            Size = new Size(contentRight - labelLeft, 1),
            BackColor = DarkTheme.BorderSubtle,
        });

        int y = 82;

        // Saved connections
        AddLabel("Saved", labelLeft, y);
        _savedBox = new ComboBox
        {
            Location = new Point(controlLeft, y),
            Size = new Size(130, 28),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = DarkTheme.Background,
            ForeColor = DarkTheme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = DarkTheme.UIFont,
        };
        _savedBox.SelectedIndexChanged += OnSavedSelected;
        Controls.Add(_savedBox);

        _saveButton = MakeSmallButton("Save", controlLeft + 136, y - 1, 60);
        _saveButton.Click += OnSaveClick;
        _deleteButton = MakeSmallButton("Delete", controlLeft + 200, y - 1, 64);
        _deleteButton.Click += OnDeleteClick;
        y += rowHeight;

        // Session Name
        AddLabel("Session Name", labelLeft, y);
        _sessionNameBox = CreateTextBox(controlLeft, y, controlWidth);
        _sessionNameBox.PlaceholderText = "optional label for the tab";
        y += rowHeight;

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

        // Workstation ID (5250 device name)
        AddLabel("Workstation ID", labelLeft, y);
        _deviceBox = CreateTextBox(controlLeft, y, controlWidth);
        _deviceBox.PlaceholderText = "blank = auto-assign; NAME* to keep unique";
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
        Controls.Add(new Panel
        {
            Location = new Point(labelLeft, y),
            Size = new Size(contentRight - labelLeft, 1),
            BackColor = DarkTheme.BorderSubtle,
        });
        y += 14;

        // Buttons (right-aligned to the content edge)
        int buttonY = y;
        _okButton = new Button
        {
            Text = _saveOnly ? "Save" : "Connect",
            DialogResult = DialogResult.OK,
            Location = new Point(contentRight - 86, buttonY),
            Size = new Size(86, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = DarkTheme.AccentDim,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        };
        _okButton.FlatAppearance.BorderColor = DarkTheme.Accent;
        _okButton.Click += OnOkClick;
        Controls.Add(_okButton);

        _cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(contentRight - 86 - 8 - 80, buttonY),
            Size = new Size(80, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = DarkTheme.SurfaceLighter,
            ForeColor = DarkTheme.TextPrimary,
            Font = DarkTheme.UIFont,
        };
        _cancelButton.FlatAppearance.BorderColor = DarkTheme.Border;
        Controls.Add(_cancelButton);

        // Size the form to its content, leaving a margin below the buttons so they
        // never sit flush against the window edge. ClientSize excludes the title
        // bar/borders, so this math is exact regardless of the OS chrome.
        ClientSize = new Size(contentRight + labelLeft, buttonY + 32 + 20);

        LoadSaved();

        AcceptButton = _okButton;
        CancelButton = _cancelButton;
    }

    private Button MakeSmallButton(string text, int x, int y, int w)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = DarkTheme.SurfaceLighter,
            ForeColor = DarkTheme.TextPrimary,
            Font = DarkTheme.UIFont,
        };
        b.FlatAppearance.BorderColor = DarkTheme.Border;
        Controls.Add(b);
        return b;
    }

    private void LoadSaved()
    {
        _loadingSaved = true;
        _saved = ConnectionStore.Load();
        _savedBox.Items.Clear();
        _savedBox.Items.Add("(new connection)");
        foreach (var c in _saved) _savedBox.Items.Add(c.DisplayName);
        _savedBox.SelectedIndex = 0;
        _deleteButton.Enabled = false;
        _loadingSaved = false;
    }

    private void OnSavedSelected(object? sender, EventArgs e)
    {
        if (_loadingSaved) return;
        int idx = _savedBox.SelectedIndex - 1; // 0 == "(new connection)"
        _deleteButton.Enabled = idx >= 0;
        if (idx >= 0 && idx < _saved.Count) FillFrom(_saved[idx]);
    }

    private void OnSaveClick(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_hostBox.Text))
        {
            _hostBox.BackColor = Color.FromArgb(60, 30, 30);
            _hostBox.Focus();
            return;
        }
        var settings = BuildSettings();
        _saved = ConnectionStore.Upsert(settings);
        LoadSaved();
        int i = _savedBox.Items.IndexOf(settings.DisplayName);
        if (i > 0) _savedBox.SelectedIndex = i;
    }

    private void OnDeleteClick(object? sender, EventArgs e)
    {
        int idx = _savedBox.SelectedIndex - 1;
        if (idx < 0 || idx >= _saved.Count) return;
        _saved = ConnectionStore.Remove(_saved[idx].DisplayName);
        LoadSaved();
    }

    private void FillFrom(ConnectionSettings c)
    {
        _sessionNameBox.Text = c.SessionName;
        _hostBox.Text = c.HostName;
        _portBox.Value = Math.Clamp(c.Port, (int)_portBox.Minimum, (int)_portBox.Maximum);
        _sslCheck.Checked = c.UseSsl;
        _deviceBox.Text = c.DeviceName;
        _sizeBox.SelectedIndex = c.ScreenSize == ScreenSize.Wide ? 1 : 0;
    }

    private ConnectionSettings BuildSettings() => new()
    {
        HostName = _hostBox.Text.Trim(),
        Port = (int)_portBox.Value,
        UseSsl = _sslCheck.Checked,
        DeviceName = _deviceBox.Text.Trim(),
        SessionName = _sessionNameBox.Text.Trim(),
        ScreenSize = _sizeBox.SelectedIndex == 1 ? ScreenSize.Wide : ScreenSize.Normal,
    };

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

        Settings = BuildSettings();
        if (_saveOnly) ConnectionStore.Upsert(Settings);
    }
}
