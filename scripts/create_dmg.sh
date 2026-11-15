#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

APP_PATH="$(pwd)/build/ScreenTranslate.app"
if [[ ! -d "${APP_PATH}" ]]; then
    bash scripts/bundle_app.sh
fi

mkdir -p release
STAGING_DIR="$(pwd)/.build/dmg_staging"
rm -rf "${STAGING_DIR}"
mkdir -p "${STAGING_DIR}"

cp -R "${APP_PATH}" "${STAGING_DIR}/ScreenTranslate.app"
ln -s /Applications "${STAGING_DIR}/Applications"

cat > "${STAGING_DIR}/はじめにお読みください.txt" <<'TXT'
ScreenTranslate for macOS (Apple Silicon)

【インストール方法】
1. 「ScreenTranslate.app」を「Applications」フォルダへドラッグ＆ドロップしてください。
2. アプリケーションフォルダから ScreenTranslate をダブルクリックして起動します。
   ※ 初回起動時に開発元未確認の警告が出た場合は、アプリを右クリック →「開く」から許可してください。
3. 監視開始時に「画面収録」権限の許可ダイアログが表示されたら許可してください。

【翻訳エンジンについて】
- 既定の「PLaMo 2B」は初回利用時に ~/Library/Application Support/ScreenTranslate 配下へ
  推論環境とモデルを自動セットアップします。
- より軽量・即時翻訳したい場合はヘッダーのエンジン切替から「PLaMo 0.8B」または「OS標準翻訳」を選択できます。

【主なショートカット】
- ⌥⇧R : 監視範囲をドラッグ選択
- ⌥⇧T : 監視の開始 / 停止
- ⌥⇧M : 表示モード切替（カード / 直接重畳 / 近傍のみ）
- ⌘K / ⌥⇧L : 38pxバーへ折りたたみ / 展開
- ⌘Q / ⌥⇧Q : 終了（バックグラウンドの推論サーバーも自動終了）
TXT

OUT_DMG="$(pwd)/release/ScreenTranslate-macOS-arm64.dmg"
rm -f "${OUT_DMG}"

hdiutil create \
    -volname "ScreenTranslate" \
    -srcfolder "${STAGING_DIR}" \
    -ov \
    -format UDZO \
    -imagekey zlib-level=9 \
    "${OUT_DMG}"

rm -rf "${STAGING_DIR}"
ls -lh "${OUT_DMG}"
