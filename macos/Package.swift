// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "MemeSoundboard",
    platforms: [.macOS("15.0")],
    targets: [
        .executableTarget(name: "MemeSoundboard", path: "Sources/MemeSoundboard")
    ]
)
