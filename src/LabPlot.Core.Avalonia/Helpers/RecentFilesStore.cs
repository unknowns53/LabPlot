using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LabPlot.Core.Avalonia.Helpers;

/// <summary>
/// アプリごとの「最近開いたファイル」一覧を JSON で永続化する best-effort ストア。
/// 失敗系は黙って空配列を返し、UI が止まらないようにする (MRU は付加価値で、欠落しても
/// ユーザーは普通の「開く」で続行できる)。
///
/// 保存先: %APPDATA%/LabPlot/recent-{appKey}.json
/// 最大件数: <see cref="MaxEntries"/> (= 5)
/// </summary>
public static class RecentFilesStore
{
    public const int MaxEntries = 5;

    private static string DirectoryPath
    {
        get
        {
            var appData = AppDataPaths.GetApplicationDataPath();
            return Path.Combine(appData, "LabPlot");
        }
    }

    private static string FilePathFor(string appKey)
        => Path.Combine(DirectoryPath, $"recent-{appKey}.json");

    /// <summary>
    /// 永続化されている履歴を読み込んで、現存するファイルだけを最大 <see cref="MaxEntries"/> 件返す。
    /// 削除済みファイルは自動的にスキップする (UI に dangling パスが残らないように)。
    /// </summary>
    public static IReadOnlyList<string> Load(string appKey)
    {
        var path = FilePathFor(appKey);
        if (!File.Exists(path)) return Array.Empty<string>();
        try
        {
            var json = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<List<string>>(json);
            if (entries is null) return Array.Empty<string>();
            return entries
                .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                .Take(MaxEntries)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 指定パスを履歴の先頭に挿入し、重複は前方の出現だけ残してそれ以外を削除する。
    /// 大文字小文字を区別しない比較 (Windows 想定)。<see cref="MaxEntries"/> 件で truncate。
    /// </summary>
    public static void Add(string appKey, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        var current = Load(appKey).ToList();
        current.RemoveAll(p => string.Equals(p, filePath, StringComparison.OrdinalIgnoreCase));
        current.Insert(0, filePath);
        Save(appKey, current.Take(MaxEntries));
    }

    /// <summary>履歴をすべて消す。利用者が「履歴をクリア」を選んだとき用。</summary>
    public static void Clear(string appKey)
    {
        try
        {
            var path = FilePathFor(appKey);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static void Save(string appKey, IEnumerable<string> entries)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = FilePathFor(appKey);
            File.WriteAllText(path, JsonSerializer.Serialize(entries.ToArray()));
        }
        catch
        {
            // ignore — MRU は付加価値で、書き込み失敗しても本体機能には影響しない。
        }
    }
}
