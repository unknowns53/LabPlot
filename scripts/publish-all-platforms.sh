#!/usr/bin/env bash
#
# scripts/publish-all-platforms.sh
#
# Windows / macOS Apple Silicon / Linux 向けの self-contained single-file portal を
# 一括で publish + zip 化する。v1.3.2 リリース時に手動で 3 回 dotnet publish を回した
# 運用を「CI でもローカルでも同じスクリプトで叩ける」形に寄せたもの。
# `scripts/publish-macos.sh` は Developer ID 署名 + 公証込みの単一 macOS RID 用、
# こちらは未署名でも構わない 3 platform 一括用と棲み分ける。
#
# 環境変数:
#   LABPLOT_VERSION   必須。バージョン文字列 (例: 1.3.3)。未指定なら git describe で
#                     直近の vX.Y.Z タグから推定する。それも失敗したら abort。
#
# 出力:
#   dist/LabPlot-v<version>-win-x64.zip
#   dist/LabPlot-v<version>-osx-arm64.zip
#   dist/LabPlot-v<version>-linux-x64.zip
#
# 使い方:
#   LABPLOT_VERSION=1.3.3 scripts/publish-all-platforms.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

VERSION="${LABPLOT_VERSION:-}"
if [ -z "$VERSION" ]; then
  VERSION="$(git -C "$REPO_ROOT" describe --tags --abbrev=0 2>/dev/null | sed 's/^v//' || true)"
fi
if [ -z "$VERSION" ]; then
  echo "ERROR: LABPLOT_VERSION not set and git describe failed" >&2
  exit 1
fi

PROJECT="$REPO_ROOT/src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj"
DIST_DIR="$REPO_ROOT/dist"
mkdir -p "$DIST_DIR"

echo "==> Settings"
echo "    Version:  $VERSION"
echo "    Project:  $PROJECT"
echo "    Output:   $DIST_DIR"
echo ""

publish_rid() {
  local rid="$1"
  local zip_name="LabPlot-v$VERSION-$rid.zip"
  local zip_path="$DIST_DIR/$zip_name"
  local publish_dir="$REPO_ROOT/src/LabPlot.Shell.Avalonia/bin/Release/net10.0/$rid/publish"

  echo "==> Publishing $rid"
  rm -rf "$publish_dir"
  dotnet publish "$PROJECT" \
    -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true \
    -p:Version="$VERSION"

  echo "==> Zipping $zip_name"
  rm -f "$zip_path"

  if [ "$rid" = "osx-arm64" ] || [ "$rid" = "osx-x64" ]; then
    # macOS RID は MSBuild の MacOSAppBundle target が .app バンドルを組むので、
    # そのバンドルだけを zip 化する。ditto があれば優先 (拡張属性 / シンボリックリンクを
    # 落とさない macOS 純正)、無ければ zip コマンドで .app ディレクトリ全体を固める。
    local app_bundle="$publish_dir/LabPlot.app"
    if [ ! -d "$app_bundle" ]; then
      echo "ERROR: $app_bundle not produced by dotnet publish" >&2
      exit 1
    fi
    if command -v ditto > /dev/null 2>&1; then
      ditto -c -k --keepParent "$app_bundle" "$zip_path"
    else
      (cd "$publish_dir" && zip -r -q "$zip_path" LabPlot.app)
    fi
  else
    # Win / Linux は publish ディレクトリの中身をそのまま zip
    (cd "$publish_dir" && zip -r -q "$zip_path" .)
  fi

  echo "    -> $zip_path"
}

publish_rid "win-x64"
publish_rid "osx-arm64"
publish_rid "linux-x64"

echo ""
echo "✓ All 3 platforms published"
ls -lh "$DIST_DIR"/LabPlot-v"$VERSION"-*.zip
