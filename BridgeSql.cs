using Microsoft.Data.SqlClient;

namespace MySqlConnect;

// T-SQL objects installed into the SSMS-side database that keep the views in sync with MySQL.
public static class BridgeSql
{
    public const string SigProperty = "MySqlBridgeSig";

    // One remote round trip per refresh: a column signature per MySQL table/view.
    // Only views whose signature changed are re-created; views for dropped tables are removed.
    public static string RefreshProc(string linked) => $@"
CREATE OR ALTER PROCEDURE dbo.mysql_refresh_views
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @remote TABLE (name sysname, sig nvarchar(100));
    INSERT @remote (name, sig)
    SELECT TABLE_NAME, sig FROM OPENQUERY([{linked}],
        'SELECT TABLE_NAME, CONCAT(COUNT(*), ''-'', SUM(CRC32(CONCAT_WS('':'', ORDINAL_POSITION, COLUMN_NAME, COLUMN_TYPE)))) AS sig
         FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() GROUP BY TABLE_NAME');

    DECLARE @local TABLE (name sysname, sig nvarchar(100));
    INSERT @local (name, sig)
    SELECT v.name, CONVERT(nvarchar(100), ep.value)
    FROM sys.views v
    JOIN sys.extended_properties ep
      ON ep.class = 1 AND ep.major_id = v.object_id AND ep.minor_id = 0 AND ep.name = N'{SigProperty}'
    WHERE v.schema_id = SCHEMA_ID(N'dbo');

    DECLARE @n sysname, @s nvarchar(100), @sql nvarchar(max), @inner nvarchar(max),
            @created int = 0, @changed int = 0, @dropped int = 0;

    DECLARE gone CURSOR LOCAL FAST_FORWARD FOR
        SELECT l.name FROM @local l WHERE NOT EXISTS (SELECT 1 FROM @remote r WHERE r.name = l.name);
    OPEN gone;
    FETCH NEXT FROM gone INTO @n;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'DROP VIEW dbo.' + QUOTENAME(@n);
        EXEC (@sql);
        SET @dropped += 1;
        FETCH NEXT FROM gone INTO @n;
    END
    CLOSE gone;
    DEALLOCATE gone;

    DECLARE todo CURSOR LOCAL FAST_FORWARD FOR
        SELECT r.name, r.sig
        FROM @remote r
        LEFT JOIN @local l ON l.name = r.name
        WHERE l.name IS NULL OR l.sig <> r.sig OR OBJECT_ID(N'dbo.' + QUOTENAME(r.name), N'V') IS NULL;
    OPEN todo;
    FETCH NEXT FROM todo INTO @n, @s;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        BEGIN TRY
            -- The signature comment changes the query text on every schema change; metadata
            -- is cached per query text, so without it the view keeps stale columns.
            SET @inner = N'SELECT * FROM `' + REPLACE(@n, N'`', N'``') + N'` /* ' + @s + N' */';
            SET @sql = N'CREATE OR ALTER VIEW dbo.' + QUOTENAME(@n)
                     + N' AS SELECT * FROM OPENQUERY([{linked}], ''' + REPLACE(@inner, N'''', N'''''') + N''')';
            EXEC (@sql);

            IF EXISTS (SELECT 1 FROM sys.extended_properties
                       WHERE class = 1 AND major_id = OBJECT_ID(N'dbo.' + QUOTENAME(@n)) AND minor_id = 0 AND name = N'{SigProperty}')
                EXEC sys.sp_updateextendedproperty @name = N'{SigProperty}', @value = @s,
                     @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'VIEW', @level1name = @n;
            ELSE
                EXEC sys.sp_addextendedproperty @name = N'{SigProperty}', @value = @s,
                     @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'VIEW', @level1name = @n;

            IF EXISTS (SELECT 1 FROM @local WHERE name = @n) SET @changed += 1; ELSE SET @created += 1;
        END TRY
        BEGIN CATCH
            PRINT N'View ' + @n + N' failed: ' + ERROR_MESSAGE();
        END CATCH
        FETCH NEXT FROM todo INTO @n, @s;
    END
    CLOSE todo;
    DEALLOCATE todo;

    SELECT @created AS created, @changed AS changed, @dropped AS dropped, (SELECT COUNT(*) FROM @remote) AS total;
END";

    // Runs any MySQL statement on the server, then syncs the views immediately.
    public static string ExecProc(string linked) => $@"
CREATE OR ALTER PROCEDURE dbo.mysql_exec @sql nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    EXEC (@sql) AT [{linked}];
    IF @sql LIKE N'%TABLE%' OR @sql LIKE N'%VIEW%'
        EXEC dbo.mysql_refresh_views;
END";

    public static string ConnectionString(string instance, string db) => new SqlConnectionStringBuilder
    {
        DataSource = instance,
        InitialCatalog = db,
        IntegratedSecurity = true,
        TrustServerCertificate = true,
        Encrypt = false,
        ConnectTimeout = 60,
    }.ConnectionString;

    public record Result(int Created, int Changed, int Dropped, int Total, List<string> Messages);

    public static async Task<Result> Refresh(string instance, string db)
    {
        var messages = new List<string>();
        await using var cn = new SqlConnection(ConnectionString(instance, db));
        cn.InfoMessage += (_, e) => messages.Add(e.Message);
        await cn.OpenAsync();
        await using var cmd = new SqlCommand("EXEC dbo.mysql_refresh_views", cn) { CommandTimeout = 600 };
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return new Result(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), messages);
    }
}
