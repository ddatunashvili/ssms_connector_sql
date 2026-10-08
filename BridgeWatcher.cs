namespace MySqlConnect;

// Periodically runs dbo.mysql_refresh_views while the app is running.
public static class BridgeWatcher
{
    static readonly Dictionary<string, System.Windows.Forms.Timer> timers = new();

    public static string Status { get; private set; } = "";

    public static void Start(string instance, string db, int minutes)
    {
        Stop(db);
        var t = new System.Windows.Forms.Timer { Interval = minutes * 60_000 };
        var busy = false;
        t.Tick += async (_, _) =>
        {
            if (busy) return;
            busy = true;
            try
            {
                var r = await BridgeSql.Refresh(instance, db);
                Status = $"{db} views synced {DateTime.Now:HH:mm} (+{r.Created} ~{r.Changed} -{r.Dropped})";
            }
            catch (Exception ex) { Status = $"{db} view sync failed {DateTime.Now:HH:mm}: {ex.Message.Split('\n')[0]}"; }
            finally { busy = false; }
        };
        t.Start();
        timers[db] = t;
    }

    public static void Stop(string db)
    {
        if (!timers.Remove(db, out var t)) return;
        t.Stop();
        t.Dispose();
    }
}
