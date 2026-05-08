# Phase 7 Avalonia 実機 smoke test 手順

Phase 7 Batch 6 の Windows 実機検証は GPC まで step 1 で 3 件の runtime issue を踏み、
すべて修正済み (commit `f8e08f0` + `03a36b3`)。残るは DLS / Spectrum の同等検証と、
Linux / macOS 環境での起動確認。本書はその際の smoke test 項目を整理したもの。
build/test では検出できない「Window 描画 / OS 連携 / 実データロード」だけに絞る。

---

## 起動方法

### Windows (debug)

```pwsh
cd D:\Task_Automation\LabPlot
dotnet run --project src\LabPlot.Shell.Avalonia\LabPlot.Shell.Avalonia.csproj
```

### Windows (release single-file)

`publish/win-x64/LabPlot.Avalonia.exe` を直接ダブルクリック。

### Linux WSL (release single-file)

WSL Ubuntu で X server 経由 (WSLg なら追加設定不要)。

```bash
cd /mnt/d/Task_Automation/LabPlot/publish/linux-x64
chmod +x LabPlot.Avalonia
./LabPlot.Avalonia
```

### macOS (release single-file)

`publish/osx-x64/LabPlot.Avalonia` を Terminal から実行。
gatekeeper で初回起動時は「右クリック → 開く」が必要な場合あり。

---

## 共通 smoke test (PortalWindow / 全 MainWindow)

Phase 7 Batch 6 step 1 で踏んだ runtime issue を再発させないための最低限の確認。

1. PortalWindow が起動し、4 セルのカード (GPC / UV-Vis / DLS / プレースホルダ) が
   表示される
2. CustomTitleBar の最小化 / 最大化-復元 / 閉じる が機能する
3. 各カードを click して MainWindow が立ち上がる (GPC は実機検証済み、DLS / Spectrum
   はここで確認)
4. MainWindow 内の Expander が滑らかに開閉する (Chevron 90 度回転 / fade-in)
5. CheckBox / ColorPickerPanel / GraphFormatPanel の hover / press / focus 視覚
   フィードバックが乗る
6. 文字描画が WPF 版と同程度の濃さで読める
   (Phase 7 Batch 6 step 2 で SubpixelAntialias 適用済み)

---

## DLS smoke test

データロード → 解析 → 保存の主要パスを通す。

### 起動 / Window
- [ ] PortalWindow から「DLS」カード click で MainWindow が開く
- [ ] サイドバーの 5 セクション (データファイル / ロード済みデータセット / フィット /
      表示モード / グラフ出力) がすべて展開できる
- [ ] グラフ領域に「データを読み込んでください」スケルトンが表示される

### データ読み込み
- [ ] 「xlsx を開く」ボタンで DLS xlsx ファイルを選択 → ロード成功
- [ ] DatasetListBox にエントリが追加され、色付きスウォッチが表示される
- [ ] グラフに実データがプロットされる
- [ ] **ファイル D&D**: Explorer から DatasetListBox に xlsx をドロップ → ロード成功
      (Phase 7 Batch 6 step 2 で ListBoxItem AllowDrop=True を立てた効果と、
      Batch 7a で `DragEventArgs.DataTransfer` / `DataFormat.File` /
      `TryGetFilesAsync` 経由に切替済みの確認)

### 解析
- [ ] Cumulant フィットを実行して D / Rh が表示される
- [ ] 距離分布フィットを実行して分布カーブが表示される
- [ ] 温度依存性プロット切り替えで X 軸が温度に変わる

### グラフ書式
- [ ] GraphFormatPanel でフォント / 目盛 / 枠とグリッド / 背景 / 凡例を変更 → 即座に反映
- [ ] AxisRangePanel で X/Y の min/max を直接入力 → グラフ反映、Reset で auto に戻る
- [ ] 凡例を non-Auto 配置に切り替え → 凡例位置が変わる
      (凡例ドラッグ移動は Phase 7 後半で対応予定なので OK)

### 保存
- [ ] グラフを PNG / SVG として保存 → ファイル生成成功
- [ ] データを CSV / xlsx として出力 → ファイル生成成功
- [ ] セッション (.dlsjson) を保存 → 別起動で読み込み復元成功
- [ ] 既定値の保存 → 次回起動時に同じ書式で立ち上がる

### キーボード
- [ ] Ctrl+O でファイルダイアログ
- [ ] Ctrl+S でグラフ保存
- [ ] Ctrl+Shift+S でセッション保存
- [ ] Ctrl+R で軸範囲リセット
- [ ] Ctrl+G でグリッド切替

---

## Spectrum smoke test

DLS よりさらに機能数が多いので、優先度を A (必須) / B (確認推奨) で分けた。

### A. 起動 / 基本ロード
- [ ] PortalWindow から「UV-Vis」カード click で MainWindow が開く
- [ ] JASCO TXT ファイルを「TXT を開く」で読み込み → スペクトル表示
- [ ] DatasetListBox にエントリ追加、色付きスウォッチ表示
- [ ] **ファイル D&D**: Explorer から DatasetListBox に TXT をドロップ → ロード成功
      (Batch 7a で `DragEventArgs.DataTransfer` / `DataFormat.File` /
      `TryGetFilesAsync` 経由に切替済みの確認)
- [ ] X 軸 / Y 軸切替 (波長 ↔ 波数 / 透過率 ↔ 吸光度) が反映される

### A. メタデータ / λmax / IR ピーク
- [ ] ShowMetadata トグルで実験条件が表示される
- [ ] λmax 自動検出が動作し、グラフに ▼ マーカーが乗る
- [ ] IR ピーク自動検出が動作し、帰属を編集できる
- [ ] λmax / IR ピークの手動追加 (グラフ click) と削除が機能する

### A. グラフ書式 / AxisRange
- [ ] GraphFormatPanel での書式変更が反映される
- [ ] AxisRangePanel が機能する (Reset 含む)
- [ ] X 軸反転トグルが正しく動く

### A. CalibrationCurveWindow
- [ ] 検量線編集ウィンドウが開く (DataGrid + Plot)
- [ ] サンプル行を追加 / 削除 / 編集 → fit 結果が更新される
- [ ] ForceOrigin / WithIntercept トグルが効く
- [ ] 濃度単位切替が正しく反映される
- [ ] Recalculate ボタンで fit が再計算される
- [ ] CSV / xlsx エクスポートが成功する
- [ ] 親ウィンドウの DataGrid (検量線サマリ) に反映される

### A. 保存
- [ ] セッション (.specjson) 保存 → 復元成功
- [ ] グラフ PNG / SVG 保存
- [ ] データ CSV / xlsx 出力
- [ ] 既定値の永続化

### B. 温度スキャン Tc 検出
- [ ] 温度スキャンモードに切替 → 温度依存プロット
- [ ] 4 種の Tc 検出メソッド (sigmoid fit 含む) を実行 → Tc が表示される

### B. 積分領域 / Beer-Lambert 連携
- [ ] グラフ上で積分領域を drag で追加 → 半透明オーバーレイが表示される
- [ ] エッジ resize / 範囲削除 / ItemsControl ライブ編集が機能する
- [ ] T → A 切替時に AbsorbanceConfirmDialog が表示され、選択が反映される
- [ ] 積分結果の CSV / xlsx エクスポートが成功する
- [ ] CalibrationCurveWindow への連携 (積分領域モード) が動作する

### B. ドラッグ並び替え
- [ ] DatasetListBox 内でエントリをドラッグして順序を変更 → InsertionLine 表示 + 反映
- [ ] 順序変更が plot の重ね順に反映される

---

## Linux / macOS 特有の確認

WSL / mac で初回起動するときに踏みやすい issue を先回り。

### Linux WSL
- [ ] WSLg / X11 forwarding でウィンドウが表示される
- [ ] フォント (Yu Gothic UI / Meiryo UI) が無いので Inter / Segoe UI 系の代替が
      使われ、日本語が豆腐にならない (要 fonts-noto-cjk 等)
- [ ] ScottPlot.Avalonia の AvaPlot が描画される
- [ ] xlsx 読み込み (System.IO.Packaging) が動作する
- [ ] ログ出力先 (`~/.local/share/LabPlot/Logs/`) が XDG 準拠で作られる

### macOS
- [ ] gatekeeper で初回ブロック → 右クリック開きで起動できる
- [ ] CustomTitleBar とシステムタイトルバーが二重にならない (mac は OS 側 chrome を
      消す側の挙動が異なる場合あり)
- [ ] ファイルダイアログ (NSOpenPanel) が機能する
- [ ] Cmd キーが Ctrl 相当として作用する (OnKeyDown の KeyModifiers.Meta 確認)

---

## トラブル時のログ確認

```pwsh
# Windows
type "$env:LOCALAPPDATA\LabPlot\Logs\shell-avalonia-error.log"
```

```bash
# Linux / macOS
cat ~/.local/share/LabPlot/Logs/shell-avalonia-error.log
```

App.OnFrameworkInitializationCompleted で 3 経路 (UI / AppDomain /
TaskScheduler) の未捕捉例外を appendle に書いている (Batch 2 で実装)。
Avalonia 起動時に MainWindow が開かない場合は、まずこのログを確認する。

---

## チェック後のフィードバック

検出した issue は以下の形式で記録する (Batch 6 step 1 でも同形式)。

```
- 実機 issue X: 症状の一行サマリ → 発生条件 → 修正方針
```

Phase 7 Batch 6 step 1 で踏んだ 3 件 (Expander 2 段 /template/ ネスト /
ColorPicker [Content] 罠 / 手動 InitializeComponent vs Generators) と
同じパターンで、build / test では検出できなかった runtime issue が
DLS / Spectrum でも出る可能性が高い。発見次第 commit する。
