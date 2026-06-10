# Windows TODO

Step-by-step hardening roadmap for `WDCableWUI/`.

Use this file one task at a time. Ask Codex for exactly one task, for example: `Do W-01 only in WDCableWUI`. After Codex finishes, run the user test on real Windows hardware and paste the report template back.

Do not add major features, including voice streaming, until at least W-01 through W-06 and the matching Android tasks F-01 through F-06 are stable.

## Standard User Test Report

Paste this after each task:

```text
Task ID:
Build/run method: Visual Studio/dotnet/packaged/unpackaged
Windows device A: model, Windows version, Wi-Fi adapter
Windows device B: model, Windows version, Wi-Fi adapter, or not used
Android peer used: yes/no, Android model/version/build, or not used
Test scenarios run:
Results:
Crash/hang/stuck: yes/no
If stuck, last visible status/log line:
Screenshots/logs attached: yes/no
Notes:
```

Use `not run` for scenarios you cannot test.

## Suggested Cross-Project Order

1. `F-01`, then `W-01`
2. `F-02`, then `W-02`
3. `F-03`, then `W-03`
4. `F-04`, then `W-04`
5. `F-05`, then `W-05`
6. `F-06`, then `W-06`
7. `F-07` and `W-07` together, because this changes the shared protocol
8. `F-08`, then `W-08`
9. `F-09`, then `W-09`

## W-01 - Build Warning And Page Subscription Cleanup - Finished

Goal: reduce crash risk without changing Wi-Fi Direct behavior.

Result:

- Codex work complete.
- User test passed.
- Final `dotnet build WDCableWUI.sln`: 0 warnings, 0 errors.

## W-02 - Startup And Service Initialization Safety

Goal: avoid crashes or unusable pages when Wi-Fi Direct is unsupported or service initialization partially fails.

Codex work:

- [ ] Make `DataManager` access safe when `ServiceManager` initialization failed.
- [ ] Make pages tolerate null service dependencies without crashing.
- [ ] Improve the Wi-Fi Direct unsupported dialog and status handling.
- [ ] Ensure app settings still work when Wi-Fi Direct services are unavailable.
- [ ] Remove blocking calls in shutdown paths where easy.
- [ ] Run `dotnet build WDCableWUI.sln`.

User test after Codex finishes:

- [ ] Run on the normal Windows test machine.
- [ ] If possible, disable Wi-Fi or use a machine without Wi-Fi Direct support.
- [ ] Open every page.
- [ ] Change settings even if Wi-Fi Direct is unavailable.
- [ ] Close app while it is scanning or advertising.

Report back:

- Whether startup ever crashes.
- Whether unsupported Wi-Fi Direct gives a clear message.
- Whether Settings still works.
- Whether closing during scan/advertise hangs.

## W-03 - Windows Wi-Fi Direct Lifecycle And Role Diagnostics

Goal: make Windows advertising, scanning, request acceptance, and role detection visible and reliable.

Codex work:

- [ ] Add clearer status logs for advertising, scanning, request received, request accepted/declined, connected, disconnected.
- [ ] Improve or verify group-owner detection. Current code infers role from endpoint IPs.
- [ ] Make `DeviceWatcher`, advertisement publisher, and connection listener stop/dispose cleanly.
- [ ] Prevent stale discovered devices from confusing the UI after stop scan or reconnect.
- [ ] Add visible role and endpoint information for diagnostics.
- [ ] Run `dotnet build WDCableWUI.sln`.

User test after Codex finishes:

- [ ] If you have two Windows PCs, test Windows-to-Windows.
- [ ] If not, wait until F-03/F-04 are done and test Windows-to-Android later.
- [ ] Start advertising on PC A.
- [ ] Scan from PC B.
- [ ] Accept one connection request.
- [ ] Decline one connection request.
- [ ] Disconnect and reconnect 5 times.

Report back:

- Whether devices discover each other.
- Which side showed Group Owner or Client.
- Reconnect success count out of 5.
- Whether declined requests recover cleanly.
- Last visible status lines for failed attempts.

## W-04 - Windows Current-Protocol Socket Lifecycle

Goal: improve reliability of the existing 3-port protocol without changing wire format yet.

Codex work:

- [ ] Make `ConnectionService.InitializeConnectionsAsync` handle partial channel failures with rollback or retry.
- [ ] Add bounded timeouts for connect, accept, header read, payload read, and send flush where safe.
- [ ] Remove blocking `.Wait(...)` calls in listener shutdown paths.
- [ ] Make socket cleanup idempotent.
- [ ] Make `ConnectionsEstablished` fire only once per session.
- [ ] Add clear `Ready`, `Partial`, `Failed`, and `Disconnected` status events.
- [ ] Run `dotnet build WDCableWUI.sln`.

User test after Codex finishes:

- [ ] Test Windows-to-Windows if possible.
- [ ] Test Windows-to-Android after F-04 if possible.
- [ ] Connect, send one chat message each direction.
- [ ] Disconnect and reconnect 5 times.
- [ ] Close the peer app while connected.
- [ ] Turn Wi-Fi off or disable adapter while connected.

Report back:

- Pairing direction and peer platform.
- Whether all three channels became ready.
- Reconnect success count out of 5.
- Whether peer app close was detected.
- Whether app became stuck after Wi-Fi off.

## W-05 - Windows File Transfer Reliability And UI Progress

Goal: make Windows file transfer visible, accurate, and safe.

Codex work:

- [ ] Implement `FileTransferPage` progress handlers.
- [ ] Update transfer records from progress events, not only final sent/received events.
- [ ] Validate download path on startup and fall back cleanly if missing or inaccessible.
- [ ] Handle zero-byte files.
- [ ] Sanitize incoming file names and prevent path traversal.
- [ ] Generate collision-safe received file names.
- [ ] Clean up partial files after cancelled or failed transfers.
- [ ] Prevent simultaneous sends from corrupting the single file-transfer socket.
- [ ] Run `dotnet build WDCableWUI.sln`.

User test after Codex finishes:

- [ ] Send a small text file.
- [ ] Send a zero-byte file if easy.
- [ ] Send the same filename twice.
- [ ] Change download location to a valid folder and receive a file.
- [ ] Try an invalid/missing download path and verify graceful fallback.
- [ ] Interrupt one transfer by closing peer app or disabling Wi-Fi.

Report back:

- File types and sizes tested.
- Whether progress moved and finished correctly.
- Whether received files opened successfully.
- Whether duplicate filenames were preserved safely.
- Whether interrupted transfer left partial files.

## W-06 - Windows Chat And Speed-Test Reliability

Goal: prevent chat and speed-test operations from wedging sockets.

Codex work:

- [ ] Prevent concurrent upload/download speed tests from racing on the same socket.
- [ ] Add timeout and failure result handling for upload and download.
- [ ] Report determinate progress on Windows speed-test UI where possible.
- [ ] Standardize speed units in UI and logs: clearly label Mbps vs MB/s.
- [ ] Ensure unsolicited speed-test data is discarded safely.
- [ ] Add send-failure status for chat messages instead of always treating optimistic UI as success.
- [ ] Preserve peer timestamps consistently.
- [ ] Run `dotnet build WDCableWUI.sln`.

User test after Codex finishes:

- [ ] Connect to a peer.
- [ ] Send 10 chat messages quickly from Windows.
- [ ] Send 10 chat messages quickly from peer to Windows.
- [ ] Run upload test 5 times.
- [ ] Run download test 5 times.
- [ ] Try starting tests repeatedly while one is running.
- [ ] Disconnect during a speed test.

Report back:

- Chat messages sent/received counts.
- Whether any duplicate or missing messages appeared.
- Upload success count out of 5.
- Download success count out of 5.
- Whether repeated clicks started more than one test.
- Whether disconnect during test recovered after reconnect.

## W-07 - Windows Protocol V2 Implementation

Goal: replace fragile string headers with a framed protocol. This must be coordinated with F-07.

Do not start this unless Android F-07 is scheduled next or compatibility mode is included.

Codex work:

- [ ] Add or share a protocol document/reference implementation.
- [ ] Implement frame format with:
  - magic/version
  - message type
  - header length
  - JSON metadata
  - payload length
  - payload bytes
- [ ] Add C# protocol frame tests.
- [ ] Add handshake, heartbeat, chat, file metadata, file chunk, speed-test request/data, cancel, ack, error, and close frame types.
- [ ] Include `sessionId` and `transferId`/`testId`.
- [ ] Keep compatibility with old protocol only if explicitly planned.
- [ ] Run `dotnet build WDCableWUI.sln`.

User test after Codex finishes:

- [ ] Test Windows-to-Windows first if both Windows devices have the W-07 build.
- [ ] Test Android-to-Windows only after Android F-07 is also installed.
- [ ] Connect, chat, transfer small file, run speed test, disconnect, reconnect.

Report back:

- Whether both sides ran protocol v2 build.
- Whether handshake reached ready.
- Which operations succeeded: chat, file, speed test.
- Any protocol error text or diagnostic log.

## W-08 - Windows Localization And Display Cleanup

Goal: remove text corruption and make displayed metadata accurate.

Codex work:

- [ ] Fix mojibake/encoding corruption in `.resw` files.
- [ ] Regenerate resources if needed.
- [ ] Localize hard-coded C# and XAML dialog strings.
- [ ] Align displayed version with `Package.appxmanifest`.
- [ ] Ensure English and Chinese resources have matching keys.
- [ ] Run `dotnet build WDCableWUI.sln`.

User test after Codex finishes:

- [ ] Switch to English and inspect all pages.
- [ ] Switch to Chinese and inspect all pages.
- [ ] Open connection dialogs, errors, settings dialogs, file transfer messages, and speed-test errors where practical.

Report back:

- Any garbled Chinese text.
- Any untranslated English in Chinese mode.
- Any text overflow or clipped labels.
- Displayed app version.

## W-09 - Windows Tests, Diagnostics, And Release Gate

Goal: make future Codex work safer.

Codex work:

- [ ] Add C# tests for protocol parsing, connection state transitions, file metadata handling, and speed-test state.
- [ ] Add fake socket/in-memory stream tests for chat, speed test, and file transfer where practical.
- [ ] Add diagnostics export or copy-log support.
- [ ] Add a concise Windows manual QA checklist to project docs.
- [ ] Make `dotnet build WDCableWUI.sln` clean enough to be a release gate.

User test after Codex finishes:

- [ ] Trigger a normal connection and export/copy logs.
- [ ] Trigger one failure, such as peer app closed, and export/copy logs.
- [ ] Run the manual Windows QA checklist.

Report back:

- Whether logs include timestamp, platform, role, session id, channel, and last error.
- Whether the manual checklist has unclear or missing steps.
