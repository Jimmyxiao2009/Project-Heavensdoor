# Architecture

## Runtime

```
Shell (UWP)                     PC
───────────                     ──
AppServices.Chat ──ws:8765──► RealServer (WireRpc → WireDispatch)
AppServices.Gateway                 │
                                    ▼
                              NapCatSessionManager
                                    │ OneBot HTTP/WS
                                    ▼
                              NapCat + NTQQ
```

**ServerHost** is the operator panel: starts RealServer + NapCat, sets access password.

## Server modules

| Module | Responsibility |
|--------|----------------|
| `Program.cs` | HTTP/WS accept, auth frame, health |
| `WireRpc` | Parse request → result JSON |
| `Wire/WireDispatch` | `type` → `ISessionBackend` |
| `Wire/WireJson` | Field readers |
| `SessionHub` / `AccountSession` / `ClientConnection` | Connection isolation + ordered send |
| `NapCat/*` | OneBot adapter (Events/Read/Write/Admin/Helpers) |

**Feature checklist (server):** `ISessionBackend` method → NapCat impl → `WireDispatch` case → add to `KnownTypes`.

## Shell modules

| Module | Responsibility |
|--------|----------------|
| `AppServices` | Composition root |
| `IChatService` | Mock-safe chat surface |
| `IGatewayService` | Full RealServer surface |
| `RemoteChatService` (+ Api.* partials) | WS client |
| `GatewayEndpoint` | Pure host/port parse (tested) |
| `Views/*` partials | UI events |
| `ViewModels/*` | State |

**Feature checklist (shell):** `IGatewayService` (+ impl) → VM/page via `AppServices.Gateway`.

## Tests

```
dotnet test server/QQReborn.RealServer.Tests
dotnet test shell/QQReborn.Shell.Logic.Tests
```

- RealServer: WireAuth, BackendFactory, KnownTypes inventory  
- Shell.Logic: GatewayEndpoint (no UWP host required)
