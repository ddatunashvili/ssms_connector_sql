<p align="center"><img src="icon.png" width="128" alt="icon"></p>

# SSMS Connector SQL (MySQL Connect)

Portable Windows app with an SSMS-style interface for **MySQL / MariaDB** servers.

SSMS (SQL Server Management Studio) only speaks Microsoft SQL Server's TDS protocol, so it cannot open MySQL databases such as shared-hosting `jdbc:mysql://...` connections. This tool fills that gap: paste the hosting panel's connection info and work with the database in a familiar layout.

## Download

Grab `MySqlConnect.exe` from [Releases](../../releases). Single file, no install, no .NET runtime required (Windows 10/11 x64).

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
