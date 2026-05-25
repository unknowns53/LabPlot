using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LabPlot.DLS.Avalonia;

/// <summary>
/// DLS 溶媒プリセットの組み込み 9 種 (各々が温度ごとの観測点の表を持つ) と、
/// ユーザー追加分を <c>%APPDATA%/LabPlot/dls-solvent-presets.json</c> に永続化
/// するストア。<c>RecentFilesStore</c> と同じ resilience パターン: 破損 /
/// 読み込み失敗時は空配列にフォールバック、書き込み失敗は無視。
///
/// 組み込みは編集 / 削除不可。<see cref="LoadAll"/> は組み込み 9 種 + ユーザー
/// 追加 (重複名は組み込みを優先) を結合した一覧を返す。各プリセットの温度点
/// は読み込み時に温度昇順でソートする。
/// </summary>
internal static class SolventPresetStore
{
    private const string FileName = "dls-solvent-presets.json";

    private static string DirectoryPath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "LabPlot");
        }
    }

    private static string FilePath => Path.Combine(DirectoryPath, FileName);

    /// <summary>
    /// 組み込み温度テーブル。CRC Handbook 等から拾った 5 / 15 / 25 / 35 / 45 deg C の
    /// 代表値 (DMSO は融点 18.5 deg C なので 20 deg C 起点)。実装時に最新文献値で
    /// 要再確認 (利用者側で fact check 想定)。線形補間で中間温度の値を推定するため、
    /// 温度範囲外のサンプルは端値クランプ + warning toast で扱う。
    /// </summary>
    public static IReadOnlyList<SolventPreset> BuiltInPresets { get; } = new[]
    {
        new SolventPreset("Water", new[]
        {
            new SolventPresetPoint( 5.0, 1.3346, 1.519),
            new SolventPresetPoint(15.0, 1.3334, 1.139),
            new SolventPresetPoint(25.0, 1.3325, 0.890),
            new SolventPresetPoint(35.0, 1.3306, 0.719),
            new SolventPresetPoint(45.0, 1.3288, 0.596),
        }, IsBuiltIn: true),
        new SolventPreset("Methanol", new[]
        {
            new SolventPresetPoint( 5.0, 1.332, 0.681),
            new SolventPresetPoint(15.0, 1.330, 0.611),
            new SolventPresetPoint(25.0, 1.328, 0.544),
            new SolventPresetPoint(35.0, 1.326, 0.485),
            new SolventPresetPoint(45.0, 1.324, 0.435),
        }, IsBuiltIn: true),
        new SolventPreset("Ethanol", new[]
        {
            new SolventPresetPoint( 5.0, 1.365, 1.475),
            new SolventPresetPoint(15.0, 1.363, 1.235),
            new SolventPresetPoint(25.0, 1.361, 1.074),
            new SolventPresetPoint(35.0, 1.357, 0.834),
            new SolventPresetPoint(45.0, 1.354, 0.694),
        }, IsBuiltIn: true),
        new SolventPreset("DMF", new[]
        {
            new SolventPresetPoint( 5.0, 1.434, 0.957),
            new SolventPresetPoint(15.0, 1.432, 0.873),
            new SolventPresetPoint(25.0, 1.430, 0.802),
            new SolventPresetPoint(35.0, 1.428, 0.733),
            new SolventPresetPoint(45.0, 1.426, 0.673),
        }, IsBuiltIn: true),
        new SolventPreset("DMSO", new[]
        {
            new SolventPresetPoint(20.0, 1.481, 2.220),
            new SolventPresetPoint(25.0, 1.479, 1.987),
            new SolventPresetPoint(35.0, 1.477, 1.676),
            new SolventPresetPoint(45.0, 1.475, 1.421),
        }, IsBuiltIn: true),
        new SolventPreset("THF", new[]
        {
            new SolventPresetPoint( 5.0, 1.410, 0.555),
            new SolventPresetPoint(15.0, 1.409, 0.500),
            new SolventPresetPoint(25.0, 1.407, 0.456),
            new SolventPresetPoint(35.0, 1.405, 0.420),
            new SolventPresetPoint(45.0, 1.403, 0.389),
        }, IsBuiltIn: true),
        new SolventPreset("Toluene", new[]
        {
            new SolventPresetPoint( 5.0, 1.501, 0.708),
            new SolventPresetPoint(15.0, 1.499, 0.625),
            new SolventPresetPoint(25.0, 1.496, 0.560),
            new SolventPresetPoint(35.0, 1.493, 0.505),
            new SolventPresetPoint(45.0, 1.491, 0.460),
        }, IsBuiltIn: true),
        new SolventPreset("Chloroform", new[]
        {
            new SolventPresetPoint( 5.0, 1.450, 0.681),
            new SolventPresetPoint(15.0, 1.448, 0.611),
            new SolventPresetPoint(25.0, 1.446, 0.537),
            new SolventPresetPoint(35.0, 1.443, 0.490),
            new SolventPresetPoint(45.0, 1.441, 0.440),
        }, IsBuiltIn: true),
        new SolventPreset("Acetone", new[]
        {
            new SolventPresetPoint( 5.0, 1.363, 0.376),
            new SolventPresetPoint(15.0, 1.361, 0.340),
            new SolventPresetPoint(25.0, 1.359, 0.306),
            new SolventPresetPoint(35.0, 1.357, 0.278),
            new SolventPresetPoint(45.0, 1.355, 0.255),
        }, IsBuiltIn: true),
    };

    /// <summary>
    /// 組み込み + ユーザー追加を結合して返す。組み込みと同名のユーザー追加は表示しない。
    /// </summary>
    public static IReadOnlyList<SolventPreset> LoadAll()
    {
        var builtInNames = new HashSet<string>(
            BuiltInPresets.Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);

        var users = LoadUserPresets()
            .Where(p => !builtInNames.Contains(p.Name))
            .ToList();

        var result = new List<SolventPreset>(BuiltInPresets.Count + users.Count);
        result.AddRange(BuiltInPresets);
        result.AddRange(users);
        return result;
    }

    /// <summary>名前 (case-insensitive) でプリセットを検索。組み込み・ユーザー両方が対象。</summary>
    public static bool TryFind(string? name, out SolventPreset preset)
    {
        preset = null!;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var trimmed = name.Trim();
        foreach (var p in LoadAll())
        {
            if (string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                preset = p;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 与えられた温度における (n, eta) を線形補間で求める。範囲外は端値クランプ。
    /// <paramref name="outOfRange"/> は補間範囲外で端値を返したときに true。
    /// </summary>
    public static (double RefractiveIndex, double ViscosityMpas) Interpolate(
        SolventPreset preset, double temperatureCelsius, out bool outOfRange)
    {
        outOfRange = false;
        var pts = preset.Points;
        if (pts.Count == 0)
        {
            return (double.NaN, double.NaN);
        }
        if (pts.Count == 1)
        {
            outOfRange = !pts[0].TemperatureCelsius.Equals(temperatureCelsius);
            return (pts[0].RefractiveIndex, pts[0].ViscosityMpas);
        }

        // 温度昇順を前提 (LoadAll / BuiltInPresets で保証)。
        if (temperatureCelsius <= pts[0].TemperatureCelsius)
        {
            outOfRange = temperatureCelsius < pts[0].TemperatureCelsius;
            return (pts[0].RefractiveIndex, pts[0].ViscosityMpas);
        }
        if (temperatureCelsius >= pts[^1].TemperatureCelsius)
        {
            outOfRange = temperatureCelsius > pts[^1].TemperatureCelsius;
            return (pts[^1].RefractiveIndex, pts[^1].ViscosityMpas);
        }

        for (var i = 0; i < pts.Count - 1; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            if (temperatureCelsius >= a.TemperatureCelsius
                && temperatureCelsius <= b.TemperatureCelsius)
            {
                var dt = b.TemperatureCelsius - a.TemperatureCelsius;
                if (dt <= 0)
                {
                    return (a.RefractiveIndex, a.ViscosityMpas);
                }
                var w = (temperatureCelsius - a.TemperatureCelsius) / dt;
                var n = a.RefractiveIndex + (b.RefractiveIndex - a.RefractiveIndex) * w;
                var eta = a.ViscosityMpas + (b.ViscosityMpas - a.ViscosityMpas) * w;
                return (n, eta);
            }
        }

        return (pts[^1].RefractiveIndex, pts[^1].ViscosityMpas);
    }

    /// <summary>
    /// ユーザー追加プリセットに温度点を 1 つ追加する。同名のユーザープリセットが
    /// なければ新規作成、既にあれば点を merge (同じ温度は上書き)。組み込みと同名
    /// なら無視 (false 返却)。
    /// </summary>
    public static bool AddUserPoint(string name, SolventPresetPoint point)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var trimmed = name.Trim();
        if (BuiltInPresets.Any(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        if (!double.IsFinite(point.TemperatureCelsius)
            || !double.IsFinite(point.RefractiveIndex)
            || !double.IsFinite(point.ViscosityMpas)
            || point.RefractiveIndex <= 0
            || point.ViscosityMpas <= 0)
        {
            return false;
        }

        var users = LoadUserPresets().ToList();
        var idx = users.FindIndex(p => string.Equals(
            p.Name, trimmed, StringComparison.OrdinalIgnoreCase));

        if (idx < 0)
        {
            users.Add(new SolventPreset(trimmed, new[] { point }, IsBuiltIn: false));
        }
        else
        {
            var merged = users[idx].Points
                .Where(p => !p.TemperatureCelsius.Equals(point.TemperatureCelsius))
                .Append(point)
                .OrderBy(p => p.TemperatureCelsius)
                .ToList();
            users[idx] = new SolventPreset(users[idx].Name, merged, IsBuiltIn: false);
        }

        Save(users);
        return true;
    }

    /// <summary>
    /// ユーザー追加プリセットから 1 温度点を削除する。組み込みは無視。点を全部
    /// 削除するとプリセット自体も消える。
    /// </summary>
    public static void RemoveUserPoint(string name, double temperatureCelsius)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var trimmed = name.Trim();
        if (BuiltInPresets.Any(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var users = LoadUserPresets().ToList();
        var idx = users.FindIndex(p => string.Equals(
            p.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;

        var remaining = users[idx].Points
            .Where(p => !p.TemperatureCelsius.Equals(temperatureCelsius))
            .ToList();
        if (remaining.Count == 0)
        {
            users.RemoveAt(idx);
        }
        else
        {
            users[idx] = new SolventPreset(users[idx].Name, remaining, IsBuiltIn: false);
        }
        Save(users);
    }

    /// <summary>ユーザー追加プリセットを名前ごと完全削除。組み込みは無視。</summary>
    public static void RemoveUser(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var trimmed = name.Trim();
        var users = LoadUserPresets().ToList();
        var before = users.Count;
        users.RemoveAll(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (users.Count != before)
        {
            Save(users);
        }
    }

    /// <summary>
    /// 永続化されているユーザー追加プリセット (温度昇順) を返す。組み込みは含まない。
    /// 旧形式 (温度なし単一値) の json は 25 deg C 単一点として読み込み、後方互換性を保つ。
    /// </summary>
    public static IReadOnlyList<SolventPreset> LoadUserPresets()
    {
        if (!File.Exists(FilePath)) return Array.Empty<SolventPreset>();
        try
        {
            var json = File.ReadAllText(FilePath);
            var entries = JsonSerializer.Deserialize<List<UserPresetDto>>(json);
            if (entries is null) return Array.Empty<SolventPreset>();

            var result = new List<SolventPreset>();
            foreach (var dto in entries)
            {
                if (string.IsNullOrWhiteSpace(dto.Name)) continue;
                var points = new List<SolventPresetPoint>();

                if (dto.Points is { Count: > 0 })
                {
                    foreach (var p in dto.Points)
                    {
                        if (!double.IsFinite(p.TemperatureCelsius)) continue;
                        if (!double.IsFinite(p.RefractiveIndex) || p.RefractiveIndex <= 0) continue;
                        if (!double.IsFinite(p.ViscosityMpas) || p.ViscosityMpas <= 0) continue;
                        points.Add(new SolventPresetPoint(p.TemperatureCelsius, p.RefractiveIndex, p.ViscosityMpas));
                    }
                }
                else if (dto.RefractiveIndex.HasValue && dto.ViscosityMpas.HasValue
                    && double.IsFinite(dto.RefractiveIndex.Value)
                    && double.IsFinite(dto.ViscosityMpas.Value)
                    && dto.RefractiveIndex.Value > 0
                    && dto.ViscosityMpas.Value > 0)
                {
                    // 後方互換: 温度フィールド未保存の旧 json は 25 deg C 単一点として扱う。
                    points.Add(new SolventPresetPoint(25.0, dto.RefractiveIndex.Value, dto.ViscosityMpas.Value));
                }

                if (points.Count == 0) continue;
                points = points.OrderBy(p => p.TemperatureCelsius).ToList();
                result.Add(new SolventPreset(dto.Name!.Trim(), points, IsBuiltIn: false));
            }
            return result;
        }
        catch
        {
            return Array.Empty<SolventPreset>();
        }
    }

    private static void Save(IEnumerable<SolventPreset> users)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var dtos = users.Select(p => new UserPresetDto
            {
                Name = p.Name,
                Points = p.Points.Select(pt => new UserPresetPointDto
                {
                    TemperatureCelsius = pt.TemperatureCelsius,
                    RefractiveIndex = pt.RefractiveIndex,
                    ViscosityMpas = pt.ViscosityMpas,
                }).ToList(),
            }).ToArray();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(dtos));
        }
        catch
        {
            // ignore - プリセットは付加価値で、書き込み失敗しても本体機能には影響しない。
        }
    }

    /// <summary>新形式 JSON 永続化用 DTO。旧形式の単一温度フィールドも読み込み専用で温存。</summary>
    private sealed class UserPresetDto
    {
        public string? Name { get; set; }
        public List<UserPresetPointDto>? Points { get; set; }

        // 旧形式 (温度なし単一値) との後方互換用。新形式では Points にのみ書く。
        public double? RefractiveIndex { get; set; }
        public double? ViscosityMpas { get; set; }
    }

    private sealed class UserPresetPointDto
    {
        public double TemperatureCelsius { get; set; }
        public double RefractiveIndex { get; set; }
        public double ViscosityMpas { get; set; }
    }
}
