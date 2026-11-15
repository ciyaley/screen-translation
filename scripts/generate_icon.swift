import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

guard CommandLine.arguments.count >= 2 else {
    fputs("Usage: generate_icon.swift <output_icns_path>\n", stderr)
    exit(1)
}

let outputIcnsPath = CommandLine.arguments[1]

func renderIconPNGData(pxSize: Int) -> Data? {
    let colorSpace = CGColorSpaceCreateDeviceRGB()
    guard let cg = CGContext(
        data: nil,
        width: pxSize,
        height: pxSize,
        bitsPerComponent: 8,
        bytesPerRow: pxSize * 4,
        space: colorSpace,
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
    ) else { return nil }

    let s = CGFloat(pxSize)
    cg.clear(CGRect(x: 0, y: 0, width: s, height: s))

    let pad = max(1.0, s * 0.06)
    let box = CGRect(x: pad, y: pad, width: s - pad * 2.0, height: s - pad * 2.0)
    let radius = s * 0.22

    // モノクロダーク丸角ベース (#141922)
    let bgPath = CGPath(roundedRect: box, cornerWidth: radius, cornerHeight: radius, transform: nil)
    cg.addPath(bgPath)
    cg.setFillColor(CGColor(red: 0x14 / 255.0, green: 0x19 / 255.0, blue: 0x22 / 255.0, alpha: 1.0))
    cg.fillPath()

    // 微細な枠線
    cg.addPath(bgPath)
    cg.setStrokeColor(CGColor(red: 0x36 / 255.0, green: 0x3E / 255.0, blue: 0x4A / 255.0, alpha: 1.0))
    cg.setLineWidth(max(1.0, s * 0.02))
    cg.strokePath()

    // エメラルドコーナーブラケット (#4DA892)
    let margin = s * 0.22
    let arm = s * 0.19
    let left = margin
    let right = s - margin
    let bottom = margin
    let top = s - margin

    cg.setStrokeColor(CGColor(red: 0x4D / 255.0, green: 0xA8 / 255.0, blue: 0x92 / 255.0, alpha: 1.0))
    cg.setLineWidth(max(1.5, s * 0.065))
    cg.setLineCap(.round)
    cg.setLineJoin(.round)

    let corners: [(CGPoint, CGPoint, CGPoint)] = [
        (CGPoint(x: left, y: top - arm), CGPoint(x: left, y: top), CGPoint(x: left + arm, y: top)),
        (CGPoint(x: right - arm, y: top), CGPoint(x: right, y: top), CGPoint(x: right, y: top - arm)),
        (CGPoint(x: left, y: bottom + arm), CGPoint(x: left, y: bottom), CGPoint(x: left + arm, y: bottom)),
        (CGPoint(x: right - arm, y: bottom), CGPoint(x: right, y: bottom), CGPoint(x: right, y: bottom + arm))
    ]
    for (p1, p2, p3) in corners {
        cg.beginPath()
        cg.move(to: p1)
        cg.addLine(to: p2)
        cg.addLine(to: p3)
        cg.strokePath()
    }

    // 中央のテキストライン（翻訳イメージ）
    let lineH = max(1.5, s * 0.065)
    let lineX = s * 0.31
    let line1 = CGRect(x: lineX, y: s * 0.54, width: s * 0.38, height: lineH)
    let line2 = CGRect(x: lineX, y: s * 0.39, width: s * 0.26, height: lineH)
    cg.setFillColor(CGColor(red: 0xE6 / 255.0, green: 0xEA / 255.0, blue: 0xF0 / 255.0, alpha: 0.94))
    cg.addPath(CGPath(roundedRect: line1, cornerWidth: lineH * 0.5, cornerHeight: lineH * 0.5, transform: nil))
    cg.fillPath()
    cg.addPath(CGPath(roundedRect: line2, cornerWidth: lineH * 0.5, cornerHeight: lineH * 0.5, transform: nil))
    cg.fillPath()

    guard let image = cg.makeImage() else { return nil }
    let mutableData = NSMutableData()
    guard let dest = CGImageDestinationCreateWithData(mutableData, UTType.png.identifier as CFString, 1, nil) else {
        return nil
    }
    CGImageDestinationAddImage(dest, image, nil)
    guard CGImageDestinationFinalize(dest) else { return nil }
    return mutableData as Data
}

func appendBigEndian(_ value: UInt32, to data: inout Data) {
    var be = value.bigEndian
    withUnsafeBytes(of: &be) { data.append(contentsOf: $0) }
}

let chunks: [(String, Int)] = [
    ("icp4", 16),
    ("icp5", 32),
    ("icp6", 64),
    ("ic07", 128),
    ("ic08", 256),
    ("ic09", 512),
    ("ic10", 1024)
]

var body = Data()
for (tag, px) in chunks {
    guard let png = renderIconPNGData(pxSize: px) else {
        fputs("Failed to render \(tag)\n", stderr)
        exit(1)
    }
    body.append(contentsOf: tag.utf8.prefix(4))
    appendBigEndian(UInt32(8 + png.count), to: &body)
    body.append(png)
}

var icns = Data()
icns.append(contentsOf: "icns".utf8)
appendBigEndian(UInt32(8 + body.count), to: &icns)
icns.append(body)

let outURL = URL(fileURLWithPath: outputIcnsPath)
try FileManager.default.createDirectory(at: outURL.deletingLastPathComponent(), withIntermediateDirectories: true)
try icns.write(to: outURL, options: .atomic)
