using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LabPlot.DLS.Avalonia;

/// <summary>
/// DLS 溶媒プリセットの組み込み 9 種と、ユーザー追加分を <c>%APPDATA%/LabPlot/
/// dls-solvent-presets.json</c> に永続化するストア。<c>RecentFilesStore</c>
/// と同じ resilience パターン: 破損 / 読み込み失敗時は空配列にフォールバック、
/// 書き込み失敗は無視 (プリセットは付加価値で、欠落しても DLS 本体機能は止まらない)。
///
/// 組み込みは編集 / 削除不可。<see cref="LoadAll"/> は組み込み 9 種 + ユーザー追加
/// (重複名は組み込みを優先) を結合した一覧を返す。
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
    /// 25°C 標準値。実装時に最新文献値で要再確認 (鷹栖くん側 fact check 想定)。
    /// </summary>
    public static IReadOnlyList<SolventPreset> BuiltInPresets { get; } = new[]
    {
        new SolventPreset("Water",       1.333, 0.890, IsBuiltIn: true),
        new SolventPreset("Methanol",    1.328, 0.544, IsBuiltIn: true),
        new SolventPreset("Ethanol",     1.361, 1.074, IsBuiltIn: true),
        new SolventPreset("DMF",         1.430, 0.802, IsBuiltIn: true),
        new SolventPreset("DMSO",        1.479, 1.987, IsBuiltIn: true),
        new SolventPreset("THF",         1.407, 0.456, IsBuiltIn: true),
        new SolventPreset("Toluene",     1.496, 0.560, IsBuiltIn: true),
        new SolventPreset("Chloroform",  1.446, 0.537, IsBuiltIn: true),
        new SolventPreset("Acetone",     1.359, 0.306, IsBuiltIn: true),
    };

    /// <summary>
    /// 組み込み + ユーザー追加を結合して返す。組み込みと同名のユーザー追加は表示しない
    /// (組み込みを優先)。並び順は「組み込み 9 種を定義順 → ユーザー追加を追加順」。
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

    /// <summary>
    /// 名前 (case-insensitive) でプリセットを検索。組み込み・ユーザー両方が対象。
    /// </summary>
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
    /// ユーザー追加プリセットを保存。同名の既存ユーザープリセットは上書き。
    /// 組み込みと同名の場合は false を返して何もしない (組み込みを尊重)。
    /// </summary>
    public static bool AddUser(SolventPreset preset)
    {
        if (preset.IsBuiltIn) return false;
        if (BuiltInPresets.Any(p => string.Equals(
                p.Name, preset.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var users = LoadUserPresets().ToList();
        users.RemoveAll(p => string.Equals(
            p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        users.Add(preset with { IsBuiltIn = false });
        Save(users);
        return true;
    }

    /// <summary>
    /// 名前指定でユーザー追加プリセットを削除。組み込みは無視 (削除不可)。
    /// </summary>
    public static void RemoveUser(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var users = LoadUserPresets().ToList();
        var before = users.Count;
        users.RemoveAll(p => string.Equals(
            p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (users.Count != before)
        {
            Save(users);
        }
    }

    /// <summary>
    /// 永続化されているユーザー追加プリセットだけを返す (組み込みは含まれない)。
    /// 管理ダイアログで「ユーザー分のみ削除可能」を判定するときに使う。
    /// </summary>
    public static IReadOnlyList<SolventPreset> LoadUserPresets()
    {
        if (!File.Exists(FilePath)) return Array.Empty<SolventPreset>();
        try
        {
            var json = File.ReadAllText(FilePath);
            var entries = JsonSerializer.Deserialize<List<UserPresetDto>>(json);
            if (entries is null) return Array.Empty<SolventPreset>();
            return entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Name)
                    && double.IsFinite(e.RefractiveIndex)
                    && double.IsFinite(e.ViscosityMpas)
                    && e.RefractiveIndex > 0
                    && e.ViscosityMpas > 0)
                .Select(e => new SolventPreset(e.Name!.Trim(), e.RefractiveIndex, e.ViscosityMpas, IsBuiltIn: false))
                .ToArray();
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
                RefractiveIndex = p.RefractiveIndex,
                ViscosityMpas = p.ViscosityMpas,
            }).ToArray();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(dtos));
        }
        catch
        {
            // ignore — プリセットは付加価値で、書き込み失敗しても本体機能には影響しない。
        }
    }

    /// <summary>JSON 永続化用 DTO (IsBuiltIn は常に false なので書き出さない)。</summary>
    private sealed class UserPresetDto
    {
        public string? Name { get; set; }
        public double RefractiveIndex { get; set; }
        public double ViscosityMpas { get; set; }
    }
}
