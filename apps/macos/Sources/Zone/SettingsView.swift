import AppKit
import SwiftUI
import ZoneKit

struct PairView: View {
  @ObservedObject var model: ZoneModel
  @Environment(\.dismiss) private var dismiss
  var onClose: (() -> Void)? = nil
  private func close() { if let onClose { onClose() } else { dismiss() } }
  @State private var joining: (name: String, code: String?)?
  @State private var attempt: Task<Void, Never>?
  @State private var invitation = ""
  @State private var code = ""
  @State private var busy = false
  @State private var error: String?
  @State private var copied = false
  @State private var manual = false
  @State private var initialPeers: Int?

  var body: some View {
    VStack(alignment: .leading, spacing: 16) {
      Text("Connect a device").font(.title3.weight(.medium))
      if let request = model.snapshot?.request {
        confirm(
          "\(request.name) wants to connect", code: request.code,
          hint: "Approve only if the other device shows this same code."
        ) {
          Button("Decline") { Task { await model.core.decline() } }
          Button("Approve") {
            Task {
              do { try await model.core.approve() } catch {
                self.error = error.localizedDescription
              }
            }
          }.keyboardShortcut(.defaultAction)
        }
      } else if let joining {
        confirm(
          "Connecting to \(joining.name)", code: joining.code,
          hint: "Check that the other device shows this code, then approve there."
        ) {
          ProgressView().controlSize(.small)
          Button("Cancel") { attempt?.cancel() }
        }
      } else {
        nearby
        DisclosureGroup("Use an invitation instead", isExpanded: $manual) {
          invite.padding(.top, 8)
        }.font(.callout)
      }
      if let error { Text(error).font(.caption).foregroundStyle(.red).textSelection(.enabled) }
      HStack {
        Spacer()
        Button("Done") { close() }.keyboardShortcut(.cancelAction)
      }
    }
    .padding(24).frame(width: 370)
    .onAppear {
      initialPeers = model.snapshot?.peers.count
      Task { await model.core.startPairing() }
    }
    .onChange(of: model.snapshot?.peers.count) { _, count in
      if let initialPeers, let count, count > initialPeers { close() }
    }
    .onDisappear {
      attempt?.cancel()
      Task { await model.core.stopPairing() }
    }
  }

  private func confirm<Actions: View>(
    _ title: String, code: String?, hint: String, @ViewBuilder actions: () -> Actions
  ) -> some View {
    VStack(spacing: 10) {
      Text(title).font(.callout)
      if let code {
        Text(code.prefix(3) + " " + code.suffix(3))
          .font(.system(size: 34, weight: .medium, design: .monospaced)).textSelection(.enabled)
        Text(hint).font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
      } else {
        ProgressView().controlSize(.small).padding(.vertical, 14)
      }
      HStack { actions() }.padding(.top, 4)
    }
    .frame(maxWidth: .infinity).padding(16)
    .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 10))
  }

  @ViewBuilder private var nearby: some View {
    let devices = model.snapshot?.nearby ?? []
    if devices.isEmpty {
      HStack(spacing: 8) {
        ProgressView().controlSize(.small)
        Text("Looking for devices… Open this panel on the other device too.")
          .font(.callout).foregroundStyle(.secondary)
      }
    } else {
      VStack(spacing: 0) {
        ForEach(devices) { device in
          HStack {
            Image(systemName: "laptopcomputer").foregroundStyle(.secondary)
            Text(device.name).font(.callout).lineLimit(1)
            Spacer()
            Button("Connect") { connect(device) }
          }.padding(.vertical, 6)
        }
      }
      Text("Keep this panel open on the other device so it can approve.")
        .font(.caption).foregroundStyle(.secondary)
    }
  }

  private func connect(_ device: Nearby) {
    error = nil
    joining = (device.name, nil)
    attempt = Task {
      do {
        joining = (device.name, try await model.core.join(device.id))
        try await model.core.awaitApproval()
        await model.refresh()
        close()
      } catch {
        if !Task.isCancelled { self.error = error.localizedDescription }
      }
      joining = nil
    }
  }

  private var invite: some View {
    VStack(alignment: .leading, spacing: 10) {
      Text("For networks where devices cannot see each other. Works once, for ten minutes.")
        .font(.caption).foregroundStyle(.secondary)
      if !invitation.isEmpty {
        Text(invitation).font(.system(size: 10, design: .monospaced)).lineLimit(3)
          .textSelection(.enabled).padding(8).frame(maxWidth: .infinity, alignment: .leading)
          .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 5))
      }
      HStack {
        Button(invitation.isEmpty ? "Create invitation" : "New invitation") {
          busy = true
          Task {
            defer { busy = false }
            do {
              invitation = try await model.core.createInvitation()
              copied = false
            } catch { self.error = error.localizedDescription }
          }
        }
        if !invitation.isEmpty {
          Button(copied ? "Copied" : "Copy") {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(invitation, forType: .string)
            copied = true
          }
        }
      }
      HStack {
        TextField("Paste an invitation", text: $code).textFieldStyle(.roundedBorder)
          .font(.system(.caption, design: .monospaced))
          .accessibilityLabel("Invitation from the other device")
        Button(busy ? "Connecting…" : "Connect") {
          busy = true
          Task {
            defer { busy = false }
            do {
              try await model.core.pair(code)
              await model.refresh()
              close()
            } catch { self.error = error.localizedDescription }
          }
        }.disabled(code.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
      }
    }.disabled(busy)
  }
}

struct SettingsView: View {
  @ObservedObject var model: ZoneModel
  @Environment(\.dismiss) private var dismiss
  var onClose: (() -> Void)? = nil
  private func close() { if let onClose { onClose() } else { dismiss() } }
  @State private var name = ""
  @State private var error: String?
  @State private var busy = false
  @State private var forgetting: Peer?

  var body: some View {
    VStack(alignment: .leading, spacing: 18) {
      Text("Settings").font(.title3.weight(.medium))
      VStack(alignment: .leading, spacing: 7) {
        Text("This device").font(.caption).foregroundStyle(.secondary)
        TextField("Device name", text: $name).textFieldStyle(.roundedBorder)
          .onChange(of: name) { _, next in if next.count > 40 { name = String(next.prefix(40)) } }
      }
      if let peers = model.snapshot?.peers, !peers.isEmpty {
        Divider()
        Text("Paired devices").font(.caption).foregroundStyle(.secondary)
        ForEach(peers) { peer in
          HStack {
            Text(peer.name).font(.callout)
            Spacer()
            Button("Forget…") { forgetting = peer }.font(.caption)
          }
        }
      }
      if let error { Text(error).font(.caption).foregroundStyle(.red) }
      HStack {
        Button("Cancel") { close() }.keyboardShortcut(.cancelAction)
        Spacer()
        Button("Save") {
          busy = true
          Task {
            defer { busy = false }
            do {
              try await model.core.rename(name)
              await model.refresh()
              close()
            } catch { self.error = error.localizedDescription }
          }
        }.keyboardShortcut(.defaultAction).disabled(
          busy || name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
      }
    }
    .padding(24).frame(width: 330)
    .onAppear { name = model.snapshot?.device.name ?? "" }
    .confirmationDialog(
      "Forget \(forgetting?.name ?? "this device")?",
      isPresented: Binding(get: { forgetting != nil }, set: { if !$0 { forgetting = nil } })
    ) {
      Button("Forget device", role: .destructive) {
        guard let peer = forgetting else { return }
        Task {
          do {
            try await model.core.forget(peer.id)
            await model.refresh()
          } catch { self.error = error.localizedDescription }
          forgetting = nil
        }
      }
    } message: {
      Text(
        "This removes its items here and stops it reconnecting to this Mac on its own. Forget it on your other devices too to remove it everywhere."
      )
    }
  }
}
