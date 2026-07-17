// ============================================================
// SqliteConnectionFactory - SQLite 连接工厂
// ============================================================

using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace MultiAgentSystem.Api.Data;

public class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _connStr;

    public SqliteConnectionFactory(string dbPath)
    {
        _connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ConnectionString;
    }

    public DbConnection CreateConnection() => new SqliteConnection(_connStr);
    public ISqlDialect Dialect { get; } = new SqliteDialect();
}
