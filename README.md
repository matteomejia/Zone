# Zone

A small, native Mac scratchpad shared with your nearby devices. One section per device. Paste, copy, drag a file. No account or hosted backend.

The macOS app is entirely Swift: SwiftUI and AppKit for the interface, Network.framework for Bonjour and TCP, CryptoKit for encrypted messages, and the system SQLite library for persistence. There are no third-party package dependencies, web views, JavaScript build tools, Rust toolchains, or language-binding layers in the app.

## Run on macOS

Requires macOS 26 or later and Xcode 26+ (or compatible Command Line Tools) with a macOS 26+ SDK. Zone is one borderless floating panel: device tabs on top, a free-form board in the middle, and a sink at the bottom. Every item is a card you can drag anywhere on the board; whatever you paste, drop, or type into the sink becomes a new card. Each device has its own board. Peer boards are read-only, including cached items from offline devices. Card positions are remembered per Mac and are not synced. The panel follows system light/dark appearance and remembers its position and size.

```sh
scripts/macos.sh run
```

This builds and opens `target/native-arm64-debug/Zone.app` on Apple Silicon (`native-x86_64-debug` on Intel). Zone stays in the menu bar without a Dock icon. Click its icon or press **Cmd+Shift+Space** to show/hide the board with the sink focused. Drag anywhere on the top bar (outside the tabs and buttons) to move the panel, resize from its edges, and use **−** or **Esc** to tuck it away. The header **+** connects another device. Right-click the menu bar icon for pairing, settings, and quit.

**Cmd+V** adds the clipboard to your board immediately: text, images, or copied files. You can also drop files, images, links, or text anywhere on the board (the card lands where you drop it), type in the sink and press **Return** (**Shift+Return** for a line break), or attach files with the paperclip or **Cmd+O**. New cards otherwise take the first free gap. Click a text or image card to copy it; click a file card to save it. Hovering a card shows copy, save, expand, and delete; long text expands into a wider, scrollable, selectable card. Links show their host and can be opened from the context menu.

Files are limited to 8 MiB each, and text to 1 MiB. Items persist automatically. Launch-at-login and automatic updates are not implemented.

## Pairing and device ownership

Open **+** on both devices, on the same local network, and allow local-network access when prompted. Each lists the other by name. Click **Connect** on one; both show the same six-digit code. Check that the codes match and click **Approve** on the other. A device only answers connection requests while its **+** panel is open.

Your devices form a group. A new device needs one approval, on any device already in the group; the rest find it on the network and connect to it without asking. Because any member can admit a device for all of them, this is meant for your own devices, not for sharing with other people.

Each device writes and deletes only on its own board. Received boards stay cached locally, including their attachments, while the owner is offline. Deletions use persistent tombstones; reconnecting devices fetch the changes they missed. Sync checks run about once per second. Devices must be reachable together to exchange changes; one device does not forward another's board.

**Forget** (in Settings) removes a device's cached board here and stops it reconnecting to this Mac on its own. It is per device: forget it on each of your other devices too. Forgetting does not change the group key, so it is not protection against a device you no longer control, and it cannot erase copies that device already received.

Where devices cannot see each other on the network, **+ → Use an invitation instead** creates a one-time code (valid ten minutes) to paste on the other device. Zone does not deliver invitations for you.

Local discovery uses Bonjour/mDNS (`_zone-pad._tcp.local.`); encrypted messages travel directly over TCP. The advertisement includes the device's ID and name, so others on the network can see that a device named, say, "Francisco's MacBook Pro" runs Zone. No public relays, NAT traversal, internet discovery, accounts, or Bluetooth are used. Direct address fallback accepts private/link-local/loopback IPv4 addresses. Bonjour can also resolve local IPv6 endpoints. Guest-network isolation, firewalls, or blocked multicast can prevent connectivity. A fresh invitation can update a changed address if Bonjour is blocked.

The Windows frontend will be developed separately on the Windows desktop with WinUI and C#. It is not implemented or build-verified here. The wire protocol and a public encryption fixture are provided below for that work.

## Storage and prototype migration

Data stays in `~/Library/Application Support/app.zone.scratchpad/zone.sqlite3`. The directory is restricted to the current user, and an exclusive file lock prevents two native instances from using one device identity. Notes, cached attachments, and pairing keys are not encrypted at rest; access relies on your OS account and disk protection.

The native version reads the previous prototype’s local device record and item format, preserving local identity and notes. Its trust table is separate because the Swift protocol replaces iroh. **Prototype devices must pair again.** Old peer caches remain in the database but are not displayed until their owners are paired with a compatible app. Old prototype invitation codes are unsupported. Do not copy a live data directory to a second device; that duplicates its identity.

## Verify and package

```sh
scripts/macos.sh test
scripts/macos.sh build
scripts/macos.sh package
```

Set `ZONE_DATA_DIR` to run the app against a throwaway store instead of your real one. Tests use temporary data directories. They cover persistence, owner-only writes, revision replay rejection, deletion tracking, exclusive access, file bounds, encryption tampering/wrong keys, one-time/cancelled pairing, approved and declined joins, group introduction, forgetting, real local TCP transfer, binary integrity, and offline restart recovery. Public fixtures for message encryption and for pairing support future Windows interoperability checks. Local tests do not establish that Bonjour, firewall behavior, or connectivity between separate physical devices works on your network. See [acceptance checks and the wire protocol](docs/acceptance.md).

Packaging produces an architecture-specific `.dmg` in `artifacts/`. The app is ad-hoc signed by default. Before public distribution, configure Developer ID signing and notarization; no signing credentials or notarization setup are included. `ZONE_SIGN_IDENTITY` can select an existing signing identity, `ZONE_ARCH` can select `arm64` or `x86_64`, and `ZONE_PROFILE=release` selects an optimized build. Cross-architecture builds require a supporting Apple SDK and have not been locally tested on Intel hardware.

The build script uses the installed macOS 26 SDK when running with Command Line Tools that ship an incomplete macOS 27 SwiftUI macro setup. Full Xcode uses its default SDK. Set `ZONE_MACOS_SDK` to override the SDK path.

The **Draft macOS release** GitHub workflow is manual. It tests/builds Apple Silicon and Intel disk images, then attaches both to a draft prerelease only if both jobs succeed. It will not modify a published release. Change the version in `apps/macos/Info.plist` before a new release. There is no Windows artifact yet. Nothing is pushed, tagged, or published by local build commands.

## License

[MIT](LICENSE)
