#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

SDK_PATH="$(xcrun --show-sdk-path)"
MOD_CACHE="$(pwd)/.build/ModuleCache"
OUT_BIN_DIR="$(pwd)/.build/bin"
mkdir -p "${MOD_CACHE}" "${OUT_BIN_DIR}"

echo "[1/6] Building & running core verification checks..."
swiftc \
    -O \
    -sdk "${SDK_PATH}" \
    -target arm64-apple-macosx15.0 \
    -swift-version 5 \
    -module-cache-path "${MOD_CACHE}" \
    -module-name ScreenTranslateCore \
    -emit-module -emit-library \
    Sources/ScreenTranslateCore/*.swift \
    -framework CoreVideo \
    -framework CoreGraphics \
    -framework NaturalLanguage \
    -o "${OUT_BIN_DIR}/libScreenTranslateCore.dylib" \
    -emit-module-path "${OUT_BIN_DIR}/ScreenTranslateCore.swiftmodule"

swiftc \
    -O \
    -sdk "${SDK_PATH}" \
    -target arm64-apple-macosx15.0 \
    -swift-version 5 \
    -module-cache-path "${MOD_CACHE}" \
    -I "${OUT_BIN_DIR}" -L "${OUT_BIN_DIR}" -lScreenTranslateCore \
    -framework CoreVideo \
    -framework CoreGraphics \
    -framework NaturalLanguage \
    -framework Vision \
    -Xlinker -rpath -Xlinker "${OUT_BIN_DIR}" \
    Sources/ScreenTranslateVerify/main.swift \
    -o "${OUT_BIN_DIR}/ScreenTranslateVerify"

"${OUT_BIN_DIR}/ScreenTranslateVerify"

echo "[2/6] Compiling optimized ScreenTranslate binary (-Osize)..."
APP_NAME="ScreenTranslate"
APP_DIR="build/${APP_NAME}.app"
CONTENTS_DIR="${APP_DIR}/Contents"
MACOS_DIR="${CONTENTS_DIR}/MacOS"
RESOURCES_DIR="${CONTENTS_DIR}/Resources"

rm -rf "${APP_DIR}"
mkdir -p "${MACOS_DIR}" "${RESOURCES_DIR}"

swiftc \
    -sdk "${SDK_PATH}" \
    -target arm64-apple-macosx15.0 \
    -swift-version 5 \
    -Osize -whole-module-optimization \
    -module-cache-path "${MOD_CACHE}" \
    Sources/ScreenTranslateCore/*.swift \
    Sources/ScreenTranslate/*.swift \
    -framework AppKit \
    -framework SwiftUI \
    -framework Translation \
    -framework NaturalLanguage \
    -framework ScreenCaptureKit \
    -framework Vision \
    -framework CoreMedia \
    -framework CoreVideo \
    -framework VideoToolbox \
    -framework QuartzCore \
    -framework Carbon \
    -o "${MACOS_DIR}/${APP_NAME}"

strip -x "${MACOS_DIR}/${APP_NAME}" || true

echo "[3/6] Generating AppIcon.icns, bundling manage_plamo.sh & writing Info.plist..."
cp scripts/manage_plamo.sh "${RESOURCES_DIR}/manage_plamo.sh"
chmod +x "${RESOURCES_DIR}/manage_plamo.sh"

rm -rf "$(pwd)/.build/AppIcon.iconset"
swift scripts/generate_icon.swift "${RESOURCES_DIR}/AppIcon.icns"

cat > "${CONTENTS_DIR}/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>ja</string>
    <key>CFBundleExecutable</key>
    <string>ScreenTranslate</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>CFBundleIdentifier</key>
    <string>com.corunya.screentranslate</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>ScreenTranslate</string>
    <key>CFBundleDisplayName</key>
    <string>ScreenTranslate</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>0.7.1</string>
    <key>CFBundleVersion</key>
    <string>12</string>
    <key>LSMinimumSystemVersion</key>
    <string>15.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSScreenCaptureUsageDescription</key>
    <string>指定した画面範囲のテキストを読み取りリアルタイム翻訳を表示するために画面収録を使用します。</string>
</dict>
</plist>
PLIST

echo "[4/6] Signing bundle (ad-hoc)..."
codesign --force --deep --sign - "${APP_DIR}"

echo "[5/6] Running isolated GUI component, resize, collapse & language self-test..."
SELF_TEST_BIN="${OUT_BIN_DIR}/ScreenTranslateSelfTest"
cp "${MACOS_DIR}/${APP_NAME}" "${SELF_TEST_BIN}"
codesign --force --sign - "${SELF_TEST_BIN}" >/dev/null 2>&1 || true

TEST_RUNTIME_DIR="$(pwd)/.build/test-runtime"
rm -rf "${TEST_RUNTIME_DIR}"
SCREEN_TRANSLATE_RUNTIME_DIR="${TEST_RUNTIME_DIR}" "${SELF_TEST_BIN}" --self-test-ui

echo "[6/6] Running isolated daemon + control-channel close E2E test..."
E2E_RUNTIME_DIR="$(pwd)/.build/e2e-runtime"
rm -rf "${E2E_RUNTIME_DIR}"
mkdir -p "${E2E_RUNTIME_DIR}"
SCREEN_TRANSLATE_RUNTIME_DIR="${E2E_RUNTIME_DIR}" "${SELF_TEST_BIN}" --self-test-daemon &
LAUNCHED_PID=$!

for _ in {1..40}; do
    [[ -f "${E2E_RUNTIME_DIR}/pid" ]] && break
    sleep 0.05
done

if [[ ! -f "${E2E_RUNTIME_DIR}/pid" ]]; then
    echo "FAIL: ScreenTranslate did not write runtime pid" >&2
    kill -9 "${LAUNCHED_PID}" 2>/dev/null || true
    exit 1
fi

printf "quit\n" > "${E2E_RUNTIME_DIR}/control.cmd"
wait "${LAUNCHED_PID}"
echo "E2E_LIFECYCLE_TEST_PASSED: launched (pid=${LAUNCHED_PID}) and exited cleanly."

ls -lh "${MACOS_DIR}/${APP_NAME}"
