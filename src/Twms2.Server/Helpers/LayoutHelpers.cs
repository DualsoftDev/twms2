using System.Globalization;
using Twms2.Server.Models.Dashboard;
using Microsoft.AspNetCore.Components;

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

    /// <summary>SVG &lt;title&gt; 툴팁 (자산 간략 정보)</summary>
    public static MarkupString RenderSvgTitle(AssetStatusInfo asset)
    {
        var online = asset.LatestPing != null
            ? (asset.LatestPing.Reachable ? "온라인" : "오프라인")
            : "Ping 내역없음";
        var health = GetHealthLabel(asset.Health);
        var backup = asset.LastBackupTime?.ToString("yyyy-MM-dd HH:mm") ?? "없음";
        var via = !string.IsNullOrEmpty(asset.AugIpVia) ? $"\n경유IP: {asset.AugIpVia}" : "";
        var txt = $"{asset.Name}\nIP: {asset.Ip ?? "-"}{via}\n상태: {health}\n{online}\n최근 백업: {backup}";
        return new MarkupString($"<title>{System.Net.WebUtility.HtmlEncode(txt)}</title>");
    }
}
