# ScreenTranslate

macOS（Apple Silicon）および Windows 10 / 11 向けのリアルタイム画面翻訳デスクトップアプリです。
画面の指定範囲を低負荷で監視し、空間クラスタ単位の「カード」表示・クリックスルー「直接重畳」・カーソル「近傍のみ」の3モードで翻訳を表示します。

## 主な特徴

* **静止時ほぼゼロ負荷 & スクロール即時追従**: 512点輝度差分検知により静止画面ではOCR・翻訳をスキップし、スクロール時は翻訳済みテキストを **0ms キャッシュ** で維持しながら新規箇所をカーソルに近い順で翻訳します。
* **3つの表示モード**:
  * **カード**: まとまりごとに番号付きカード（`01`, `02`）を表示。カードをホバーすると画面側の該当箇所をエメラルド枠でハイライトします。
  * **直接重畳**: ゲームやアプリの原文座標の上にクリックスルー翻訳チップを重ねて表示します（マウスカーソルを近づけると自動で `Opacity = 0.12` に透過し、背後の操作を妨げません）。
  * **近傍のみ**: マウスカーソル直下・近傍の1まとまりだけを翻訳します。
* **翻訳エンジン選択**: ローカル完結の高精度翻訳 **PLaMo 3 Translate**（`0.8B` / `2B` / `8B`）のほか、macOS標準翻訳や外部ローカルAPI（LM Studio / Ollama 互換）にも対応しています。

---

## macOS版（Apple Silicon / macOS 15.0 以降）

### インストールと起動
1. `release/ScreenTranslate-macOS-arm64.zip` をダウンロードして展開し、`ScreenTranslate.app` を「アプリケーション」フォルダへ移動します（`.dmg` 形式で配布する場合は `bash scripts/create_dmg.sh` を実行して `release/ScreenTranslate-macOS-arm64.dmg` を作成できます）。
2. `ScreenTranslate.app` をダブルクリックして起動します。
   * 初回起動時に「開発元を確認できないため開けません」と表示された場合は、アプリを **右クリック（Control + クリック）→「開く」** から起動を許可してください。
3. 初回に監視範囲を選択すると **「画面収録」権限** の許可を求められるため、「システム設定 → プライバシーとセキュリティ → 画面収録」で `ScreenTranslate` をオンにしてください。
4. 既定の **PLaMo 2B** 選択時は、初回利用時に `~/Library/Application Support/ScreenTranslate` 配下へ Apple Silicon 向け推論環境（MLX-4bit）とモデルが自動セットアップされます。軽量・即時応答を優先したい場合は、ヘッダーから **`PLaMo 0.8B`** または **`OS標準`** へ切り替えられます。

### macOS版ショートカット
* `⌥⇧R`: 監視範囲をドラッグ選択
* `⌥⇧T`: 監視の開始 / 一時停止
* `⌥⇧M`: 表示モード切替（`カード` / `直接重畳` / `近傍のみ`）
* `⌥⇧L` / `⌘K`: 38pxバーへ折りたたみ / 展開（最小幅 `360pt`・幅 `520pt` 未満で自動2段ヘッダー）
* `⌥⇧W`: ウィンドウの一時非表示 / 再表示
* `⌥⇧Q` / `⌘Q` / `⌘W` / `✕`: 終了（バックグラウンドのPLaMoサーバーも自動終了）

### ソースからのビルド（macOS）
* `bash scripts/bundle_app.sh`（単体テスト・UI自己検証・`build/ScreenTranslate.app` 生成）
* `bash scripts/create_dmg.sh`（`release/ScreenTranslate-macOS-arm64.dmg` 生成）

---

## Windows版（Windows 10 2004以降 / Windows 11 x64）

* `windows/release/ScreenTranslate-Windows-x64.zip` を展開し、単一実行ファイル `ScreenTranslate.exe` をダブルクリックで起動します（`.NET` の事前インストール不要。詳細は `windows/README.md` を参照）。
* 初回起動時にウィンドウ内のボタン（`PLaMo 2B` 約1.81 GB / `PLaMo 0.8B` 約570 MB）をクリックすると、GPU推論エンジンとモデルがアプリ内プログレスバー付きで自動ダウンロードされます。
* 右上の **`⚙` ボタン →「デスクトップ＆スタートメニューに登録」** からショートカット登録でき、GPUランタイム（Vulkan / NVIDIA CUDA）の切り替えも可能です。
* Windows上での再ビルド: `powershell -ExecutionPolicy Bypass -File windows\scripts\bundle_app.ps1`

---

## ライセンス（License & Third-Party Models）

* **本ソフトウェアのソースコード**: [MIT License](LICENSE)
* **翻訳モデル（PLaMo 3 Translate）**:
  本アプリはリポジトリ内にモデルの重みを含まず、利用者の端末上で Preferred Networks 社の `pfnet/plamo-3-translate-*` モデルを取得・推論します。モデルのご利用にあたっては **PLaMo Community License** が適用されます（詳細は [NOTICE](NOTICE) を参照）。
  * 個人利用・学術用途を含む非商用利用: 事前登録不要・無償
  * 商用利用（直近事業年度の収入・売上が10億円以下）: PLaMo商用利用登録フォームの送信により無償利用可能
  * 商用利用（収入・売上が10億円超）: Preferred Networks 社との別途ライセンス契約が必要
* **推論エンジン**: `ggml-org/llama.cpp`（MIT License）、`mlx-lm`（MIT License）
