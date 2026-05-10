using System;
using System.Collections.Generic;
using DlsAnalyzer.Core;

namespace LabPlot.DLS.Avalonia;

/// <summary>
/// AnalysisWindow が親 (MainWindow) を覗き込むための読み取り API + 通知 event。
/// 親子の循環参照を避けつつ、子から「グラフを再描画してほしい」「分布の種類を切り替えてほしい」
/// と要求する経路を提供する。将来 DlsAnalysisContext (POCO) に置き換えやすいよう、
/// 親 MainWindow が partial で実装する形を取る。
/// </summary>
public interface IDlsAnalysisHost
{
    IReadOnlyList<DlsDatasetItem> DatasetItems { get; }
    IReadOnlyList<DlsDataset> SelectedDatasets { get; }
    int ActiveItemIndex { get; }
    DistributionMode SelectedMode { get; }

    /// <summary>子側で CONTIN の設定 (重み / α) が変わったときなど、現在の SelectedMode に基づき親グラフを再描画させる。</summary>
    void RequestPlotRefresh();

    /// <summary>子の「グラフとして見る」ボタン押下時。親側 DistributionTypeComboBox を切替 + RefreshPlot。</summary>
    void RequestShowAsGraph(DistributionMode mode);

    /// <summary>xlsx 読み込み / セッション復元 / メタデータ変更 / dataset 並び替え時に raise。子は現在 Tab を再計算する。</summary>
    event EventHandler AnalysisDataChanged;

    /// <summary>ListBox の選択変更で active item index が変わったときに raise。</summary>
    event EventHandler ActiveItemChanged;
}
