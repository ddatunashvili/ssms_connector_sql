using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;

namespace MySqlConnect;

// Bridges SSMS to MySQL: registers the MySQL server as a linked server on a local
// SQL Server instance (LocalDB / Express) via the MariaDB ODBC driver, then creates
// one view per MySQL table so everything shows up in SSMS Object Explorer.
public class BridgeForm : Form
{
    const string OdbcMsiUrl = "https://dlm.mariadb.com/4864638/Connectors/odbc/connector-odbc-3.2.10/windows-amd64/mariadb-connector-odbc-3.2.10-win64.msi";
    const string OdbcMsiSha256 = "4a020e297ab7cfe5de1bca63ab6016f4c36e7f30e850bedd25320025355c74d5";
    const string LocalDb = @"(localdb)\MSSQLLocalDB";

    readonly ConnInfo info;
    readonly ComboBox cboInstance = new() { Dock = DockStyle.Fill };
    readonly TextBox txtLinked = new() { Dock = DockStyle.Fill };
    readonly TextBox txtLocalDb = new() { Dock = DockStyle.Fill };
    readonly CheckBox chkViews = new() { Text = "Create a view for every MySQL table (shows in SSMS Object Explorer)", AutoSize = true, Checked = true };
    readonly TextBox log = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9.5f), BackColor = SystemColors.Window };
    readonly Button btnRun = new() { Text = "Create bridge", Width = 120 };
    readonly Button btnRemove = new() { Text = "Remove bridge", Width = 120 };
    readonly Button btnCopy = new() { Text = "Copy server name", Width = 130 };
    readonly Button btnSsms = new() { Text = "Open SSMS", Width = 110 };
    readonly Button btnClose = new() { Text = "Close", Width = 90, DialogResult = DialogResult.Cancel };

    public BridgeForm(ConnInfo info)
    {
        this.info = info;
        Text = "Bridge to SSMS";
        Icon = Program.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(720, 560);
        MinimumSize = new Size(600, 450);
        CancelButton = btnClose;

        var intro = new Label
        {
            Dock = DockStyle.Top, Height = 58, Padding = new Padding(12, 10, 12, 0),
            Text = "SSMS only connects to Microsoft SQL Server. This sets up a local SQL Server (LocalDB) with a linked server " +
                   $"pointing at {info.User}@{info.Host}:{info.Port}. Then connect SSMS to the local server and your MySQL tables appear inside it.",
        };

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(12, 4, 12, 4), AutoSize = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string label, Control c)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) });
            grid.Controls.Add(c);
        }
        Row("Local SQL Server (SSMS):", cboInstance);
        Row("Linked server name:", txtLinked);
        Row("SSMS database name:", txtLocalDb);
        grid.Controls.Add(new Label());
        grid.Controls.Add(chkViews);

        var logPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 4) };
        logPanel.Controls.Add(log);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 46, Padding = new Padding(8) };
        buttons.Controls.AddRange(new Control[] { btnClose, btnSsms, btnCopy, btnRemove, btnRun });

        Controls.Add(logPanel);
        Controls.Add(grid);
        Controls.Add(intro);
        Controls.Add(buttons);

        foreach (var i in FindInstances()) cboInstance.Items.Add(i);
        cboInstance.Text = cboInstance.Items.Count > 0 ? cboInstance.Items[0]!.ToString() : LocalDb;
        var baseName = Sanitize(info.Database.Length > 0 ? info.Database : info.Host);
        txtLinked.Text = ("MYSQL_" + baseName).ToUpperInvariant();
        txtLocalDb.Text = baseName;

        btnRun.Click += async (_, _) => await Run(create: true);
        btnRemove.Click += async (_, _) => await Run(create: false);
        btnCopy.Click += (_, _) => { Clipboard.SetText(cboInstance.Text); Log($"Copied \"{cboInstance.Text}\" to clipboard."); };
        btnSsms.Click += (_, _) => LaunchSsms();

        Log("Press \"Create bridge\".");
    }

    static string Sanitize(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        return new string(chars);
    }

    void Log(string msg)
    {
        log.AppendText(msg + Environment.NewLine);
    }

    static IEnumerable<string> FindInstances()
    {
        var list = new List<string>();
        if (FindSqlLocalDb() != null) list.Add(LocalDb);
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
            foreach (var name in k?.GetValueNames() ?? Array.Empty<string>())
                list.Add(name == "MSSQLSERVER" ? "localhost" : @".\" + name);
        }
        catch { }
        return list;
    }

    static string? FindSqlLocalDb()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SQL Server");
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root)
            .Select(d => Path.Combine(d, "Tools", "Binn", "SqlLocalDB.exe"))
            .Where(File.Exists)
            .OrderByDescending(p => p)
            .FirstOrDefault();
    }

    static string? FindOdbcDriver()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers");
        var names = k?.GetValueNames() ?? Array.Empty<string>();
        return names.Where(n => n.StartsWith("MariaDB ODBC", StringComparison.OrdinalIgnoreCase)).OrderByDescending(n => n).FirstOrDefault()
            ?? names.Where(n => n.StartsWith("MySQL ODBC", StringComparison.OrdinalIgnoreCase) && n.Contains("Unicode")).OrderByDescending(n => n).FirstOrDefault();
    }

    async Task<string?> EnsureOdbcDriver()
    {
        var driver = FindOdbcDriver();
        if (driver != null) { Log($"✔ ODBC driver found: {driver}"); return driver; }

        Log("MySQL/MariaDB ODBC driver not installed. Downloading MariaDB Connector/ODBC 3.2.10…");
        var msi = Path.Combine(Path.GetTempPath(), "mariadb-connector-odbc-3.2.10-win64.msi");
        using (var http = new HttpClient())
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 MySqlConnect");
            var bytes = await http.GetByteArrayAsync(OdbcMsiUrl);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (hash != OdbcMsiSha256) throw new Exception("Downloaded driver failed checksum verification.");
            await File.WriteAllBytesAsync(msi, bytes);
        }
        Log("✔ Downloaded and verified. Installing (accept the Windows admin prompt)…");

        var p = Process.Start(new ProcessStartInfo("msiexec", $"/i \"{msi}\" /qb /norestart") { UseShellExecute = true, Verb = "runas" })!;
        await p.WaitForExitAsync();
        if (p.ExitCode != 0 && p.ExitCode != 3010) throw new Exception($"Driver installer exited with code {p.ExitCode}.");

        driver = FindOdbcDriver() ?? throw new Exception("Driver installed but not registered.");
        Log($"✔ ODBC driver installed: {driver}");
        return driver;
    }

    async Task EnsureLocalDbRunning()
    {
        if (!cboInstance.Text.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase)) return;
        var exe = FindSqlLocalDb() ?? throw new Exception(
            "SQL Server LocalDB is not installed. Install SQL Server Express (choose \"LocalDB\" or \"Basic\") from " +
            "https://www.microsoft.com/sql-server/sql-server-downloads and run this again.");
        var name = cboInstance.Text.Split('\\').Last();
        var p = Process.Start(new ProcessStartInfo(exe, $"create \"{name}\" -s") { CreateNoWindow = true, UseShellExecute = false })!;
        await p.WaitForExitAsync();
        var s = Process.Start(new ProcessStartInfo(exe, $"start \"{name}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        await s.WaitForExitAsync();
        Log($"✔ LocalDB instance \"{name}\" running.");
    }

    static string N(string s) => "N'" + s.Replace("'", "''") + "'";
    static string B(string s) => "[" + s.Replace("]", "]]") + "]";

    async Task Exec(SqlConnection cn, string sql, bool ignoreErrors = false)
    {
        try
        {
            await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync();
        }
        catch when (ignoreErrors) { }
    }

    async Task Run(bool create)
    {
        var linked = txtLinked.Text.Trim();
        var localDb = txtLocalDb.Text.Trim();
        if (linked.Length == 0 || localDb.Length == 0) { Log("Names cannot be empty."); return; }

        btnRun.Enabled = btnRemove.Enabled = false;
        UseWaitCursor = true;
        try
        {
            string? driver = create ? await EnsureOdbcDriver() : null;
            await EnsureLocalDbRunning();

            var cs = new SqlConnectionStringBuilder
            {
                DataSource = cboInstance.Text.Trim(),
                InitialCatalog = "master",
                IntegratedSecurity = true,
                TrustServerCertificate = true,
                Encrypt = false,
                ConnectTimeout = 60,
            };
            await using var cn = new SqlConnection(cs.ConnectionString);
            await cn.OpenAsync();
            Log($"✔ Connected to local SQL Server {cboInstance.Text} ({cn.ServerVersion}).");

            await Exec(cn, $"IF EXISTS (SELECT 1 FROM sys.servers WHERE name = {N(linked)}) EXEC sp_dropserver {N(linked)}, 'droplogins';");
            if (!create)
            {
                Log($"✔ Linked server {linked} removed. (Database {localDb} left in place — delete it in SSMS if you want.)");
                return;
            }

            var provStr = $"DRIVER={{{driver}}};SERVER={info.Host};PORT={info.Port};DATABASE={info.Database};OPTION=3;" +
                          (info.SslMode == "None" ? "" : "SSLMODE=PREFERRED;");
            await Exec(cn, "EXEC master.dbo.sp_MSset_oledb_prop N'MSDASQL', N'AllowInProcess', 1;", ignoreErrors: true);
            await Exec(cn, $"EXEC sp_addlinkedserver @server = {N(linked)}, @srvproduct = N'MySQL', @provider = N'MSDASQL', @provstr = {N(provStr)};");
            await Exec(cn, $"EXEC sp_addlinkedsrvlogin @rmtsrvname = {N(linked)}, @useself = N'False', @locallogin = NULL, @rmtuser = {N(info.User)}, @rmtpassword = {N(info.Password)};");
            foreach (var opt in new[] { "rpc", "rpc out", "data access" })
                await Exec(cn, $"EXEC sp_serveroption {N(linked)}, {N(opt)}, N'true';");
            Log($"✔ Linked server {linked} created.");

            await using (var cmd = new SqlCommand($"SELECT * FROM OPENQUERY({B(linked)}, 'SELECT VERSION()')", cn) { CommandTimeout = 60 })
                Log($"✔ MySQL reachable through bridge — server version {await cmd.ExecuteScalarAsync()}.");

            if (chkViews.Checked) await CreateViews(cn, linked, localDb);

            Log("");
            Log("━━━━━━━━ DONE — now in SSMS ━━━━━━━━");
            Log($"  Server name:     {cboInstance.Text}");
            Log("  Authentication:  Windows Authentication");
            Log("  (Encryption: Optional / Trust server certificate)");
            if (chkViews.Checked) Log($"  Then: Databases → {localDb} → Views  (right-click → Select Top 1000 Rows)");
            Log($"  Also: Server Objects → Linked Servers → {linked}");
            Log("");
            Log("  Query examples in SSMS:");
            Log($"    SELECT * FROM {B(localDb)}.dbo.[your_table];");
            Log($"    SELECT * FROM OPENQUERY({B(linked)}, 'SELECT * FROM your_table LIMIT 10');");
            Log($"    EXEC ('UPDATE your_table SET col = 1 WHERE id = 5') AT {B(linked)};");
            Log("  Added/removed tables in MySQL? Press \"Create bridge\" again to refresh the views.");
        }
        catch (Exception ex)
        {
            Log("✖ " + ex.Message);
            if (ex.Message.Contains("Authentication failed") || ex.Message.Contains("Access denied"))
                Log("  MySQL rejected the username/password. Check them in your hosting panel (and any IP allow-list for remote access).");
        }
        finally
        {
            btnRun.Enabled = btnRemove.Enabled = true;
            UseWaitCursor = false;
        }
    }

    async Task CreateViews(SqlConnection cn, string linked, string localDb)
    {
        await Exec(cn, $"IF DB_ID({N(localDb)}) IS NULL CREATE DATABASE {B(localDb)};");

        var tables = new List<string>();
        var mysqlSql = "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME";
        await using (var cmd = new SqlCommand($"SELECT * FROM OPENQUERY({B(linked)}, {N(mysqlSql)})", cn) { CommandTimeout = 120 })
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) tables.Add(r.GetString(0));

        cn.ChangeDatabase(localDb);
        int ok = 0;
        foreach (var t in tables)
        {
            var inner = "SELECT * FROM `" + t.Replace("`", "``") + "`";
            var sql = $"CREATE OR ALTER VIEW dbo.{B(t)} AS SELECT * FROM OPENQUERY({B(linked)}, {N(inner)});";
            try { await Exec(cn, sql); ok++; }
            catch (Exception ex) { Log($"  ! view {t}: {ex.Message.Split('\n')[0]}"); }
        }
        cn.ChangeDatabase("master");
        Log($"✔ Database {localDb}: {ok}/{tables.Count} table views created.");
    }

    static string? FindSsms()
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\ssms.exe");
                if (k?.GetValue(null) is string p && File.Exists(p)) return p;
            }
            catch { }
        }
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (!Directory.Exists(pf)) continue;
            foreach (var d in Directory.GetDirectories(pf, "Microsoft SQL Server Management Studio*"))
            {
                var hit = Directory.EnumerateFiles(d, "Ssms.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (hit != null) return hit;
            }
        }
        return null;
    }

    void LaunchSsms()
    {
        var ssms = FindSsms();
        if (ssms == null) { Log("SSMS not found on this PC. Install it from https://aka.ms/ssms"); return; }
        Process.Start(new ProcessStartInfo(ssms, $"-S \"{cboInstance.Text}\" -E") { UseShellExecute = false });
        Log($"Launched SSMS → {cboInstance.Text}");
    }
}
