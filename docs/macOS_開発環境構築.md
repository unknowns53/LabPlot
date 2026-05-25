# Apple Silicon 実機への LabPlot 開発環境構築手順

まっさらな Apple Silicon Mac (M1 / M2 / M3 / M4 系) に LabPlot
(`.NET 10 / Avalonia`) のソースから動かせる開発環境をセットアップする
ための内部ドキュメント。Phase 7 Batch 6 で Windows 実機検証を済ませた
後、macOS 側 (`osx-arm64`) の実機検証 / 不具合切り分けに使う想定。

`docs/Phase7_smoke_test.md` (起動シナリオ) と
`src/LabPlot.Shell.Avalonia/README.md` (build / publish コマンド集)
を補完する位置付け。

---

## 0. 想定環境

| 項目 | 内容 |
| --- | --- |
| ハードウェア | Apple Silicon (arm64)。Intel Mac は対象外 (`osx-x64` 用追加 RID は未配布) |
| OS | macOS 14 Sonoma 以降。13 Ventura でも .NET 10 はサポート対象だが推奨は 14+ |
| ネットワーク | App Store と公式 SDK インストーラのダウンロードが必要 |
| ユーザ権限 | sudo 可能な管理者アカウント |

---

## 1. システム前提を整える

### 1.1 Xcode Command Line Tools

`git` / 各種ヘッダ / `clang` が入る最小セット。Xcode 本体は不要。

```bash
xcode-select --install
```

GUI ダイアログが出る。インストール完了後:

```bash
xcode-select -p
# /Library/Developer/CommandLineTools が返れば OK
git --version
```

### 1.2 Homebrew (任意)

`gh` (GitHub CLI) や `wget`、`tree` などをまとめて入れたい場合は
Homebrew が一番速い。Apple Silicon のデフォルト prefix は `/opt/homebrew`
で、`PATH` 設定はインストーラが案内するスクリプトをそのまま実行すれば良い。

```bash
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
# 完了時に表示される 'eval "$(/opt/homebrew/bin/brew shellenv)"' を .zprofile に追記
```

LabPlot 開発で実際に使うのは:

```bash
brew install gh         # GitHub Release 作成 / PR レビュー
brew install --cask visual-studio-code   # 任意
```

Rider を入れたい場合は JetBrains Toolbox 経由が無難 (`brew install --cask jetbrains-toolbox`)。

---

## 2. .NET 10 SDK の導入

### 2.1 推奨: 公式インストーラ (.pkg)

Apple Silicon 版バイナリ。Homebrew の `dotnet-sdk` formula は更新が
遅れがちなので、開発機では公式 .pkg を直接当てる方が確実。

1. https://dotnet.microsoft.com/download/dotnet/10.0 にアクセス
2. "SDK x.y.z" の **Arm64 / macOS** の `.pkg` をダウンロード
3. ダブルクリックでインストール (`/usr/local/share/dotnet`)
4. ターミナルを開き直して確認:

   ```bash
   dotnet --info
   # OS Platform: Darwin, Architecture: arm64 が両方表示されれば OK
   ```

### 2.2 Rosetta は不要

LabPlot は `osx-arm64` ネイティブビルドだけを配布している (`osx-x64` 配布は
将来検討)。Rosetta 2 を入れる必要はない。

### 2.3 NuGet キャッシュ

初回 `dotnet restore` で `~/.nuget/packages/` 配下に約 600 MB 落ちる。
SSD 残量に注意。

---

## 3. Git の設定

```bash
git config --global user.name  "<github_username>"
git config --global user.email "<github_email>"
git config --global init.defaultBranch main
git config --global pull.ff only
git config --global core.autocrlf input    # macOS / Linux 側は LF 維持
```

GitHub への push 認証は GitHub CLI が一番楽:

```bash
gh auth login
# HTTPS + ブラウザ認証を選択
```

このリポジトリは `https://github.com/unknowns53/LabPlot.git` を origin と
する想定。SSH を使いたい場合は `gh ssh-key add` で公開鍵を登録するか、
通常の `~/.ssh/id_ed25519` 経由で構わない。

---

## 4. リポジトリの取得

任意の作業ディレクトリで:

```bash
mkdir -p ~/Code && cd ~/Code
git clone https://github.com/unknowns53/LabPlot.git
cd LabPlot
git switch main
git log --oneline -5
```

ブランチ運用は CLAUDE.md (リポジトリ直下) のとおり:
- `main` に直接 commit しない
- 作業はすべて `feature/*` / `fix/*` / `docs/*` ブランチで

---

## 5. ビルドと初回起動

### 5.1 依存復元 + Debug ビルド

```bash
dotnet restore LabPlot.slnx
dotnet build src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj -c Debug
```

`net10.0` ターゲットなので警告は基本ゼロ、AVLN3001 (XAML resource 未公開
コンストラクタ) が 1 件出るのは既知。

### 5.2 直接実行 (Debug)

```bash
dotnet run --project src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj
```

LabPlot Portal が立ち上がり、GPC / Spectrum / DLS のタイルが表示されれば
OK。各タイルから個別モジュールを起動して、Window が描けるところまで
確認する。

### 5.3 Release single-file の publish

Windows 側と同じコマンドで `osx-arm64` ターゲットを直接ビルドできる:

```bash
dotnet publish src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj \
  -c Release -r osx-arm64 --self-contained \
  -p:PublishSingleFile=true \
  -p:Version=1.3.1
```

成果物は `src/LabPlot.Shell.Avalonia/bin/Release/net10.0/osx-arm64/publish/LabPlot.Avalonia`。
ダブルクリック起動可能だが、初回は Gatekeeper の警告が出る (詳細は 7 章)。

### 5.4 ユニットテスト

DLS Core の 179 件が PASS することだけ確認しておく:

```bash
dotnet test src/LabPlot.DLS/DlsAnalyzer.Tests/DlsAnalyzer.Tests.csproj
```

GPC / Spectrum の test も同様に走らせる場合は:

```bash
dotnet test LabPlot.slnx
```

---

## 6. エディタ環境 (任意)

### 6.1 Visual Studio Code

軽量で十分。最低限入れる拡張:

- `ms-dotnettools.csharp` (C# Dev Kit)
- `ms-dotnettools.csdevkit`
- `AvaloniaTeam.vscode-avalonia` (Avalonia XAML プレビューと IntelliSense)
- `redhat.vscode-yaml` (CI 用 yaml を触る時)
- `EditorConfig.EditorConfig`

XAML プレビューは Avalonia 拡張が `LabPlot.slnx` を読めれば自動的に
有効になる。WPF 版コードは触らないので "Microsoft 純正 XAML 拡張" 系は
入れなくて良い。

### 6.2 JetBrains Rider

ブレークポイントとリファクタリング機能を重視するなら Rider が一番安定。
Apple Silicon ネイティブビルド版 (`Rider.app`) を Toolbox 経由で入れる。
Avalonia の XAML プレビューは Rider 標準で動く (拡張不要)。

---

## 7. macOS 特有の落とし穴と対策

### 7.1 Gatekeeper / Quarantine

`dotnet publish` で作った `LabPlot.Avalonia` バイナリは未署名なので、
初回ダブルクリックすると「開発元を確認できない」ダイアログで起動拒否
される。回避策:

```bash
# 個別バイナリの quarantine 属性を外す
xattr -dr com.apple.quarantine \
  src/LabPlot.Shell.Avalonia/bin/Release/net10.0/osx-arm64/publish/LabPlot.Avalonia

# あるいは右クリック → 「開く」→ ダイアログで「開く」を押す (1 回で永続化)
```

正式に他人に配る場合は Apple Developer Program に登録 ($99/yr) して
`codesign` + `notarytool submit` が必要だが、社内 / 自分用検証なら quarantine
解除で十分。GitHub Release から落とした zip も同様の扱い。

### 7.2 AppData の物理パス

LabPlot は `RecentFilesStore` / `WindowStateStore` / `SolventPresetStore` が
`Environment.SpecialFolder.ApplicationData` 配下に JSON を吐く。.NET 10 の
macOS 実装ではここが:

```text
~/.config/LabPlot/
```

になる (Windows の `%APPDATA%\LabPlot` 相当)。`~/Library/Application Support/`
ではないことに注意。動作確認時の grep 先:

```bash
ls -la ~/.config/LabPlot/
# recent-{dls,gpc,spectrum,portal}.json
# window-{dls,gpc,spectrum,portal}.json
# dls-solvent-presets.json
```

WindowStateStore のサブモニタ切断時フォールバックを確認する時など、
ここを直接いじって試すのが速い。

### 7.3 フォント

`Window.FontFamily = "Segoe UI, Yu Gothic UI, Meiryo UI, sans-serif"` を
全 Window で指定しているが、macOS には Segoe UI / Yu Gothic UI / Meiryo UI
のいずれも存在しないので最終的に system `sans-serif` (Helvetica Neue) に
落ちる。日本語混じりのフォールバックは "Hiragino Sans" が自動的に当たる
ので追加インストール不要。Windows と完全に同じ字面にはならない点だけ
理解しておけば良い。Avalonia 11 のフォント解決はクロスプラットフォーム
互換重視で、明示指定なしでも崩れない。

### 7.4 ファイルダイアログ

Windows の `IFileOpenDialog` 相当として macOS は NSOpenPanel を呼ぶ。
Avalonia の `IStorageProvider` 経由なら抽象化されているので code 変更は
不要だが、UI の細部 (アイコン、サイドバーの並び、ホームディレクトリ
初期位置) は macOS の作法に従う。`SuggestedStartLocation` で渡したパスが
無効だと無音で `~` フォールバックする挙動なので注意。

### 7.5 ScottPlot の描画

ScottPlot 5 は macOS で SkiaSharp 経由で描く。Retina ディスプレイの DPI
スケーリングは自動だが、Windows と比べると等幅フォントの軸ラベルが
微妙に詰まって見えることがある。`PaddingBetweenTickAndAxisLabels` を
FontSize 連動で広げた fix (`07166f6`) が macOS でも効くかは実機で
確認しておく。

### 7.6 ホットリロード / dotnet watch

`dotnet watch run --project src/LabPlot.Shell.Avalonia/...` で XAML 変更
即時反映が効く。Windows 同様、`-nodeReuse:false /p:UseSharedCompilation=false`
を付けて MSBuild ghost を防ぐと安全。

---

## 8. smoke test 項目 (macOS 固有)

Windows 側で済んでいる項目に加えて、以下を実機確認する:

1. **Portal 起動**: タスクバー (Dock) アイコンが LabPlot アイコンになる
2. **3 モジュール起動**: GPC / Spectrum / DLS それぞれ Window が描ける
3. **ファイル D&D**: Finder からの D&D が各 MainWindow で効く
4. **「開く」ダイアログ**: NSOpenPanel が出てファイル選択できる
5. **MRU 永続化**: 開いたファイルが `~/.config/LabPlot/recent-{app}.json` に
   反映される + 再起動後に ComboBox で復元
6. **Window 位置永続化**: リサイズ / 移動して閉じる → 再起動で同じ位置
7. **マルチモニタ**: 外部ディスプレイがあれば、サブモニタ側に出した
   状態で閉じる → 外して再起動 → 内蔵ディスプレイ中央に戻る
8. **キーボードショートカット**: `Cmd` ではなく `Ctrl` で動くか (Avalonia の
   デフォルト挙動。Mac 慣習の `Cmd` には未対応。今後の課題候補)
9. **F1 ショートカット一覧**: 開ける
10. **ToastHost / StatusBar**: 文字化けせず日本語表示される
11. **DLS 溶媒プリセット**: AutoCompleteBox の候補表示 + 温度補間
12. **不正入力 Toast**: 数値欄に "abc" を入れて Tab で警告 Toast 表示
13. **MRU 履歴クリア**: ComboBox 右クリック → 「履歴をクリア」
14. **グラフ保存**: PNG / SVG / PDF が `~/Downloads` 等に保存できる
15. **Excel 出力**: GPC / Spectrum / DLS の Export が動く (`ClosedXML` の
    macOS 動作確認)

詳細手順は `docs/Phase7_smoke_test.md` の Windows 版を流用。

---

## 9. トラブルシューティング

| 症状 | 原因と対処 |
| --- | --- |
| `dotnet` コマンドが見つからない | PATH に `/usr/local/share/dotnet` が無い。ターミナルを開き直すか `.zprofile` に追記 |
| ビルド時に `MSB4019: ... .NETCoreApp,Version=v10.0 ... was not found` | SDK バージョンが古い。`dotnet --list-sdks` で 10.x が入っているか確認 |
| `LabPlot.Avalonia` を起動すると即終了 | quarantine 属性 (7.1 参照)。`xattr -dr com.apple.quarantine ...` |
| 日本語が `□□□` になる | フォント解決失敗。Avalonia 11 の最新版で再ビルドすると直る場合が多い |
| ScottPlot の描画が真っ黒 | SkiaSharp の依存ライブラリ未配置。`dotnet publish` で `--self-contained` を必ず付ける |
| `dotnet test` が異常終了 | `~/.dotnet/` の壊れたキャッシュ。`rm -rf ~/.dotnet/` で再生成 |
| Gatekeeper が `xattr` 後も警告 | macOS 15 以降はノータリーゼーション必須化が進む。最新 macOS では未署名バイナリの起動条件が厳しくなるので、別途 `codesign --force --deep --sign -` で ad-hoc 署名する |

---

## 10. 次に検討する項目

- `osx-x64` (Intel Mac) RID を CI に追加するか
- Apple Silicon 実機での `codesign` + `notarytool` での署名フロー
- `.app` バンドル化 (`PublishMacOSAppBundle` プロパティ or 手動 plist)
- macOS の `Cmd+O` / `Cmd+S` ショートカット対応 (現状 `Ctrl+O` 固定)
- ファイルダイアログのデフォルト保存先を `~/Documents` ベースに切り替え
  (Windows のレジストリ参照ロジックが macOS で動かないので fallback ルート)

ここに着手するときは別 PR / 別ドキュメントで切り出す。
