using AC5250.Security;
using AC5250.Session;

namespace AC5250.UI;

/// <summary>
/// Manage IBM i sign-on credentials in the Windows Credential Manager. Each credential is bound
/// to a <em>saved connection</em> (not just a host), so two connections to the same host can hold
/// different logins and a login follows its connection through a rename. A connection can hold
/// several logins, each under a short label (e.g. "ADMIN", "TESTUSER"); one may be marked the
/// default. Used by the Home page's Quick Launch, the Session menu, and the MCP `signon` tool.
/// Passwords are never displayed back — to change one, re-enter it.
/// </summary>
internal sealed class CredentialsDialog : Form
{
    private readonly ListBox _list = new();
    private readonly ComboBox _connCombo = new();
    private readonly Label _noConnLabel = new();
    private readonly TextBox _labelBox = new();
    private readonly TextBox _userBox = new();
    private readonly TextBox _pwBox = new();

    private List<ConnectionSettings> _connections = new();
    private List<(ConnectionSettings Conn, string Label, string User)> _entries = new();
    private string? _editConnId;    // the entry currently loaded from the list (null = creating new)
    private string? _editLabel;

    public CredentialsDialog()
    {
        Text = "Saved Credentials";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = DarkTheme.Surface;
        ForeColor = DarkTheme.TextPrimary;
        Font = DarkTheme.UIFont;
        ClientSize = new Size(480, 410);
        HandleCreated += (_, _) => MainForm.ApplyDarkTitleBar(this);

        Controls.Add(new Label
        {
            Text = "Sign-on credentials (stored in Windows Credential Manager)",
            ForeColor = DarkTheme.TextSecondary,
            AutoSize = true,
            Location = new Point(16, 12),
        });

        _list.SetBounds(16, 40, 448, 150);
        _list.BackColor = DarkTheme.Background;
        _list.ForeColor = DarkTheme.TextPrimary;
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.IntegralHeight = false;
        _list.SelectedIndexChanged += OnSelect;
        Controls.Add(_list);

        AddLabel("Connection", 16, 200);
        _connCombo.SetBounds(110, 196, 354, 28);
        _connCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _connCombo.FlatStyle = FlatStyle.Flat;
        _connCombo.BackColor = DarkTheme.Background;
        _connCombo.ForeColor = DarkTheme.TextPrimary;
        Controls.Add(_connCombo);

        // Shown instead of the combo when there are no saved connections to attach a login to.
        _noConnLabel.SetBounds(110, 200, 354, 22);
        _noConnLabel.Text = "No saved connections — add one via File ▸ Manage Saved Connections.";
        _noConnLabel.ForeColor = DarkTheme.TextMuted;
        _noConnLabel.Visible = false;
        Controls.Add(_noConnLabel);

        AddLabel("Label", 16, 234);
        Config(_labelBox, 110, 230, 354);
        _labelBox.PlaceholderText = "e.g. ADMIN or TESTUSER (blank = default)";

        AddLabel("User", 16, 268);
        Config(_userBox, 110, 264, 354);

        AddLabel("Password", 16, 302);
        Config(_pwBox, 110, 298, 354);
        _pwBox.UseSystemPasswordChar = true;
        _pwBox.PlaceholderText = "leave blank to keep, or re-enter to change";

        var newBtn = MakeButton("New", 16, 344, 60);
        newBtn.Click += OnNew;
        var save = MakeButton("Save", 82, 344, 66);
        save.Click += OnSave;
        var del = MakeButton("Delete", 154, 344, 66);
        del.Click += OnDelete;
        var def = MakeButton("Set Default", 226, 344, 96);
        def.Click += OnSetDefault;
        var close = MakeButton("Close", 386, 344, 78);
        close.DialogResult = DialogResult.OK;

        AcceptButton = save;
        CancelButton = close;

        LoadConnections();
        Refresh_();
    }

    private void LoadConnections()
    {
        _connections = ConnectionStore.Load();
        _connCombo.Items.Clear();
        foreach (var c in _connections) _connCombo.Items.Add(c.DisplayName);

        bool has = _connections.Count > 0;
        _connCombo.Visible = has;
        _noConnLabel.Visible = !has;
        if (has) _connCombo.SelectedIndex = 0;
    }

    private ConnectionSettings? SelectedConnection()
    {
        int i = _connCombo.SelectedIndex;
        return i >= 0 && i < _connections.Count ? _connections[i] : null;
    }

    private void Refresh_()
    {
        // All connection-scoped credentials, mapped to their connection; orphans (whose
        // connection was deleted) are skipped.
        var byId = new Dictionary<string, ConnectionSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _connections)
            if (!string.IsNullOrEmpty(c.Id)) byId[c.Id] = c;

        _entries = CredentialStore.ListForConnection()
            .Where(e => byId.ContainsKey(e.ConnId))
            .Select(e => (Conn: byId[e.ConnId], e.Label, e.User))
            .OrderBy(e => e.Conn.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Default label + count per connection, for the "[default]" marker.
        var defaults = _entries.Select(e => e.Conn.Id).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(id => id, id => CredentialStore.GetDefaultLabelForConnection(id), StringComparer.OrdinalIgnoreCase);
        var counts = _entries.GroupBy(e => e.Conn.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        _list.Items.Clear();
        foreach (var e in _entries)
        {
            defaults.TryGetValue(e.Conn.Id, out string? defLabel);
            bool isDefault = defLabel != null
                ? string.Equals(defLabel, e.Label, StringComparison.OrdinalIgnoreCase)
                : counts[e.Conn.Id] == 1;
            string user = string.IsNullOrEmpty(e.User) ? "" : $"   ({e.User})";
            _list.Items.Add($"{e.Conn.DisplayName}  ›  {e.Label}{user}{(isDefault ? "   [default]" : "")}");
        }
    }

    private void OnSelect(object? sender, EventArgs e)
    {
        int i = _list.SelectedIndex;
        if (i < 0 || i >= _entries.Count) return;
        var entry = _entries[i];
        SelectConnectionInCombo(entry.Conn.Id);
        _labelBox.Text = entry.Label;
        _userBox.Text = entry.User;
        _pwBox.Text = ""; // never surface stored passwords
        _editConnId = entry.Conn.Id;   // remember what we're editing, so a move removes the original
        _editLabel = entry.Label;
    }

    private void SelectConnectionInCombo(string connId)
    {
        for (int i = 0; i < _connections.Count; i++)
            if (string.Equals(_connections[i].Id, connId, StringComparison.OrdinalIgnoreCase))
            { _connCombo.SelectedIndex = i; return; }
    }

    private string LabelOrDefault() =>
        string.IsNullOrWhiteSpace(_labelBox.Text) ? CredentialStore.DefaultLabel : _labelBox.Text.Trim();

    private void OnSave(object? sender, EventArgs e)
    {
        var conn = SelectedConnection();
        if (conn == null)
        {
            MessageBox.Show(this, "Add a saved connection first (File ▸ Manage Saved Connections).",
                "AC5250", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string connId = conn.Id;
        string label = LabelOrDefault();

        // Password left blank keeps the stored one — of the entry being edited (so a move still
        // keeps it), or of the target connection/label when creating fresh.
        string password = _pwBox.Text;
        if (password.Length == 0)
        {
            var existing = _editConnId != null
                ? CredentialStore.GetForConnection(_editConnId, _editLabel)
                : CredentialStore.GetForConnection(connId, label);
            if (existing is null)
            {
                _pwBox.BackColor = Color.FromArgb(60, 30, 30);
                _pwBox.Focus();
                return; // a new entry needs a password
            }
            password = existing.Value.Password;
        }

        // Moving an existing entry to a different connection/label: remove the original so we
        // don't leave a duplicate.
        if (_editConnId != null &&
            (!string.Equals(_editConnId, connId, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(_editLabel, label, StringComparison.OrdinalIgnoreCase)))
            CredentialStore.DeleteForConnection(_editConnId, _editLabel!);

        CredentialStore.SaveForConnection(connId, label, _userBox.Text.Trim(), password);
        _pwBox.Text = "";
        Refresh_();
        SelectEntry(connId, label);
    }

    private void OnDelete(object? sender, EventArgs e)
    {
        string? connId = _editConnId ?? SelectedConnection()?.Id;
        string label = _editLabel ?? LabelOrDefault();
        if (string.IsNullOrEmpty(connId)) return;
        CredentialStore.DeleteForConnection(connId, label);
        ClearForm();
        Refresh_();
    }

    private void OnSetDefault(object? sender, EventArgs e)
    {
        string? connId = _editConnId ?? SelectedConnection()?.Id;
        string label = _editLabel ?? LabelOrDefault();
        if (string.IsNullOrEmpty(connId)) return;
        CredentialStore.SetDefaultLabelForConnection(connId, label);
        Refresh_();
        SelectEntry(connId, label);
    }

    private void OnNew(object? sender, EventArgs e) => ClearForm();

    private void ClearForm()
    {
        _editConnId = _editLabel = null;
        _list.ClearSelected();
        _labelBox.Text = _userBox.Text = _pwBox.Text = "";
        if (_connections.Count > 0) _connCombo.SelectedIndex = 0;
        _labelBox.Focus();
    }

    private void SelectEntry(string connId, string label)
    {
        for (int i = 0; i < _entries.Count; i++)
            if (string.Equals(_entries[i].Conn.Id, connId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_entries[i].Label, label, StringComparison.OrdinalIgnoreCase))
            {
                _list.SelectedIndex = i;   // fires OnSelect -> refreshes the edit target
                return;
            }
    }

    private Label AddLabel(string text, int x, int y)
    {
        var l = new Label { Text = text, Location = new Point(x, y + 4), AutoSize = true, ForeColor = DarkTheme.TextSecondary };
        Controls.Add(l);
        return l;
    }

    private void Config(TextBox box, int x, int y, int w)
    {
        box.SetBounds(x, y, w, 28);
        box.BackColor = DarkTheme.Background;
        box.ForeColor = DarkTheme.TextPrimary;
        box.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(box);
    }

    private Button MakeButton(string text, int x, int y, int w)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = DarkTheme.SurfaceLighter,
            ForeColor = DarkTheme.TextPrimary,
        };
        b.FlatAppearance.BorderColor = DarkTheme.Border;
        Controls.Add(b);
        return b;
    }
}

/// <summary>Small modal that asks which credential label to use when a connection (or host) has several.</summary>
internal sealed class CredentialPicker : Form
{
    private readonly ListBox _list = new();

    private CredentialPicker(string host, IReadOnlyList<string> labels)
    {
        Text = "Choose Credential";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = DarkTheme.Surface;
        ForeColor = DarkTheme.TextPrimary;
        Font = DarkTheme.UIFont;
        ClientSize = new Size(320, 260);
        HandleCreated += (_, _) => MainForm.ApplyDarkTitleBar(this);

        Controls.Add(new Label
        {
            Text = $"Sign on to '{host}' as:",
            ForeColor = DarkTheme.TextSecondary,
            AutoSize = true,
            Location = new Point(16, 12),
        });

        _list.SetBounds(16, 40, 288, 160);
        _list.BackColor = DarkTheme.Background;
        _list.ForeColor = DarkTheme.TextPrimary;
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.IntegralHeight = false;
        foreach (var l in labels) _list.Items.Add(l);
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        _list.DoubleClick += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        Controls.Add(_list);

        var ok = MakeButton("Sign On", 132, 214, 84, DialogResult.OK);
        var cancel = MakeButton("Cancel", 224, 214, 80, DialogResult.Cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>Show the picker; returns the chosen label, or null if cancelled.</summary>
    public static string? Choose(IWin32Window owner, string host, IReadOnlyList<string> labels)
    {
        using var dlg = new CredentialPicker(host, labels);
        return dlg.ShowDialog(owner) == DialogResult.OK && dlg._list.SelectedItem is string s ? s : null;
    }

    private Button MakeButton(string text, int x, int y, int w, DialogResult result)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = DarkTheme.SurfaceLighter,
            ForeColor = DarkTheme.TextPrimary,
            DialogResult = result,
        };
        b.FlatAppearance.BorderColor = DarkTheme.Border;
        Controls.Add(b);
        return b;
    }
}
