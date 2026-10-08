namespace MySqlConnect;

// Periodically runs dbo.mysql_refresh_views while the app is running.
public static class BridgeWatcher
{
    static readonly Dictionary<string, System.Windows.Forms.Timer> timers = new();

    public static string Status { get; private set; } = "";

    public static void Start(string instance, string db, int seconds)
    {
        Stop(db);
        var t = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
        var busy = false;
        t.Tick += async (_, _) =>
        {
            if (busy) return;
            busy = true;
            try
            {
                var r = await BridgeSql.Refresh(instance, db);
                Status = $"{db} in sync {DateTime.Now:HH:mm:ss}" + (r.Created + r.Changed + r.Dropped > 0 ? $" (+{r.Created} ~{r.Changed} -{r.Dropped})" : "");
            }
            catch (Exception ex) { Status = $"{db} sync failed {DateTime.Now:HH:mm:ss}: {ex.Message.Split('\n')[0]}"; }
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
