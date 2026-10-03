using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Twms2.Server.Services.Drive;

/// <summary>
/// DriveView 9 DataFile 에 정의된 (기종, 모델버전) 목록.
/// 드라이브 백업은 그 버전의 파라미터 맵(.INV, S300 은 .json)이 있어야 성립하므로,
/// 실물에서 읽은 버전이 여기 없으면 등록은 되어도 백업이 실패한다. 등록 전에 그걸 경고하는 데 쓴다.
///
/// DriveView 9 는 백업을 수행하는 에이전트 PC 에 설치된다. 이 서버 PC 에 없으면 판정을 포기한다(null) —
/// 없는 걸 "없다"고 단정하면 멀쩡한 등록을 막게 된다.
/// </summary>
public static class DriveCatalog
{
    private static readonly Regex FileRx = new(@"^(?<model>[A-Za-z0-9]+)_(?<major>\d+)_(?<minor>\d+)\.(inv|json)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>DriveView 9 의 DataFile 폴더. 레지스트리 InstallLocation 우선, 없으면 기본 설치 경로.</summary>
    public static string? FindDataFileDir()
    {
        foreach (var root in new[]
        {
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        })
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(root);
                if (k is null) continue;
                foreach (var name in k.GetSubKeyNames())
                {
                    using var sk = k.OpenSubKey(name);
                    if (sk?.GetValue("DisplayName") is not string dn ||
                        !dn.Contains("DriveView", StringComparison.OrdinalIgnoreCase)) continue;
                    if (sk.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc))
                    {
                        var dir = Path.Combine(loc, "DataFile");
                        if (Directory.Exists(dir)) return dir;
                    }
                }
            }
            catch { /* 레지스트리를 못 읽으면 기본 경로로 넘어간다 */ }
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LS", "DriveView9", "DataFile");
        return Directory.Exists(fallback) ? fallback : null;
    }

    /// <summary>해당 기종 폴더에 정의된 버전들 ("1.04" 형식). DriveView 9 가 없으면 null.</summary>
    public static IReadOnlySet<string>? VersionsOf(string model)
    {
        var dir = FindDataFileDir();
        if (dir is null) return null;
        var modelDir = Path.Combine(dir, model);
        if (!Directory.Exists(modelDir)) return new HashSet<string>();

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(modelDir))
        {
            var m = FileRx.Match(Path.GetFileName(f));
            if (!m.Success || !m.Groups["model"].Value.Equals(model, StringComparison.OrdinalIgnoreCase)) continue;
            set.Add($"{int.Parse(m.Groups["major"].Value)}.{m.Groups["minor"].Value}");
        }
        return set;
    }

    /// <summary>true/false = 판정됨, null = DriveView 9 가 이 PC 에 없어 판정 불가.</summary>
    public static bool? HasVersion(string model, string version) =>
        VersionsOf(model)?.Contains(version);
}
