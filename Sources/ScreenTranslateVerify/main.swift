import Foundation
import CoreVideo
import CoreGraphics
import Vision
import ScreenTranslateCore

private func check(_ condition: @autoclosure () -> Bool, _ message: String) {
    if !condition() {
        fputs("FAIL: \(message)\n", stderr)
        exit(1)
    }
}

private func fillBuffer(_ pb: CVPixelBuffer, pattern: (Int, Int) -> UInt8) {
    CVPixelBufferLockBaseAddress(pb, [])
    defer { CVPixelBufferUnlockBaseAddress(pb, []) }
    let w = CVPixelBufferGetWidth(pb)
    let h = CVPixelBufferGetHeight(pb)
    let bpr = CVPixelBufferGetBytesPerRow(pb)
    let base = CVPixelBufferGetBaseAddress(pb)!.assumingMemoryBound(to: UInt8.self)
    for y in 0..<h {
        let row = base.advanced(by: y * bpr)
        for x in 0..<w {
            let v = pattern(x, y)
            row[x * 4 + 0] = v
            row[x * 4 + 1] = v
            row[x * 4 + 2] = v
            row[x * 4 + 3] = 255
        }
    }
}

// 1. タイプライター安定待ちゲート＋ファジーOCR揺れ吸収＋長文即時commit＋IMPORTANT_UPDATE保全の検証
var gate = TextStabilityGate(requiredStableFrames: 2)
check(gate.feed("The northern") == .provisional("The northern"), "短文1フレーム目はprovisional")
check(gate.feed("The northern gate is") == .provisional("The northern gate is"), "伸長中はprovisional")
check(gate.feed("The northern gate is sealed.") == .provisional("The northern gate is sealed."), "到達直後はprovisional")
check(gate.feed("The northern gate is seaIed.") == .commit("The northern gate is seaIed."), "1文字OCR揺れがあっても2フレーム目で確実にcommitすること")
check(gate.feed("The northern gate is sealed.") == .ignore, "確定後の1文字揺れ・句読点揺れでは無駄な再翻訳を起こさないこと")
check(gate.feed("The northern gate is not sealed.") == .provisional("The northern gate is not sealed."), "否定語の追加(IMPORTANT_UPDATE)は確実に検知")
check(gate.feed("The northern gate is not sealed.") == .commit("The northern gate is not sealed."), "否定語更新の2フレーム目でcommit")

let steamParagraph = "The ancient sacred tree rooted in the Kingdom of Karen, bestowing blessings upon people, is the faith of the entire nation."
check(gate.feed(steamParagraph, averageConfidence: 0.91) == .commit(steamParagraph), "60文字以上の段落は初回フレームで即commitすること")
let wobbledParagraph = "The ancient sacred tree rooted in the Kingdom of Karen, bestowing blessings upon peop1e, is the faith of the entire nation"
check(gate.feed(wobbledParagraph, averageConfidence: 0.89) == .ignore, "長文確定後のリモートデスクトップ圧縮OCR揺れは無視すること")

// 2. 上限付きLRUキャッシュ検証
var cache = BoundedLRUCache<String, String>(capacity: 8)
for i in 0..<10 {
    cache.set("k\(i)", value: "v\(i)")
}
check(cache.get("k0") == nil && cache.get("k1") == nil, "古いエントリが破棄されること")
check(cache.get("k9") == "v9", "最新エントリが保持されること")

// 3. 512点輝度比較＋dHash 差分検出・ベンチマーク検証
var pb1: CVPixelBuffer?
var pbNoise: CVPixelBuffer?
var pb2: CVPixelBuffer?
var pbHD: CVPixelBuffer?
CVPixelBufferCreate(kCFAllocatorDefault, 64, 32, kCVPixelFormatType_32BGRA, nil, &pb1)
CVPixelBufferCreate(kCFAllocatorDefault, 64, 32, kCVPixelFormatType_32BGRA, nil, &pbNoise)
CVPixelBufferCreate(kCFAllocatorDefault, 64, 32, kCVPixelFormatType_32BGRA, nil, &pb2)
CVPixelBufferCreate(kCFAllocatorDefault, 960, 540, kCVPixelFormatType_32BGRA, nil, &pbHD)
guard let buf1 = pb1, let bufNoise = pbNoise, let buf2 = pb2, let bufHD = pbHD else {
    fputs("FAIL: PixelBuffer creation\n", stderr)
    exit(1)
}
fillBuffer(buf1, pattern: { x, _ in UInt8(clamping: x * 4) })
fillBuffer(bufNoise, pattern: { x, y in UInt8(clamping: x * 4 + ((x + y) % 2 == 0 ? 2 : -1)) })
fillBuffer(buf2, pattern: { x, _ in UInt8(clamping: (63 - x) * 4) })
fillBuffer(bufHD, pattern: { x, y in UInt8(clamping: (x + y) & 0xFF) })
let d1 = FrameHash.compute(pixelBuffer: buf1)!
let dNoise = FrameHash.compute(pixelBuffer: bufNoise)!
let d2 = FrameHash.compute(pixelBuffer: buf2)!
check(FrameHash.isEffectivelyUnchanged(d1, dNoise), "微小な映像圧縮ノイズではOCRを発火させないこと")
check(!FrameHash.isEffectivelyUnchanged(d1, d2), "画面内容が変化した場合は確実に検知すること")

let benchStart = CFAbsoluteTimeGetCurrent()
var sink: UInt64 = 0
for _ in 0..<2000 {
    if let d = FrameHash.compute(pixelBuffer: bufHD) {
        sink ^= d.lo
    }
}
let perFrameMicros = ((CFAbsoluteTimeGetCurrent() - benchStart) * 1_000_000.0) / 2000.0
check(perFrameMicros < 200.0 && sink != 0xDEAD_BEEF_CAFE_BABE, "32BGRA 512点輝度比較が極小負荷(<200us)であること (got \(perFrameMicros)us)")

// 3b. FrameChangeGate（静止中はOCRゼロ、画面移動中は即時判定）
var changeGate = FrameChangeGate()
let dFrameA = FrameHash.Digest(lo: 0x0000_0000_0000_0001, hi: 0, meanLuma: 40)
let dMoving1 = FrameHash.Digest(lo: 0x0000_FFFF_0000_0000, hi: 0, meanLuma: 60)
let dMoving2 = FrameHash.Digest(lo: 0xFFFF_0000_0000_FFFF, hi: 0x00FF, meanLuma: 85)
let dSettled = FrameHash.Digest(lo: 0xFFFF_0000_0000_FFFF, hi: 0x00FE, meanLuma: 86)
check(changeGate.shouldRunOCR(for: dFrameA) == true, "初回フレームは即実行")
check(changeGate.shouldRunOCR(for: dFrameA) == false, "静止継続中はOCRゼロ")
check(changeGate.shouldRunOCR(for: dMoving1) == true, "画面が動いた1フレーム目から即座にOCR判定を実行すること")
check(changeGate.shouldRunOCR(for: dMoving2) == true, "連続して動いている最中も遅延なくOCR判定を実行すること")
check(changeGate.shouldRunOCR(for: dSettled) == false, "動きが止まり実質同一になったフレームはOCRゼロに戻ること")

// 4. Vision OCR クラッシュ防止不変条件テスト
let probeReq = VNRecognizeTextRequest()
probeReq.revision = VNRecognizeTextRequestRevision3
probeReq.recognitionLevel = .accurate
let osSupported = (try? probeReq.supportedRecognitionLanguages()) ?? []
check(!osSupported.isEmpty, "VNRecognizeTextRequest(.accurate) のサポート言語一覧が取得できること")
let osSupportedSet = Set(osSupported)

for opt in TranslationLanguage.sourceOptions {
    let spec = ResolvedVisionOCRSpec.resolve(sourceLangCode: opt.code, supportedByOS: osSupported)
    check(spec.pixelFormat == kCVPixelFormatType_32BGRA, "\(opt.code): 32BGRA ピクセルフォーマットであること")
    check(spec.usesFastLevel == false, "\(opt.code): .fast レベルが絶対に有効化されないこと")
    check(spec.usesLanguageCorrection == true, "\(opt.code): 言語補正が有効であること")
    check(!spec.recognitionLanguages.isEmpty, "\(opt.code): recognitionLanguages が絶対に空配列にならないこと")
    check(spec.recognitionLanguages.allSatisfy { osSupportedSet.contains($0) }, "\(opt.code): 全言語がOSサポート集合に含まれること")
    check(spec.automaticallyDetectsLanguage == (opt.code == "auto"), "\(opt.code): 自動言語判定フラグが一致すること")
}

// 5. 第1段 OCRSpatialFilter（複数クラスタ座標保持・見出し横唐草 "co" 除去・折り返し行結合）の検証
let multiCharacterScene: [OCRTextObservation] = [
    // 見出し + 右横の金色唐草装飾が "co" と誤読されたケース
    OCRTextObservation(text: "Spirit of the Sacred Tree", confidence: 0.95, normX: 0.20, normY: 0.82, normWidth: 0.36, normHeight: 0.06),
    OCRTextObservation(text: "co", confidence: 0.78, normX: 0.60, normY: 0.82, normWidth: 0.04, normHeight: 0.05),
    // 上段キャラ説明（2行折り返し）
    OCRTextObservation(text: "The ancient tower", confidence: 0.94, normX: 0.10, normY: 0.62, normWidth: 0.42, normHeight: 0.04),
    OCRTextObservation(text: "awaits your return.", confidence: 0.92, normX: 0.10, normY: 0.56, normWidth: 0.44, normHeight: 0.04),
    // 下段別キャラ説明（縦に離れた独立クラスタ）
    OCRTextObservation(text: "A nun deeply devoted to the sacred tree.", confidence: 0.93, normX: 0.10, normY: 0.18, normWidth: 0.60, normHeight: 0.04),
    // 周辺ノイズ
    OCRTextObservation(text: "120 FPS", confidence: 0.96, normX: 0.82, normY: 0.90, normWidth: 0.14, normHeight: 0.04),
    OCRTextObservation(text: "AUTO SKIP", confidence: 0.91, normX: 0.80, normY: 0.04, normWidth: 0.16, normHeight: 0.04)
]
let extracted = OCRSpatialFilter.extractPrimaryText(from: multiCharacterScene, sourceLangCode: "auto", targetLangCode: "ja")
check(extracted.clusters.count == 3, "見出し・上段本文・下段本文の3クラスタに分離されること (got \(extracted.clusters.count))")
check(extracted.clusters[0].text == "Spirit of the Sacred Tree", "見出し右横の装飾誤読 'co' が除去されること (got: \(extracted.clusters[0].text))")
check(extracted.clusters[1].text == "The ancient tower awaits your return.", "折り返し行がスペース結合されること (got: \(extracted.clusters[1].text))")
check(extracted.clusters[2].text == "A nun deeply devoted to the sacred tree.", "下段クラスタが独立保持されること")

// 6. ClusterGeometry & ClusterTranslationPlanner（座標変換・既訳0ms引き継ぎ・カーソル近傍優先・スポット抽出）の検証
let capRect = CGRect(x: 200, y: 100, width: 1000, height: 800)
let gRectBottom = ClusterGeometry.globalRect(for: extracted.clusters[2], in: capRect)
check(gRectBottom.minX >= 299 && gRectBottom.minX <= 301, "クラスタX座標がグローバル座標へ正確に変換されること")
check(gRectBottom.minY < 320, "下段クラスタがAppKit左下原点の画面下側座標へ変換されること")

// マウスが下段クラスタ(c2)の上にある場合:
let mouseOnBottom = CGPoint(x: 400, y: 250) // topY換算 = (900 - 250)/800 = 0.8125 (下段クラスタ付近)
let normPt = ClusterGeometry.normalizedCursorPoint(globalMousePoint: mouseOnBottom, in: capRect)
check(normPt != nil, "領域内マウス座標が正規化されること")

// スポットモードでは下段クラスタ1件のみが抽出されること
let spotSelected = ClusterTranslationPlanner.selectActiveClusters(
    from: extracted.clusters,
    mode: .cursorSpot,
    normalizedCursor: normPt
)
check(spotSelected.count == 1 && spotSelected[0].text == "A nun deeply devoted to the sacred tree.", "近傍のみモードでカーソル直下のクラスタ1件が選ばれること")

// カード/重畳モードで未翻訳が複数ある場合、カーソル下のクラスタ(index 2)が最優先(先頭)になること
let prioritized = ClusterTranslationPlanner.prioritizedIndices(
    clusters: extracted.clusters,
    untranslatedIndices: [0, 1, 2],
    normalizedCursor: normPt
)
check(prioritized.first == 2, "マウスカーソルが乗っている未翻訳クラスタが最優先で翻訳されること (got \(prioritized))")

// 既訳キャッシュのファジーヒット検証（スクロールで1文字揺れても0msで引き継げること）
let cachedHistory = [
    ClusterTranslationPlanner.CachedEntry(
        sourceText: "A nun deeply devoted to the sacred tree.",
        translatedText: "聖樹に深く帰依する修道女。"
    )
]
let slightlyWobbledCluster = ExtractedTextCluster(
    text: "A nun deeply devoted to the sacred tree",
    averageConfidence: 0.90,
    minX: 0.10, maxX: 0.70, topY: 0.35, bottomY: 0.39 // スクロールしてY位置が移動
)
check(
    ClusterTranslationPlanner.findCachedTranslation(for: slightlyWobbledCluster, in: cachedHistory) == "聖樹に深く帰依する修道女。",
    "スクロール移動後のクラスタがLLM再実行なしで即座にキャッシュヒットすること"
)

// インプレースチップへのカーソル接近退避判定
check(
    ClusterGeometry.shouldFadeChipForCursor(
        chipGlobalRect: CGRect(x: 300, y: 200, width: 240, height: 48),
        globalMousePoint: CGPoint(x: 350, y: 220)
    ) == true,
    "カーソル重畳時はインプレースチップが透過退避すること"
)
check(
    ClusterGeometry.shouldFadeChipForCursor(
        chipGlobalRect: CGRect(x: 300, y: 200, width: 240, height: 48),
        globalMousePoint: CGPoint(x: 100, y: 500)
    ) == false,
    "カーソル離脱時はインプレースチップが不透明表示を維持すること"
)

// 7. 第2・第3段 TranslatabilityGate & PLaMo PFML3 検証
check(TranslatabilityGate.evaluate(rawText: "HP 450 / 500", selectedSourceCode: "auto", targetCode: "ja") == .skip(reason: .numericOrStatsOnly), "HP数値はスキップ")
let sampleProtoURL = "https" + "://app.local/v1/status"
check(TranslatabilityGate.evaluate(rawText: sampleProtoURL, selectedSourceCode: "auto", targetCode: "ja") == .skip(reason: .codeOrIdentifier), "URLはスキップ")
check(TranslatabilityGate.evaluate(rawText: "すでに日本語で書かれた文章です。", selectedSourceCode: "auto", targetCode: "ja") == .skip(reason: .alreadyTargetLanguage), "翻訳先と同一言語はスキップ")

let pfmlPrompt = PFML3PromptBuilder.buildPrompt(
    sourceText: "Hekate attends the festival with Ela.",
    sourceLangCode: "en",
    targetLangCode: "ja",
    history: []
)
check(pfmlPrompt.hasPrefix("<|plamo:tag|>dataset<|plamo:msg|>translation"), "PFML3プロンプトが構築されること")

print(String(format: "ALL VERIFICATION CHECKS PASSED (32BGRA dHash=%.2fus, MULTI-CLUSTER PLANNER, IN-PLACE GEOMETRY, AND PLAMO 3 PFML3)", perFrameMicros))
