// swift-tools-version: 6.0
import PackageDescription

let package = Package(
  name: "Zone",
  platforms: [.macOS("26.0")],
  products: [.executable(name: "Zone", targets: ["Zone"])],
  targets: [
    .systemLibrary(name: "CSQLite"),
    .target(name: "ZoneKit", dependencies: ["CSQLite"]),
    .executableTarget(name: "Zone", dependencies: ["ZoneKit"]),
    .testTarget(name: "ZoneKitTests", dependencies: ["ZoneKit"], resources: [.process("Fixtures")]),
  ]
)
