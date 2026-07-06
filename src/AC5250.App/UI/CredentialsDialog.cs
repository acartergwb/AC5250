using AC5250.Security;

namespace AC5250.UI;

/// <summary>
/// Manage IBM i sign-on credentials stored in the Windows Credential Manager. A host can hold
/// several logins, each under a short label (e.g. "ADMIN", "TESTUSER"); one may be marked the
/// default. Used by the MCP `signon` tool and the Session menu to fill the sign-on screen
/// without exposing the password. Passwords are never displayed back — to change one, re-enter it.
/// </summary>
internal sealed class CredentialsDialog : Form
{
    private readonly ListBox _list = new();
    private readonly TextBox _hostBox = new();
    private readonly TextBox _labelBox = new();
    private readonly TextBox _userBox = new();
    private readonly TextBox _pwBox = new();
    private List<(string Host, string Label, string User)> _entries = new();

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
        ClientSize = new Size(480, 404);
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

        AddLabel("Host", 16, 200);
        Config(_hostBox, 110, 196, 354);
        _hostBox.PlaceholderText = "hostname or IP (e.g. 10.1.1.1)";

        AddLabel("Label", 16, 234);
        Config(_labelBox, 110, 230, 354);
        _labelBox.PlaceholderText = "e.g. ADMIN or TESTUSER (blank = default)";

        AddLabel("User", 16, 268);
        Config(_userBox, 110, 264, 354);

        AddLabel("Password", 16, 302);
        Config(_pwBox, 110, 298, 354);
        _pwBox.UseSystemPasswordChar = true;
        _pwBox.PlaceholderText = "leave blank to keep, or re-enter to change";

        var save = MakeButton("Save", 110, 338, 84);
        save.Click += OnSave;
        var del = MakeButton("Delete", 200, 338, 84);
        del.Click += OnDelete;
        var def = MakeButton("Set Default", 290, 338, 90);
        def.Click += OnSetDefault;
        var close = MakeButton("Close", 386, 338, 78);
        close.DialogResult = DialogResult.OK;

        AcceptButton = save;
        CancelButton = close;
        Refresh_();
    }

    private void Refresh_()
    {
        _entries = CredentialStore.List()
            .OrderBy(e => e.Host).ThenBy(e => e.Label)
            .ToList();

        // Precompute the default label and count per host for the "[default]" marker.
        var defaults = _entries.Select(e => e.Host).Distinct()
            .ToDictionary(h => h, h => CredentialStore.GetDefaultLabel(h), StringComparer.OrdinalIgnoreCase);
        var counts = _entries.GroupBy(e => e.Host, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        _list.Items.Clear();
        foreach (var e in _entries)
        {
            defaults.TryGetValue(e.Host, out string? defLabel);
            bool isDefault = defLabel != null
                ? string.Equals(defLabel, e.Label, StringComparison.OrdinalIgnoreCase)
                : counts[e.Host] == 1;
            string user = string.IsNullOrEmpty(e.User) ? "" : $"   ({e.User})";
            _list.Items.Add($"{e.Host}  ›  {e.Label}{user}{(isDefault ? "   [default]" : "")}");
        }
    }

    private void OnSelect(object? sender, EventArgs e)
    {
        int i = _list.SelectedIndex;
        if (i < 0 || i >= _entries.Count) return;
        _hostBox.Text = _entries[i].Host;
        _labelBox.Text = _entries[i].Label;
        _userBox.Text = _entries[i].User;
        _pwBox.Text = ""; // never surface stored passwords
    }

    private string LabelOrDefault() =>
        string.IsNullOrWhiteSpace(_labelBox.Text) ? CredentialStore.DefaultLabel : _labelBox.Text.Trim();

    private void OnSave(object? sender, EventArgs e)
    {
        string host = _hostBox.Text.Trim();
        if (string.IsNullOrEmpty(host))
        {
            _hostBox.BackColor = Color.FromArgb(60, 30, 30);
            _hostBox.Focus();
            return;
        }
        string label = LabelOrDefault();

        // Editing an existing entry with the password left blank keeps the stored one.
        string password = _pwBox.Text;
        if (password.Length == 0)
        {
            var existing = CredentialStore.Get(host, label);
            if (existing is null)
            {
                _pwBox.BackColor = Color.FromArgb(60, 30, 30);
                _pwBox.Focus();
                return; // a new entry needs a password
            }
            password = existing.Value.Password;
        }
        CredentialStore.Save(host, label, _userBox.Text.Trim(), password);
        _pwBox.Text = "";
        Refresh_();
    }

    private void OnDelete(object? sender, EventArgs e)
    {
        string host = _hostBox.Text.Trim();
        if (string.IsNullOrEmpty(host)) return;
        CredentialStore.Delete(host, LabelOrDefault());
        _hostBox.Text = _labelBox.Text = _userBox.Text = _pwBox.Text = "";
        Refresh_();
    }

    private void OnSetDefault(object? sender, EventArgs e)
    {
        string host = _hostBox.Text.Trim();
        if (string.IsNullOrEmpty(host)) return;
        CredentialStore.SetDefaultLabel(host, LabelOrDefault());
        Refresh_();
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

/// <summary>Small modal that asks which credential label to use when a host has several.</summary>
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
