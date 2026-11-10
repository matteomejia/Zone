import AppKit
import SwiftUI

/// The sink's text field. Uses the OS text system for selection, spelling, undo, and input methods.
struct NativeEditor: NSViewRepresentable {
  @Binding var text: String
  var onSubmit: () -> Void
  /// Offers a pasteboard to the board; the flag says whether plain text may be taken too.
  var onIngest: (NSPasteboard, Bool) -> Bool
  var onHeight: (CGFloat) -> Void

  func makeNSView(context: Context) -> NSScrollView {
    let scroll = NSScrollView()
    scroll.drawsBackground = false
    scroll.hasVerticalScroller = false
    let editor = PadTextView()
    editor.isRichText = false
    editor.importsGraphics = false
    editor.drawsBackground = false
    editor.font = .systemFont(ofSize: 13)
    editor.textColor = .labelColor
    editor.insertionPointColor = .labelColor
    editor.textContainerInset = .zero
    editor.isVerticallyResizable = true
    editor.isHorizontallyResizable = false
    editor.autoresizingMask = [.width]
    editor.textContainer?.widthTracksTextView = true
    editor.allowsUndo = true
    editor.isAutomaticQuoteSubstitutionEnabled = false
    editor.setAccessibilityLabel("Add to board")
    editor.registerForDraggedTypes([.fileURL, .png, .tiff])
    scroll.documentView = editor
    update(editor)
    return scroll
  }

  func updateNSView(_ scroll: NSScrollView, context: Context) {
    guard let editor = scroll.documentView as? PadTextView else { return }
    update(editor)
    if editor.string != text {
      editor.string = text
      editor.needsDisplay = true
      editor.reportHeight()
    }
  }

  private func update(_ editor: PadTextView) {
    editor.onChange = { value in text = value }
    editor.onSubmit = onSubmit
    editor.onIngest = onIngest
    editor.onHeight = onHeight
  }
}

@MainActor
final class PadTextView: NSTextView {
  var onChange: ((String) -> Void)?
  var onSubmit: (() -> Void)?
  var onIngest: ((NSPasteboard, Bool) -> Bool)?
  var onHeight: ((CGFloat) -> Void)?
  private var reported: CGFloat = 0

  func reportHeight() {
    guard let layoutManager, let textContainer else { return }
    layoutManager.ensureLayout(for: textContainer)
    let height = ceil(layoutManager.usedRect(for: textContainer).height)
    guard height != reported else { return }
    reported = height
    // Deferred: this can run while SwiftUI is updating the view.
    DispatchQueue.main.async { [weak self] in self?.onHeight?(height) }
  }

  override func didChangeText() {
    super.didChangeText()
    onChange?(string)
    needsDisplay = true
    reportHeight()
  }

  override func setFrameSize(_ newSize: NSSize) {
    super.setFrameSize(newSize)
    reportHeight()
  }

  override func draw(_ dirtyRect: NSRect) {
    super.draw(dirtyRect)
    if string.isEmpty {
      ("Paste, type, or drop anything" as NSString).draw(
        at: NSPoint(x: textContainerInset.width + 5, y: textContainerInset.height),
        withAttributes: [
          .font: NSFont.systemFont(ofSize: 13), .foregroundColor: NSColor.tertiaryLabelColor,
        ])
    }
  }

  // Return adds the item; Shift- or Option-Return inserts a line break.
  override func keyDown(with event: NSEvent) {
    let isReturn = event.keyCode == 36 || event.keyCode == 76
    if isReturn, !hasMarkedText(), event.modifierFlags.intersection([.shift, .option]).isEmpty {
      onSubmit?()
    } else {
      super.keyDown(with: event)
    }
  }

  // Pasting into an empty sink adds the clipboard straight to the board.
  override func paste(_ sender: Any?) {
    let empty = string.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    if onIngest?(.general, empty) != true { super.paste(sender) }
  }

  override func draggingEntered(_ sender: any NSDraggingInfo) -> NSDragOperation {
    if sender.draggingPasteboard.canReadObject(
      forClasses: [NSURL.self, NSImage.self], options: nil)
    {
      return .copy
    }
    return super.draggingEntered(sender)
  }

  override func performDragOperation(_ sender: any NSDraggingInfo) -> Bool {
    onIngest?(sender.draggingPasteboard, false) == true || super.performDragOperation(sender)
  }
}
