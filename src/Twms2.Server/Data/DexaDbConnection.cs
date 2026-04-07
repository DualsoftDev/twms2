using System.Data;
using System.Data.SQLite;
using System.Text.RegularExpressions;
using System.Xml;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Twms2.Server.Data;

public enum DexaDbProvider { Sqlite, SqlServer }

/// <summary>
/// DEXA DB 연결 팩토리.
/// SQLite: 읽기/쓰기 모두 System.Data.SQLite (DEXA Server와 동일 라이브러리 → 잠금 프로토콜 공유)
/// MSSQL:  읽기/쓰기 모두 Microsoft.Data.SqlClient
/// </summary>
public class DexaDbConnection
{
    private const string DefaultServerConfigPath =
        @"C:\Program Files (x86)\LS\DEXA\Server\DEXA.ServerService.exe.config";

    private readonly string _readOnlyConnStr;
    private readonly string _readWriteConnStr;

    /// <summary>현재 사용 중인 DB 프로바이더</summary>
    public DexaDbProvider Provider { get; }

    /// <summary>DEXA DB 파일 경로 (SQLite일 때만 유효, MSSQL이면 null)</summary>
    public string? DbFilePath { get; }

    /// <summary>MSSQL 스키마 이름 (MSSQL일 때만 유효)</summary>
    public string? Schema { get; }

    public DexaDbConnection(IConfiguration configuration, ILogger<DexaDbConnection> logger)
    {
        var configPath = configuration["DexaServer:ConfigPath"] ?? DefaultServerConfigPath;
        var (connStr, providerName) = TryReadDexaServerConfig(configPath, logger);

        // Provider 결정: config에서 감지 → appsettings → 기본값
        Provider = ResolveProvider(providerName, configuration);

        // ConnectionString 결정: config에서 감지 → appsettings → 기본값
        var raw = connStr
            ?? configuration["DexaDb:ConnectionString"]
            ?? (Provider == DexaDbProvider.SqlServer
                ? "Server=localhost;Database=lpb;Trusted_Connection=True;"
                : @"Data Source=C:\ProgramData\LS\DEXA\Storage\DEXA.sqlite3;");

        if (Provider == DexaDbProvider.SqlServer)
        {
            // MSSQL: 단일 라이브러리 (Microsoft.Data.SqlClient)
            // TrustServerCertificate 미지정 시 자동 추가 (로컬 자체 서명 인증서 대응)
            if (!raw.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase))
                raw += "TrustServerCertificate=True;";

            _readOnlyConnStr = raw;
            _readWriteConnStr = raw;
            Schema = ExtractValue(raw, "Database") ?? ExtractValue(raw, "Initial Catalog") ?? "lpb";

            logger.LogInformation("DEXA DB (SqlServer) 연결: {Schema}", Schema);
            TestSqlServerConnection(logger);
        }
        else
        {
            // SQLite: 읽기/쓰기 모두 System.Data.SQLite
            var dbPath = ExtractValue(raw, "Data Source");
            DbFilePath = dbPath;
            _readOnlyConnStr = $"Data Source={dbPath};Version=3;Journal Mode=WAL;Read Only=True;";
            _readWriteConnStr = $"Data Source={dbPath};Version=3;Journal Mode=WAL;";

            logger.LogInformation("DEXA DB (SQLite) 읽기: {ConnStr}", _readOnlyConnStr);
            logger.LogInformation("DEXA DB (SQLite) 쓰기: {ConnStr}", _readWriteConnStr);
            TestSqliteWrite(logger);
        }

        ValidateSchema(logger);
    }

    private void TestSqliteWrite(ILogger logger)
    {
        try
        {
            using var conn = new SQLiteConnection(_readWriteConnStr);
            conn.Open();
            using var cmd = conn.CreateCommand();

            cmd.CommandText = "PRAGMA journal_mode;";
            var journalMode = cmd.ExecuteScalar()?.ToString();

            if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL;";
                var result = cmd.ExecuteScalar()?.ToString();

                if (string.Equals(result, "wal", StringComparison.OrdinalIgnoreCase))
                    logger.LogInformation("DEXA DB journal_mode 변경: {From} → WAL", journalMode);
                else
                    logger.LogWarning("DEXA DB journal_mode WAL 전환 실패 (현재: {Mode}) — DEXA Server가 DB를 사용 중이면 서버 재시작 후 다시 시도하세요", result);
            }
            else
            {
                logger.LogInformation("DEXA DB journal_mode: WAL");
            }

            cmd.CommandText = "UPDATE asset SET parameter = parameter WHERE 0";
            cmd.ExecuteNonQuery();
            logger.LogInformation("DEXA DB SQLite 쓰기 테스트 성공");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DEXA DB SQLite 쓰기 연결 실패");
        }
    }

    private void TestSqlServerConnection(ILogger logger)
    {
        try
        {
            using var conn = new SqlConnection(_readWriteConnStr);
            conn.Open();
            conn.Execute($"USE {Schema}; SET LANGUAGE us_english; SET DATEFORMAT mdy;");

            var result = conn.QuerySingleOrDefault<int>("SELECT 1");
            logger.LogInformation("DEXA DB SqlServer 연결 테스트 성공");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DEXA DB SqlServer 연결 실패 — MSSQL 서버가 실행 중인지 확인하세요");
        }
    }

    /// <summary>읽기 전용 연결</summary>
    public IDbConnection Create()
    {
        if (Provider == DexaDbProvider.SqlServer)
        {
            var conn = new SqlConnection(_readOnlyConnStr);
            conn.Open();
            conn.Execute($"USE {Schema}; SET LANGUAGE us_english; SET DATEFORMAT mdy;");
            return conn;
        }
        else
        {
            var conn = new SQLiteConnection(_readOnlyConnStr);
            conn.Open();
            return conn;
        }
    }

    /// <summary>읽기/쓰기 연결</summary>
    public IDbConnection CreateReadWrite()
    {
        if (Provider == DexaDbProvider.SqlServer)
        {
            var conn = new SqlConnection(_readWriteConnStr);
            conn.Open();
            conn.Execute($"USE {Schema}; SET LANGUAGE us_english; SET DATEFORMAT mdy;");
            return conn;
        }
        else
        {
            var conn = new SQLiteConnection(_readWriteConnStr);
            conn.Open();
            return conn;
        }
    }

    // ── 스키마 검증 ──────────────────────────────────────────

    public IReadOnlyList<string> SchemaWarnings => _schemaWarnings;
    private readonly List<string> _schemaWarnings = [];

    /// <summary>DEXA DB 스키마가 TWMS가 기대하는 구조와 일치하는지 검증</summary>
    public void ValidateSchema(ILogger logger)
    {
        var expectedSchema = new Dictionary<string, string[]>
        {
            ["asset"] = ["id", "parentId", "assetTypeId", "agentPreferences", "parameter", "deleted"],
            ["assetType"] = ["id", "userFriendlyName", "fake"],
            ["trigger"] = ["id", "name", "cronSpec", "enable", "deleted", "description"],
            ["schedule"] = ["id", "assetId", "triggerId", "deleted"],
            ["user"] = ["id", "userName", "isAdmin"],
            ["permission"] = ["id", "uid", "assetId"],
            ["action.base"] = ["id", "started", "finished", "memo"],
            ["action.schedule"] = ["actionId", "assetId", "version", "contentsChanged", "nthSucceeded"],
            ["actionLog"] = ["id", "actionId", "level", "message", "dateTime"],
            ["agent"] = ["id", "name", "ip", "swVersion", "online", "connected", "disconnected"],
        };

        try
        {
            using var conn = Create();

            if (Provider == DexaDbProvider.SqlServer)
                ValidateSchemaMssql(conn, expectedSchema, logger);
            else
                ValidateSchemaSqlite(conn, expectedSchema, logger);

            if (_schemaWarnings.Count == 0)
                logger.LogInformation("DEXA DB 스키마 검증 통과 — 모든 테이블/컬럼 정상");
            else
                logger.LogWarning("DEXA DB 스키마 검증 완료 — 경고 {Count}건", _schemaWarnings.Count);
        }
        catch (Exception ex)
        {
            _schemaWarnings.Add($"DEXA DB 스키마 검증 실패: {ex.Message}");
            logger.LogWarning(ex, "DEXA DB 스키마 검증 중 오류");
        }
    }

    private void ValidateSchemaSqlite(IDbConnection conn, Dictionary<string, string[]> expected, ILogger logger)
    {
        var tables = conn.Query<string>("SELECT name FROM sqlite_master WHERE type='table'")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (table, expectedCols) in expected)
        {
            if (!tables.Contains(table))
            {
                var msg = $"DEXA 스키마 경고: 테이블 '{table}' 없음";
                _schemaWarnings.Add(msg);
                logger.LogWarning(msg);
                continue;
            }

            var cols = conn.Query("PRAGMA table_info([" + table + "])")
                .Select(r => (string)r.name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var col in expectedCols.Where(c => !cols.Contains(c)))
            {
                var msg = $"DEXA 스키마 경고: '{table}'.'{col}' 없음";
                _schemaWarnings.Add(msg);
                logger.LogWarning(msg);
            }
        }
    }

    private void ValidateSchemaMssql(IDbConnection conn, Dictionary<string, string[]> expected, ILogger logger)
    {
        var tables = conn.Query<string>(
            "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (table, expectedCols) in expected)
        {
            if (!tables.Contains(table))
            {
                var msg = $"DEXA 스키마 경고: 테이블 '{table}' 없음";
                _schemaWarnings.Add(msg);
                logger.LogWarning(msg);
                continue;
            }

            var cols = conn.Query<string>(
                "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @Table",
                new { Table = table })
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var col in expectedCols.Where(c => !cols.Contains(c)))
            {
                var msg = $"DEXA 스키마 경고: '{table}'.'{col}' 없음";
                _schemaWarnings.Add(msg);
                logger.LogWarning(msg);
            }
        }
    }

    // ── Private helpers ──────────────────────────────────────

    /// <summary>DEXA ServerService.exe.config에서 (connectionString, providerName) 읽기</summary>
    private static (string? connStr, string? providerName) TryReadDexaServerConfig(
        string configPath, ILogger logger)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                logger.LogInformation("DEXA Server config 없음: {Path}", configPath);
                return (null, null);
            }

            var doc = new XmlDocument();
            doc.Load(configPath);

            var dbConnNode = doc.SelectSingleNode(
                "//appSettings/add[@key='DatabaseConnection']/@value");
            var dbConnName = dbConnNode?.Value;   // e.g. "Sqlite3", "SqlServer"
            if (string.IsNullOrEmpty(dbConnName))
            {
                logger.LogWarning("DEXA Server config에 DatabaseConnection 키 없음");
                return (null, null);
            }

            var connNode = doc.SelectSingleNode(
                $"//connectionStrings/add[@name='{dbConnName}']");
            var connStr = connNode?.Attributes?["connectionString"]?.Value;
            var providerName = connNode?.Attributes?["providerName"]?.Value;

            if (string.IsNullOrEmpty(connStr))
            {
                logger.LogWarning("DEXA Server config에 connectionString '{Name}' 없음", dbConnName);
                return (null, null);
            }

            logger.LogInformation(
                "DEXA Server config 로드: DatabaseConnection={Name}, Provider={Provider}",
                dbConnName, providerName);
            return (connStr, providerName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DEXA Server config 읽기 실패");
            return (null, null);
        }
    }

    /// <summary>providerName으로 DbProvider 결정</summary>
    private static DexaDbProvider ResolveProvider(string? providerName, IConfiguration config)
    {
        // 1) DEXA config의 providerName으로 결정
        if (!string.IsNullOrEmpty(providerName))
        {
            if (providerName.Contains("SqlClient", StringComparison.OrdinalIgnoreCase))
                return DexaDbProvider.SqlServer;
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
                return DexaDbProvider.Sqlite;
        }

        // 2) appsettings.json의 DexaDb:Provider로 결정
        var cfgProvider = config["DexaDb:Provider"];
        if (!string.IsNullOrEmpty(cfgProvider))
        {
            if (cfgProvider.Contains("sql", StringComparison.OrdinalIgnoreCase)
                && !cfgProvider.Contains("lite", StringComparison.OrdinalIgnoreCase))
                return DexaDbProvider.SqlServer;
        }

        return DexaDbProvider.Sqlite;
    }

    /// <summary>connection string에서 key=value 추출 (e.g. "Data Source", "Database")</summary>
    private static string? ExtractValue(string connStr, string key)
    {
        var match = Regex.Match(connStr, key + @"\s*=\s*(.+?)(?:;|$)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}
