// ============================================================
// IDbConnectionFactory - 数据库连接工厂接口
// 未来扩展：实现 NpgsqlConnectionFactory 即可切 PostgreSQL
// ============================================================

using System.Data.Common;

namespace MultiAgentSystem.Api.Data;

/// <summary>数据库连接工厂（策略模式）</summary>
public interface IDbConnectionFactory
{
    DbConnection CreateConnection();
    ISqlDialect Dialect { get; }
}

/// <summary>DbCommand 扩展：跨提供商的参数添加</summary>
public static class DbExtensions
{
    public static void AddParam(this DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    public static long LastInsertRowId(this DbCommand cmd)
    {
        cmd.CommandText += "; SELECT last_insert_rowid();";
        return (long)cmd.ExecuteScalar()!;
    }
}
