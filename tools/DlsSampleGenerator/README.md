# DlsSampleGenerator

DLS モジュール用のデモ xlsx を生成するスタンドアロンツール。Zetasizer の xlsx エクスポート形式を模した合成データを書き出す。

## 出力先

既定では `src/LabPlot.DLS/samples/demo.xlsx` に書き出す。引数で別の出力パスを指定できる。

## 使い方

```pwsh
dotnet run --project tools/DlsSampleGenerator
# 出力先を変える場合:
dotnet run --project tools/DlsSampleGenerator -- C:\tmp\demo.xlsx
```

## 生成されるシート

- `PNIPAM_25C` — 25 °C 水中で d_h ≈ 10 nm のコイル状態を想定した単峰分布
- `PNIPAM_35C` — 35 °C（LCST 越え）で d_h ≈ 8 nm の残留コイル + 200 nm 凝集体の二峰分布

各シートには `Number / Intensity / Volume` の 3 種類の粒径分布（各 3 run）と、`Time / Correlation Coefficient (g₂-1)` の自己相関関数（3 run）が同じ Zetasizer xlsx 規約で並ぶ。物理パラメータ（散乱角 173°、波長 633 nm、水の屈折率 1.330・粘度）は Zetasizer Nano 後方散乱の典型値に合わせてある。

## なぜ生成ツールにしているか

Zetasizer の生実測データはサンプル提供元の IP に当たることが多く、リポジトリに直接 commit しづらい。合成データなら測定条件と物理が完全に再現可能なので、利用者がデモ xlsx を見て LabPlot のキュムラント解析と Stokes–Einstein 計算が `d_h ≈ 10 nm` を取り戻せることを目視で確認できる。

このツールは `LabPlot.slnx` に登録していない（publish に乗せない）。サンプルを更新したいときだけ手元で `dotnet run` して、生成された xlsx を commit する運用。

## 使われている物理

- 散乱ベクトル: q = (4π n / λ) sin(θ/2)
- 拡散係数: D = k_B T / (3π η d_h)
- 第一キュムラント: Γ = D q²（μs⁻¹ にスケール）
- 自己相関関数: g₂(τ) − 1 = β |Σ_k w_k exp(−Γ_k τ + (μ₂_k / 2) τ²)|² + noise
- 粒径分布: 各母集団を lognormal(d_h_k, σ_k) で表現、σ² = ln(1 + PdI)。Number → Volume → Intensity は Rayleigh 領域の d³ / d⁶ スケーリングで変換

`DlsAnalyzer.Core.StokesEinstein` と完全に同じ式を使っているので、生成された xlsx を LabPlot に読み込ませると、レシピで指定した d_h が数 % 以内で取り戻る。
