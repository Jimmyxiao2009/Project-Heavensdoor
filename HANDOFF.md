# QQ-Reborn Handoff

**Last updated:** 2026-07-21 (post–Gemini cleanup pass)  
**Audience:** next agent / human continuing this repo  
**Product:** UWP QQ client (Metro-style) + ASP.NET RealServer bridge over LagrangeV2 (Linux protocol)

### Cleanup pass notes (this session)

**Removed (cannot do / fake only):**

- Voice/Video call pages (`VoiceCallPage`, `VideoCallPage`, `CallArgs`) and all UI entries (chat header, contact detail).
- “发起群聊” / “扫一扫” dead menu items (no protocol).
- Silent **mock empty WAV** for SILK voice (was pretending to play).

**Correction vs earlier HANDOFF (re-read LagrangeV2 + Milky feature list):**

Some items previously labeled “cannot” **are available in Core** — see §16. Notable:

| Item | Earlier claim | Actual (Core / Milky) |
|------|---------------|------------------------|
| @ Mention | ⚠️ TBD | ✅ `MessageBuilder.Mention` — **can implement** |
| Group reaction | deleted as local-only | ✅ `SetGroupReaction` — **can implement** (group msg) |
| Group join accept/reject | weak | ✅ `SetGroupNotification` — **can implement** (server already has hooks) |
| Multi-forward / 合并转发 | placeholder | ✅ `MessageBuilder.MultiMsg` — **can implement** |
| Send video | partial | ✅ `MessageBuilder.Video` — **can implement** (Milky outgoing video unchecked, Core has entity) |
| File send/recv group | 🔵 | ✅ Core APIs exist; wired partially |
| Friend accept request | UI says unsupported | ❌ still **no** Core accept API (event only); Milky also `[ ]` |
| Mute/kick/admin | maybe | ❌ Milky `[ ]` and no public Core ext |
| Session pin / DND cloud | local only | ❌ still **no** API in Core/Milky feature list |
| Moments / Channel | spike | ❌ still no high-level Core API |

**Still blocked without tools / out of scope:**

- **SILK → WAV** needs `silk_v3_decoder` (or Lagrange native codec) on RealServer host + `ffmpeg`.
- Moments/Channel protocol names only in sign whitelist.
- Payment / real A-V calls.

---

## 16. LagrangeV2 capability truth table (authoritative for this fork)

Sources: `_ref/LagrangeV2/Lagrange.Core/Common/Interface/{Message,Operation,Bot}Ext.cs`, `MessageBuilder.cs`, `Lagrange.Milky/README.md` Feature List.

### ✅ Core has public API — we should use / finish wiring

| Capability | Core API | Our app status |
|------------|----------|----------------|
| Friend/group send text/image/record/reply | `Send*Message` + `MessageBuilder` | ✅ mostly |
| **@ mention / mention all** | `MessageBuilder.Mention` | ⚠️ draft only — **finish wire** |
| **Merge forward (MultiMsg)** | `MessageBuilder.MultiMsg` | ⚠️ partial / UI forward may only re-send text |
| Roam / group history | `GetRoamMessage`, `GetGroupMessage`, `GetC2CMessage` | ✅ |
| Recall | `RecallMessage` | ✅ |
| Nudge | `SendFriendNudge` / `SendGroupNudge` | ✅ |
| **Group rename / member card / special title** | `GroupRename`, `GroupMemberRename`, `GroupSetSpecialTitle` | 🔵 server hooks may exist — finish UI |
| **Group reaction** | `SetGroupReaction` | ❌ UI removed as “fake” — **restore with real API** |
| **Group notifications list + operate** | `FetchGroupNotifications`, `SetGroupNotification` | 🔵 server started — finish UI |
| Group quit | `GroupQuit` | ✅ |
| Upload private/group file | `SendFriendFile`, `SendGroupFile` | ⚠️ partial |
| Group file download/delete/move | `GroupFSDownload/Delete/Move` | ⚠️ download partial |
| Rich media URL | `GetNTV2RichMediaUrl` | ✅ |
| Set bot avatar | `SetBotAvatar` | ✅ |
| Profile stranger | `FetchStranger` | ⚠️ |
| Cookies / client key | `FetchCookies`, `FetchClientKey` | unused |
| Send video entity | `MessageBuilder.Video` | ❌ not wired |
| Friend request **event** | `BotFriendRequestEvent` | ⚠️ list may be incomplete |

### ❌ Not in Core public API / Milky unchecked — do not promise

| Capability | Evidence |
|------------|----------|
| Accept/reject **friend** request | Milky `[ ] get/accept/reject_friend_request`; Core only **event** |
| Kick / mute member / whole mute / set admin | Milky `[ ]` all |
| Group announcement CRUD | Milky `[ ]` |
| Essence messages | Milky `[ ]` |
| Private file download URL | Milky `[ ] get_private_file_download_url` |
| Mark message as read (protocol) | Milky `[ ]` |
| Session **pin** / **DND** cloud sync | nowhere in Core Ext or Milky list |
| Moments / QZone / Channel | sign whitelist only, no Core Ext |
| Real voice/video call | not in stack |
| Payment | not in stack |

### Codec note (voice)

`Lagrange.Codec.AudioCodec.EncodeSilkV3` exists but needs native **`LagrangeCodec`** (`silk_encode` / `silk_decode`). Without that binary (or `silk_v3_decoder`+ffmpeg), voice remain best-effort AMR/ffmpeg only.

---

## 1. Architecture (read this first)

```
┌─────────────────────┐     WebSocket :8765      ┌──────────────────────────┐
│  QQReborn.App (UWP) │ ◄──────────────────────► │  QQReborn.RealServer     │
│  shell/QQReborn.App │   JSON request/push      │  server/.../RealServer   │
└─────────────────────┘                          │         │                │
                                                 │    LagrangeV2 BotContext │
                                                 │         │                │
                                                 │         ▼                │
                                                 │   Tencent QQ (NT)        │
                                                 └──────────────────────────┘
                                                            ▲
                 optional local proxy                       │
┌─────────────────────┐   serial + retry                    │
│ tools/LocalSignProxy│ ────────────────────────────────────┘
│ :18488 → upstream   │   POST /api/sign/sec-sign
│ sign.lagrangecore.org│
└─────────────────────┘
```

- **RealServer** holds the real QQ session (keystore, friends, messages). It will be **deployed on a remote server** later — do **not** spend effort on local Windows-service / watchdog / “keep RealServer alive on the laptop” unless the user asks.
- **UWP App** is a thin client: WS to RealServer only. No direct Tencent sockets from the phone/PC shell.
- **Sign:** `TokenSignProvider` → `POST {url}/api/sign/sec-sign` + `Authorization: Bearer {token}`. Concurrent stampede to public sign → HTTP 401. LocalSignProxy serializes + retries. True offline energy is **not** available without reverse-engineered libs.

### Wire protocol (high level)

- Client → server: `{ "id", "type", ...fields }` over WS text frames  
- Server → client: `{ "type":"result", "id", "data"|"error" }` or pushes: `messageReceived`, `qrCode`, `loginStatus`, `typing`  
- Handlers live in `server/QQReborn.RealServer/Program.cs` switch on `type`.

---

## 2. Repo map

| Path | Role |
|------|------|
| `shell/QQReborn.App/` | UWP client (TargetPlatform **10.0.15063**) |
| `server/QQReborn.RealServer/` | Live LagrangeV2 bridge |
| `server/QQReborn.FakeServer/` | Demo mock (same wire, no QQ) |
| `server/QQReborn.Signing/` | `TokenSignProvider` |
| `tools/LocalSignProxy/` | Local sign front (serialize + retry) |
| `tools/start-local-sign.ps1` | Start proxy |
| `_ref/LagrangeV2/` | Protocol stack (do not “fix” casually) |
| `ACCOUNT-NOTES.local.md` | Local secrets / sign token notes (**do not commit if public**) |
| `HANDOFF.md` | This file |

### App ↔ backend switch

`shell/QQReborn.App/App.xaml.cs`:

```csharp
private const bool UseRemoteBackend = true; // false → MockChatService
public static IChatService ChatService { get; } = ...
```

---

## 3. How to run (dev)

### Multi-protocol backend (Lagrange / NapCat)

App wire is unchanged. RealServer selects an `ISessionBackend` at process start:

| Backend | How |
|---------|-----|
| **lagrange** (default) | existing `BotSessionManager` + sign |
| **napcat** | `NapCatSessionManager` → OneBot HTTP + event WS |

```powershell
# Lagrange (default)
dotnet run --project server\QQReborn.RealServer --no-launch-profile

# NapCat
$env:QQREBORN_BACKEND="napcat"
$env:NAPCAT_HTTP="http://127.0.0.1:3000"
$env:NAPCAT_WS="ws://127.0.0.1:3001"
powershell -ExecutionPolicy Bypass -File tools\start-server-napcat.ps1
```

See **`docs/BACKEND-SWITCH.md`** for archive/branch notes and NapCat setup.

### Recommended: Server Host 控制面板（WPF）

一键启动 RealServer + LocalSignProxy、看日志、测空间 Webhook：

```powershell
powershell -ExecutionPolicy Bypass -File tools\start-server-host.ps1
# 发布单文件到 publish\ServerHost：
powershell -ExecutionPolicy Bypass -File tools\start-server-host.ps1 -Publish
```

项目：`server/QQReborn.ServerHost/`（面板可选 **Lagrange / NapCat**）

### RealServer (CLI)

```powershell
dotnet run --project server\QQReborn.RealServer\QQReborn.RealServer.csproj -c Release --no-launch-profile
# listens http://0.0.0.0:8765  ws://localhost:8765/ws
# GET /backend → { "backend": "lagrange"|"napcat" }
```

If port locked: kill PID on 8765, rebuild, rerun. Session/login state is **in-process** — restart = need re-login / keystore resume.

### Local sign proxy (recommended)

```powershell
powershell -ExecutionPolicy Bypass -File tools\start-local-sign.ps1
# http://127.0.0.1:18488  health: /health
```

App settings → 账号:

- 使用自建签名服务器: **开**
- URL: `http://127.0.0.1:18488`
- API Key: optional (proxy injects UpstreamToken from `tools/LocalSignProxy/appsettings.json`)
- After change: **re-login / ConfigureAccount** so RealServer gets new `signUrl`

### UWP app

```powershell
# Build package (x86 Debug common on desktop)
msbuild shell\QQReborn.App\QQReborn.App.csproj /p:Configuration=Debug /p:Platform=x86 /p:AppxBundle=Never

# Install (version same → remove first)
Remove-AppxPackage (Get-AppxPackage QQReborn.App).PackageFullName
Add-AppxPackage shell\QQReborn.App\AppPackages\QQReborn.App_0.1.0.0_x86_Debug_Test\QQReborn.App_0.1.0.0_x86_Debug.appx

# Launch
$p = Get-AppxPackage QQReborn.App
Start-Process "shell:AppsFolder\$($p.PackageFamilyName)!App"
```

**NuGet / platform note:** project min version 15063; if restore fails on 10240 assets, align package assets to 15063.

---

## 4. Product constraints (owner decisions)

| Constraint | Implication |
|------------|-------------|
| Server later on **remote host** | No local ops work for “keep server process up”; design WS URL as configurable (Settings already has remote server address patterns). |
| **No** 转账 / 红包 / 钱包 | Do not implement payment-related protocol or UI beyond hiding stubs. |
| **Voice:** can **send**, cannot **receive/play** reliably | Priority fix: SILK download + decode → playable WAV; encode outbound to SILK for real QQ peers. |
| 置顶 / 免打扰 | **Local only** today; cloud sync **not** available via LagrangeV2 public API — investigate before promising. |
| 频道 / QQ 动态 | **Consider later**; only sign-whitelist cmd names exist, **no** high-level Lagrange.Core API. |

---

## 5. What’s already working

### Account / session

- QR login, keystore resume, `configureAccount(signUrl, signToken, signUin)`
- Login status + QR pushes to App

### Chat core

- Conversations, contacts, group members (basic)
- Text send/receive, reply quote, recall, nudge
- Image send (base64 JPEG), receive CDN URL, full-screen viewer: zoom / pan / fit / 1:1 / save / **gallery swipe** in conversation
- History: `getEarlierMessages` (friend roam Direction=1 older; group sequence page)
- Search: **localOnly** path (must not stampede sign via cloud backfill)
- Pin / mute: **local prefs** on RealServer (`setConversationFlags`, `conv_prefs_{uin}.json`)
- Unread: server-side increment on incoming + `markConversationRead` + client `UnreadBadgeStore` (app-lifetime while process alive)
- Avatar set, media URL resolve `getMediaUrl` for image/video/record uuid

### Tooling

- LocalSignProxy + TokenSignProvider gate/retry to reduce 401 under concurrency

### UI shells (not real protocol)

- Voice/Video **call pages** (UI only)
- Moments UI largely **Mock**
- Some add-friend / scan menus are stubs

---

## 6. Capability matrix (vs real QQ / LagrangeV2)

Legend: ✅ done · ⚠️ partial · 🔵 Lagrange API exists, not wired · 🟡 only sign whitelist / packets · ❌ out of scope or impossible with current public API

### Messaging

| Feature | Status | Notes |
|---------|--------|--------|
| Text | ✅ | |
| Reply | ✅ | |
| Image send/receive/view | ✅ | Gallery swipe done |
| Voice **send** | ⚠️ | Path exists; format may not be silk |
| Voice **receive/play** | ❌ | CDN is SILK; UWP MediaPlayer cannot play; need decode |
| Sticker (local assets as image) | ⚠️ | |
| Recall | ✅ | |
| Nudge | ✅ | |
| File send | 🔵 | `SendFriendFile` / `SendGroupFile` |
| File receive/download | 🔵 | e.g. `GroupFSDownload` |
| Forward | ❌ | Menu may exist; no end-to-end |
| Mixed text+image / multi-image msg | ⚠️ | Only single `ImageEntity` promoted to Image |
| Video | ⚠️ | getMediaUrl + VideoPlayerPage |
| Location | ⚠️ | Sent as text |
| Cards / multi-forward | ⚠️ | Placeholder text |
| @ Mention | ⚠️ | Draft picker; real entity TBD |

### List / unread / notifications

| Feature | Status | Notes |
|---------|--------|--------|
| Unread while **inside app** (other chat/settings) | ⚠️ | Server unread + UnreadBadgeStore |
| System Toast / Action Center | ❌ | Not implemented |
| Tile badge | ❌ | Not implemented |
| Resuming → reconnect + refresh | ❌ | Suspending hook empty of useful work |
| Pin / mute **local** | ✅ | |
| Pin / mute **cloud / phone sync** | ❌ | No public Lagrange API found |

### History / search

| Feature | Status | Notes |
|---------|--------|--------|
| Roam / group history scroll-up | ✅ | Careful with sign load |
| Local search | ✅ | `localOnly` |
| Cloud search | ❌ | |

### Group / contact

| Feature | Status | Notes |
|---------|--------|--------|
| Member list, quit group | ✅ | |
| Rename group / card / special title | 🔵 | `GroupRename`, `GroupMemberRename`, `GroupSetSpecialTitle` |
| Group join requests | 🔵 | `FetchGroupNotifications` / `SetGroupNotification` |
| Kick / mute member | 🟡/❌ | Not in public OperationExt surface |

### Moments / Channel

| Feature | Status | Notes |
|---------|--------|--------|
| QQ 动态 / 空间 feeds | 🟡 | `FeedCloudSvr.*`, `SQQzoneSvc.*` in sign whitelist only |
| 频道 QChannel | 🟡 | `QChannelSvr.*` whitelist only |
| App Moments UI | ⚠️ | Mock |

### Explicitly out of scope

- 转账 / 红包 / 钱包  
- Real A/V calls (keep UI hidden or labeled demo)  
- Server process supervisor on developer laptop  

---

## 7. Pin / mute investigation result

- RealServer stores pin/mute in **local JSON prefs** per bot uin (`SetConversationFlags`).
- LagrangeV2 public APIs (`OperationExt` / `MessageExt`) expose **no** “set conversation sticky / DND” or “fetch phone pin list”.
- `SsoInfoSync.SetMute` is a **login info-sync field**, not a per-conversation settings API.
- **Product copy:** “仅本机” is correct. Cloud sync only if a future reverse-engineering spike finds a stable OIDB — do not schedule as P0.

---

## 8. Voice receive gap (owner-reported: send works, receive doesn’t)

```
Send:  App records m4a → base64 → MessageBuilder.Record → Highway
Recv:  RecordEntity → FileUrl / FileUuid → CDN bytes are typically SILK
       → App MediaPlayer(URI) fails or silent failure
```

### Recommended fix (server-side decode preferred)

1. Confirm wire messages use `contentType: "Voice"` (MapEntities already promotes sole `RecordEntity`).
2. On play (or on receive): `getMediaUrl` → download bytes.
3. **Decode SILK → WAV** on RealServer (or use `Lagrange.Codec` + native `LagrangeCodec` if binary is obtained).
4. Return a **playable URL or base64 WAV** to the client (new field or `getMediaUrl` mode).
5. Align **outbound** encode to SILK so peer official QQ accepts reliably.

Native codec: `Lagrange.Codec` P/Invokes `LagrangeCodec` (`silk_encode` / `silk_decode` / `audio_to_pcm`). **Binary is not in this repo.** Options: build from LagrangeCodec sources, vendor a prebuilt for server OS (Linux when deployed), or use another silk decoder on the server.

Client today: `ConversationPage.Voice_Tapped` resolves URL then `MediaPlayer`; on failure shows system line about SILK.

---

## 9. Unread / “background” reality

| Layer | Status |
|-------|--------|
| In-app navigation (not on MainPage) | UnreadBadgeStore + server unread |
| App process suspended / killed | **No** client receive; depends on **remote RealServer** staying online |
| OS Toast / lock-screen push | **Not done** |
| RealServer message persistence across process restart | Mostly **in-memory**; restart loses session cache (keystore may resume login) |

For remote deploy: RealServer online ⇒ QQ session can receive while App is dead; App on resume should **reconnect + getConversations** (still need Resuming/refresh work on client).

Key files:

- `shell/.../Services/UnreadBadgeStore.cs`
- `shell/.../ViewModels/MainViewModel.cs` (merge Max(server, store))
- `server/.../BotSessionManager.cs` (`BumpConversationOrCreate(..., incrementUnread)`, `MarkConversationRead`, clear on `getMessages` when not localOnly)

---

## 10. Suggested roadmap

### P0 — chat usable day-to-day

1. **Voice receive/play** (SILK → WAV) + outbound SILK encode  
2. **Toast** for non-active, non-outgoing, non-muted (while App process alive / connected)  
3. **Resuming / foreground:** force WS reconnect + soft refresh conversations/unread  
4. **File send + receive** (Lagrange APIs ready)  
5. **Forward** to a conversation  

### P1 — group / polish

6. Group rename / member card / special title  
7. Group notification accept/reject  
8. Multi-image / mixed entity rendering  
9. Real `@` Mention entity  
10. Hide payment/call stubs or label as unavailable  

### P2 — Moments / Channel (optional spikes)

11. **Spike:** capture/replay FeedCloud or SQQzone list response with working sign; document packet shape  
12. **Spike:** QChannel list/read  
13. Only then UI; do not block P0  

### Do not schedule

- Payment  
- Cloud pin/mute without new API evidence  
- Local RealServer babysitting (remote deploy is owner’s job)  

---

## 11. Important code pointers

| Concern | Where |
|---------|--------|
| WS dispatch | `server/QQReborn.RealServer/Program.cs` |
| Bot session, send, history, unread, map entities | `server/QQReborn.RealServer/BotSessionManager.cs` |
| Sign HTTP | `server/QQReborn.Signing/TokenSignProvider.cs` |
| Client WS + send image/voice | `shell/.../Services/RemoteChatService.cs` |
| Unread store | `shell/.../Services/UnreadBadgeStore.cs` |
| Image gallery viewer | `shell/.../Views/ImageViewerPage.xaml(.cs)` |
| Chat UI / voice record-play | `shell/.../Views/ConversationPage.xaml(.cs)` |
| Lagrange send/file/roam APIs | `_ref/LagrangeV2/Lagrange.Core/Common/Interface/MessageExt.cs` |
| Lagrange ops | `_ref/LagrangeV2/.../OperationExt.cs` |
| Audio codec C# (needs native) | `_ref/LagrangeV2/Lagrange.Codec/AudioCodec.cs` |

### RealServer request types (as of handoff)

`getSelf`, `getConversations`, `getContacts`, `getMessages`, `getGroupMembers`, `getFriendRequests`, `acceptFriendRequest`, `getUserProfile`, `getEarlierMessages`, `recallMessage`, `quitGroup`, `nudge`, `setAvatar`, `getMediaUrl`, `send`, `configureAccount`, `setConversationFlags`, `markConversationRead`

### Send content types supported by RealServer

`Text`, `Location` (as text), `Image`, `Sticker`, `Voice` (raw bytes; silk preferred)

---

## 12. Known pitfalls

1. **Sign 401:** concurrent energy to public signer. Use LocalSignProxy or serialize; TokenSignProvider already gates.  
2. **Search + empty getMessages:** must use `localOnly: true` or cloud backfill stamps sign and freezes UI.  
3. **MainPage Detach:** leaves list unsubscribed; UnreadBadgeStore + server unread + SoftRefresh on re-enter mitigate.  
4. **ActiveConversationId:** stuck value suppresses unread forever — ConversationPage must clear on leave.  
5. **Friend C2C message ids:** client vs server sequence prefixes (`WireMessageId`) — do not “simplify” without reading comments in BotSessionManager.  
6. **UWP 15063:** limited APIs; test on target platform.  
7. **Package version 0.1.0.0:** reinstall often needs `Remove-AppxPackage` first.  
8. **FakeServer vs RealServer:** same wire; features like `configureAccount` only on RealServer.

---

## 13. Secrets / local notes

- Sign token & usage notes: `ACCOUNT-NOTES.local.md` (keep private).  
- LocalSignProxy token: `tools/LocalSignProxy/appsettings.json` (`SignProxy:UpstreamToken`).  
- Keystore path: under RealServer content root (see BotSessionManager login path) — remote deploy must persist this volume.

---

## 14. Space webhook (动态接入路径)

Lagrange 无 空间高层 API。RealServer 提供：

```
POST http://<host>:8765/webhook/space
Content-Type: application/json

{ "author": "张三", "text": "内容", "images": ["https://..."], "time": "2026-07-21T12:00:00Z" }
# 或 { "items": [ {...}, {...} ] }
```

- 入库后 `getMoments` / `getSpaceFeed` 给 App  
- 推送 `spaceFeedUpdated`  
- App `RemoteMomentsService` 读该 feed  

Web 空间 / 爬虫把更新 POST 到此即可。

## 14b. Immediate next task

1. SILK 解码工具链（`silk_v3_decoder` + ffmpeg）部署到 RealServer 主机  
2. 频道 / 真空间协议 spike（可选）  
3. 合并转发 UI 体验打磨

---

## 15. Handoff checklist for the next agent

- [ ] Read this file + `ACCOUNT-NOTES.local.md` (if present)  
- [ ] Confirm RealServer `:8765` and optional sign proxy `:18488`  
- [ ] Confirm App points at correct WS host (local vs remote) and sign URL  
- [ ] Re-login after server restart  
- [ ] Do **not** invent pin/mute cloud sync without API evidence  
- [ ] Do **not** implement payment features  
- [ ] Prefer server-side media transcode for voice when deploying to Linux server  
- [ ] Keep search on localOnly; never backfill all convs for search  

---

*End of handoff.*
