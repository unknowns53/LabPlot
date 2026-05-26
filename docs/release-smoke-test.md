# LabPlot リリース前 smoke test 手順

Avalonia 主流の `LabPlot.Avalonia` を Windows / macOS / Linux にリリースする前
に、`build` / `dotnet test` では検出できない「Window 描画 / OS 連携 / 実データ
ロード」をひととおり通すためのチェックリスト。装置別チェックは
[`user-guide/`](user-guide/) の各モジュール手順を補完する位置付け。

---

## 起動方法

### Windows (Debug)

```powershell
cd <repo>
dotnet run --project src\LabPlot.Shell.Avalonia\LabPlot.Shell.Avalonia.csproj
```

### Windows (Release single-file)

`bin\Release\net10.0\win-x64\publish\LabPlot.Avalonia.exe` をダブルクリック。

### macOS (Debug)

```bash
cd <repo>
dotnet run --project src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj
```

### macOS (Release `.app` バンドル)

`bin/Release/net10.0/osx-arm64/publish/LabPlot.app` を Finder から起動。
未署名配布 zip の場合は初回 `xattr -dr com.apple.quarantine /path/to/LabPlot.app`
が必要。詳細は [`macOS_開発環境構築.md`](macOS_開発環境構築.md) §7 を参照。

### Linux (Release single-file)

```bash
cd <repo>/bin/Release/net10.0/linux-x64/publish
chmod +x LabPlot.Avalonia
./LabPlot.Avalonia
```

WSL2 + WSLg なら追加設定不要。日本語フォントは `apt install fonts-noto-cjk` を
推奨。

---

## 共通 smoke test (PortalWindow / 全 MainWindow)

1. PortalWindow が起動し、3 カード (GPC / UV-Vis / DLS) と将来枠 1 セルが
   表示される
2. `CustomTitleBar` の最小化 / 最大化-復元 / 閉じる が機能する
3. 各カードを click して MainWindow が立ち上がる
4. MainWindow 内の Expander が滑らかに開閉する (Chevron 90 度回転 / fade-in)
5. CheckBox / `ColorPickerPanel` / `GraphFormatPanel` の hover / press / focus
   視覚フィードバックが乗る
6. 文字描画 (SubpixelAntialias) が読みやすい濃度で出る
7. 同じモジュールを 2 回開こうとすると既存ウィンドウがアクティブ化する

---

## DLS smoke test

データロード → 解析 → 保存の主要パスを通す。

### 起動 / Window
- [ ] PortalWindow から「DLS」カード click で MainWindow が開く
- [ ] サイドバーの 5 セクション (データファイル / 読み込み済みデータセット /
      フィット / 表示モード / グラフ出力) がすべて展開できる
- [ ] グラフ領域に「データを読み込んでください」スケルトンが表示される

### データ読み込み
- [ ] 「xlsx を開く」ボタンで DLS xlsx ファイルを選択 → ロード成功
- [ ] DatasetListBox にエントリが追加され、色付きスウォッチが表示される
- [ ] グラフに実データがプロットされる
- [ ] **ファイル D&D**: Explorer / Finder / Files から DatasetListBox に xlsx を
      ドロップ → ロード成功 (Avalonia 11.3 `DataTransfer` / `DataFormat.File` /
      `TryGetFilesAsync` 経由)

### 解析 (AnalysisWindow)
- [ ] Cumulant フィットを実行して D / Rh が表示される
- [ ] 距離分布 (CONTIN) フィットを実行して分布カーブが表示される
- [ ] 温度ランプ (Boltzmann) で Tc が表示される
- [ ] 濃度シリーズで D₀ / k_D が表示される
- [ ] AnalysisWindow が独立して最小化できる (macOS では `Show(owner)` 回避済み)

### グラフ書式
- [ ] `GraphFormatPanel` でフォント / 目盛 / 枠とグリッド / 背景 / 凡例を変更 →
      即座に反映
- [ ] `AxisRangePanel` で X/Y の min/max を直接入力 → グラフ反映、Reset で auto
      に戻る
- [ ] 凡例を non-Auto 配置に切り替え → 凡例位置が変わる
- [ ] 凡例ドラッグで 9-cell anchor に再アンカーされる

### 保存
- [ ] グラフを PNG / SVG として保存 → ファイル生成成功
- [ ] データを CSV / xlsx として出力 → ファイル生成成功
- [ ] セッション (.dlsjson) を保存 → 別起動で読み込み復元成功
- [ ] 既定値の保存 → 次回起動時に同じ書式で立ち上がる
- [ ] 「既定値に戻す」で `ConfirmDialog` が出る

### キーボード (Ctrl は mac で Cmd に読み替え)
- [ ] `Ctrl+O` / `Cmd+O` でファイルダイアログ
- [ ] `Ctrl+S` / `Cmd+S` でグラフ保存
- [ ] `Ctrl+Shift+S` / `Cmd+Shift+S` でセッション保存
- [ ] `Ctrl+R` / `Cmd+R` で軸範囲リセット
- [ ] `Ctrl+G` / `Cmd+G` でグリッド切替
- [ ] `F1` でショートカット一覧 (mac は "Cmd + …" 表示)

---

## Spectrum smoke test

機能数が多いので優先度を A (必須) / B (確認推奨) で分けてある。

### A. 起動 / 基本ロード
- [ ] PortalWindow から「UV-Vis」カード click で MainWindow が開く
- [ ] JASCO TXT ファイルを「TXT を開く」で読み込み → スペクトル表示
- [ ] DatasetListBox にエントリ追加、色付きスウォッチ表示
- [ ] **ファイル D&D**: Explorer / Finder からの drop でロード成功
- [ ] X 軸 / Y 軸切替 (波長 ↔ 波数 / 透過率 ↔ 吸光度) が反映される

### A. メタデータ / λmax / IR ピーク
- [ ] ShowMetadata トグルで実験条件が表示される
- [ ] λmax 自動検出が動作し、グラフに ▼ マーカーが乗る
- [ ] IR ピーク自動検出が動作し、帰属を編集できる
- [ ] λmax / IR ピークの手動追加 (グラフ click) と削除が機能する

### A. グラフ書式 / AxisRange
- [ ] `GraphFormatPanel` での書式変更が反映される
- [ ] `AxisRangePanel` が機能する (Reset 含む)
- [ ] X 軸反転トグルが正しく動く

### A. `CalibrationCurveWindow`
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
- [ ] 中点 / 1 次微分 / 2 次微分の 3 種で Tc が表示される
  (Boltzmann sigmoid fit は ROADMAP §2-Spectrum の今後課題)

### B. 積分領域 / Beer-Lambert 連携
- [ ] グラフ上で積分領域を drag で追加 → 半透明オーバーレイが表示される
- [ ] エッジ resize / 範囲削除 / `ItemsControl` ライブ編集が機能する
- [ ] T → A 切替時に `AbsorbanceConfirmDialog` が表示され、選択が反映される
- [ ] 積分結果の CSV / xlsx エクスポートが成功する
- [ ] `CalibrationCurveWindow` への連携 (積分領域モード) が動作する

### B. ドラッグ並び替え
- [ ] DatasetListBox 内でエントリをドラッグして順序を変更 → InsertionLine 表示
- [ ] 順序変更が plot の重ね順に反映される

---

## GPC smoke test

### A. 起動 / 基本ロード
- [ ] PortalWindow から「GPC」カード click で MainWindow が開く
- [ ] LabSolutions TXT または CSV (Time, Signal) を「ファイルを開く」で
      読み込み → クロマトグラム表示
- [ ] DatasetListBox にエントリ追加、ヘッダに solvent / detector バッジ
- [ ] **ファイル D&D**: Explorer / Finder からの drop でロード成功

### A. 検量線 / 分子量分布
- [ ] `CalibrationCurveWindow` を開いて curve を編集 → 親ウィンドウに反映
- [ ] 分子量分布モードに切替 → log scale X 軸 + Mn / Mw / Đ chip が出る
- [ ] 統計 chip の数値がコピー可能 (`SelectableTextBlock`)

### A. グラフ書式 / 保存
- [ ] `GraphFormatPanel` / `AxisRangePanel` が機能する
- [ ] PNG / SVG / CSV / xlsx エクスポートが成功する
- [ ] セッション (.gpcjson) 保存 → 復元成功

---

## OS 特有の確認

### Windows
- [ ] タスクバーに LabPlot アイコンが出る
- [ ] WindowChrome の最小化 / 最大化 / 閉じるが正常
- [ ] `%LocalAppData%\LabPlot\Logs\shell-error.log` にエラー時のログが出る

### macOS
- [ ] Dock に LabPlot アイコンが出る (`dotnet run` 経路は
      `MacAppIcon.TrySetDockIcon` 経由、`.app` バンドル経路は `Info.plist` +
      `.icns` 経由)
- [ ] メニューバーに「LabPlot ▸ About / Preferences (Cmd+,) / Hide / Quit
      (Cmd+Q)」が出る (`<NativeMenu.Menu>`)
- [ ] About ダイアログにバージョン番号 (v1.3.x) が表示される
- [ ] Cmd+O / Cmd+S / Cmd+Shift+S / Cmd+R / Cmd+G などが動く
      (`KeyboardShortcuts.HasCommandModifier` 経由)
- [ ] F1 cheat-sheet と各 Tooltip が "Cmd + …" 表記
- [ ] ファイルダイアログ既定パスが `~/Documents`
      (`FormattingDefaultsStore.GetEffectiveDefaultOutputDirectory`)
- [ ] Gatekeeper で初回ブロック → 右クリック開き or `xattr -dr` で起動できる
      (未署名 zip 配布の場合のみ)
- [ ] AppData が `~/Library/Application Support/LabPlot/` 配下に出る
- [ ] `~/Library/Application Support/LabPlot/Logs/shell-error.log` に
      エラー時のログが出る

### Linux
- [ ] WSLg / X11 でウィンドウが描画される
- [ ] フォントが Inter / Noto Sans CJK 系で日本語が豆腐にならない
- [ ] ScottPlot.Avalonia (SkiaSharp) の描画が出る
- [ ] xlsx 読み込み (ClosedXML / System.IO.Packaging) が動作する
- [ ] AppData が `~/.local/share/LabPlot/` 配下に出る

---

## トラブル時のログ確認

```powershell
# Windows
type "$env:LOCALAPPDATA\LabPlot\Logs\shell-error.log"
```

```bash
# Linux
cat ~/.local/share/LabPlot/Logs/shell-error.log

# macOS
cat ~/Library/Application\ Support/LabPlot/Logs/shell-error.log
```

`App.OnFrameworkInitializationCompleted` で UI / `AppDomain` / `TaskScheduler`
の 3 経路の未捕捉例外を append で書いている。MainWindow が開かないときは
まずこのログを見る。

---

## 検出した issue の記録

```
- issue X: 症状の一行サマリ → 発生条件 → 修正方針
```

修正は当該箇所を実装した PR と同じ branch / PR で commit、リリース直前であれば
`release/v<x.y.z>` ブランチに直接乗せる。
