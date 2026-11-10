# Native acceptance and Windows protocol handoff

## Native macOS acceptance

1. Launch Zone. Confirm the native toolbar, system appearance, device label, and existing local notes. Resize the compact window and use light/dark appearance. System accessibility preferences should control the native controls and materials.
2. Press Cmd+V with text, a screenshot, and a copied file on the clipboard: each must appear as a card at once. Type in the sink and press Return. Drop a file onto the board and confirm the card lands at the drop point. Drag cards around, quit, and relaunch: positions must persist. Click a text card and paste into another application; save an attachment and compare it with the original. Drag the panel by any empty part of the top bar. Native undo should work in the sink.
3. Open pairing and settings sheets. Check Escape, Return, focus, keyboard navigation, and device renaming. Closing pairing must invalidate any unused invitation.
4. Close the window and reopen via the menu bar and Cmd+Shift+Space. Launch a second copy: it must activate the first, not open a second writer. Cmd+Q must stop networking and release the data lock.

For two-device checks, use another compatible Mac now, or the future Windows implementation:

5. Run Zone on both devices on one private LAN and open **+** on each. Each must list the other. Connect from A; both must show the same code; approve on B. Confirm both show two tabs. Declining, closing the panel on B, or waiting two minutes must leave them unpaired. With a third device, approve it on A only and confirm B connects to it without a prompt. Forget a device and confirm it does not reconnect by itself. Also pair once with an invitation: reusing it, changing its key, or letting it expire must fail.
6. Add different notes simultaneously on both devices. Confirm each appears under its owner and both copies converge. Remote notes must have no delete/edit controls.
7. Transfer text, images, and files both ways. Verify bytes of a saved remote file. Try a file larger than 8 MiB: it must be rejected visibly.
8. Quit B. Confirm A keeps its cached B section and marks B offline. Add and delete A notes. Restart B; confirm missing notes arrive and deleted notes disappear. Repeat with roles reversed.
9. Restart both devices. Identity, names, pairing keys, cursors, notes, and deletion progress must survive. Change network addresses and confirm Bonjour rediscovery restores connectivity. Test failure visibility on isolated/guest Wi-Fi.
10. Interrupt a file transfer. Reconnect and verify one intact card, no partial attachment, no duplicate items, and no cursor advanced past an uncommitted item. Forget the devices on both sides and confirm content is no longer exchanged.

Before publication, install every disk image on a clean supported Mac, verify signing/notarization as configured, and repeat the pairing/file checks. Local automated TCP tests do not replace this procedure. Windows runtime behavior remains unverified until its implementation is tested on Windows.

## Starting the Windows client

Build in this order, so that each step is checked against the Mac before the next depends on it:

1. **Codec.** Reproduce both public fixtures (message encryption, then pairing) in unit tests. Nothing on the network yet.
2. **Discovery.** Advertise `_zone-pad._tcp` with the `id` and `name` TXT record and browse for others. Success is the Windows device appearing in the Mac's **+** panel and the Mac appearing on Windows. This is the least certain part on Windows; settle it before any interface work.
3. **Join.** Run a join in both directions against the Mac and confirm the codes match on both screens.
4. **Sync.** Pull the Mac's board, then serve the Windows board to the Mac. Check text, an image, and a file near 8 MiB.
5. **Interface.** Tabs, the free-form board, and the sink, matching the Mac's behaviour described in the README.

The Mac build has only been exercised on one machine. A disagreement between the two clients may be a Mac bug or an unclear sentence below rather than a Windows mistake; fix the document along with the code.

## Protocol v1: native Swift / future C#

All integers are JSON integers; timestamps are Unix milliseconds. IDs are case-sensitive strings. New device and note IDs are lowercase UUIDs. A migrated prototype device may retain its previous public-key-shaped ID (up to 128 UTF-8 bytes). Validate device names (1–100 UTF-8 bytes), platform (up to 32 bytes), and IDs before storing them.

### Discovery and invitations

Advertise a TCP listener with Bonjour service type `_zone-pad._tcp.local.`. Its instance name is `zone-` followed by the first 16 bytes of SHA-256(device ID UTF-8), rendered as 32 lowercase hex characters. Discovery supplies addresses only; it grants no trust. A pairing key authenticates every content exchange.

An invitation is `zone:swift1:` followed by unpadded base64url of UTF-8 JSON:

```json
{"version":1,"device":{"id":"uuid","name":"MacBook","platform":"macos"},"hosts":["192.168.1.10"],"port":54321,"key":"base64-of-32-random-bytes","expires":1794268800000}
```

Limit invitations to 16 KiB. Require version 1, an unexpired timestamp, exactly 32 decoded key bytes, a nonzero UInt16 port, and 1–16 canonical numeric local IPv4 addresses. Supported direct address ranges are RFC 1918, link-local 169.254/16, and loopback 127/8. Do not follow URLs, resolve arbitrary hostnames, or contact a relay from an invitation. Bonjour service endpoints are resolved in the local domain.

The invitation key becomes the per-pair shared key. It grants access to the inviter’s section; transfer the invitation through a trusted channel. Pairing consumes the active invitation before replying, and closing the invitation UI revokes any unused invitation. If a pairing response is lost after acceptance, generate a fresh invitation and pair again. Updating an existing pair preserves its replication cursor.

### Joining a nearby device

The Bonjour service carries a TXT record with `id` (device ID) and `name` (display name). List a service only if its instance name matches the hash of that `id`. Joining uses two unencrypted exchanges, framed like every other message, followed by an ordinary encrypted `pair`. A device answers them only while its connect panel is open; otherwise it closes the connection without content.

The joiner creates a fresh P-256 key pair. Public keys travel as standard base64 of the 65-byte uncompressed point (`04 || X || Y`). It first sends a commitment, the base64 SHA-256 of its public key bytes:

```json
{"version":1,"id":"join-uuid","action":"join","device":{"id":"joiner-id","name":"Desktop","platform":"windows"},"commit":"base64-sha256"}
```

The host replies with its own fresh public key:

```json
{"id":"join-uuid","device":{"id":"host-id","name":"MacBook","platform":"macos"},"public":"base64-point"}
```

The joiner then reveals its key with `{"version":1,"id":"join-uuid","action":"reveal","public":"base64-point"}`; the host must reject it unless its hash equals the commitment, and replies `{"id":"join-uuid","device":{…}}`. The commitment order is what stops a device in the middle from choosing keys that produce a matching code; do not reorder or merge these steps.

Both sides compute the ECDH shared secret (the raw X coordinate) and derive, with HKDF-SHA256 and an empty salt:

- the pairing key: 32 bytes, info = UTF-8 `zone/1 join key` followed by the joiner's then the host's public key bytes;
- the code: 4 bytes, info = UTF-8 `zone/1 join code` followed by the same two keys, read as a big-endian unsigned integer modulo 1,000,000 and shown as six digits with leading zeros.

The host shows the joiner's name and the code and waits for the user. On approval it accepts one `pair` request (below) encrypted with the pairing key, from that device only, for 30 seconds. The joiner retries `pair` about once per second for up to two minutes. Between tries it resends `reveal`: a reply means the host is still waiting, no reply means declined or expired (try `pair` once more before giving up). A host holds one request at a time and drops it after two minutes.

### Groups

Every device holds a 32-byte group key, random at first launch. Every `paired` response to an approved join or an invitation carries it as `"group":"base64"`, and the joiner replaces its own group key with it. Existing pairs keep their stored keys.

Two devices in one group share the key HKDF-SHA256(group key, empty salt, info = UTF-8 `zone/1 pair\n{id1}\n{id2}`, 32 bytes), with the two device IDs ordered by their UTF-8 bytes. A device sends an ordinary `pair` request, encrypted with that key, to each listed nearby device it is not paired with, at most every 30 seconds per device. The receiver tries that key for any message from an unpaired sender; if it opens a valid `pair`, it stores the pair with that key and replies `paired`. Devices outside the group cannot open the message and send nothing.

Forgetting a device adds its ID to a local block list: do not offer it group pairing and do not accept group pairing from it. An approved join or an invitation involving that device removes the block. The group key is not changed, so every member can derive every pair key in the group, and a former member still holds the key; state this limit in any security claims.

The [public pairing fixture](../apps/macos/Tests/ZoneKitTests/Fixtures/pairing.json) gives both private keys, both public keys, the commitment, and the expected pairing key and code for one join, plus a group key, two device IDs, and their expected pair key. A Windows implementation must reproduce every value from the private keys before attempting a live join.

### Framing and authentication

One TCP connection carries one request, one response, and a receipt. Each frame is a four-byte unsigned **big-endian** length followed by exactly that many bytes. Reject lengths of zero or greater than 18 MiB before allocating the body. Incoming requests are further limited to 64 KiB, and the receipt is exactly one byte. Handle partial TCP reads until the declared length is complete; EOF before completion is a failed transfer.

Request and response bodies are UTF-8 JSON envelopes:

```json
{"version":1,"sender":"device-a-id","recipient":"device-b-id","sealed":"base64-combined-sealed-box"}
```

Use **AES-256-GCM**, a fresh random **12-byte nonce** for every message, and a **16-byte tag**. `sealed` encodes `nonce || ciphertext || tag` using standard padded base64. The authenticated additional data is exactly UTF-8 `zone/1\n{sender}\n{recipient}`, with LF bytes and no trailing newline. Limit decrypted JSON to 12 MiB. The receiver verifies the envelope recipient matches itself; the caller also verifies the response sender matches the paired device.

Use CryptoKit `AES.GCM` on Apple platforms; the future Windows codec can use .NET `AesGcm`. The [public codec fixture](../apps/macos/Tests/ZoneKitTests/Fixtures/codec.json) provides a known key, nonce, AAD, plaintext, and envelope. Its fixed key/nonce are for tests only. A Windows implementation must decrypt it and reject altered tags/headers before attempting live pairing. No cross-language interoperability is claimed yet. This protocol uses persistent symmetric pairing keys and does not provide forward secrecy; keep this distinction in any security claims.

### Pair

A fresh lowercase UUID `id` correlates each request with its response. The pair request is encrypted with the invitation key:

```json
{"id":"request-uuid","action":"pair","device":{"id":"joining-id","name":"Desktop","platform":"windows"},"hosts":["192.168.1.20"],"port":54322}
```

Require the device ID to equal the envelope sender. The server accepts this only with its currently active invitation key, validates the record, stores the pair, and consumes the invitation. It returns the following response encrypted with the same key:

```json
{"id":"same-request-uuid","kind":"paired","device":{"id":"inviter-id","name":"MacBook","platform":"macos"},"hosts":["192.168.1.10"],"port":54321}
```

The caller verifies the response ID and authenticated device identity, then stores that peer and key. An invalid or unauthenticated request gets no content. There is no global group key or transitive pairing.

### Pull changes

An already paired device sends this request encrypted with its stored per-pair key:

```json
{"id":"fresh-request-uuid","action":"sync","since":12}
```

The server returns only the **next current item in its own section** whose revision is greater than `since`, ordered by revision:

```json
{"id":"same-request-uuid","kind":"change","device":{"id":"owner-id","name":"MacBook","platform":"macos"},"item":{"id":"note-uuid","revision":13,"createdAt":1794268800000,"kind":"text","text":"hello","name":"","mime":"","data":"","deleted":false}}
```

If caught up, `item` is omitted or null. The response device ID must equal the authenticated sender and must be a paired remote owner. Never accept another device’s items or allow a remote request to mutate the receiver’s local section. Do not accept unsolicited responses; require the response ID to match the current request.

After receiving and authenticating the response, the client sends a framed one-byte receipt `0x01`; the server waits for that receipt before closing its connection. This ensures it does not cancel a connection while a large response is still in flight. A receipt acknowledges transport completion, not database commit. Retries still use the last durably committed cursor.

For each changed item, validate it and atomically commit the cached item and that peer’s cursor in one SQLite transaction. Reject revisions less than or equal to the stored cursor. Keep a single current row per `(owner,id)`. A deletion increments the owner’s revision and retains a tombstone with empty text/data; never erase the local revision history by discarding tombstones. A changed item can move forward in revision while another peer is pulling; returning current rows in revision order remains sufficient because only the owner writes.

Kinds are `text`, `image`, or `file`. Text is bounded at 1 MiB, file bytes at 8 MiB, filenames at 255 UTF-8 bytes, MIME types at 128 bytes. `data` is standard base64; it is empty for text and cleared on deletion. Raster PNG/JPEG/WebP/GIF attachments may be classified as images. Preserve opaque file bytes. Treat received filenames as display names; use only their final path component as a suggested name in the native save dialog.

Fetch until no next item, with a bounded batch of 64 changes per poll. Persist the cursor after each item. Normal polls are about one second apart; timeouts/disconnections mark a peer unavailable while preserving its cache. Retry incomplete transfers without advancing the cursor. Metadata such as device renaming travels with each response. Rediscover addresses using Bonjour after restart/address changes; a new invitation is the fallback when discovery is blocked.
