import AppKit
import Carbon
import Combine
import SwiftUI

@main
struct ZoneApp: App {
  @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
  var body: some Scene {
    Settings { SettingsView(model: delegate.model) }
      .commands {
        CommandGroup(replacing: .newItem) {
          Button("New Item") { delegate.newNote() }.keyboardShortcut("n")
          Button("Attach Files…") { delegate.model.attach() }.keyboardShortcut("o")
        }
      }
  }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
  static weak var shared: AppDelegate?
  let model = ZoneModel()
  private var notebook: NotePanel?
  private var utility: NSWindow?
  private var status: NSStatusItem?
  private var observation: AnyCancellable?
  private var draftObservation: AnyCancellable?
  private var hotKey: EventHotKeyRef?
  private var eventHandler: EventHandlerRef?
  private var terminating = false

  func applicationDidFinishLaunching(_ notification: Notification) {
    Self.shared = self
    if let id = Bundle.main.bundleIdentifier,
      let other = NSRunningApplication.runningApplications(withBundleIdentifier: id)
        .first(where: { $0.processIdentifier != ProcessInfo.processInfo.processIdentifier })
    {
      other.activate()
      NSApp.terminate(nil)
      return
    }
    model.draft = UserDefaults.standard.string(forKey: "notebook.draft") ?? ""
    draftObservation = model.$draft.sink { UserDefaults.standard.set($0, forKey: "notebook.draft") }
    observation = model.$snapshot.receive(on: RunLoop.main).sink { [weak self] snapshot in
      guard let self, let snapshot else { return }
      if let selected = self.model.notebookDeviceID,
        selected != snapshot.device.id, !snapshot.peers.contains(where: { $0.id == selected })
      {
        self.model.notebookDeviceID = nil
      }
    }
    makeNotebook()
    let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
    item.button?.image = NSImage(
      systemSymbolName: "note.text", accessibilityDescription: "Zone notebook")
    item.button?.toolTip = "Zone · show/hide notebook · right-click for actions"
    item.button?.target = self
    item.button?.action = #selector(clicked)
    item.button?.sendAction(on: [.leftMouseUp, .rightMouseUp])
    status = item
    var kind = EventTypeSpec(
      eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
    InstallEventHandler(
      GetApplicationEventTarget(),
      { _, _, _ in
        Task { @MainActor in AppDelegate.shared?.toggle() }
        return noErr
      }, 1, &kind, nil, &eventHandler)
    RegisterEventHotKey(
      UInt32(kVK_Space), UInt32(cmdKey | shiftKey),
      EventHotKeyID(signature: 0x5A4F_4E45, id: 1), GetApplicationEventTarget(), 0, &hotKey)
    Task {
      await model.start()
      newNote()
    }
  }

  private func makeNotebook() {
    let panel = NotePanel(
      contentRect: NSRect(x: 0, y: 0, width: 392, height: 540),
      styleMask: [.borderless, .resizable], backing: .buffered, defer: false)
    panel.title = "Zone notebook"
    panel.level = .floating
    panel.isOpaque = false
    panel.backgroundColor = .clear
    panel.hasShadow = true
    panel.hidesOnDeactivate = false
    panel.isReleasedWhenClosed = false
    panel.minSize = NSSize(width: 320, height: 340)
    panel.onPaste = { [weak self] in self?.model.ingest(.general, text: true) }
    panel.collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary]
    panel.contentView = NSHostingView(
      rootView: NotebookView(
        model: model,
        connect: { [weak self] in self?.pair() }, hide: { [weak panel] in panel?.orderOut(nil) }))
    panel.center()
    _ = panel.setFrameUsingName("notebook.single")
    panel.setFrameAutosaveName("notebook.single")
    notebook = panel
  }

  /// Shows the board with the sink focused, so ⌘V works straight away.
  private func show() {
    NSApp.activate()
    guard let panel = notebook else { return }
    panel.makeKeyAndOrderFront(nil)
    @MainActor func editor(in view: NSView) -> PadTextView? {
      if let text = view as? PadTextView { return text }
      return view.subviews.lazy.compactMap { editor(in: $0) }.first
    }
    if let root = panel.contentView, let text = editor(in: root) {
      panel.makeFirstResponder(text)
    }
  }

  @objc func newNote() {
    model.notebookDeviceID = model.snapshot?.device.id
    show()
  }
  @objc func toggle() {
    if notebook?.isVisible == true { notebook?.orderOut(nil) } else { show() }
  }
  @objc private func clicked() {
    guard NSApp.currentEvent?.type == .rightMouseUp, let button = status?.button else {
      toggle()
      return
    }
    let menu = NSMenu()
    for (title, action) in [
      ("New item", #selector(newNote)), ("Show / hide board", #selector(toggle)),
      ("Connect a device…", #selector(pair)), ("Settings…", #selector(settings)),
      ("Quit Zone", #selector(quit)),
    ] {
      let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
      item.target = self
      menu.addItem(item)
    }
    menu.popUp(positioning: nil, at: .zero, in: button)
  }
  private func present<V: View>(_ view: V) {
    utility?.close()
    let window = NSWindow(
      contentRect: NSRect(x: 0, y: 0, width: 460, height: 360),
      styleMask: [.titled, .closable], backing: .buffered, defer: false)
    window.isReleasedWhenClosed = false
    window.delegate = self
    window.contentView = NSHostingView(rootView: view)
    window.center()
    window.level = .floating
    utility = window
    NSApp.activate()
    window.makeKeyAndOrderFront(nil)
  }
  @objc private func pair() {
    present(PairView(model: model, onClose: { [weak self] in self?.utility?.close() }))
  }
  @objc private func settings() {
    present(SettingsView(model: model, onClose: { [weak self] in self?.utility?.close() }))
  }
  func windowWillClose(_ notification: Notification) {
    (notification.object as? NSWindow)?.contentView = nil
    Task { await model.core.stopPairing() }
  }
  @objc private func quit() { NSApp.terminate(nil) }
  func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
  func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool
  {
    show()
    return true
  }
  func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
    guard !terminating else { return .terminateLater }
    terminating = true
    if let hotKey { UnregisterEventHotKey(hotKey) }
    if let eventHandler { RemoveEventHandler(eventHandler) }
    Task {
      await model.shutdown()
      NSApp.reply(toApplicationShouldTerminate: true)
    }
    return .terminateLater
  }
}

final class NotePanel: NSPanel {
  override var canBecomeKey: Bool { true }
  override var canBecomeMain: Bool { true }
  var onPaste: (() -> Void)?
  override func cancelOperation(_ sender: Any?) { orderOut(nil) }
  /// Catches ⌘V when focus is on the board rather than in the sink.
  @objc func paste(_ sender: Any?) { onPaste?() }
}
