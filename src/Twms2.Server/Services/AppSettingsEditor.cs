using System.Text.Json;
using System.Text.Json.Nodes;

namespace Twms2.Server.Services;

/// <summary>
/// 런타임 App 설정(Title, ShowDate, LogoPadding, NavTitle, NavSubtitle)을 ProgramData의 appsettings.json에 저장.
/// 사이드바 브랜드(NavTitle/NavSubtitle)는 메모리 캐시로도 보관해 저장 즉시 반영한다 —
/// IConfiguration reloadOnChange 는 프로덕션에서만 동작(Development 는 시작 시 스냅샷)하므로,
/// 같은 프로세스의 저장값을 환경과 무관하게 곧바로 읽기 위함.
/// </summary>
public class AppSettingsEditor
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    // 파일 RMW 직렬화 — 싱글톤 인스턴스가 동시 저장으로 키를 잃거나 깨진 JSON 을 쓰지 않도록.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // 브랜드(제목/부제) 캐시. 시작 시 설정에서 초기화하고 저장 시 갱신.
    private readonly object _brandGate = new();
    private string _navTitle;
    private string _navSubtitle;

    // 다운로드/리포트 로그인 요구 여부. 기본 false(개방 — 구 TWM 동작).
    // 미들웨어가 요청마다 읽으므로 브랜드와 같은 이유로 메모리 캐시(저장 즉시 반영).
    private volatile bool _requireLoginForDownload;

    // 실험 기능: 드라이브 기종·버전 자동 인식(연결 확인 때 Modbus 로 식별 레지스터를 읽는 것).
    // 기본 꺼짐 — 현장 세팅 때만 쓰는 기능이라 평소엔 숨기고, 꺼져 있으면 드라이브 연결 확인도 핑으로만 동작한다.
    // 설정 > 일반에 ?lab=1 로 들어가야 보이는 체크박스로 켠다. "App" 이 아니라 "Features" 섹션에 저장.
    private volatile bool _driveIdentify;

    public const string DefaultNavTitle = "TWMS";
    public const string DefaultNavSubtitle = "Total Web Management System";

    public AppSettingsEditor(IConfiguration config)
    {
        _navTitle = config["App:NavTitle"] ?? DefaultNavTitle;
        _navSubtitle = config["App:NavSubtitle"] ?? DefaultNavSubtitle;
        _requireLoginForDownload = bool.TryParse(config["App:RequireLoginForDownload"], out var r) && r;
        _driveIdentify = bool.TryParse(config["Features:DriveIdentify"], out var di) && di;
    }

    /// <summary>드라이브 기종·버전 자동 인식 사용 여부 (실험 기능, 기본 false). 저장 즉시 반영.</summary>
    public bool DriveIdentify => _driveIdentify;

    public Task SaveDriveIdentifyAsync(bool on)
    {
        _driveIdentify = on;
        return UpdateSectionAsync("Features", f => f["DriveIdentify"] = on);
    }

    /// <summary>백업 ZIP 다운로드·DEXA 리포트 열람에 로그인을 요구할지. 저장 즉시 반영.</summary>
    public bool RequireLoginForDownload => _requireLoginForDownload;

    public Task SaveRequireLoginForDownloadAsync(bool require)
    {
        _requireLoginForDownload = require;
        return UpdateAppSectionAsync(app => app["RequireLoginForDownload"] = require);
    }

    /// <summary>현재 사이드바 브랜드(제목/부제). 저장 즉시 반영(메모리 캐시) — Dev/Prod 공통 동작.</summary>
    public (string Title, string Subtitle) GetBrand()
    {
        lock (_brandGate) return (_navTitle, _navSubtitle);
    }

    public Task SaveAppSectionAsync(string title, bool showDate)
        => UpdateAppSectionAsync(app =>
        {
            app["Title"] = title;
            app["ShowDate"] = showDate;
        });

    public Task SaveLogoPaddingAsync(int logoPadding)
        => UpdateAppSectionAsync(app => app["LogoPadding"] = logoPadding);

    /// <summary>사이드바 브랜드(로고 우측 제목/부제) 저장. App:Title(페이지 제목용)과 분리된 NavTitle/NavSubtitle 키 사용.</summary>
    public Task SaveBrandAsync(string navTitle, string navSubtitle)
    {
        lock (_brandGate) { _navTitle = navTitle; _navSubtitle = navSubtitle; }
        return UpdateAppSectionAsync(app =>
        {
            app["NavTitle"] = navTitle;
            app["NavSubtitle"] = navSubtitle;
        });
    }

    private Task UpdateAppSectionAsync(Action<JsonObject> mutate) => UpdateSectionAsync("App", mutate);

    private async Task UpdateSectionAsync(string section, Action<JsonObject> mutate)
    {
        var path = TwmsDataPath.LocalConfig;
        await _writeLock.WaitAsync();
        try
        {
            var root = await ReadOrCreateRootAsync(path);

            var obj = root[section]?.AsObject() ?? new JsonObject();
            mutate(obj);
            root[section] = obj;

            // 원자적 쓰기: 임시 파일에 기록 후 교체 → 부분 기록(torn write)으로 설정 로드가 깨지지 않도록.
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, root.ToJsonString(WriteOptions));
            File.Move(tmp, path, overwrite: true);
        }
        finally { _writeLock.Release(); }
    }

    private static async Task<JsonObject> ReadOrCreateRootAsync(string path)
    {
        if (File.Exists(path))
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
        }
        return new JsonObject();
    }
}
