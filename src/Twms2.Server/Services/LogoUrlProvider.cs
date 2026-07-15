namespace Twms2.Server.Services;

/// <summary>
/// 사이드바 로고(app-logo.*) URL 캐시.
/// /api/nav 가 모든 페이지에서 30초마다 호출되는데 매번 업로드 폴더를 디스크 스캔하던 것을
/// 1회 스캔 후 캐시하고, 로고 업로드/삭제 시에만 무효화한다.
/// </summary>
public static class LogoUrlProvider
{
    private static readonly object Lock = new();
    private static string? _cached;
    private static bool _loaded;

    public static string? Get()
    {
        if (_loaded) return _cached;
        lock (Lock)
        {
            if (!_loaded)
            {
                _cached = Scan();
                _loaded = true;
            }
            return _cached;
        }
    }

    /// <summary>로고 파일 변경(업로드/삭제) 후 호출 — 다음 조회 시 재스캔.</summary>
    public static void Invalidate()
    {
        lock (Lock) { _loaded = false; }
    }

    private static string? Scan()
    {
        try
        {
            if (!Directory.Exists(TwmsDataPath.Uploads)) return null;
            var files = Directory.GetFiles(TwmsDataPath.Uploads, "app-logo.*");
            if (files.Length == 0) return null;
            var fi = new FileInfo(files[0]);
            return $"/uploads/{Path.GetFileName(files[0])}?t={fi.LastWriteTimeUtc.Ticks}";
        }
        catch { return null; }
    }
}
