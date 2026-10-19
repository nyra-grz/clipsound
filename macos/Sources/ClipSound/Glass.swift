import SwiftUI

// Liquid Glass ab macOS 26. Auf macOS 15 bleibt das Layout gleich, nur mit Material statt Glas.

extension View {
    /// Glas-Fläche hinter dem Inhalt (Bedienelemente, die über dem Inhalt schweben).
    /// `clear`: durchsichtigeres Glas für kleine Elemente auf farbigem Inhalt (mit weißer Schrift)
    @ViewBuilder
    func glassSurface<S: Shape>(_ shape: S, tint: Color? = nil, clear: Bool = false, interactive: Bool = false) -> some View {
        if #available(macOS 26.0, *) {
            glassEffect((clear ? Glass.clear : Glass.regular).tint(tint).interactive(interactive), in: shape)
        } else if clear {
            background(shape.fill(Color.black.opacity(0.22)))
        } else {
            background {
                ZStack {
                    shape.fill(.regularMaterial)
                    if let tint { shape.fill(tint.opacity(0.35)) }
                }
                .overlay(shape.stroke(Color.white.opacity(0.1), lineWidth: 1))
                .shadow(color: .black.opacity(0.18), radius: 12, y: 4)
            }
        }
    }

    /// Glas-Knopf (prominent = in Akzentfarbe gefüllt)
    @ViewBuilder
    func glassButton(prominent: Bool = false) -> some View {
        if #available(macOS 26.0, *) {
            if prominent { buttonStyle(.glassProminent) } else { buttonStyle(.glass) }
        } else {
            if prominent { buttonStyle(.borderedProminent) } else { buttonStyle(.bordered) }
        }
    }

    /// Kennung, damit Glas-Formen im selben Container ineinander fließen
    @ViewBuilder
    func glassID(_ id: String, in namespace: Namespace.ID) -> some View {
        if #available(macOS 26.0, *) { glassEffectID(id, in: namespace) } else { self }
    }

    /// Leiste am Rand, unter der der Inhalt weich ausblendet (macOS 26), sonst ein normaler Einschub
    @ViewBuilder
    func edgeBar<Content: View>(_ edge: VerticalEdge, @ViewBuilder content: () -> Content) -> some View {
        if #available(macOS 26.0, *) {
            safeAreaBar(edge: edge, spacing: 0, content: content)
        } else {
            safeAreaInset(edge: edge, spacing: 0, content: content)
        }
    }

    /// Weiche Kante, wo Inhalt unter Toolbar und Leisten verschwindet
    @ViewBuilder
    func softScrollEdges() -> some View {
        if #available(macOS 26.0, *) { scrollEdgeEffectStyle(.soft, for: .all) } else { self }
    }
}

/// Fasst Glas-Formen zusammen, damit sie sich wie Flüssigkeit verbinden und trennen
struct GlassGroup<Content: View>: View {
    var spacing: CGFloat = 12
    @ViewBuilder var content: Content

    var body: some View {
        if #available(macOS 26.0, *) {
            GlassEffectContainer(spacing: spacing) { content }
        } else {
            content
        }
    }
}

// MARK: - Inhalt (kein Glas – Glas gehört nur auf die Bedienebene)

extension View {
    /// Abgerundete Fläche für Bereiche auf einer Seite
    func card(padding: CGFloat = 18) -> some View {
        self.padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 22, style: .continuous).fill(.quinary))
            .overlay(RoundedRectangle(cornerRadius: 22, style: .continuous).strokeBorder(.separator.opacity(0.5)))
    }
}

/// Farbe pro Name/Datei – feste Systemfarben, damit man Dinge wiedererkennt
enum Palette {
    static let colors: [Color] = [.blue, .purple, .pink, .orange, .green, .teal, .indigo, .red]

    static func color(for key: String) -> Color {
        var h: UInt32 = 0
        for unit in key.utf16 { h = h &* 31 &+ UInt32(unit) }
        return colors[Int(h % UInt32(colors.count))]
    }

    /// Gedämpfter Verlauf für die Sound-Kacheln – viele helle Flächen nebeneinander blenden sonst
    static func tileGradient(_ color: Color) -> LinearGradient {
        LinearGradient(colors: [color.mix(with: .black, by: 0.2), color.mix(with: .black, by: 0.42)],
                       startPoint: .topLeading, endPoint: .bottomTrailing)
    }

    /// Verlauf wie in der Kurzbefehle-App: oben heller, unten satter
    static func gradient(_ color: Color) -> LinearGradient {
        LinearGradient(colors: [color.mix(with: .white, by: 0.14), color.mix(with: .black, by: 0.16)],
                       startPoint: .topLeading, endPoint: .bottomTrailing)
    }
}

/// Großes Symbol in einem farbigen Squircle (Kopf von Seiten)
struct IconTile: View {
    let systemImage: String
    var color: Color = .accentColor
    var size: CGFloat = 64

    var body: some View {
        RoundedRectangle(cornerRadius: size * 0.27, style: .continuous)
            .fill(Palette.gradient(color))
            .frame(width: size, height: size)
            .overlay {
                Image(systemName: systemImage)
                    .font(.system(size: size * 0.44, weight: .semibold))
                    .foregroundStyle(.white)
            }
            .overlay(RoundedRectangle(cornerRadius: size * 0.27, style: .continuous).strokeBorder(.white.opacity(0.2), lineWidth: 1))
            .shadow(color: .black.opacity(0.15), radius: size * 0.06, y: size * 0.03)
    }
}
