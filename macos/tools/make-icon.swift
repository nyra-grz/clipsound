// Rendert das App-Icon (SF Symbol auf dunklem Squircle) als 1024px-PNG
import AppKit
let size = 1024.0
let img = NSImage(size: NSSize(width: size, height: size))
img.lockFocus()
// Raster nach Apples macOS-Icon-Vorlage: 824px Fläche, Radius ~185
let rect = NSRect(x: 100, y: 100, width: 824, height: 824)
let path = NSBezierPath(roundedRect: rect, xRadius: 185, yRadius: 185)
NSGraphicsContext.current?.saveGraphicsState()
let shadow = NSShadow()
shadow.shadowColor = NSColor.black.withAlphaComponent(0.3)
shadow.shadowBlurRadius = 20
shadow.shadowOffset = NSSize(width: 0, height: -8)
shadow.set()
NSColor.black.setFill(); path.fill()
NSGraphicsContext.current?.restoreGraphicsState()
NSGradient(colors: [NSColor(white: 0.24, alpha: 1), NSColor(white: 0.12, alpha: 1)])!.draw(in: path, angle: -90)
NSColor(white: 1, alpha: 0.08).setStroke(); path.lineWidth = 4; path.stroke()

let config = NSImage.SymbolConfiguration(pointSize: 400, weight: .semibold)
    .applying(.init(paletteColors: [NSColor.systemOrange]))
let symbol = NSImage(systemSymbolName: "speaker.wave.3.fill", accessibilityDescription: nil)!.withSymbolConfiguration(config)!
let s = symbol.size
symbol.draw(in: NSRect(x: (size - s.width) / 2, y: (size - s.height) / 2, width: s.width, height: s.height))
img.unlockFocus()
let rep = NSBitmapImageRep(data: img.tiffRepresentation!)!
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: CommandLine.arguments[1]))
