using Microsoft.Data.SqlClient;

namespace MySqlConnect;

// T-SQL objects installed into the SSMS-side database that keep the views in sync with MySQL.
public static class BridgeSql
{
    public const string SigProperty = "MySqlBridgeSig";
    public const string PendingProperty = "MySqlBridgePending";
    public const string InternalFlag = "mysql_bridge_internal";

    // Builds a MySQL CREATE TABLE statement from a local SQL Server table definition.
    public static string DdlFunction() => @"
CREATE OR ALTER FUNCTION dbo.mysql_create_table_ddl (@id int)
RETURNS nvarchar(max)
AS
BEGIN
    DECLARE @cols nvarchar(max), @pk nvarchar(max), @idcol nvarchar(400), @idInPk bit = 0;

    SELECT @cols = STRING_AGG(CONVERT(nvarchar(max),
        N'`' + REPLACE(c.name, N'`', N'``') + N'` ' +
        CASE t.name
            WHEN N'int' THEN N'INT'
            WHEN N'bigint' THEN N'BIGINT'
            WHEN N'smallint' THEN N'SMALLINT'
            WHEN N'tinyint' THEN N'TINYINT UNSIGNED'
            WHEN N'bit' THEN N'TINYINT(1)'
            WHEN N'decimal' THEN CONCAT(N'DECIMAL(', c.precision, N',', c.scale, N')')
            WHEN N'numeric' THEN CONCAT(N'DECIMAL(', c.precision, N',', c.scale, N')')
            WHEN N'money' THEN N'DECIMAL(19,4)'
            WHEN N'smallmoney' THEN N'DECIMAL(10,4)'
            WHEN N'float' THEN N'DOUBLE'
            WHEN N'real' THEN N'FLOAT'
            WHEN N'date' THEN N'DATE'
            WHEN N'time' THEN CONCAT(N'TIME(', IIF(c.scale > 6, 6, c.scale), N')')
            WHEN N'datetime2' THEN CONCAT(N'DATETIME(', IIF(c.scale > 6, 6, c.scale), N')')
            WHEN N'datetimeoffset' THEN CONCAT(N'DATETIME(', IIF(c.scale > 6, 6, c.scale), N')')
            WHEN N'datetime' THEN N'DATETIME(3)'
            WHEN N'smalldatetime' THEN N'DATETIME'
            WHEN N'char' THEN CONCAT(N'CHAR(', c.max_length, N')')
            WHEN N'nchar' THEN CONCAT(N'CHAR(', c.max_length / 2, N')')
            WHEN N'varchar' THEN IIF(c.max_length = -1, N'LONGTEXT', CONCAT(N'VARCHAR(', c.max_length, N')'))
            WHEN N'nvarchar' THEN IIF(c.max_length = -1, N'LONGTEXT', CONCAT(N'VARCHAR(', c.max_length / 2, N')'))
            WHEN N'binary' THEN CONCAT(N'BINARY(', c.max_length, N')')
            WHEN N'varbinary' THEN IIF(c.max_length = -1, N'LONGBLOB', CONCAT(N'VARBINARY(', c.max_length, N')'))
            WHEN N'image' THEN N'LONGBLOB'
            WHEN N'uniqueidentifier' THEN N'CHAR(36)'
            ELSE N'LONGTEXT'
        END +
        IIF(c.is_nullable = 1, N' NULL', N' NOT NULL') +
        IIF(c.is_identity = 1, N' AUTO_INCREMENT', N'')), N', ') WITHIN GROUP (ORDER BY c.column_id)
    FROM sys.columns c
    JOIN sys.types t ON t.user_type_id = c.system_type_id
    WHERE c.object_id = @id;

    SELECT @pk = STRING_AGG(CONVERT(nvarchar(max), N'`' + REPLACE(c.name, N'`', N'``') + N'`'), N', ') WITHIN GROUP (ORDER BY ic.key_ordinal),
           @idInPk = MAX(CONVERT(int, c.is_identity))
    FROM sys.indexes i
    JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE i.object_id = @id AND i.is_primary_key = 1;

    -- MySQL requires an AUTO_INCREMENT column to be indexed.
    SELECT @idcol = N'`' + REPLACE(name, N'`', N'``') + N'`' FROM sys.columns WHERE object_id = @id AND is_identity = 1;

    RETURN N'CREATE TABLE `' + REPLACE(OBJECT_NAME(@id), N'`', N'``') + N'` (' + @cols
         + ISNULL(N', PRIMARY KEY (' + @pk + N')', N'')
         + IIF(@idcol IS NOT NULL AND ISNULL(@idInPk, 0) = 0, N', KEY (' + @idcol + N')', N'')
         + N') DEFAULT CHARSET=utf8mb4';
END";

    // INSERTs through OPENQUERY views fail in the ODBC driver when columns are omitted
    // (identity/defaults), so views get an INSTEAD OF INSERT trigger that copies the rows
    // into #mysql_ins and calls this, which sends plain MySQL INSERTs (omitted/NULL columns skipped).
    public static string InsertProc(string linked) => $@"
CREATE OR ALTER PROCEDURE dbo.mysql_insert_rows @table sysname
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @cols nvarchar(max), @vals nvarchar(max), @sql nvarchar(max), @stmt nvarchar(max);

    SELECT
        @cols = STRING_AGG(CONVERT(nvarchar(max),
            N'IIF(' + QUOTENAME(c.name) + N' IS NULL, N'''', N'',`' + REPLACE(REPLACE(c.name, N'`', N'``'), N'''', N'''''') + N'`'')'), N' + '),
        @vals = STRING_AGG(CONVERT(nvarchar(max),
            N'IIF(' + QUOTENAME(c.name) + N' IS NULL, N'''', N'','' + ' +
            CASE
                WHEN t.name IN (N'tinyint', N'smallint', N'int', N'bigint', N'decimal', N'numeric', N'money', N'smallmoney', N'float', N'real', N'bit')
                    THEN N'CONVERT(nvarchar(100), ' + QUOTENAME(c.name) + N')'
                WHEN t.name IN (N'date', N'time', N'datetime', N'datetime2', N'smalldatetime')
                    THEN N'N'''''''' + CONVERT(nvarchar(50), ' + QUOTENAME(c.name) + N', 121) + N'''''''''
                WHEN t.name = N'datetimeoffset'
                    THEN N'N'''''''' + CONVERT(nvarchar(50), CONVERT(datetime2, ' + QUOTENAME(c.name) + N'), 121) + N'''''''''
                WHEN t.name IN (N'binary', N'varbinary', N'image', N'timestamp')
                    THEN N'CONVERT(nvarchar(max), CONVERT(varbinary(max), ' + QUOTENAME(c.name) + N'), 1)'
                ELSE N'N'''''''' + REPLACE(REPLACE(CONVERT(nvarchar(max), ' + QUOTENAME(c.name) + N'), N''\'', N''\\''), N'''''''', N'''''''''''') + N'''''''''
            END + N')'), N' + ')
    FROM tempdb.sys.columns c
    JOIN sys.types t ON t.user_type_id = c.system_type_id
    WHERE c.object_id = OBJECT_ID(N'tempdb..#mysql_ins');

    CREATE TABLE #mysql_stmts (stmt nvarchar(max));
    SET @sql = N'INSERT #mysql_stmts SELECT N''INSERT INTO `' + REPLACE(REPLACE(@table, N'`', N'``'), N'''', N'''''')
             + N'` ('' + ISNULL(STUFF(' + @cols + N', 1, 1, N''''), N'''') + N'') VALUES ('' + ISNULL(STUFF(' + @vals + N', 1, 1, N''''), N'''') + N'')'' FROM #mysql_ins';
    EXEC (@sql);

    DECLARE s CURSOR LOCAL FAST_FORWARD FOR SELECT stmt FROM #mysql_stmts;
    OPEN s;
    FETCH NEXT FROM s INTO @stmt;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC (@stmt) AT [{linked}];
        FETCH NEXT FROM s INTO @stmt;
    END
    CLOSE s;
    DEALLOCATE s;
END";

    // Mirrors SSMS schema changes onto MySQL as they happen:
    //   CREATE TABLE        -> CREATE TABLE on MySQL (local table becomes a live view at next sync)
    //   ALTER / rename column on a not-yet-synced table -> MySQL table re-created (still empty)
    //   DROP VIEW / TABLE   -> DROP TABLE on MySQL
    //   rename view / table -> RENAME TABLE on MySQL
    // Drops/renames done by the sync itself are skipped via the session flag.
    public static string DdlTrigger(string linked) => $@"
CREATE OR ALTER TRIGGER mysql_bridge_ddl ON DATABASE
FOR CREATE_TABLE, ALTER_TABLE, DROP_TABLE, DROP_VIEW, RENAME
AS
BEGIN
    SET NOCOUNT ON;
    IF SESSION_CONTEXT(N'{InternalFlag}') = 1 RETURN;

    DECLARE @e xml = EVENTDATA();
    DECLARE @type sysname = @e.value('(/EVENT_INSTANCE/EventType)[1]', 'sysname'),
            @schema sysname = @e.value('(/EVENT_INSTANCE/SchemaName)[1]', 'sysname'),
            @name sysname = @e.value('(/EVENT_INSTANCE/ObjectName)[1]', 'sysname'),
            @objType sysname = @e.value('(/EVENT_INSTANCE/ObjectType)[1]', 'sysname'),
            @newName sysname = @e.value('(/EVENT_INSTANCE/NewObjectName)[1]', 'sysname'),
            @target sysname = @e.value('(/EVENT_INSTANCE/TargetObjectName)[1]', 'sysname');
    IF @schema <> N'dbo' RETURN;

    DECLARE @sql nvarchar(max), @id int, @pending bit;

    IF @type IN (N'DROP_TABLE', N'DROP_VIEW')
    BEGIN
        SET @sql = N'DROP TABLE IF EXISTS `' + REPLACE(@name, N'`', N'``') + N'`';
        EXEC (@sql) AT [{linked}];
        RETURN;
    END

    IF @type = N'RENAME' AND @objType IN (N'TABLE', N'VIEW')
    BEGIN
        SET @sql = N'RENAME TABLE `' + REPLACE(@name, N'`', N'``') + N'` TO `' + REPLACE(@newName, N'`', N'``') + N'`';
        EXEC (@sql) AT [{linked}];
        IF @objType = N'VIEW'
        BEGIN
            -- The renamed view still points at the old MySQL name: drop its insert trigger,
            -- clear its signature and let the sync rebuild it under the new name.
            SET @sql = N'DROP TRIGGER IF EXISTS dbo.' + QUOTENAME(@name + N'__mysql_insert');
            EXEC (@sql);
            EXEC sys.sp_updateextendedproperty @name = N'{SigProperty}', @value = N'renamed',
                 @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'VIEW', @level1name = @newName;
            EXEC dbo.mysql_refresh_views @quiet = 1;
        END
        RETURN;
    END

    IF @type = N'RENAME' AND @objType = N'COLUMN' SET @name = @target;
    ELSE IF @type = N'RENAME' RETURN;

    SET @id = OBJECT_ID(QUOTENAME(@schema) + N'.' + QUOTENAME(@name), N'U');
    IF @id IS NULL RETURN;
    SET @pending = IIF(EXISTS (SELECT 1 FROM sys.extended_properties
        WHERE class = 1 AND major_id = @id AND minor_id = 0 AND name = N'{PendingProperty}'), 1, 0);
    IF @type <> N'CREATE_TABLE' AND @pending = 0 RETURN;

    IF @type <> N'CREATE_TABLE'
    BEGIN
        SET @sql = N'DROP TABLE IF EXISTS `' + REPLACE(@name, N'`', N'``') + N'`';
        EXEC (@sql) AT [{linked}];
    END

    SET @sql = dbo.mysql_create_table_ddl(@id);
    EXEC (@sql) AT [{linked}];

    IF @pending = 0
        EXEC sys.sp_addextendedproperty @name = N'{PendingProperty}', @value = 1,
             @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = @name;
END";

    // One remote round trip per refresh: a column signature per MySQL table/view.
    // Only views whose signature changed are re-created; views for dropped tables are removed.
    public static string RefreshProc(string linked) => $@"
CREATE OR ALTER PROCEDURE dbo.mysql_refresh_views @quiet bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    -- Our own view/table drops must not be mirrored back to MySQL by mysql_bridge_ddl.
    DECLARE @outerFlag sql_variant = SESSION_CONTEXT(N'{InternalFlag}');
    EXEC sys.sp_set_session_context N'{InternalFlag}', 1;

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

    -- Tables created in SSMS were already created on MySQL by the DDL trigger.
    -- Move any rows typed in meanwhile to MySQL, then swap the local table for a live view.
    DECLARE pending CURSOR LOCAL FAST_FORWARD FOR
        SELECT t.name FROM sys.tables t
        JOIN sys.extended_properties ep
          ON ep.class = 1 AND ep.major_id = t.object_id AND ep.minor_id = 0 AND ep.name = N'{PendingProperty}'
        WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name IN (SELECT name FROM @remote);
    OPEN pending;
    FETCH NEXT FROM pending INTO @n;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        BEGIN TRY
            -- No local transaction: a remote INSERT inside one would need MSDTC.
            -- If the copy fails, the DROP is skipped and nothing is lost.
            SET @sql = N'IF EXISTS (SELECT 1 FROM dbo.' + QUOTENAME(@n) + N') INSERT INTO OPENQUERY([{linked}], '''
                     + REPLACE(N'SELECT * FROM `' + REPLACE(@n, N'`', N'``') + N'`', N'''', N'''''')
                     + N''') SELECT * FROM dbo.' + QUOTENAME(@n) + N';';
            EXEC (@sql);
            SET @sql = N'DROP TABLE dbo.' + QUOTENAME(@n);
            EXEC (@sql);
        END TRY
        BEGIN CATCH
            PRINT N'Table ' + @n + N' not converted to view yet: ' + ERROR_MESSAGE();
        END CATCH
        FETCH NEXT FROM pending INTO @n;
    END
    CLOSE pending;
    DEALLOCATE pending;

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
        WHERE l.name IS NULL OR l.sig <> r.sig
           OR OBJECT_ID(N'dbo.' + QUOTENAME(r.name), N'V') IS NULL
           OR OBJECT_ID(N'dbo.' + QUOTENAME(r.name + N'__mysql_insert'), N'TR') IS NULL;
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

            SET @sql = N'CREATE OR ALTER TRIGGER dbo.' + QUOTENAME(@n + N'__mysql_insert') + N' ON dbo.' + QUOTENAME(@n)
                     + N' INSTEAD OF INSERT AS BEGIN SET NOCOUNT ON; SELECT * INTO #mysql_ins FROM inserted; EXEC dbo.mysql_insert_rows '
                     + QUOTENAME(@n, N'''') + N'; END';
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

    EXEC sys.sp_set_session_context N'{InternalFlag}', @outerFlag;
    IF @quiet = 0
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
        EXEC dbo.mysql_refresh_views @quiet = 1;
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
