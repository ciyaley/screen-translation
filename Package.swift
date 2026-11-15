// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "ScreenTranslate",
    platforms: [
        .macOS(.v15)
    ],
    products: [
        .executable(name: "ScreenTranslate", targets: ["ScreenTranslate"])
    ],
    targets: [
        .target(
            name: "ScreenTranslateCore",
            path: "Sources/ScreenTranslateCore",
            swiftSettings: [
                .swiftLanguageMode(.v5)
            ],
            linkerSettings: [
                .linkedFramework("NaturalLanguage")
            ]
        ),
        .executableTarget(
            name: "ScreenTranslate",
            dependencies: ["ScreenTranslateCore"],
            path: "Sources/ScreenTranslate",
            swiftSettings: [
                .swiftLanguageMode(.v5)
            ],
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("SwiftUI"),
                .linkedFramework("Translation"),
                .linkedFramework("NaturalLanguage"),
                .linkedFramework("ScreenCaptureKit"),
                .linkedFramework("Vision"),
                .linkedFramework("CoreMedia"),
                .linkedFramework("CoreVideo"),
                .linkedFramework("QuartzCore"),
                .linkedFramework("Carbon")
            ]
        ),
        .executableTarget(
            name: "ScreenTranslateVerify",
            dependencies: ["ScreenTranslateCore"],
            path: "Sources/ScreenTranslateVerify",
            swiftSettings: [
                .swiftLanguageMode(.v5)
            ],
            linkerSettings: [
                .linkedFramework("CoreVideo"),
                .linkedFramework("NaturalLanguage")
            ]
        )
    ]
)
