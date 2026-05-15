namespace LabPlot.Core.Avalonia.Controls;

/// <summary>
/// StatusBar の severity 4 段階。WPF 版 LabPlot は単純な文字色切替 (isError bool) だったが、
/// Avalonia 主流化に合わせて Success / Warning / Error / Info の 4 値 + None (非表示) に拡張する。
/// アイコン形状と前景色は StatusBar 側で severity をキーに切り替える。
/// </summary>
public enum StatusSeverity
{
    /// <summary>状態なし。アイコンを隠して中立色のテキストのみ表示する。</summary>
    None,

    /// <summary>標準的な進捗・案内メッセージ。中立色 (slate-600)。</summary>
    Info,

    /// <summary>保存完了 / 既定値書き出し等の正常完了。緑 (emerald-700)。</summary>
    Success,

    /// <summary>外挿警告 / 推定値の信頼度低下など、注意喚起。茶 (amber-700)。</summary>
    Warning,

    /// <summary>ファイル open 失敗 / parse error / 解析失敗など。赤 (red-700)。</summary>
    Error,
}
