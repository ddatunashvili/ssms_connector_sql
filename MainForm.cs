using System.Data;
using System.Diagnostics;
using MySqlConnector;

namespace MySqlConnect;

// SSMS-style layout: Object Explorer on the left, query editor top-right, results bottom-right.
public class MainForm : Form
{
    readonly TreeView tree = new() { Dock = DockStyle.Fill, HideSelection = false };
    readonly TextBox editor = new()
    {
        Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = false,
        AcceptsTab = true, Font = new Font("Consolas", 11f), MaxLength = 0,
    };
    readonly TabControl results = new() { Dock = DockStyle.Fill };
    readonly ToolStripComboBox cboDb = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    readonly ToolStripButton btnExec = new("▶ Execute (F5)");
    readonly ToolStripButton btnCancel = new("■ Cancel") { Enabled = false };
    readonly ToolStripStatusLabel stServer = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel stInfo = new();
    readonly ContextMenuStrip tableMenu = new();

    ConnInfo? info;
    MySqlConnection? queryConn;
    CancellationTokenSource? cts;

    public MainForm()
    {
        Text = "MySQL Connect — portable";
        Icon = Program.AppIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        Size = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        var btnConnect = new ToolStripButton("Connect…");
        var btnDisconnect = new ToolStripButton("Disconnect");
        var btnNew = new ToolStripButton("New Query");
        tools.Items.AddRange(new ToolStripItem[]
        {
            btnConnect, btnDisconnect, new ToolStripSeparator(), btnNew, new ToolStripSeparator(),
            new ToolStripLabel("Database:"), cboDb, btnExec, btnCancel,
        });

        var status = new StatusStrip();
        status.Items.AddRange(new ToolStripItem[] { stServer, stInfo });

        var objHeader = new Label { Text = "Object Explorer", Dock = DockStyle.Top, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0), BackColor = SystemColors.ControlLight };
        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(tree);
        left.Controls.Add(objHeader);

        var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        right.Panel1.Controls.Add(editor);
        right.Panel2.Controls.Add(results);

        var main = new SplitContainer { Dock = DockStyle.Fill };
        main.Panel1.Controls.Add(left);
        main.Panel2.Controls.Add(right);

        Controls.Add(main);
        Controls.Add(tools);
        Controls.Add(status);

        Load += (_, _) =>
        {
            main.SplitterDistance = 300;
            right.SplitterDistance = (int)(right.Height * 0.45);
            ShowConnect();
        };

        tableMenu.Items.Add("Select Top 1000 Rows", null, (_, _) => SelectTop());
        tableMenu.Items.Add("Script CREATE TABLE", null, async (_, _) => await ScriptCreate());
        tableMenu.Items.Add("Refresh", null, async (_, _) => await RefreshNode(tree.SelectedNode));

        btnConnect.Click += (_, _) => ShowConnect();
        btnDisconnect.Click += async (_, _) => await Disconnect();
        btnNew.Click += (_, _) => { editor.Clear(); editor.Focus(); };
        btnExec.Click += async (_, _) => await Execute();
        btnCancel.Click += (_, _) => cts?.Cancel();
        cboDb.SelectedIndexChanged += async (_, _) => await ChangeDb();
        tree.BeforeExpand += async (_, e) => await Expand(e.Node!);
        tree.NodeMouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            tree.SelectedNode = e.Node;
            if (e.Node.Tag is TableTag) tableMenu.Show(tree, e.Location);
        };
        tree.NodeMouseDoubleClick += (_, e) => { if (e.Node.Tag is TableTag) SelectTop(); };
        KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.F5 || (e.Control && e.KeyCode == Keys.E)) { e.SuppressKeyPress = true; await Execute(); }
            else if (e.Control && e.KeyCode == Keys.A && ActiveControl == editor) { e.SuppressKeyPress = true; editor.SelectAll(); }
        };
        FormClosing += (_, _) => { cts?.Cancel(); queryConn?.Dispose(); };

        SetConnected(false);
    }

    record DbTag(string Db);
    record FolderTag(string Db, string Kind);
    record TableTag(string Db, string Table);
    record ColumnTag;

    string ConnStr(string? db = null)
    {
        var b = new MySqlConnectionStringBuilder(info!.ToConnectionString());
        if (db != null) b.Database = db;
        return b.ConnectionString;
    }

    void SetConnected(bool on)
    {
        btnExec.Enabled = on;
        cboDb.Enabled = on;
        stServer.Text = on ? $"Connected: {info!.User}@{info.Host}:{info.Port}   |   MySQL {queryConn?.ServerVersion}" : "Not connected";
    }

    async void ShowConnect()
    {
        using var dlg = new ConnectForm();
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;
        await Disconnect();
        info = dlg.Result;
        try
        {
            queryConn = new MySqlConnection(ConnStr());
            await queryConn.OpenAsync();
            SetConnected(true);
            await LoadTree();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Connection failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            await Disconnect();
        }
    }

    async Task Disconnect()
    {
        cts?.Cancel();
        if (queryConn != null) { await queryConn.DisposeAsync(); queryConn = null; }
        tree.Nodes.Clear();
        cboDb.Items.Clear();
        SetConnected(false);
    }

    async Task<List<string>> List(string sql, string? db = null, params (string, object)[] args)
    {
        var list = new List<string>();
        await using var conn = new MySqlConnection(ConnStr(db));
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(Convert.ToString(r.GetValue(0)) ?? "");
        return list;
    }

    static TreeNode Lazy(string text, object tag)
    {
        var n = new TreeNode(text) { Tag = tag };
        n.Nodes.Add(new TreeNode("loading…"));
        return n;
    }

    async Task LoadTree()
    {
        tree.Nodes.Clear();
        var root = new TreeNode($"{info!.Host} (MySQL {queryConn!.ServerVersion} - {info.User})");
        var dbs = new TreeNode("Databases");
        root.Nodes.Add(dbs);
        tree.Nodes.Add(root);

        var names = await List("SHOW DATABASES");
        cboDb.Items.Clear();
        foreach (var d in names)
        {
            dbs.Nodes.Add(Lazy(d, new DbTag(d)));
            cboDb.Items.Add(d);
        }
        root.Expand();
        dbs.Expand();

        var current = string.IsNullOrEmpty(info.Database) ? null : info.Database;
        if (current != null && cboDb.Items.Contains(current))
        {
            cboDb.SelectedItem = current;
            var node = dbs.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Text == current);
            node?.Expand();
        }
    }

    async Task Expand(TreeNode node)
    {
        if (node.Nodes.Count != 1 || node.Nodes[0].Tag != null || node.Nodes[0].Text != "loading…") return;
        await Populate(node);
    }

    async Task RefreshNode(TreeNode? node)
    {
        if (node == null) return;
        await Populate(node);
        node.Expand();
    }

    async Task Populate(TreeNode node)
    {
        try
        {
            var children = new List<TreeNode>();
            switch (node.Tag)
            {
                case DbTag d:
                    children.Add(Lazy("Tables", new FolderTag(d.Db, "BASE TABLE")));
                    children.Add(Lazy("Views", new FolderTag(d.Db, "VIEW")));
                    break;
                case FolderTag f:
                    foreach (var t in await List(
                        "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=@db AND TABLE_TYPE=@k ORDER BY TABLE_NAME",
                        null, ("@db", f.Db), ("@k", f.Kind)))
                        children.Add(Lazy(t, new TableTag(f.Db, t)));
                    break;
                case TableTag t:
                    foreach (var c in await List(
                        "SELECT CONCAT(COLUMN_NAME, ' (', COLUMN_TYPE, IF(COLUMN_KEY='PRI', ', PK', ''), IF(IS_NULLABLE='YES', ', null', ', not null'), ')') " +
                        "FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@db AND TABLE_NAME=@t ORDER BY ORDINAL_POSITION",
                        null, ("@db", t.Db), ("@t", t.Table)))
                        children.Add(new TreeNode(c) { Tag = new ColumnTag() });
                    break;
                default: return;
            }
            node.Nodes.Clear();
            node.Nodes.AddRange(children.ToArray());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static string Q(string id) => "`" + id.Replace("`", "``") + "`";

    async void SelectTop()
    {
        if (tree.SelectedNode?.Tag is not TableTag t) return;
        editor.Text = $"SELECT *\r\nFROM {Q(t.Db)}.{Q(t.Table)}\r\nLIMIT 1000;";
        await Execute();
    }

    async Task ScriptCreate()
    {
        if (tree.SelectedNode?.Tag is not TableTag t) return;
        try
        {
            await using var conn = new MySqlConnection(ConnStr());
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand($"SHOW CREATE TABLE {Q(t.Db)}.{Q(t.Table)}", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync()) editor.Text = r.GetString(1).Replace("\n", "\r\n") + ";";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    async Task ChangeDb()
    {
        if (queryConn == null || cboDb.SelectedItem is not string db) return;
        try { await queryConn.ChangeDatabaseAsync(db); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    async Task Execute()
    {
        if (queryConn == null || cts != null) return;
        var sql = editor.SelectionLength > 0 ? editor.SelectedText : editor.Text;
        if (string.IsNullOrWhiteSpace(sql)) return;

        results.TabPages.Clear();
        var messages = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 10f), BackColor = SystemColors.Window };
        var msgPage = new TabPage("Messages");
        msgPage.Controls.Add(messages);

        cts = new CancellationTokenSource();
        btnExec.Enabled = false;
        btnCancel.Enabled = true;
        stInfo.Text = "Executing…";
        var sw = Stopwatch.StartNew();
        int set = 0;
        long totalRows = 0;

        try
        {
            if (queryConn.State != ConnectionState.Open) await queryConn.OpenAsync(cts.Token);
            await using var cmd = new MySqlCommand(sql, queryConn);
            await using var r = await cmd.ExecuteReaderAsync(cts.Token);
            do
            {
                if (r.FieldCount == 0) continue;
                var dt = new DataTable();
                for (int i = 0; i < r.FieldCount; i++)
                {
                    var type = r.GetFieldType(i);
                    var name = r.GetName(i);
                    while (dt.Columns.Contains(name)) name += "_";
                    dt.Columns.Add(name, type == typeof(byte[]) ? typeof(string) : type);
                }
                var vals = new object[r.FieldCount];
                while (await r.ReadAsync(cts.Token))
                {
                    for (int i = 0; i < r.FieldCount; i++)
                    {
                        object v;
                        try { v = r.GetValue(i); } catch { v = DBNull.Value; }
                        vals[i] = v is byte[] b ? "0x" + Convert.ToHexString(b.Length > 512 ? b[..512] : b) : v;
                    }
                    dt.Rows.Add(vals);
                }
                set++;
                totalRows += dt.Rows.Count;
                var grid = new DataGridView
                {
                    Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                    DataSource = dt, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
                    ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithAutoHeaderText,
                    RowHeadersWidth = 50, BackgroundColor = SystemColors.Window,
                };
                grid.RowPostPaint += (s, e) =>
                {
                    var g = (DataGridView)s!;
                    TextRenderer.DrawText(e.Graphics, (e.RowIndex + 1).ToString(), g.Font,
                        new Rectangle(e.RowBounds.Left, e.RowBounds.Top, g.RowHeadersWidth - 4, e.RowBounds.Height),
                        SystemColors.GrayText, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                };
                var page = new TabPage(set == 1 ? "Results" : $"Results {set}");
                page.Controls.Add(grid);
                results.TabPages.Add(page);
                messages.AppendText($"({dt.Rows.Count} row(s) returned)\r\n");
            } while (await r.NextResultAsync(cts.Token));

            if (r.RecordsAffected >= 0) messages.AppendText($"({r.RecordsAffected} row(s) affected)\r\n");
            messages.AppendText($"\r\nCompleted in {sw.Elapsed.TotalSeconds:0.000}s");
            stInfo.Text = $"Query executed successfully.  {totalRows} rows  |  {sw.Elapsed:hh\\:mm\\:ss\\.fff}";
        }
        catch (Exception ex)
        {
            messages.ForeColor = Color.Firebrick;
            messages.AppendText(ex is OperationCanceledException ? "Query cancelled." : ex.Message);
            stInfo.Text = "Query completed with errors.";
            if (queryConn.State != ConnectionState.Open)
            {
                try { await queryConn.OpenAsync(); } catch { }
            }
        }
        finally
        {
            results.TabPages.Add(msgPage);
            if (set == 0) results.SelectedTab = msgPage;
            cts.Dispose();
            cts = null;
            btnExec.Enabled = true;
            btnCancel.Enabled = false;
        }
    }
}
