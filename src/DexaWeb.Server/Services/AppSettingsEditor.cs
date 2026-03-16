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

    public async Task SaveAppSectionAsync(string title, bool showDate)
    {
        var path = TwmsDataPath.LocalConfig;
        var root = await ReadOrCreateRootAsync(path);

        var appSection = root["App"]?.AsObject() ?? new JsonObject();
        appSection["Title"] = title;
        appSection["ShowDate"] = showDate;
        root["App"] = appSection;

        await File.WriteAllTextAsync(path, root.ToJsonString(WriteOptions));
    }

    public async Task SaveLogoPaddingAsync(int logoPadding)
    {
        var path = TwmsDataPath.LocalConfig;
        var root = await ReadOrCreateRootAsync(path);

        var appSection = root["App"]?.AsObject() ?? new JsonObject();
        appSection["LogoPadding"] = logoPadding;
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
