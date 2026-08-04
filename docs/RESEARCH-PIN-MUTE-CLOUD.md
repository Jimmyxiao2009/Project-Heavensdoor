# Research: pin / mute cloud sync

**Status:** research track. Product path stores pin/mute on the RealServer gateway per NapCat account (`conv_prefs_*.json`).

## Current implementation

- Shell → `setConversationFlags` / `markConversationRead`
- RealServer `NapCatSessionManager` persists prefs next to the process
- Multi-device same gateway shares prefs; not Tencent-side cloud sync

## Why not “true QQ cloud pin/mute”

Official clients sync session flags via proprietary NT packets. OneBot / NapCat public APIs do not expose a stable set-top / set-mute that mirrors the official client.

## Next steps (if needed)

1. Watch NapCat / OneBot extensions for session-flag APIs.
2. Keep gateway-local prefs as source of truth for this product.
3. Optional: multi-device via shared RealServer prefs file / small sync store — not protocol reverse-engineering.

## Out of scope

- Reintroducing a third-party protocol stack just for session flags.
