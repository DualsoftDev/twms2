using System.Data;
using System.Data.SQLite;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DexaWeb.Server.Data;

/// <summary>
/// DEXA DB 연결 팩토리.
/// 읽기: Microsoft.Data.Sqlite (ReadOnly)
/// 쓰기: System.Data.SQLite (DEXA Server와 동일 라이브러리 → 잠금 프로토콜 공유)
/// </summary>
public class DexaDbConnection
{
    private readonly string _readOnlyConnStr;
    private readonly string _readWriteConnStr;

    /// <summary>DEXA DB 파일 경로 (드라이브 용량 조회 등에 활용)</summary>
    public string DbFilePath { get; }

    public DexaDbConnection(IConfiguration configuration, ILogger<DexaDbConnection> logger)
    {
        var raw = configuration["DexaDb:ConnectionString"]
            ?? "Data Source=C:\\ProgramData\\DEXA\\data\\DEXA.db;";

        // 기존 Mode= 옵션 제거
        var baseStr = Regex.Replace(raw.TrimEnd(';'), @";?\s*Mode=[^;]*", "", RegexOptions.IgnoreCase);

        // Microsoft.Data.Sqlite용 (읽기)
        _readOnlyConnStr = baseStr + ";Mode=ReadOnly;";

        // System.Data.SQLite용 (쓰기) — 커넥션 문자열 형식이 다름
        // "Data Source=path" 에서 path만 추출
        var dbPath = ExtractDataSource(baseStr);
        DbFilePath = dbPath;
        _readWriteConnStr = $"Data Source={dbPath};Version=3;Journal Mode=WAL;";

        logger.LogInformation("DEXA DB 읽기 (Microsoft.Data.Sqlite): {ConnStr}", _readOnlyConnStr);
        logger.LogInformation("DEXA DB 쓰기 (System.Data.SQLite): {ConnStr}", _readWriteConnStr);

        TestWriteConnection(logger);
    }

    /// <summary>
    /// 서버 시작 시 쓰기 연결 테스트 (System.Data.SQLite).
    /// </summary>
    private void TestWriteConnection(ILogger logger)
    {
        try
        {
            using var conn = new SQLiteConnection(_readWriteConnStr);
            conn.Open();
            using var cmd = conn.CreateCommand();

            cmd.CommandText = "PRAGMA journal_mode;";
            var journalMode = cmd.ExecuteScalar()?.ToString();
            logger.LogInformation("DEXA DB journal_mode: {Mode} (System.Data.SQLite)", journalMode);

            // 쓰기 테스트
            cmd.CommandText = "UPDATE asset SET parameter = parameter WHERE 0";
            cmd.ExecuteNonQuery();
            logger.LogInformation("DEXA DB 쓰기 테스트 성공 (System.Data.SQLite)");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DEXA DB 쓰기 연결 실패 (System.Data.SQLite)");
        }
    }

    /// <summary>"Data Source=C:\path\DEXA.db" 에서 경로만 추출</summary>
    private static string ExtractDataSource(string connStr)
    {
        var match = Regex.Match(connStr, @"Data Source\s*=\s*(.+?)(?:;|$)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : connStr;
    }

    /// <summary>읽기 전용 연결 — Microsoft.Data.Sqlite (조회용)</summary>
    public IDbConnection Create()
    {
        var connection = new SqliteConnection(_readOnlyConnStr);
        connection.Open();
        return connection;
    }

    /// <summary>읽기/쓰기 연결 — System.Data.SQLite (자산 수정용)</summary>
    public IDbConnection CreateReadWrite()
    {
        var connection = new SQLiteConnection(_readWriteConnStr);
        connection.Open();
        return connection;
    }
}
