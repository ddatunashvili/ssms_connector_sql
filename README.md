<p align="center"><img src="icon.png" width="128" alt="icon"></p>

# SSMS Connector SQL (MySQL Connect)

Portable Windows app with an SSMS-style interface for **MySQL / MariaDB** servers.

SSMS (SQL Server Management Studio) only speaks Microsoft SQL Server's TDS protocol, so it cannot open MySQL databases such as shared-hosting `jdbc:mysql://...` connections. This tool fills that gap: paste the hosting panel's connection info and work with the database in a familiar layout.

## Download

Grab `MySqlConnect.exe` from [Releases](../../releases). Single file, no install, no .NET runtime required (Windows 10/11 x64).

## Bridge to SSMS

Click **Bridge to SSMS…** (connect dialog or toolbar). The app:

1. Installs the MariaDB ODBC driver if missing (downloaded from mariadb.com, SHA-256 verified, admin prompt).
2. Starts SQL Server LocalDB `(localdb)\MSSQLLocalDB` (or uses a local SQL Server / Express instance).
3. Creates a linked server (`MYSQL_<db>`) pointing at your MySQL host.
4. Creates a local database with one view per MySQL table.

Then in SSMS connect to `(localdb)\MSSQLLocalDB` with **Windows Authentication** → *Databases → &lt;db&gt; → Views* → right-click → *Select Top 1000 Rows*.

```sql
SELECT * FROM mydb.dbo.my_table;                          -- via views
SELECT * FROM OPENQUERY(MYSQL_MYDB, 'SELECT * FROM my_table LIMIT 10');
EXEC ('UPDATE my_table SET x = 1 WHERE id = 5') AT MYSQL_MYDB;   -- writes
```

Requires SQL Server LocalDB or Express ([download](https://www.microsoft.com/sql-server/sql-server-downloads)) and SSMS.

## Features

- **Paste & go** – paste any of these and all fields fill in automatically:
  - JDBC URL: `jdbc:mysql://user:pass@host:3306/db`
  - mysql CLI: `mysql -h host -P 3306 -u user -p db`
  - ADO.NET: `Server=host;Port=3306;User ID=user;Password=...;Database=db`
- URL-encoded passwords decoded (`%2B` → `+`, `%3D` → `=`)
- Saved connections in `connections.json` next to the exe; passwords encrypted with Windows DPAPI (current user)
- Object Explorer: databases → tables / views → columns (type, PK, nullability)
- Query editor: **F5** / Ctrl+E executes all or selected text, cancel running queries, database picker
- Multiple result sets, row numbers, copy with headers, Messages tab
- Right-click table: *Select Top 1000 Rows*, *Script CREATE TABLE*

## Build

Requires .NET 8 SDK.

```
dotnet publish -c Release -o publish
```

Output: `publish/MySqlConnect.exe`.
