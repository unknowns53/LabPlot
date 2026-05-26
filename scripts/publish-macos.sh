#!/usr/bin/env bash
#
# scripts/publish-macos.sh
#
# Apple Silicon / Intel Mac 向けの配布用 .app バンドルを 1 コマンドで作成する。
# dotnet publish → 内部 dylib / 実行ファイルの deep codesign → ditto zip →
# notarytool submit --wait → stapler staple までを一括で行い、最終的に
# dist/LabPlot-<version>-<rid>.zip を生成する。
#
# 前提:
#   - Apple Developer Program 加入 ($99/yr)
#   - "Developer ID Application: <Name> (<TeamID>)" 証明書を keychain にインストール済み
#   - notarytool 用の app-specific password を発行済み
#     (https://appleid.apple.com/account/manage → セキュリティ → App 用パスワード)
#   - Xcode 13+ / macOS 12+ (notarytool / stapler が同梱)
#
# 必須環境変数:
#   APPLE_DEVELOPER_ID   例: "Developer ID Application: Foo Bar (ABCDE12345)"
#   APPLE_ID             Apple ID メールアドレス
#   APPLE_TEAM_ID        例: ABCDE12345
#   APPLE_APP_PASSWORD   app-specific password (xxxx-xxxx-xxxx-xxxx)
#
# 任意環境変数:
#   LABPLOT_VERSION      既定: git describe / 1.3.2
#   LABPLOT_RID          osx-arm64 (既定) or osx-x64
#
# 使い方:
#   export APPLE_DEVELOPER_ID="Developer ID Application: ..."
#   export APPLE_ID=you@example.com
#   export APPLE_TEAM_ID=ABCDE12345
#   export APPLE_APP_PASSWORD=xxxx-xxxx-xxxx-xxxx
#   scripts/publish-macos.sh
#
# 出力:
#   dist/LabPlot-<version>-<rid>.zip  (notarized + stapled)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

RID="${LABPLOT_RID:-osx-arm64}"
VERSION="${LABPLOT_VERSION:-$(git -C "$REPO_ROOT" describe --tags --abbrev=0 2>/dev/null | sed 's/^v//' || echo '1.3.2')}"

require_env() {
  local name="$1"
  if [ -z "${!name:-}" ]; then
    echo "ERROR: required env var $name not set" >&2
    echo "       See header of $0 for details" >&2
    exit 1
  fi
}

require_env APPLE_DEVELOPER_ID
require_env APPLE_ID
require_env APPLE_TEAM_ID
require_env APPLE_APP_PASSWORD

PROJECT="$REPO_ROOT/src/LabPlot.Shell.Avalonia/LabPlot.Shell.Avalonia.csproj"
PUBLISH_DIR="$REPO_ROOT/src/LabPlot.Shell.Avalonia/bin/Release/net10.0/$RID/publish"
APP_BUNDLE="$PUBLISH_DIR/LabPlot.app"
ENTITLEMENTS="$REPO_ROOT/src/LabPlot.Shell.Avalonia/macOS/entitlements.plist"
DIST_DIR="$REPO_ROOT/dist"
ZIP_PATH="$DIST_DIR/LabPlot-$VERSION-$RID.zip"

echo "==> Settings"
echo "    RID:       $RID"
echo "    Version:   $VERSION"
echo "    Project:   $PROJECT"
echo "    Bundle:    $APP_BUNDLE"
echo "    Output:    $ZIP_PATH"
echo ""

echo "==> Cleaning previous publish output"
rm -rf "$PUBLISH_DIR"

echo "==> dotnet publish ($RID, $VERSION)"
dotnet publish "$PROJECT" \
  -c Release -r "$RID" --self-contained \
  -p:PublishSingleFile=true \
  -p:Version="$VERSION"

if [ ! -d "$APP_BUNDLE" ]; then
  echo "ERROR: $APP_BUNDLE not produced by dotnet publish" >&2
  exit 1
fi

# codesign のルール:
#   - 中身 (dylib / 実行ファイル) を先に署名してから、最後に .app 全体を署名する
#     (Apple 推奨。逆順だとネスト署名の整合性検証で落ちる)
#   - --options runtime で Hardened Runtime を有効化 (notarize 必須)
#   - --timestamp で Apple のタイムスタンプサーバから刻印 (notarize 必須)
#   - --entitlements で JIT / 自前 dylib を許可
#   - --force で既存の ad-hoc 署名を上書き
echo "==> codesign inner binaries (deep, hardened runtime)"
find "$APP_BUNDLE/Contents/MacOS" -type f \( -name "*.dylib" -o -perm -u+x \) -print0 |
  while IFS= read -r -d '' f; do
    codesign --force --options runtime --timestamp \
      --entitlements "$ENTITLEMENTS" \
      --sign "$APPLE_DEVELOPER_ID" "$f"
  done

echo "==> codesign .app bundle"
codesign --force --options runtime --timestamp \
  --entitlements "$ENTITLEMENTS" \
  --sign "$APPLE_DEVELOPER_ID" "$APP_BUNDLE"

echo "==> codesign verification"
codesign --verify --strict --verbose=2 "$APP_BUNDLE"
# spctl はこの時点ではまだ notarize されていないので fail することがある。エラーは無視。
spctl --assess --type execute --verbose=2 "$APP_BUNDLE" || true

# notarytool に投げる zip は ditto (macOS 純正) で作る。zip コマンドは
# 拡張属性 / シンボリックリンクが落ちて検証エラーになる場合がある。
echo "==> Zipping for notarytool submission"
mkdir -p "$DIST_DIR"
rm -f "$ZIP_PATH"
ditto -c -k --keepParent "$APP_BUNDLE" "$ZIP_PATH"

echo "==> notarytool submit (typically 2-10 min — Apple's queue, not our build)"
xcrun notarytool submit "$ZIP_PATH" \
  --apple-id "$APPLE_ID" \
  --team-id "$APPLE_TEAM_ID" \
  --password "$APPLE_APP_PASSWORD" \
  --wait

# Notarization 完了後、staple でチケットを .app に同梱する。これで配布先 Mac が
# Apple のサーバに問い合わせできない (オフライン) 環境でも Gatekeeper が通る。
echo "==> Stapling notarization ticket"
xcrun stapler staple "$APP_BUNDLE"

echo "==> Re-zipping with stapled ticket"
rm -f "$ZIP_PATH"
ditto -c -k --keepParent "$APP_BUNDLE" "$ZIP_PATH"

echo "==> Final verification"
spctl --assess --type execute --verbose=2 "$APP_BUNDLE"
xcrun stapler validate "$APP_BUNDLE"

echo ""
echo "✓ Done."
echo "  Bundle: $APP_BUNDLE"
echo "  Zip:    $ZIP_PATH"
