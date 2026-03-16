using System.Text.Json;
using System.Text.Json.Nodes;

namespace DexaWeb.Server.Services;

/// <summary>
/// appsettings.json의 App 섹션(Title, ShowDate)을 런타임에 수정.
/// IConfiguration은 reloadOnChange=true로 등록되어 있어 저장 후 자동 반영.
/// </summary>
public class AppSettingsEditor(IWebHostEnvironment env)
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public async Task SaveAppSectionAsync(string title, bool showDate)
    {
        var path = Path.Combine(env.ContentRootPath, "appsettings.json");
        var json = await File.ReadAllTextAsync(path);
        var root = JsonNode.Parse(json)!.AsObject();

        var appSection = root["App"]?.AsObject() ?? new JsonObject();
        appSection["Title"] = title;
        appSection["ShowDate"] = showDate;
        root["App"] = appSection;

        await File.WriteAllTextAsync(path, root.ToJsonString(WriteOptions));
    }

    public async Task SaveLogoPaddingAsync(int logoPadding)
    {
        var path = Path.Combine(env.ContentRootPath, "appsettings.json");
        var json = await File.ReadAllTextAsync(path);
        var root = JsonNode.Parse(json)!.AsObject();

        var appSection = root["App"]?.AsObject() ?? new JsonObject();
        appSection["LogoPadding"] = logoPadding;
        root["App"] = appSection;

        await File.WriteAllTextAsync(path, root.ToJsonString(WriteOptions));
    }
}
