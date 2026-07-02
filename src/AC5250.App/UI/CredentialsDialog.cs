using AC5250.Security;

namespace AC5250.UI;

/// <summary>
/// Manage IBM i sign-on credentials stored in the Windows Credential Manager, keyed by
/// host. Used by the MCP `signon` tool and the Session menu to fill the sign-on screen
/// without exposing the password. Passwords are never displayed back — to change one,
/// re-enter it.
/// </summary>
internal sealed class CredentialsDialog : Form
{
    private readonly ListBox _list = new();
    private readonly TextBox _hostBox = new();
    private readonly TextBox _userBox = new();
    private readonly TextBox _pwBox = new();
    private List<(string Host, string User)> _entries = new();

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
        ClientSize = new Size(460, 360);
        HandleCreated += (_, _) => MainForm.ApplyDarkTitleBar(this);

        Controls.Add(new Label
        {
            Text = "Sign-on credentials (stored in Windows Credential Manager)",
            ForeColor = DarkTheme.TextSecondary,
            AutoSize = true,
            Location = new Point(16, 12),
        });

        _list.SetBounds(16, 40, 428, 150);
        _list.BackColor = DarkTheme.Background;
        _list.ForeColor = DarkTheme.TextPrimary;
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.SelectedIndexChanged += OnSelect;
        Controls.Add(_list);

        AddLabel("Host", 16, 202);
        Config(_hostBox, 110, 198, 334);
        _hostBox.PlaceholderText = "hostname or IP (e.g. 10.1.1.1)";

        AddLabel("User", 16, 238);
        Config(_userBox, 110, 234, 334);

        AddLabel("Password", 16, 274);
        Config(_pwBox, 110, 270, 334);
        _pwBox.UseSystemPasswordChar = true;
        _pwBox.PlaceholderText = "leave blank to keep, or re-enter to change";

        var save = MakeButton("Save", 110, 310, 90);
        save.Click += OnSave;
        var del = MakeButton("Delete", 206, 310, 90);
        del.Click += OnDelete;
        var close = MakeButton("Close", 354, 310, 90);
        close.DialogResult = DialogResult.OK;

        AcceptButton = save;
        CancelButton = close;
        Refresh_();
    }

    private void Refresh_()
    {
        _entries = CredentialStore.List().OrderBy(e => e.Host).ToList();
        _list.Items.Clear();
        foreach (var e in _entries)
            _list.Items.Add(string.IsNullOrEmpty(e.User) ? e.Host : $"{e.Host}   ({e.User})");
    }

    private void OnSelect(object? sender, EventArgs e)
    {
        int i = _list.SelectedIndex;
        if (i < 0 || i >= _entries.Count) return;
        _hostBox.Text = _entries[i].Host;
        _userBox.Text = _entries[i].User;
        _pwBox.Text = ""; // never surface stored passwords
    }

    private void OnSave(object? sender, EventArgs e)
    {
        string host = _hostBox.Text.Trim();
        if (string.IsNullOrEmpty(host))
        {
            _hostBox.BackColor = Color.FromArgb(60, 30, 30);
            _hostBox.Focus();
            return;
        }
        // If editing an existing entry with the password left blank, keep the stored one.
        string password = _pwBox.Text;
        if (password.Length == 0)
        {
            var existing = CredentialStore.Get(host);
            if (existing is null)
            {
                _pwBox.BackColor = Color.FromArgb(60, 30, 30);
                _pwBox.Focus();
                return; // new entry needs a password
            }
            password = existing.Value.Password;
        }
        CredentialStore.Save(host, _userBox.Text.Trim(), password);
        _pwBox.Text = "";
        Refresh_();
    }

    private void OnDelete(object? sender, EventArgs e)
    {
        string host = _hostBox.Text.Trim();
        if (string.IsNullOrEmpty(host)) return;
        CredentialStore.Delete(host);
        _hostBox.Text = _userBox.Text = _pwBox.Text = "";
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
