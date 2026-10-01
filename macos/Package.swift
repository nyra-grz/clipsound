// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "ClipSound",
    platforms: [.macOS("15.0")],
    targets: [
        .executableTarget(name: "ClipSound", path: "Sources/ClipSound")
    ]
)
