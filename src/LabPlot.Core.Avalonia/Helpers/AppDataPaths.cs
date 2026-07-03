using System;

namespace LabPlot.Core.Avalonia.Helpers;

/// <summary>
/// <see cref="Environment.GetFolderPath"/> の ApplicationData / LocalApplicationData を
/// 差し替え可能にする薄いラッパー。環境変数 <c>LABPLOT_APPDATA_OVERRIDE</c> が非空なら
/// そのパスを Roaming 相当のルートとして使い、LocalApplicationData 相当は
/// <c>&lt;override&gt;\Local</c> を返す。未設定時は従来どおり OS 既定のフォルダを返すので、
/// 通常起動時の挙動は不変。
///
/// 用途: ユーザーガイド用スクリーンショットを Avalonia.Headless で生成するハーネスなど、
/// 実行のたびに利用者本人の AppData (MRU / window 状態 / formatting_config.json 等) を
/// 汚したくない場面で、起動前に一時ディレクトリへ差し替えるために使う。将来的にポータブル
/// 実行 (実行ファイル隣にデータを置く運用) を足す場合の差し込み口にもなる。
/// </summary>
public static class AppDataPaths
{
    private const string OverrideEnvironmentVariableName = "LABPLOT_APPDATA_OVERRIDE";

    /// <summary>
    /// <see cref="Environment.SpecialFolder.ApplicationData"/> (Roaming) 相当のパスを返す。
    /// override 環境変数が設定されていればそちらを優先する。
    /// </summary>
    public static string GetApplicationDataPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideEnvironmentVariableName);
        return string.IsNullOrEmpty(overridePath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : overridePath;
    }

    /// <summary>
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> 相当のパスを返す。
    /// override 環境変数が設定されていれば <c>&lt;override&gt;\Local</c> を返す。
    /// </summary>
    public static string GetLocalApplicationDataPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideEnvironmentVariableName);
        return string.IsNullOrEmpty(overridePath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : System.IO.Path.Combine(overridePath, "Local");
    }
}
