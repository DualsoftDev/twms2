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

    public const string DefaultNavTitle = "TWMS";
    public const string DefaultNavSubtitle = "Total Web Management System";

    public AppSettingsEditor(IConfiguration config)
    {
        _navTitle = config["App:NavTitle"] ?? DefaultNavTitle;
        _navSubtitle = config["App:NavSubtitle"] ?? DefaultNavSubtitle;
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

    private async Task UpdateAppSectionAsync(Action<JsonObject> mutate)
    {
        var path = TwmsDataPath.LocalConfig;
        await _writeLock.WaitAsync();
        try
        {
            var root = await ReadOrCreateRootAsync(path);

            var appSection = root["App"]?.AsObject() ?? new JsonObject();
            mutate(appSection);
            root["App"] = appSection;

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
