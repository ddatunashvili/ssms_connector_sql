using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MySqlConnector;

namespace MySqlConnect;

public class ConnInfo
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public uint Port { get; set; } = 3306;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string Database { get; set; } = "";
    public string SslMode { get; set; } = "Preferred";

    public string ToConnectionString()
    {
        var b = new MySqlConnectionStringBuilder
        {
            Server = Host,
            Port = Port,
            UserID = User,
            Password = Password,
            Database = Database,
            SslMode = Enum.TryParse<MySqlSslMode>(SslMode, true, out var m) ? m : MySqlSslMode.Preferred,
            ConnectionTimeout = 15,
            DefaultCommandTimeout = 0,
            AllowUserVariables = true,
            ConvertZeroDateTime = true,
            AllowPublicKeyRetrieval = true,
            CharacterSet = "utf8mb4",
        };
        return b.ConnectionString;
    }

    public override string ToString() => string.IsNullOrEmpty(Name) ? $"{User}@{Host}:{Port}" : Name;

    // Accepts JDBC URLs, mysql:// URLs, mysql CLI commands and ADO.NET connection strings.
    public static ConnInfo Parse(string input)
    {
        var s = input.Trim();
        if (s.Length == 0) throw new FormatException("Empty input.");

        if (s.StartsWith("jdbc:", StringComparison.OrdinalIgnoreCase)) s = s[5..];
        if (s.Contains("://")) return ParseUrl(s);
        if (s.StartsWith("mysql", StringComparison.OrdinalIgnoreCase) || s.StartsWith("-")) return ParseCli(s);
        if (s.Contains('=') && s.Contains(';')) return ParseAdo(s);
        throw new FormatException("Unrecognized format. Paste a JDBC URL, mysql command or connection string.");
    }

    static ConnInfo ParseUrl(string s)
    {
        var info = new ConnInfo();
        var rest = s[(s.IndexOf("://", StringComparison.Ordinal) + 3)..];

        string query = "";
        var q = rest.IndexOf('?');
        if (q >= 0) { query = rest[(q + 1)..]; rest = rest[..q]; }

        var at = rest.LastIndexOf('@');
        var hostPart = rest;
        if (at >= 0)
        {
            var userInfo = rest[..at];
            hostPart = rest[(at + 1)..];
            var colon = userInfo.IndexOf(':');
            if (colon >= 0)
            {
                info.User = Uri.UnescapeDataString(userInfo[..colon]);
                info.Password = Uri.UnescapeDataString(userInfo[(colon + 1)..]);
            }
            else info.User = Uri.UnescapeDataString(userInfo);
        }

        var slash = hostPart.IndexOf('/');
        if (slash >= 0)
        {
            info.Database = Uri.UnescapeDataString(hostPart[(slash + 1)..].TrimEnd('/'));
            hostPart = hostPart[..slash];
        }
        hostPart = hostPart.Split(',')[0]; // first host of a failover list
        var pc = hostPart.LastIndexOf(':');
        if (pc >= 0 && uint.TryParse(hostPart[(pc + 1)..], out var port))
        {
            info.Port = port;
            hostPart = hostPart[..pc];
        }
        info.Host = hostPart;

        foreach (var kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = kv.IndexOf('=');
            if (eq < 0) continue;
            var k = kv[..eq].ToLowerInvariant();
            var v = Uri.UnescapeDataString(kv[(eq + 1)..]);
            switch (k)
            {
                case "user": info.User = v; break;
                case "password": info.Password = v; break;
                case "usessl": info.SslMode = v.Equals("true", StringComparison.OrdinalIgnoreCase) ? "Required" : "None"; break;
                case "sslmode":
                    info.SslMode = v.ToUpperInvariant() switch
                    {
                        "DISABLED" => "None",
                        "REQUIRED" => "Required",
                        "VERIFY_CA" => "VerifyCA",
                        "VERIFY_IDENTITY" => "VerifyFull",
                        _ => "Preferred",
                    };
                    break;
            }
        }
        return info;
    }

    static ConnInfo ParseCli(string s)
    {
        var info = new ConnInfo();
        var t = Tokenize(s);
        int i = 0;
        if (i < t.Count && !t[i].StartsWith('-')) i++; // "mysql" executable name

        string Next() => ++i < t.Count ? t[i] : "";

        for (; i < t.Count; i++)
        {
            var a = t[i];
            if (a.StartsWith("--"))
            {
                var eq = a.IndexOf('=');
                var key = eq >= 0 ? a[2..eq] : a[2..];
                string Val() => eq >= 0 ? a[(eq + 1)..] : Next();
                switch (key)
                {
                    case "host": info.Host = Val(); break;
                    case "port": info.Port = uint.TryParse(Val(), out var p) ? p : 3306; break;
                    case "user": info.User = Val(); break;
                    case "password": if (eq >= 0) info.Password = a[(eq + 1)..]; break;
                    case "database": info.Database = Val(); break;
                    case "ssl-mode": info.SslMode = Val().ToUpperInvariant() == "DISABLED" ? "None" : "Required"; break;
                }
            }
            else if (a.StartsWith('-') && a.Length >= 2)
            {
                var flag = a[1];
                var attached = a.Length > 2 ? a[2..] : null;
                switch (flag)
                {
                    case 'h': info.Host = attached ?? Next(); break;
                    case 'P': info.Port = uint.TryParse(attached ?? Next(), out var p) ? p : 3306; break;
                    case 'u': info.User = attached ?? Next(); break;
                    case 'p': if (attached != null) info.Password = attached; break; // bare -p means "prompt"
                    case 'D': info.Database = attached ?? Next(); break;
                }
            }
            else info.Database = a;
        }
        return info;
    }

    static ConnInfo ParseAdo(string s)
    {
        var b = new MySqlConnectionStringBuilder(s);
        return new ConnInfo
        {
            Host = b.Server,
            Port = b.Port,
            User = b.UserID,
            Password = b.Password,
            Database = b.Database,
            SslMode = b.SslMode.ToString(),
        };
    }

    static List<string> Tokenize(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        bool has = false;
        foreach (var c in s)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0'; else sb.Append(c);
            }
            else if (c == '"' || c == '\'') { quote = c; has = true; }
            else if (char.IsWhiteSpace(c))
            {
                if (has || sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); has = false; }
            }
            else sb.Append(c);
        }
        if (has || sb.Length > 0) list.Add(sb.ToString());
        return list;
    }
}

// Saved connections live next to the exe (portable). Passwords are DPAPI-encrypted
// for the current Windows user, so they won't decrypt on another PC/account.
public static class ConnStore
{
    static string FilePath => Path.Combine(AppContext.BaseDirectory, "connections.json");

    class Stored
    {
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        public uint Port { get; set; }
        public string User { get; set; } = "";
        public string Pwd { get; set; } = "";
        public string Database { get; set; } = "";
        public string SslMode { get; set; } = "Preferred";
    }

    public static List<ConnInfo> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var items = JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(FilePath)) ?? new();
            return items.Select(x => new ConnInfo
            {
                Name = x.Name, Host = x.Host, Port = x.Port, User = x.User,
                Password = Unprotect(x.Pwd), Database = x.Database, SslMode = x.SslMode,
            }).ToList();
        }
        catch { return new(); }
    }

    public static void Save(List<ConnInfo> list)
    {
        var items = list.Select(x => new Stored
        {
            Name = x.Name, Host = x.Host, Port = x.Port, User = x.User,
            Pwd = Protect(x.Password), Database = x.Database, SslMode = x.SslMode,
        });
        File.WriteAllText(FilePath, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
    }

    static string Protect(string s) => s.Length == 0 ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser));

    static string Unprotect(string s)
    {
        if (s.Length == 0) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}
