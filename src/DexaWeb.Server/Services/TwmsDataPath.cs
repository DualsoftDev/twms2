using System.Text.Json;

namespace DexaWeb.Server.Services;

/// <summary>
/// TWMS 런타임 데이터 경로 (C:\ProgramData\DualSoft\TWMS2\)
/// DB, PDF, 업로드 파일, 인증서, 설정 파일 등 런타임에 생성/수정되는 파일은 모두 이 경로 하위에 저장.
/// </summary>
public static class TwmsDataPath
{
    public static readonly string Base = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DualSoft", "TWMS2");

    public static readonly string Manuals = Path.Combine(Base, "manuals");
    public static readonly string Uploads = Path.Combine(Base, "uploads");
    public static readonly string LocalConfig = Path.Combine(Base, "appsettings.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Base);
        Directory.CreateDirectory(Manuals);
        Directory.CreateDirectory(Uploads);
        SeedConfigIfMissing();
    }

    private static void SeedConfigIfMissing()
    {
        if (File.Exists(LocalConfig)) return;

        var defaults = new
        {
            App = new { Title = "TWM", ShowDate = false, LogoPadding = 10 },
            DexaServer = new
            {
                ServerIp = Environment.MachineName,
                ServerPort = 50001,
                AskTimeoutSeconds = 5,
                PingTimeoutSeconds = 5,
                ClientName = "twm-web"
            },
            DexaDb = new
            {
                Provider = "sqlite",
                ConnectionString = $"Data Source={Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "LS", "DEXA", "Storage", "DEXA.sqlite3")};"
            },
            TwmDb = new
            {
                ConnectionString = $"Data Source={Path.Combine(Base, "twm.db.sqlite3")};"
            },
            PingSchedule = new { IntervalMinutes = 5, Phase1MaxConcurrency = 10 },
            Mdns = new { Hostname = "twms", Enabled = true }
        };

        var json = JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(LocalConfig, json);
    }
}
