using System;
using Avalonia.Controls;
using LabPlot.Core.Avalonia.Helpers;

namespace LabPlot.DLS.Avalonia;

/// <summary>
/// AnalysisWindow の解析 4 セクション (Cumulant / Ramp / Concentration / Inversion) で繰り返されていた
/// 「結果 TextBlock 群を placeholder に戻す」「ステータス文字列を表示/非表示する」ボイラープレートを
/// 束ねる軽量プレゼンタ。挙動は従来のセクション別ヘルパー (ResetXxxDisplay / ShowXxxStatus /
/// HideXxxStatus) と完全に同じで、置き換え目的の純粋リファクタ用に用意した。
/// </summary>
internal sealed class AnalysisSectionView
{
    private readonly TextBlock _statusText;
    private readonly TextBlock[] _resultTexts;

    public AnalysisSectionView(TextBlock statusText, params TextBlock[] resultTexts)
    {
        _statusText = statusText ?? throw new ArgumentNullException(nameof(statusText));
        _resultTexts = resultTexts ?? Array.Empty<TextBlock>();
    }

    /// <summary>結果 TextBlock 群を placeholder ("—" 既定) に戻し、走っているアニメは止める。</summary>
    public void Reset(string placeholder = "—")
    {
        foreach (var t in _resultTexts)
        {
            NumberCountUp.Cancel(t, placeholder);
        }
    }

    /// <summary>ステータス行を表示する。</summary>
    public void ShowStatus(string message)
    {
        _statusText.Text = message;
        _statusText.IsVisible = true;
    }

    /// <summary>ステータス行を非表示にして空文字に戻す。</summary>
    public void HideStatus()
    {
        _statusText.Text = string.Empty;
        _statusText.IsVisible = false;
    }

    /// <summary>早期 return 用: 結果を placeholder に戻しつつ失敗理由をステータスに出す。</summary>
    public void FailWith(string message)
    {
        Reset();
        ShowStatus(message);
    }
}
