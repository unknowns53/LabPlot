using System;
using System.IO;
using System.Text.Json;
using LabPlot.Core;

namespace LabPlot.Core.Avalonia.Helpers;

/// <summary>
/// Avalonia 版の formatting_config.json 永続化ヘルパ。WPF 版
/// (LabPlot.Core.Wpf.Helpers.FormattingDefaultsStore) と同シグネチャの
/// Load / Save / Clone / GetExistingDefaultOutputDirectory を提供する。
/// WPF 版にあった `ApplyDefaultOutputDirectoryToDialog(FileDialog, ...)` は
/// Avalonia の StorageProvider が <see cref="System.IO.DirectoryInfo"/> 経由で
/// initial folder を渡す API のため、呼び出し側で
/// <see cref="GetExistingDefaultOutputDirectory(GraphFormattingConfigBase)"/> を
/// 直接使う形に倒した（共通 dialog 抽象は無い）。
/// </summary>
public static class FormattingDefaultsStore
{
    public static JsonSerializerOptions DefaultJsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static T Load<T>(
        string path,
        JsonSerializerOptions? options = null,
        Action<string>? onError = null)
        where T : GraphFormattingConfigBase, new()
    {
        var defaults = new T();

        try
        {
            if (!File.Exists(path)) return defaults;

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<T>(json, options ?? DefaultJsonOptions);
            if (loaded is null) return defaults;

            loaded.Normalize();
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            onError?.Invoke($"書式設定configを読み込めませんでした: {ex.Message}");
            return defaults;
        }
    }

    public static void Save<T>(
        T config,
        string path,
        JsonSerializerOptions? options = null)
        where T : GraphFormattingConfigBase
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Normalize();

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(config, config.GetType(), options ?? DefaultJsonOptions);
        File.WriteAllText(path, json);
    }

    public static T Clone<T>(T source, JsonSerializerOptions? options = null)
        where T : GraphFormattingConfigBase, new()
    {
        ArgumentNullException.ThrowIfNull(source);
        var resolved = options ?? DefaultJsonOptions;
        var json = JsonSerializer.Serialize(source, source.GetType(), resolved);
        var clone = JsonSerializer.Deserialize<T>(json, resolved) ?? new T();
        clone.Normalize();
        return clone;
    }

    public static string? GetExistingDefaultOutputDirectory(GraphFormattingConfigBase config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var dir = config.DefaultOutputDirectory;
        return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : null;
    }
}
