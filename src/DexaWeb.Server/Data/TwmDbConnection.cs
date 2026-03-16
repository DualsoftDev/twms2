using System.Data;
using Microsoft.Data.Sqlite;

namespace DexaWeb.Server.Data;

/// <summary>
/// TWM 자체 DB 읽기/쓰기 연결 팩토리
/// </summary>
public class TwmDbConnection
{
    private readonly string _connectionString;

    public TwmDbConnection(IConfiguration configuration)
    {
        _connectionString = configuration["TwmDb:ConnectionString"]
            ?? "Data Source=twm.db.sqlite3;";
    }

    public IDbConnection Create()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
