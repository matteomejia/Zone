# Windows client foundation

Requires Windows 10 version 2004 or newer and the .NET 8 SDK. Run from the repository root:

```powershell
./scripts/windows.ps1 test
./scripts/windows.ps1 build
./scripts/windows.ps1 run
./scripts/windows.ps1 package
./scripts/windows.ps1 probe -DeviceName 'Windows Desktop' -Seconds 120
./scripts/windows.ps1 pair -DeviceName 'Windows Desktop'
```

The native WinUI desktop app has device boards, pairing, automatic text/image/file sync,
and offline caches. Each device can change only its own board.
The discovery-only `probe` command uses
a temporary random identity, advertises a real TCP listener, and closes incoming
connections without content. Do not click Connect to the probe expecting a join.
Ctrl+C or expiry withdraws its advertisement and closes the listener. A third argument
to `Zone.Probe` may specify a repeatable test identity; never reuse an app's identity.

## Desktop app

Run `./scripts/windows.ps1 run` to build and open the x64 WinUI app. The build uses
Windows App SDK 1.8 and includes its native runtime beside the executable, without
MSIX registration. The .NET 8 runtime is required. `build` now builds both the probe
and desktop app; `test` runs the core tests.

`package` publishes a portable x64 ZIP under `artifacts/`, including both the .NET
and Windows App SDK runtimes, LICENSE, quick-start instructions, and `BUILD-INFO.json`
with the exact commit and whether the source tree was dirty. A `.zip.sha256` file
provides its checksum. Extract the whole folder and open `Zone.Desktop.exe`; no SDK
is required. The package is unsigned and clean-install testing is still pending.
The version is set in `apps/windows/Directory.Build.props`.

The manual **Attach Windows release** GitHub workflow tests and packages Windows,
then adds new ZIP/checksum assets to an existing matching release. It never replaces
existing assets or moves a release tag. Windows assets can accompany the macOS
v0.1.0 release while their exact newer source commit is recorded in BUILD-INFO and
the release notes.

The app uses the same `%LOCALAPPDATA%\Zone\PairingProbe` store as the pairing probe
so your existing identity and saved Mac pair carry over. Close the probe before opening
the app; only one process can write that store. `ZONE_DATA_DIR` also works for the app.

The window follows the Mac notebook: a compact panel with no title bar, device tabs
with presence dots across the top, **+** (connect) and **−** (minimize) on the right,
a dotted free-form board, and a rounded sink at the bottom.

- Type in the sink and press Enter or click the arrow. Shift+Enter inserts a line break.
  An unsent draft is kept across restarts.
- Ctrl+V adds the clipboard to your board at once: copied files, an image, or text.
  With text already in the sink, Ctrl+V pastes text into the draft as usual, while
  files and images still become cards. When the clipboard offers both text and a
  bitmap (as Office apps do for copied text), the text is used.
- Drop files, images, links or text anywhere on the board; the card lands where you
  drop it. The paperclip or Ctrl+O attaches one or more files. Files are 8 MiB at most.
- Drag cards to arrange them. Positions, stacking order, the window frame and the
  draft are stored in `board-layout.json` beside the store on this PC and are never
  synced. Alt+arrow keys move the focused card.
- Click a text, link or image card to copy it; click a file card to save it. Hovering
  (or focusing a card with Tab) shows Expand/Collapse, Copy, Save and Delete; the same
  actions, plus **Open link**, are in the right-click menu. With a card focused, Enter
  activates it, Ctrl+C copies and Delete removes it. Long text collapses to seven
  lines; expanded text can be selected. Saving always uses a native file dialog and a
  sanitized suggested name. Deletions keep a revisioned tombstone that propagates.
- Saved peers have read-only tabs: no Delete, and anything you add always goes to your
  own board, which is then shown. A green dot means a recent authenticated sync
  succeeded; a grey dot means offline with the cached board shown. Hover a tab for the
  reason. Ctrl+Tab and Ctrl+Shift+Tab switch boards.
- **+** opens pairing. Open **+** on the Mac, select Connect on either device, compare
  codes, then approve on the receiving device. **Done** or Escape closes pairing and
  revokes unused approval. Closing the pairing dialog does not stop sync.
  Changes are pulled about once per second, in batches of at most 64 current items.
- Drag the empty part of the top bar to move the window; edges resize and snap as
  usual. **−** or Esc minimizes. Right-click the top bar for New item, Attach,
  Connect, **Keep on top**, Minimize and **Quit Zone**; Alt+F4 also quits. Ctrl+N
  focuses the sink on your board. Errors appear in a dismissible bar over the board.
- The colours follow the system light or dark theme.

Checked on the development PC against an isolated `ZONE_DATA_DIR`: the empty state,
text/link/file/image cards, an offline peer tab, adding from the sink while a peer tab
is open, draft/frame/position persistence across a restart, clean shutdown, the
top-bar drag region (by window hit test) and the accessibility tree. These were checked
from window captures and accessibility calls. Parent verification then exercised real
input: text paste into the empty sink, card dragging, hover controls, the context menu,
click-to-copy and restart persistence of the moved card. A double Ctrl+V handler was
fixed and one-paste/one-card behavior was confirmed. Clipboard publication retries
transient COM failures. Not yet exercised with real input: drag-and-drop from Explorer,
Ctrl+V of files and images, touch, the light theme and high contrast. Layout placement
and persistence have unit tests. The desktop pairing controls still need the live Mac
approval check; the user confirmed Mac pairing through the earlier console probe.

Differences from the Mac app: Windows draws its own window corners (smaller radius)
and the sink uses in-app acrylic instead of Liquid Glass; the serif heading is Georgia;
file cards show a generic document glyph instead of the file type's icon; names
truncate at the end rather than the middle; the pairing panel is an in-window dialog
rather than a separate window; **−** minimizes to the taskbar because there is no tray
icon or global hotkey yet, and the window is not always-on-top unless you choose
**Keep on top**. Device renaming, settings, Forget and card animations are pending.

## Sync verification

Local TCP tests cover both owners, image bytes, an 8 MiB binary file, tombstones,
offline cache retention, rediscovery at a changed port, restart recovery, a 64-item
batch limit, rejection of replayed/foreign-owner/stale-key changes, interrupted frames,
and mismatched response IDs. A forced SQLite failure verifies item and cursor rollback
together. Only the owner's current rows are served; cached boards are never relayed.

On the development LAN, Windows successfully pulled the paired Mac's board, including
a deletion tombstone and a PNG that rendered in the desktop app. This verifies live
Mac-to-Windows receiving and codec interoperability. Complete these remaining checks:

1. Add text, an image and a near-8-MiB file on Windows. Confirm they appear on the
   Mac's Windows tab; save the remote file and compare its bytes with the original.
2. Repeat the file check from Mac to Windows, using **Save** in the Mac tab.
3. Quit one device, add/delete notes on the other, then restart and check convergence.
4. Interrupt a transfer and reconnect: one intact card, no partial attachment or duplicate.
5. Try a file larger than 8 MiB and confirm the visible rejection.

Window-to-Mac transfer and cross-device file-save/recovery checks are not established
by the local TCP tests or the successful live PNG receive.

## Nearby pairing

`./scripts/windows.ps1 pair -DeviceName 'Windows Desktop'` opens an interactive
pairing session for ten minutes (override with `-Seconds`). It opens pairing immediately,
so the Mac can initiate a connection while the probe is running. It saves the Windows
identity, group key and per-peer keys in SQLite under `%LOCALAPPDATA%\Zone\PairingProbe`.
Set `ZONE_DATA_DIR` to use a separate directory. The name is used when creating an identity;
an existing store retains its saved name. A file lock prevents another process from
using the same identity. Keys are not encrypted at rest; access relies on your Windows
account and disk protection. The SQLite provider is `Microsoft.Data.Sqlite`.

To check **Windows → Mac**:

1. Open **+** on the Mac and run `pair` on Windows.
2. Windows lists numbered nearby devices. Type `connect 1` (use the Mac's actual number).
3. Compare the six-digit code on both screens. Approve on the Mac only if they match.
4. Windows must print `Paired with …`. Type `peers` to confirm the saved peer.

To check **Mac → Windows**, connect to Windows from the Mac's **+** panel. Windows
prints the incoming device and code. Compare both screens, then type `approve` or
`decline` on Windows. For the reverse-direction test of an existing pair, forget Windows
on the Mac first if its UI no longer offers Connect; Windows permits explicit re-pairing.

Other commands are `list`, `peers`, `close`, `open` and `quit`. `close` cancels an outgoing
join and revokes pending requests and unused approvals. A join waits up to two minutes;
an approval authorizes one encrypted pair request from that device for 30 seconds.
Repeated pairing updates the peer without resetting its replication cursor. The client
adopts the host's group key in the same transaction as saving the peer.

The console `pair` command is pairing-only: run the desktop app to pull and serve boards
automatically. Invitations, automatic group introduction and Forget are still pending
on Windows. A lost accepted-pair response requires a fresh join. Do not copy a live data
directory to another device.

`Zone.Core` implements the Swift AES-256-GCM envelope and AAD, P-256 raw ECDH,
HKDF-SHA256 join/group derivation, length framing and receipts. Tests consume the
original Mac fixtures directly, including deriving both public keys from the private
scalars, matching encryption bytes, rejecting authentication changes, and handling
fragmented/truncated frames. Explicit nonces are internal to fixture testing; normal
messages generate random nonces.

Discovery uses Windows' built-in desktop `dnsapi.dll` DNS-SD registration, browse and
resolve calls, without requiring an Apple Bonjour installation. Native async request
memory stays alive until completion; cancellation waits for the browse callback.
The service is `_zone-pad._tcp.local.`, with the SHA-256 instance name and `id`/`name`
TXT fields. Listings require matching instance hashes and a local service endpoint;
they are untrusted addresses, not authenticated peers. Errors surface in the probe.
Native browse callbacks also write to .NET diagnostic Trace listeners.
Set `$env:ZONE_DISCOVERY_TRACE = '1'` to print raw callback statuses and record types
in the probe when diagnosing an empty nearby list.

Repeated announcements are coalesced by service name. Successful lookups are cached
for 30 seconds before another announcement can trigger a refresh, and unchanged
results do not print another `Nearby` line. Changed names or endpoints print `Updated`.
A lookup timeout gets one retry using the same native request while it is still in
flight. Repeated failures remain visible as service-specific errors, with a five-second
retry cooldown; a failed refresh keeps the previous device and addresses. Closing the
probe cancels its worker without reporting a discovery error.

## Verification so far

On the development Windows PC, both Swift fixtures and framing tests pass, native
advertising completes successfully, and timed shutdown/deregistration exits normally.
Initially, two simultaneous probes did not list each other. The browse query had a
trailing dot: the native API accepted the request but returned no records. Removing
the trailing dot fixed browsing on this Windows PC. Registration retains the fully
qualified service name. No firewall or network-profile settings were changed.

On 2026-11-09, the user confirmed that the MacBook lists the Windows desktop.
After the query fix, the Windows diagnostic probe also resolved the MacBook as
`My Mac`, with its ID, host, TCP port, IPv4 address and scoped link-local IPv6 address.
The user subsequently confirmed discovery without duplicate entries or errors.
This verifies discovery in both directions on this LAN. Local TCP tests exercise
joining in both directions, code agreement, encrypted approval claims, persistence,
decline/close, expiry, commitment mismatch and single-use approval. Live Mac pairing
and code agreement remain unverified until the checks above are performed.

An opt-in native regression test starts two temporary advertisements and checks that
each resolves the other's identity, name, port and addresses:

```powershell
$env:ZONE_TEST_DISCOVERY = '1'
./scripts/windows.ps1 test
Remove-Item Env:ZONE_TEST_DISCOVERY
```

This test requires working local multicast and is skipped in the default test run.

## Repeat the discovery check

On a trusted LAN, run the probe while the Mac app is open. Open **+** on the Mac:

1. Confirm the Mac lists `Windows Desktop`.
2. Confirm Windows prints a `Nearby` entry with the Mac's name, ID, host and port.
3. Stop/restart the probe and check disappearance/reappearance; quit the Mac and
   check removal on Windows.
4. If nothing appears, inspect local-network permission, network profile, multicast
   reachability and firewall rules on both machines. Do not broadly disable the firewall.

The user confirmed console pairing with the Mac. The UI milestone was brought forward
at the user's request to make sync checks easier. Complete the cross-device sync checks
above before treating the Windows client as fully accepted.
