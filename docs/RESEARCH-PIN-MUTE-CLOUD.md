# Cloud Pin / Mute Research (Plan B)

**Goal:** make QQ-Reborn session **pin (置顶)** and **mute (消息免打扰)** sync with official QQ endpoints (phone QQ / kids watch), not only RealServer local prefs.

**Status:** research track. No stable public LagrangeV2 API today.

---

## 0. Product truth

| Layer | Today |
|-------|--------|
| UWP UI toggles | ✅ MainPage long-press + GroupInfoPage switches |
| RealServer | ✅ `setConversationFlags` → `conv_prefs_{uin}.json` |
| Multi-client on **same RealServer** | ✅ local authority |
| Official phone / kids watch cloud | ❌ not wired |

Kids watch works because it is an **official client** with full NT session-config sync. We are on Lagrange/NT bot path and currently only store local prefs.

---

## 1. What we already ruled out

1. **Lagrange.Core public Ext** (`OperationExt` / `MessageExt` / `BotExt`)
   - No set/get conversation sticky or per-session DND.

2. **Lagrange.Milky feature list**
   - No `/set_conversation_pin`, `/set_conversation_mute`, no pin list fetch.
   - Group mute items (`set_group_member_mute`, `set_group_whole_mute`) are **group admin mutes**, not chat-list “消息免打扰”.

3. **`SsoInfoSync.RegisterInfo.SetMute`**
   - Login/register field only (`InfoSyncService` hardcodes `SetMute = 0`).
   - Not a per-conversation setting.

4. **Repo text search**
   - No `IsTop` / `TopFlag` / `RecentContact` / `disturb` session-flag packet types in LagrangeV2 sources.

Conclusion: B requires **reverse engineering** (or waiting for upstream Core), not a thin wrap of existing APIs.

---

## 2. High-value command candidates (sign whitelist)

These appear in our `TokenSignProvider` / sign allowlist. They are **suspects**, not proven pin/mute cmds:

| Command / family | Why interesting | Notes |
|------------------|-----------------|-------|
| `oidb_0x5d6_19`, `oidb_0x5d6_21` | Historically used for friend/session attribute updates in QQ reverse communities | **Priority capture target** when toggling mute/pin |
| `OidbSvc.0x592_*` (large family) | Broad “settings/profile-ish” OIDB cluster in whitelist | Filter by timestamp correlation |
| `OidbSvc.0x89a_0` / `OidbSvcTrpcTcp.0x89a_*` | Group setting surface; Core already maps `0x89a_15` to **group rename** | Whole-group mute may live nearby; still not chat pin |
| `OidbSvc.0x8a0_0` | Group-adjacent | Capture only |
| `OidbSvc.0x587_normalNightSet` | Name looks DND-ish | Likely **global night mode**, not per chat |
| `SsoSnsSession.Cmd0x3_SubCmd0x1_FuncGetBlockList` | Session/SNS | Block list, not pin |
| `RegPrxySvc.infoSync` / `trpc.msg.register_proxy.RegisterProxy.*` | Session register/proxy | May carry flags as side data during login sync |
| `ConfigPushSvc.PushResp` | Server-pushed config | Watch for **push after** official pin/mute |

Use capture correlation, not guessing.

---

## 3. Capture plan (required before coding cloud write)

### 3.1 Devices

- **A (source of truth):** official phone QQ **or** kids watch (your proven working client)
- **B (under test):** QQ-Reborn RealServer + UWP

### 3.2 Actions to capture (script)

On official client, for **one friend** and **one group**, do each action twice:

1. Pin chat → wait 3s → unpin  
2. Enable 消息免打扰 → wait 3s → disable  
3. Cold start official client (login sync pull)  
4. Optional: toggle on official, then open QQ-Reborn list (see if we can at least **read** later)

Record: wall-clock time, peer uin/group uin, action.

### 3.3 How to capture command names

**Path 1 — easiest with our stack (partial):**

- Keep RealServer online with `LocalSignProxy`.
- Proxy now logs **command histogram + recent commands** to  
  `tools/runtime_logs/sign-commands.jsonl` and `GET /commands`.
- Limitation: only commands **our bot** signs appear. Official kids watch traffic will **not** hit this proxy unless we MITM the official client (usually hard).

**Path 2 — real B capture (recommended):**

- Android phone QQ with Frida / packet logger / rooted MITM (if TLS pinned, need SSL unpin).
- Or PC NT QQ + existing community packet tools if you already use them.
- Export: timestamp, `cmd`, request hex/pb, response hex/pb, action label.

**Path 3 — literature assist:**

- Search public Lagrange / Mirai / llOneBot / chronocat issues for “置顶” “免打扰” “0x5d6” “session flag”.
- Treat as leads only; re-verify on current NT.

### 3.4 Success criteria for RE

We only implement after we have:

1. **Write pin** command + fields (friend + group if different)  
2. **Write mute** command + fields  
3. **Read/list** command that returns current pin/mute set after login  
4. At least one **round-trip proof**: official toggle → our parse, or our send → official UI updates

Without (3), we can write-but-not-merge; without (1)(2), only local prefs.

---

## 4. Implementation skeleton (after RE)

Keep local prefs as fallback forever.

```
UWP toggle
  → WS setConversationFlags
     → RealServer:
        1) update conv_prefs (local, always)
        2) if CloudFlags enabled && mapped API known:
             call Lagrange custom OIDB helper
        3) broadcast conversationFlagsChanged
  ← login/populate:
        merge cloud flags over local when cloud available
```

### Suggested files (future)

| File | Role |
|------|------|
| `server/QQReborn.RealServer/CloudConversationFlags.cs` | OIDB encode/decode + send |
| `BotSessionManager.SetConversationFlags` | dual-write local+cloud |
| `BotSessionManager.PopulateConversationsAsync` | merge cloud list |
| `shell/.../RemoteChatService` | handle `conversationFlagsChanged` push (optional polish) |
| `docs/RESEARCH-PIN-MUTE-CLOUD.md` | this doc; append confirmed cmds |

### API shape (internal)

```csharp
// aspirational — not implemented until capture proves commands
Task<(bool ok, string? error)> CloudSetPinAsync(string conversationId, bool pinned);
Task<(bool ok, string? error)> CloudSetMuteAsync(string conversationId, bool muted);
Task<IReadOnlyDictionary<string, (bool pinned, bool muted)>> CloudFetchFlagsAsync();
```

ConversationId mapping must stay consistent with today: `f{uin}` / `g{uin}`.

---

## 5. Risk notes

- Wrong OIDB = silent no-op or account risk; start on **secondary QQ**.  
- Sign whitelist may need new cmds if capture finds ones outside TokenSignProvider.  
- Friend pin/mute and group pin/mute are often **different** services.  
- Read path may only appear at login (`infoSync` / config push), not as a dedicated “get pins” API.

---

## 6. Near-term engineering (done / next)

### Done in this track

- Research conclusion documented (this file).
- `LocalSignProxy` command capture: `GET /commands`, append `tools/runtime_logs/sign-commands.jsonl`.

### Next human steps

1. Capture official pin/mute packets (Path 2).  
2. Paste cmd names + sample hex into this doc §7.  
3. Implement `CloudConversationFlags` behind a feature flag.  
4. Keep UI copy honest: “同步中/仅本机” until cloud confirmed.

---

## 7. Capture log (fill in)

| When (UTC+8) | Client | Action | Peer | cmd | result |
|--------------|--------|--------|------|-----|--------|
| | kids watch / phone QQ | pin on | | | |
| | | pin off | | | |
| | | mute on | | | |
| | | mute off | | | |
| | | cold login | | | |

### Confirmed (empty until capture)

```
WRITE_PIN   =
WRITE_MUTE  =
READ_FLAGS  =
```

---

## 8. Decision rule

- **Do not** claim cloud sync in UI until §7 confirmed.  
- **Do** keep local prefs as source of truth for QQ-Reborn multi-device.  
- If capture fails for >1 serious attempt, fall back to Plan A (RealServer-only realtime push) without pretending cloud works.

## 9. Gateway multi-client sync (implemented 2026-07-25)

While official NTQQ cloud write is still blocked (no NapCat pin/mute API; `get_recent_contact` strips flags),
RealServer now broadcasts:

```json
{ "type": "conversationFlagsChanged", "data": { "conversationId": "f123", "isPinned": true, "isMuted": false } }
```

after every successful `setConversationFlags`. All Shells on the same gateway update list order,
mute badges, and `NotificationMuteGate` immediately. Local `conv_prefs_napcat_{uin}.json` remains
the source of truth for QQ-Reborn devices.

Official desktop/phone QQ still will **not** change until §7 capture lands a write path.
