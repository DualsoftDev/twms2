using System.Globalization;
using Twms2.Server.Models.Dashboard;
using Twms2.Server.Models.Twm;
using Microsoft.AspNetCore.Components;
using MudColor = MudBlazor.Color;

namespace Twms2.Server.Helpers;

/// <summary>
/// 레이아웃 / 도면 컴포넌트에서 공통으로 사용하는 헬퍼 메서드.
/// Razor 파일에서 <c>@using static Twms2.Server.Helpers.LayoutHelpers</c>로 참조.
/// </summary>
public static class LayoutHelpers
{
    public static string GetAssetIcon(string? typeName, int? isRobotPLC = null) => typeName switch
    {
        var n when n != null && n.Contains("PLC") && isRobotPLC is > 0   => "images/icons/robot.png",
        var n when n != null && n.Contains("PLC")                        => "images/icons/plc.png",
        var n when n != null && n.Contains("Servo")                      => "images/icons/servo.png",
        var n when n != null && (n.Contains("HMI") || n.Contains("XP")) => "images/icons/hmi.png",
        var n when n != null && n.Contains("Drive")                      => "images/icons/drive.png",
        var n when n != null && n.Contains("FTP")                        => "images/icons/ftp.png",
        _ => "images/icons/plc.png",
    };

    public static string GetHealthColor(AssetHealthStatus health) => health switch
    {
        AssetHealthStatus.BackedUp   => "#65B991",
        AssetHealthStatus.Unchanged  => "#6BA0DE",
        AssetHealthStatus.Failed     => "#E67E7E",
        AssetHealthStatus.InProgress => "#f59e0b",
        _                            => "#999",
    };

    /// <summary>
    /// 자산 아이콘 배경색: 백업 상태 색상의 밝은 버전, 오프라인이면 약간 어둡게.
    /// </summary>
    public static string GetIconBgColor(AssetHealthStatus health, bool isOffline) => (health, isOffline) switch
    {
        (AssetHealthStatus.BackedUp,   true)  => "#a3d4b5",
        (AssetHealthStatus.BackedUp,   false) => "#d4f0e0",
        (AssetHealthStatus.Unchanged,  true)  => "#a0c4e4",
        (AssetHealthStatus.Unchanged,  false) => "#d6e8f7",
        (AssetHealthStatus.Failed,     true)  => "#dca0a0",
        (AssetHealthStatus.Failed,     false) => "#f5d0d0",
        (AssetHealthStatus.InProgress, true)  => "#e0c080",
        (AssetHealthStatus.InProgress, false) => "#fde8c4",
        (_,                            true)  => "#b8b8b8",
        _                                     => "#e0e0e0",
    };

    /// <summary>
    /// 건강 색상 hex 문자열로부터 밝은 배경색 반환 (그룹 아이콘용).
    /// </summary>
    public static string GetIconBgColorFromHex(string healthColor) => healthColor switch
    {
        "#65B991" => "#d4f0e0",
        "#6BA0DE" => "#d6e8f7",
        "#E67E7E" => "#f5d0d0",
        "#f59e0b" => "#fde8c4",
        _         => "#e0e0e0",
    };

    public static string GetHealthLabel(AssetHealthStatus health) => health switch
    {
        AssetHealthStatus.BackedUp   => "백업 갱신",
        AssetHealthStatus.Unchanged  => "변경 없음",
        AssetHealthStatus.Failed     => "작업 실패",
        AssetHealthStatus.InProgress => "작업중",
        _                            => "내역 없음",
    };

    public static string GetHealthLabelShort(AssetHealthStatus health) => health switch
    {
        AssetHealthStatus.BackedUp   => "갱신",
        AssetHealthStatus.Unchanged  => "유지",
        AssetHealthStatus.Failed     => "실패",
        AssetHealthStatus.InProgress => "작업중",
        _                            => "내역없음",
    };

    public static (string Label, string Color) GetHealthLabelAndColor(AssetHealthStatus health) => health switch
    {
        AssetHealthStatus.BackedUp   => ("갱신", "#65B991"),
        AssetHealthStatus.Unchanged  => ("유지", "#6BA0DE"),
        AssetHealthStatus.Failed     => ("실패", "#E67E7E"),
        AssetHealthStatus.InProgress => ("진행중", "#f59e0b"),
        _                            => ("내역없음", "#999"),
    };

    public static MudColor GetHealthChipColor(AssetHealthStatus health) => health switch
    {
        AssetHealthStatus.BackedUp   => MudColor.Success,
        AssetHealthStatus.Unchanged  => MudColor.Info,
        AssetHealthStatus.Failed     => MudColor.Error,
        AssetHealthStatus.InProgress => MudColor.Warning,
        _                            => MudColor.Default,
    };

    public static string GetHeatmapTileClass(AssetHealthStatus health) => health switch
    {
        AssetHealthStatus.BackedUp   => "heatmap-tile-success",
        AssetHealthStatus.Unchanged  => "heatmap-tile-info",
        AssetHealthStatus.Failed     => "heatmap-tile-error",
        AssetHealthStatus.InProgress => "heatmap-tile-inprogress",
        _                            => "heatmap-tile-warning",
    };

    public static string AggregateHealthColor(IEnumerable<AssetStatusInfo> assets)
    {
        var list = assets as ICollection<AssetStatusInfo> ?? assets.ToList();
        if (list.Count == 0) return "#999";
        if (list.Any(a => a.Health == AssetHealthStatus.Failed)) return "#E67E7E";
        if (list.Any(a => a.Health == AssetHealthStatus.InProgress)) return "#f59e0b";
        if (list.Any(a => a.Health == AssetHealthStatus.BackedUp)) return "#65B991";
        if (list.Any(a => a.Health == AssetHealthStatus.Unchanged)) return "#6BA0DE";
        return "#999";
    }

    public static string TruncName(string? name, int maxLen = 8) =>
        name != null && name.Length > maxLen ? name[..maxLen] + ".." : name ?? "";

    /// <summary>double → InvariantCulture 문자열 (SVG 좌표용)</summary>
    public static string F(double v) =>
        v.ToString(CultureInfo.InvariantCulture);

    /// <summary>SVG &lt;text&gt; 요소를 MarkupString으로 렌더링 (Razor @switch 내 &lt;text&gt; 충돌 회피)</summary>
    public static MarkupString RenderSvgText(
        double x, double y, string fill, double fontSize, string? txt, double opacity = 1) =>
        new($"<g transform=\"translate({F(x)},{F(y)})\" opacity=\"{F(opacity)}\">" +
            $"<text x=\"0\" y=\"0\" fill=\"{fill}\" font-size=\"{F(fontSize)}\" " +
            $"class=\"drawing-text-shape\">{System.Net.WebUtility.HtmlEncode(txt ?? "")}</text></g>");

    /// <summary>
    /// 도면 이미지의 xMidYMid meet 렌더링 영역을 계산.
    /// DEXA 좌표 임포트와 동일한 로직으로, SVG &lt;image&gt;의 preserveAspectRatio 대신 직접 위치를 지정할 때 사용.
    /// </summary>
    public static (double X, double Y, double W, double H) CalcImageRect(
        TwmsBlueprintConfig? config, double vbW = 1000, double vbH = 600)
    {
        if (config == null || config.ImageWidth is not > 0 || config.ImageHeight is not > 0)
            return (0, 0, vbW, vbH); // 크기 정보 없으면 전체 채움

        var imgRatio = config.ImageWidth.Value / config.ImageHeight.Value;
        var vbRatio = vbW / vbH;

        if (imgRatio > vbRatio)
        {
            var h = vbW / imgRatio;
            return (0, (vbH - h) / 2, vbW, h);
        }
        else
        {
            var w = vbH * imgRatio;
            return ((vbW - w) / 2, 0, w, vbH);
        }
    }

    /// <summary>층 번호 표시 (지하는 B1/B2, 지상은 1F/2F 형태)</summary>
    public static string FormatFloorLabel(int floor) =>
        floor < 0 ? $"B{-floor}" : $"{floor}F";

    /// <summary>SVG &lt;title&gt; 툴팁 (자산 간략 정보)</summary>
    public static MarkupString RenderSvgTitle(AssetStatusInfo asset, int? floor = null)
    {
        var online = asset.LatestPing != null
            ? (asset.LatestPing.Reachable ? "온라인" : "오프라인")
            : "Ping 내역없음";
        var health = GetHealthLabel(asset.Health);
        var backup = asset.LastBackupTime?.ToString("yyyy-MM-dd HH:mm") ?? "없음";
        var via = !string.IsNullOrEmpty(asset.AugIpVia) ? $"\n경유IP: {asset.AugIpVia}" : "";
        var floorLine = floor.HasValue ? $"\n층: {FormatFloorLabel(floor.Value)}" : "";
        var txt = $"{asset.Name}\nIP: {asset.Ip ?? "-"}{via}{floorLine}\n상태: {health}\n{online}\n최근 백업: {backup}";
        return new MarkupString($"<title>{System.Net.WebUtility.HtmlEncode(txt)}</title>");
    }
}
