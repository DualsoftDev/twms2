using System.Data;
using System.Data.SQLite;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Data.Sqlite;

namespace DexaWeb.Server.Data;

/// <summary>
/// DEXA DB 연결 팩토리.
/// 읽기: Microsoft.Data.Sqlite (ReadOnly)
/// 쓰기: System.Data.SQLite (DEXA Server와 동일 라이브러리 → 잠금 프로토콜 공유)
/// </summary>
public class DexaDbConnection
{
    private const string DefaultServerConfigPath =
        @"C:\Program Files (x86)\LS\DEXA\Server\DEXA.ServerService.exe.config";

    private readonly string _readOnlyConnStr;
    private readonly string _readWriteConnStr;

    /// <summary>DEXA DB 파일 경로 (드라이브 용량 조회 등에 활용)</summary>
    public string DbFilePath { get; }

    public DexaDbConnection(IConfiguration configuration, ILogger<DexaDbConnection> logger)
    {
        var serverConfigPath = configuration["DexaServer:ConfigPath"] ?? DefaultServerConfigPath;
        var raw = TryReadDexaServerConfig(serverConfigPath, logger)
            ?? configuration["DexaDb:ConnectionString"]
            ?? "Data Source=C:\\ProgramData\\LS\\DEXA\\Storage\\DEXA.sqlite3;";

        // 기존 Mode= 옵션 제거
        var baseStr = Regex.Replace(raw.TrimEnd(';'), @";?\s*Mode=[^;]*", "", RegexOptions.IgnoreCase);

        // Microsoft.Data.Sqlite용 (읽기) — Pooling=False로 Dispose 시 파일 핸들 즉시 해제
        // DEXA Server가 DB 파일의 소유자이므로, TWMS가 파일을 물고 있으면 안 됨
        _readOnlyConnStr = baseStr + ";Mode=ReadOnly;Pooling=False;";

        // System.Data.SQLite용 (쓰기) — 커넥션 문자열 형식이 다름
        // "Data Source=path" 에서 path만 추출
        var dbPath = ExtractDataSource(baseStr);
        DbFilePath = dbPath;
        _readWriteConnStr = $"Data Source={dbPath};Version=3;Journal Mode=WAL;";

        logger.LogInformation("DEXA DB 읽기 (Microsoft.Data.Sqlite): {ConnStr}", _readOnlyConnStr);
        logger.LogInformation("DEXA DB 쓰기 (System.Data.SQLite): {ConnStr}", _readWriteConnStr);

        TestWriteConnection(logger);
        ValidateSchema(logger);
    }

    /// <summary>DEXA DB 스키마 호환성 경고 목록 (시작 시 수집)</summary>
    public IReadOnlyList<string> SchemaWarnings => _schemaWarnings;
    private readonly List<string> _schemaWarnings = [];

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

    /// <summary>
    /// DEXA DB 스키마가 TWMS가 기대하는 구조와 일치하는지 검증.
    /// 테이블/컬럼 누락 시 경고 로그 + SchemaWarnings에 기록.
    /// </summary>
    private void ValidateSchema(ILogger logger)
    {
        // TWMS가 사용하는 테이블 → 필수 컬럼 매핑
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

        // 선택적 테이블 (존재하지 않아도 정상 동작)
        var optionalTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "asset.status" };

        try
        {
            using var conn = new SqliteConnection(_readOnlyConnStr);
            conn.Open();

            // 현재 DB의 테이블 목록
            var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    existingTables.Add(reader.GetString(0));
            }

            foreach (var (table, expectedCols) in expectedSchema)
            {
                if (!existingTables.Contains(table))
                {
                    var msg = $"DEXA 스키마 경고: 테이블 '{table}' 없음";
                    _schemaWarnings.Add(msg);
                    if (optionalTables.Contains(table))
                        logger.LogDebug(msg);
                    else
                        logger.LogWarning(msg);
                    continue;
                }

                // PRAGMA table_info로 컬럼 확인
                var actualCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"PRAGMA table_info([{table}])";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                        actualCols.Add(reader.GetString(1)); // column 1 = name
                }

                foreach (var col in expectedCols)
                {
                    if (!actualCols.Contains(col))
                    {
                        var msg = $"DEXA 스키마 경고: 테이블 '{table}'에 컬럼 '{col}' 없음";
                        _schemaWarnings.Add(msg);
                        logger.LogWarning(msg);
                    }
                }
            }

            if (_schemaWarnings.Count == 0)
                logger.LogInformation("DEXA DB 스키마 검증 통과 — 모든 테이블/컬럼 정상");
            else
                logger.LogWarning("DEXA DB 스키마 검증 완료 — 경고 {Count}건", _schemaWarnings.Count);
        }
        catch (Exception ex)
        {
            var msg = $"DEXA DB 스키마 검증 실패: {ex.Message}";
            _schemaWarnings.Add(msg);
            logger.LogWarning(ex, "DEXA DB 스키마 검증 중 오류");
        }
    }

    /// <summary>
    /// DEXA ServerService.exe.config에서 DB 연결 문자열을 읽는다.
    /// appSettings/DatabaseConnection → connectionStrings에서 해당 name의 connectionString 룩업.
    /// </summary>
    private static string? TryReadDexaServerConfig(string configPath, ILogger logger)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                logger.LogInformation("DEXA Server config 없음: {Path}", configPath);
                return null;
            }

            var doc = new XmlDocument();
            doc.Load(configPath);

            // 1) appSettings에서 사용 중인 DB 프로바이더 이름 읽기 (e.g. "Sqlite3")
            var providerNode = doc.SelectSingleNode(
                "//appSettings/add[@key='DatabaseConnection']/@value");
            var providerName = providerNode?.Value;
            if (string.IsNullOrEmpty(providerName))
            {
                logger.LogWarning("DEXA Server config에 DatabaseConnection 키 없음");
                return null;
            }

            // 2) connectionStrings에서 해당 name의 connectionString 읽기
            var connNode = doc.SelectSingleNode(
                $"//connectionStrings/add[@name='{providerName}']/@connectionString");
            var connStr = connNode?.Value;
            if (string.IsNullOrEmpty(connStr))
            {
                logger.LogWarning("DEXA Server config에 connectionString '{Provider}' 없음", providerName);
                return null;
            }

            logger.LogInformation(
                "DEXA Server config에서 DB 연결 정보 로드: Provider={Provider}", providerName);
            return connStr;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DEXA Server config 읽기 실패");
            return null;
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
