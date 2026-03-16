using System.Text.Json;
using System.Text.Json.Nodes;

namespace DexaWeb.Server.Services;

/// <summary>
/// 런타임 App 설정(Title, ShowDate, LogoPadding)을 ProgramData의 appsettings.json에 저장.
/// IConfiguration은 reloadOnChange=true로 등록되어 있어 저장 후 자동 반영.
/// </summary>
public class AppSettingsEditor
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public Task SaveAppSectionAsync(string title, bool showDate)
        => UpdateAppSectionAsync(app =>
        {
            app["Title"] = title;
            app["ShowDate"] = showDate;
        });

    public Task SaveLogoPaddingAsync(int logoPadding)
        => UpdateAppSectionAsync(app => app["LogoPadding"] = logoPadding);

    private async Task UpdateAppSectionAsync(Action<JsonObject> mutate)
    {
        var path = TwmsDataPath.LocalConfig;
        var root = await ReadOrCreateRootAsync(path);

        var appSection = root["App"]?.AsObject() ?? new JsonObject();
        mutate(appSection);
        root["App"] = appSection;

        await File.WriteAllTextAsync(path, root.ToJsonString(WriteOptions));
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
