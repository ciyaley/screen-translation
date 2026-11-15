# ScreenTranslate

macOS（Apple Silicon）および Windows 10 / 11 向けのリアルタイム画面翻訳デスクトップアプリです。
画面の指定範囲を低負荷で監視し、空間クラスタ単位のカード表示・クリックスルー直接重畳・カーソル近傍翻訳の3モードで翻訳を表示します。

## 対応環境と使い方

* **macOS (Apple Silicon / macOS 15.0+)**:

* **Windows (Windows 10 2004+ / Windows 11 x64)**:
  * 単一実行ファイル（`.exe`）をダブルクリックで起動し、アプリ内ボタンからGPU推論エンジンとモデルをダウンロードして利用できます（詳細は `windows/README.md` を参照）。


## ライセンス（License & Third-Party Models）

* **本ソフトウェアのソースコード**: [MIT License](LICENSE)
* **翻訳モデル（PLaMo 3 Translate）**:
  本アプリはリポジトリ内にモデルの重みを含まず、利用者の端末上で Preferred Networks 社の `pfnet/plamo-3-translate-*` モデルを取得・推論します。モデルのご利用にあたっては **PLaMo Community License** が適用されます（詳細は [NOTICE](NOTICE) を参照）。
  * 個人利用・学術用途を含む非商用利用: 事前登録不要・無償
  * 商用利用（直近事業年度の収入・売上が10億円以下）: PLaMo商用利用登録フォームの送信により無償利用可能
  * 商用利用（収入・売上が10億円超）: Preferred Networks 社との別途ライセンス契約が必要
* **推論エンジン**: `ggml-org/llama.cpp`（MIT License）、`mlx-lm`（MIT License）
