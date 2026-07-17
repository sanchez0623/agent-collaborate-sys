// ============================================================
// NpgsqlConnectionFactory + SqlDialectProvider
// 配置驱动：appsettings.json → Database:Provider 选 sqlite/pgsql
// ============================================================

using System.Data.Common;
using Npgsql;

namespace MultiAgentSystem.Api.Data;

public class NpgsqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _connStr;
    public NpgsqlConnectionFactory(string connStr) => _connStr = connStr;
    public DbConnection CreateConnection() => new NpgsqlConnection(_connStr);
    public ISqlDialect Dialect { get; } = new PgSqlDialect();
}

/// <summary>SQL 方言差异封装</summary>
public interface ISqlDialect
{
    string GetLastInsertIdSql(string table, string pkColumn);
    string NowSql();
}

public class SqliteDialect : ISqlDialect
{
    public string GetLastInsertIdSql(string _, string __) => "; SELECT last_insert_rowid();";
    public string NowSql() => "datetime('now')";
}

public class PgSqlDialect : ISqlDialect
{
    public string GetLastInsertIdSql(string table, string pkColumn) => $" RETURNING {pkColumn}";
    public string NowSql() => "NOW()";
}

/// <summary>配置驱动的连接工厂选择器</summary>
public class DatabaseConfig
{
    public string Provider { get; set; } = "sqlite";
    public string ConnectionString { get; set; } = "";
}
