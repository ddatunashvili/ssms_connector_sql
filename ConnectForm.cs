using MySqlConnector;

namespace MySqlConnect;

public class ConnectForm : Form
{
    readonly ComboBox cboSaved = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly TextBox txtPaste = new() { Multiline = true, Height = 60, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, PlaceholderText = "Paste JDBC URL, mysql command or connection string here — fields fill automatically" };
    readonly Label lblParse = new() { AutoSize = true, ForeColor = Color.DimGray };
    readonly TextBox txtName = new() { Dock = DockStyle.Fill };
    readonly TextBox txtHost = new() { Dock = DockStyle.Fill };
    readonly NumericUpDown numPort = new() { Minimum = 1, Maximum = 65535, Value = 3306, Width = 90 };
    readonly TextBox txtUser = new() { Dock = DockStyle.Fill };
    readonly TextBox txtPass = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    readonly CheckBox chkShow = new() { Text = "Show", AutoSize = true };
    readonly TextBox txtDb = new() { Dock = DockStyle.Fill };
    readonly ComboBox cboSsl = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    readonly CheckBox chkRemember = new() { Text = "Remember this connection (password encrypted for this Windows user)", AutoSize = true, Checked = true };
    readonly Button btnTest = new() { Text = "Test", Width = 90 };
    readonly Button btnConnect = new() { Text = "Connect", Width = 90 };
    readonly Button btnCancel = new() { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
    readonly Button btnDelete = new() { Text = "Delete", Width = 70 };

    List<ConnInfo> saved = ConnStore.Load();
    public ConnInfo? Result { get; private set; }

    public ConnectForm()
    {
        Text = "Connect to MySQL / MariaDB Server";
        Icon = Program.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(560, 470);
        AcceptButton = btnConnect;
        CancelButton = btnCancel;

        cboSsl.Items.AddRange(new object[] { "Preferred", "Required", "None", "VerifyCA", "VerifyFull" });
        cboSsl.SelectedIndex = 0;

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12), AutoSize = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var savedRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Height = 30, Margin = Padding.Empty };
        savedRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        savedRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        savedRow.Controls.Add(cboSaved, 0, 0);
        savedRow.Controls.Add(btnDelete, 1, 0);

        var passRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Height = 30, Margin = Padding.Empty, WrapContents = false };
        txtPass.Width = 330;
        passRow.Controls.Add(txtPass);
        passRow.Controls.Add(chkShow);

        void Row(string label, Control c)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) });
            grid.Controls.Add(c);
        }

        Row("Saved:", savedRow);
        Row("Paste string:", txtPaste);
        grid.Controls.Add(new Label());
        grid.Controls.Add(lblParse);
        Row("Name:", txtName);
        Row("Server / Host:", txtHost);
        Row("Port:", numPort);
        Row("Login / User:", txtUser);
        Row("Password:", passRow);
        Row("Database:", txtDb);
        Row("SSL mode:", cboSsl);
        grid.Controls.Add(new Label());
        grid.Controls.Add(chkRemember);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
        buttons.Controls.AddRange(new Control[] { btnCancel, btnConnect, btnTest });

        Controls.Add(grid);
        Controls.Add(buttons);

        cboSaved.Items.Add("(new connection)");
        foreach (var c in saved) cboSaved.Items.Add(c);
        cboSaved.SelectedIndex = saved.Count > 0 ? 1 : 0;

        cboSaved.SelectedIndexChanged += (_, _) => { if (cboSaved.SelectedItem is ConnInfo c) Fill(c); };
        if (cboSaved.SelectedItem is ConnInfo first) Fill(first);

        txtPaste.TextChanged += (_, _) => TryParsePaste();
        chkShow.CheckedChanged += (_, _) => txtPass.UseSystemPasswordChar = !chkShow.Checked;
        btnTest.Click += async (_, _) => await Test();
        btnConnect.Click += async (_, _) => await ConnectClicked();
        btnDelete.Click += (_, _) => DeleteSaved();
    }

    void TryParsePaste()
    {
        if (txtPaste.Text.Trim().Length == 0) { lblParse.Text = ""; return; }
        try
        {
            var c = ConnInfo.Parse(txtPaste.Text);
            Fill(c);
            if (txtName.Text.Length == 0) txtName.Text = c.Database.Length > 0 ? c.Database : c.Host;
            lblParse.ForeColor = Color.SeaGreen;
            lblParse.Text = c.Password.Length > 0
                ? "✔ Parsed. Password decoded (%2B → +, %3D → =, etc.)."
                : "✔ Parsed. No password in string — type it in the Password box.";
            if (c.Password.Length == 0) txtPass.Focus();
        }
        catch (Exception ex)
        {
            lblParse.ForeColor = Color.Firebrick;
            lblParse.Text = ex.Message;
        }
    }

    void Fill(ConnInfo c)
    {
        txtName.Text = c.Name;
        txtHost.Text = c.Host;
        numPort.Value = Math.Clamp(c.Port, 1u, 65535u);
        txtUser.Text = c.User;
        if (c.Password.Length > 0 || cboSaved.SelectedItem is ConnInfo) txtPass.Text = c.Password;
        txtDb.Text = c.Database;
        var idx = cboSsl.Items.IndexOf(c.SslMode);
        cboSsl.SelectedIndex = idx >= 0 ? idx : 0;
    }

    ConnInfo Current() => new()
    {
        Name = txtName.Text.Trim(),
        Host = txtHost.Text.Trim(),
        Port = (uint)numPort.Value,
        User = txtUser.Text.Trim(),
        Password = txtPass.Text,
        Database = txtDb.Text.Trim(),
        SslMode = cboSsl.SelectedItem?.ToString() ?? "Preferred",
    };

    async Task<string?> TryOpen(ConnInfo c)
    {
        UseWaitCursor = true;
        btnTest.Enabled = btnConnect.Enabled = false;
        try
        {
            await using var conn = new MySqlConnection(c.ToConnectionString());
            await conn.OpenAsync();
            return conn.ServerVersion;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Connection failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
        finally
        {
            UseWaitCursor = false;
            btnTest.Enabled = btnConnect.Enabled = true;
        }
    }

    async Task Test()
    {
        var c = Current();
        if (!Validate(c)) return;
        var v = await TryOpen(c);
        if (v != null) MessageBox.Show(this, $"Connected successfully.\nServer version: {v}", "Test", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    async Task ConnectClicked()
    {
        var c = Current();
        if (!Validate(c)) return;
        if (await TryOpen(c) == null) return;

        if (chkRemember.Checked)
        {
            if (c.Name.Length == 0) c.Name = c.ToString();
            saved.RemoveAll(x => x.Name == c.Name);
            saved.Insert(0, c);
            try { ConnStore.Save(saved); } catch { /* read-only location; ignore */ }
        }
        Result = c;
        DialogResult = DialogResult.OK;
        Close();
    }

    bool Validate(ConnInfo c)
    {
        if (c.Host.Length > 0 && c.User.Length > 0) return true;
        MessageBox.Show(this, "Host and User are required.", "Missing info", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    void DeleteSaved()
    {
        if (cboSaved.SelectedItem is not ConnInfo c) return;
        saved.Remove(c);
        cboSaved.Items.Remove(c);
        cboSaved.SelectedIndex = 0;
        try { ConnStore.Save(saved); } catch { }
    }
}
