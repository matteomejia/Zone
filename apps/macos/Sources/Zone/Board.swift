import AppKit
import SwiftUI
import UniformTypeIdentifiers
import ZoneKit

enum Paper {
  private static func tone(_ light: CGFloat, _ dark: CGFloat) -> Color {
    Color(
      nsColor: NSColor(name: nil) { appearance in
        let isDark = appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
        return NSColor(white: isDark ? dark : light, alpha: 1)
      })
  }
  static let board = tone(0.945, 0.105)
  static let card = tone(1, 0.185)
  static let online = Color(red: 0.2, green: 0.72, blue: 0.42)
}

/// Card metrics. Sizes are derived from the note alone so new cards can be placed before layout.
enum Board {
  static let cardWidth: CGFloat = 168
  static let wideWidth: CGFloat = 296
  static let pad: CGFloat = 12
  static let gap: CGFloat = 10
  static let inset: CGFloat = 11
  static let collapsedText: CGFloat = 132
  static let expandedText: CGFloat = 380

  static func link(_ note: Note) -> URL? {
    guard note.kind == "text", note.text.utf8.count <= 2048 else { return nil }
    let text = note.text.trimmingCharacters(in: .whitespacesAndNewlines)
    guard !text.contains(where: \.isWhitespace), let url = URL(string: text),
      let scheme = url.scheme?.lowercased(), scheme == "http" || scheme == "https",
      url.host() != nil
    else { return nil }
    return url
  }

  static func excerpt(_ note: Note, expanded: Bool) -> String {
    let limit = expanded ? 20_000 : 1_200
    return String(
      String(note.text.prefix(limit + 200)).trimmingCharacters(in: .whitespacesAndNewlines)
        .prefix(limit))
  }

  static func textHeight(_ text: String, width: CGFloat) -> CGFloat {
    ceil(
      (text as NSString).boundingRect(
        with: CGSize(width: width, height: .greatestFiniteMagnitude),
        options: [.usesLineFragmentOrigin, .usesFontLeading],
        attributes: [.font: NSFont.systemFont(ofSize: 13)]
      ).height)
  }

  static func truncated(_ note: Note) -> Bool {
    guard note.kind == "text", link(note) == nil else { return false }
    return note.text.utf8.count > 1_200
      || textHeight(excerpt(note, expanded: false), width: cardWidth - inset * 2)
        > collapsedText - 8
  }

  static func size(_ note: Note, expanded: Bool = false) -> CGSize {
    switch note.kind {
    case "image": return CGSize(width: cardWidth, height: 118)
    case "file": return CGSize(width: cardWidth, height: 54)
    default:
      if link(note) != nil { return CGSize(width: cardWidth, height: 54) }
      let width = expanded ? wideWidth : cardWidth
      let full = textHeight(excerpt(note, expanded: expanded), width: width - inset * 2)
      return CGSize(
        width: width, height: min(full, expanded ? expandedText : collapsedText) + inset * 2)
    }
  }

  /// First gap, reading top to bottom, where a card of `size` fits without touching another.
  static func freeSpot(_ size: CGSize, among taken: [CGRect], width: CGFloat) -> CGPoint {
    let columns = max(1, Int((width - pad * 2 + gap) / (cardWidth + gap)))
    for y in ([pad] + taken.map { $0.maxY + gap }).sorted() {
      for column in 0..<columns {
        let origin = CGPoint(x: pad + CGFloat(column) * (cardWidth + gap), y: y)
        let rect = CGRect(origin: origin, size: size).insetBy(dx: 1 - gap, dy: 1 - gap)
        if !taken.contains(where: { $0.intersects(rect) }) { return origin }
      }
    }
    return CGPoint(x: pad, y: pad)
  }
}

struct WindowDragArea: NSViewRepresentable {
  func makeNSView(context: Context) -> DragView { DragView() }
  func updateNSView(_ view: DragView, context: Context) {}
  final class DragView: NSView {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseDown(with event: NSEvent) { window?.performDrag(with: event) }
  }
}

struct NotebookView: View {
  @ObservedObject var model: ZoneModel
  let connect: () -> Void
  let hide: () -> Void
  @State private var sinkHeight: CGFloat = 60
  @State private var editorHeight: CGFloat = 17
  private var selected: String? { model.notebookDeviceID ?? model.snapshot?.device.id }
  private var shape: RoundedRectangle { RoundedRectangle(cornerRadius: 14, style: .continuous) }

  var body: some View {
    VStack(spacing: 0) {
      header
      BoardView(model: model, owner: selected ?? "", bottomInset: sinkHeight)
        .id(selected)
        .overlay(alignment: .bottom) { sink }
    }
    .buttonStyle(.plain)
    .background(Paper.board)
    .clipShape(shape)
    .overlay(shape.strokeBorder(.primary.opacity(0.14)))
    .alert(
      "Zone",
      isPresented: Binding(get: { model.error != nil }, set: { if !$0 { model.error = nil } })
    ) {
      Button("OK") { model.error = nil }
    } message: {
      Text(model.error ?? "")
    }
  }

  // The whole bar moves the window; only the tabs and buttons take clicks.
  private var header: some View {
    HStack(spacing: 2) {
      ViewThatFits(in: .horizontal) {
        tabs
        ScrollView(.horizontal) { tabs }.scrollIndicators(.hidden)
      }
      Spacer(minLength: 28)
      chrome("plus", "Connect a device", connect)
      chrome("minus", "Tuck away (Esc)", hide)
    }
    .padding(.horizontal, 8)
    .frame(height: 44)
    .background(WindowDragArea())
    .overlay(alignment: .bottom) { Color.primary.opacity(0.07).frame(height: 1) }
  }

  private var tabs: some View {
    HStack(spacing: 2) {
      if let snapshot = model.snapshot {
        tab(snapshot.device.id, name: snapshot.device.name, online: true)
        ForEach(snapshot.peers) { tab($0.id, name: $0.name, online: $0.online) }
      }
    }
  }

  private func tab(_ id: String, name: String, online: Bool) -> some View {
    let active = selected == id
    return Button {
      model.notebookDeviceID = id
    } label: {
      HStack(spacing: 6) {
        Circle().fill(online ? Paper.online : Color.secondary.opacity(0.35))
          .frame(width: 6, height: 6)
        Text(name).font(.system(size: 12, weight: active ? .semibold : .regular)).lineLimit(1)
          .foregroundStyle(active ? .primary : .secondary)
      }
      .padding(.horizontal, 10).frame(height: 28)
      .background {
        if active {
          RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Paper.card)
            .shadow(color: .black.opacity(0.1), radius: 1.5, y: 0.5)
        }
      }
      .contentShape(Rectangle())
    }
    .accessibilityAddTraits(active ? .isSelected : [])
    .help(online ? name : "\(name) · offline, showing saved items")
  }

  private func chrome(_ symbol: String, _ label: String, _ action: @escaping () -> Void)
    -> some View
  {
    Button(action: action) {
      Image(systemName: symbol).font(.system(size: 11, weight: .semibold))
        .foregroundStyle(.secondary).frame(width: 26, height: 26).contentShape(Rectangle())
    }.help(label).accessibilityLabel(label)
  }

  private var sink: some View {
    let empty = model.draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    return HStack(alignment: .bottom, spacing: 4) {
      Button(action: model.attach) {
        Image(systemName: "paperclip").font(.system(size: 13)).foregroundStyle(.secondary)
          .frame(width: 28, height: 28).contentShape(Rectangle())
      }.help("Attach files (⌘O)").accessibilityLabel("Attach files")
      NativeEditor(
        text: $model.draft, onSubmit: model.addText,
        onIngest: { board, text in model.ingest(board, text: text) },
        onHeight: { editorHeight = $0 }
      )
      .frame(height: min(max(editorHeight, 17), 102)).padding(.vertical, 6)
      Button(action: model.addText) {
        Image(systemName: "arrow.up").font(.system(size: 11, weight: .bold))
          .foregroundStyle(Paper.board).frame(width: 22, height: 22)
          .background(Color.primary.opacity(empty ? 0.18 : 0.85), in: Circle())
          .frame(width: 28, height: 28).contentShape(Rectangle())
      }.disabled(empty).help("Add to board (Return)").accessibilityLabel("Add to board")
    }
    .padding(.horizontal, 6).padding(.vertical, 4)
    .glassEffect(.regular, in: .rect(cornerRadius: 18))
    .padding(10)
    .onGeometryChange(for: CGFloat.self) {
      $0.size.height
    } action: {
      sinkHeight = $0
    }
  }
}

private struct Origin: LayoutValueKey { static let defaultValue = CGPoint.zero }

/// Places each card at its own origin and grows to contain them all.
private struct FreeLayout: Layout {
  var minimum: CGSize
  func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
    var size = minimum
    for view in subviews {
      let origin = view[Origin.self]
      let fit = view.sizeThatFits(.unspecified)
      size.width = max(size.width, origin.x + fit.width + Board.pad)
      size.height = max(size.height, origin.y + fit.height + Board.pad)
    }
    return size
  }
  func placeSubviews(
    in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()
  ) {
    for view in subviews {
      let origin = view[Origin.self]
      view.place(
        at: CGPoint(x: bounds.minX + origin.x, y: bounds.minY + origin.y), anchor: .topLeading,
        proposal: .unspecified)
    }
  }
}

private struct DotGrid: View {
  var offset: CGPoint
  var body: some View {
    Canvas { context, size in
      let step: CGFloat = 20
      var dots = Path()
      var y = step / 2 - offset.y.truncatingRemainder(dividingBy: step) - step
      while y < size.height + step {
        var x = step / 2 - offset.x.truncatingRemainder(dividingBy: step) - step
        while x < size.width + step {
          dots.addEllipse(in: CGRect(x: x - 0.75, y: y - 0.75, width: 1.5, height: 1.5))
          x += step
        }
        y += step
      }
      context.fill(dots, with: .color(.primary.opacity(0.13)))
    }
  }
}

struct BoardView: View {
  @ObservedObject var model: ZoneModel
  let owner: String
  let bottomInset: CGFloat
  @State private var dragging: String?
  @State private var translation = CGSize.zero
  @State private var expanded: Set<String> = []
  @State private var scroll = CGPoint.zero
  @State private var targeted = false

  private var local: Bool { owner == model.snapshot?.device.id }
  private var notes: [Note] { (model.snapshot?.items ?? []).filter { $0.ownerId == owner } }
  private var ownerName: String {
    model.snapshot?.peers.first { $0.id == owner }?.name ?? "this device"
  }

  private func origin(_ note: Note) -> CGPoint {
    let spot = model.spots[note.id]
    var point = CGPoint(x: spot?.x ?? Board.pad, y: spot?.y ?? Board.pad)
    if dragging == note.id {
      point.x = max(4, point.x + translation.width)
      point.y = max(4, point.y + translation.height)
    }
    return point
  }

  var body: some View {
    GeometryReader { geometry in
      ScrollViewReader { proxy in
        ScrollView([.vertical, .horizontal]) {
          FreeLayout(
            minimum: CGSize(
              width: geometry.size.width, height: max(0, geometry.size.height - bottomInset))
          ) {
            ForEach(notes) { note in
              Card(
                model: model, note: note, own: local, expanded: expanded.contains(note.id),
                lifted: dragging == note.id
              ) {
                expanded.formSymmetricDifference([note.id])
                model.raise(note.id)
              }
              .layoutValue(key: Origin.self, value: origin(note))
              .zIndex(
                dragging == note.id
                  ? .greatestFiniteMagnitude : Double(model.spots[note.id]?.z ?? 0)
              )
              .simultaneousGesture(
                DragGesture(minimumDistance: 3, coordinateSpace: .global)
                  .onChanged { value in
                    dragging = note.id
                    translation = value.translation
                  }
                  .onEnded { value in
                    model.move(note.id, by: value.translation)
                    dragging = nil
                    translation = .zero
                  }
              )
              .id(note.id)
              .transition(.scale(scale: 0.8).combined(with: .opacity))
            }
          }
          .animation(.spring(duration: 0.3, bounce: 0.25), value: notes.map(\.id))
          .animation(.snappy(duration: 0.22), value: expanded)
          .contentShape(Rectangle())
          .onDrop(of: [.fileURL, .image, .url, .plainText], isTargeted: $targeted) {
            drop($0, at: $1)
          }
        }
        .contentMargins(.bottom, bottomInset, for: .scrollContent)
        .onScrollGeometryChange(for: CGPoint.self) {
          $0.contentOffset
        } action: {
          scroll = $1
        }
        .background(DotGrid(offset: scroll))
        .onChange(of: model.fresh) { _, id in
          guard let id, notes.contains(where: { $0.id == id }) else { return }
          withAnimation(.snappy) { proxy.scrollTo(id, anchor: .center) }
        }
      }
      .overlay {
        if notes.isEmpty, model.snapshot != nil {
          VStack(spacing: 6) {
            Text(local ? "Paste anything." : "Nothing from \(ownerName) yet.")
              .font(.system(size: 22, design: .serif)).italic()
            if local {
              Text("⌘V, drop a file, or type below.\nIt appears on your other devices.")
                .font(.system(size: 12)).foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            }
          }
          .padding(.bottom, bottomInset).allowsHitTesting(false)
        }
      }
      .overlay {
        if targeted {
          RoundedRectangle(cornerRadius: 10, style: .continuous)
            .strokeBorder(.primary.opacity(0.35), style: StrokeStyle(lineWidth: 1.5, dash: [6, 5]))
            .padding(6).allowsHitTesting(false)
        }
      }
      .onChange(of: geometry.size.width, initial: true) { _, width in
        model.boardWidth = width
        model.arrange()
      }
    }
  }

  private func drop(_ providers: [NSItemProvider], at point: CGPoint) -> Bool {
    let model = model
    var handled = false
    for (index, provider) in providers.enumerated() {
      let spot = CGPoint(x: point.x + CGFloat(index) * 18, y: point.y + CGFloat(index) * 18)
      if provider.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier) {
        _ = provider.loadObject(ofClass: URL.self) { url, _ in
          guard let url else { return }
          Task { @MainActor in model.addFiles([url], at: spot) }
        }
      } else if provider.hasItemConformingToTypeIdentifier(UTType.image.identifier) {
        _ = provider.loadDataRepresentation(forTypeIdentifier: UTType.image.identifier) {
          data, _ in
          guard let data else { return }
          Task { @MainActor in
            if let image = NSImage(data: data) { model.addImage(image, at: spot) }
          }
        }
      } else if provider.hasItemConformingToTypeIdentifier(UTType.url.identifier) {
        _ = provider.loadObject(ofClass: URL.self) { url, _ in
          guard let url else { return }
          Task { @MainActor in model.add(text: url.absoluteString, at: spot) }
        }
      } else if provider.canLoadObject(ofClass: String.self) {
        _ = provider.loadObject(ofClass: String.self) { text, _ in
          guard let text else { return }
          Task { @MainActor in model.add(text: text, at: spot) }
        }
      } else {
        continue
      }
      handled = true
    }
    return handled
  }
}

private struct Card: View {
  @ObservedObject var model: ZoneModel
  let note: Note
  let own: Bool
  let expanded: Bool
  let lifted: Bool
  let toggle: () -> Void
  @State private var hover = false
  @State private var copied = false
  @State private var image: NSImage?
  private var shape: RoundedRectangle { RoundedRectangle(cornerRadius: 11, style: .continuous) }

  var body: some View {
    content
      .frame(width: expanded ? Board.wideWidth : Board.cardWidth, alignment: .topLeading)
      .background(Paper.card)
      .clipShape(shape)
      .overlay(shape.strokeBorder(.primary.opacity(0.09)))
      .overlay(alignment: .topTrailing) {
        if hover && !lifted { actions.padding(5).transition(.opacity) }
      }
      .overlay {
        if copied {
          Label("Copied", systemImage: "checkmark").font(.system(size: 11, weight: .semibold))
            .padding(.horizontal, 10).padding(.vertical, 6)
            .background(.regularMaterial, in: Capsule())
            .transition(.scale(scale: 0.85).combined(with: .opacity))
        }
      }
      .shadow(
        color: .black.opacity(lifted ? 0.24 : 0.09), radius: lifted ? 14 : 2.5,
        y: lifted ? 8 : 1
      )
      .scaleEffect(lifted ? 1.035 : 1)
      .animation(.snappy(duration: 0.18), value: lifted)
      .animation(.easeOut(duration: 0.12), value: hover)
      .animation(.snappy(duration: 0.2), value: copied)
      .contentShape(shape)
      .onHover { hover = $0 }
      .onTapGesture {
        model.raise(note.id)
        if note.kind == "file" { model.save(note) } else if !expanded { copy() }
      }
      .contextMenu {
        if note.kind != "file" { Button("Copy") { copy() } }
        if let url = Board.link(note) { Button("Open Link") { NSWorkspace.shared.open(url) } }
        if note.kind != "text" { Button("Save…") { model.save(note) } }
        if own {
          Divider()
          Button("Delete", role: .destructive) { model.remove(note) }
        }
      }
      .help(note.date.formatted(date: .abbreviated, time: .shortened))
      .accessibilityLabel(note.kind == "text" ? note.text : note.name)
      .task(id: note.id) {
        if note.kind == "image", let data = try? await model.attachment(note) {
          image = NSImage(data: data)
        }
      }
  }

  private func copy() {
    model.copy(note)
    copied = true
    Task {
      try? await Task.sleep(for: .seconds(0.9))
      copied = false
    }
  }

  @ViewBuilder private var content: some View {
    if note.kind == "image" {
      Color.primary.opacity(0.05).frame(width: Board.cardWidth, height: 118)
        .overlay {
          if let image {
            Image(nsImage: image).resizable().scaledToFill()
          } else {
            Image(systemName: "photo").foregroundStyle(.tertiary)
          }
        }
        .clipped()
    } else if note.kind == "file" {
      row(
        Image(
          nsImage: NSWorkspace.shared.icon(
            for: UTType(filenameExtension: (note.name as NSString).pathExtension) ?? .data)
        ).resizable(), title: note.name,
        detail: ByteCountFormatter.string(fromByteCount: Int64(note.size), countStyle: .file))
    } else if let url = Board.link(note) {
      let path = url.path() + (url.query().map { "?\($0)" } ?? "")
      row(
        Image(systemName: "link").resizable(), title: url.host() ?? note.text,
        detail: path.isEmpty || path == "/" ? url.scheme ?? "" : path, symbol: true)
    } else if expanded {
      ScrollView {
        Text(Board.excerpt(note, expanded: true)).font(.system(size: 13)).textSelection(.enabled)
          .frame(maxWidth: .infinity, alignment: .leading).padding(Board.inset)
      }
      .frame(height: Board.size(note, expanded: true).height + 4)
    } else {
      Text(Board.excerpt(note, expanded: false)).font(.system(size: 13))
        .frame(maxWidth: .infinity, alignment: .topLeading)
        .frame(maxHeight: Board.collapsedText, alignment: .top)
        .padding(Board.inset)
    }
  }

  private func row(_ icon: Image, title: String, detail: String, symbol: Bool = false)
    -> some View
  {
    HStack(spacing: 9) {
      icon.scaledToFit().frame(width: symbol ? 14 : 30, height: symbol ? 14 : 30)
        .foregroundStyle(.secondary).frame(width: 30, height: 30)
        .background(symbol ? Color.primary.opacity(0.06) : .clear, in: Circle())
      VStack(alignment: .leading, spacing: 2) {
        Text(title).font(.system(size: 12, weight: .medium)).lineLimit(1).truncationMode(.middle)
        Text(detail).font(.system(size: 10.5)).foregroundStyle(.secondary).lineLimit(1)
          .truncationMode(.middle)
      }
      Spacer(minLength: 0)
    }
    .padding(.horizontal, Board.inset).frame(height: 54)
  }

  private var actions: some View {
    HStack(spacing: 0) {
      if expanded || Board.truncated(note) {
        action(
          expanded ? "arrow.down.right.and.arrow.up.left" : "arrow.up.left.and.arrow.down.right",
          expanded ? "Collapse" : "Expand", toggle)
      }
      if note.kind != "file" { action("doc.on.doc", "Copy", copy) }
      if note.kind != "text" { action("arrow.down.to.line", "Save…") { model.save(note) } }
      if own { action("trash", "Delete") { model.remove(note) } }
    }
    .padding(.horizontal, 3)
    .background(.regularMaterial, in: Capsule())
    .overlay(Capsule().strokeBorder(.primary.opacity(0.1)))
  }

  private func action(_ symbol: String, _ label: String, _ run: @escaping () -> Void) -> some View {
    Button(action: run) {
      Image(systemName: symbol).font(.system(size: 10, weight: .medium))
        .frame(width: 22, height: 22).contentShape(Rectangle())
    }.buttonStyle(.plain).help(label).accessibilityLabel(label)
  }
}
