using System;
using System.IO;
using System.Text.Json;
using LabPlot.Core;
using Microsoft.Win32;

namespace LabPlot.Core.Wpf.Helpers;

/// <summary>
/// Shared persistence for per-app <c>GraphFormattingConfig</c> defaults that
/// live in <c>%AppData%\&lt;app&gt;\formatting_config.json</c>. Centralizes the
/// load / save / error-handling pattern that GPC, Spectrum and DLS otherwise
/// reimplement verbatim, plus the small dialog helpers that surface the saved
/// "default output directory" preference.
/// </summary>
public static class FormattingDefaultsStore
{
    /// <summary>
    /// Default JsonSerializerOptions used by every LabPlot app when reading or
    /// writing <c>formatting_config.json</c>. Apps that need custom options can
    /// pass their own override; otherwise this instance keeps formatting
    /// payloads byte-compatible across apps.
    /// </summary>
    public static JsonSerializerOptions DefaultJsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>
    /// Load formatting defaults from <paramref name="path"/>. Missing file,
    /// malformed JSON, IO errors and access errors are all treated as "fall
    /// back to a fresh <typeparamref name="T"/> instance"; <paramref name="onError"/>
    /// is invoked with a localized message so the caller can surface it via
    /// SetStatus / ShowError.
    /// </summary>
    public static T Load<T>(
        string path,
        JsonSerializerOptions? options = null,
        Action<string>? onError = null)
        where T : GraphFormattingConfigBase, new()
    {
        var defaults = new T();

        try
        {
            if (!File.Exists(path))
            {
                return defaults;
            }

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<T>(json, options ?? DefaultJsonOptions);
            if (loaded is null)
            {
                return defaults;
            }

            loaded.Normalize();
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            onError?.Invoke($"書式設定configを読み込めませんでした: {ex.Message}");
            return defaults;
        }
    }

    /// <summary>
    /// Persist formatting defaults to <paramref name="path"/>, creating the
    /// containing directory as needed. Normalizes the config first to keep the
    /// on-disk representation in canonical form.
    /// </summary>
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
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(config, config.GetType(), options ?? DefaultJsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Returns the saved default output directory if it is non-empty and still
    /// exists on disk. The existence check guards against stale paths after
    /// a folder is renamed or removed outside the app.
    /// </summary>
    public static string? GetExistingDefaultOutputDirectory(GraphFormattingConfigBase config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var dir = config.DefaultOutputDirectory;
        return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : null;
    }

    /// <summary>
    /// Pre-seed an Open / Save dialog's <see cref="FileDialog.InitialDirectory"/>
    /// with the saved default output directory, so each export starts from the
    /// folder the user last opted into. No-op if the directory is unset or no
    /// longer exists.
    /// </summary>
    public static void ApplyDefaultOutputDirectoryToDialog(
        FileDialog dialog,
        GraphFormattingConfigBase config)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        if (GetExistingDefaultOutputDirectory(config) is { } initialDirectory)
        {
            dialog.InitialDirectory = initialDirectory;
        }
    }
}
