# Server（PC 侧）

只两件事：

| 工程 | 作用 |
|------|------|
| **QQReborn.RealServer** | 网关 `ws://:8765`，对接本机 NapCat，给 Shell 用 |
| **QQReborn.ServerHost** | 管家面板：一键安装 NapCat、配置 OneBot、启停网关 |

```
ServerHost（面板）
    └── RealServer :8765  ──OneBot──►  NapCat + NTQQ
              ▲
              │ ws + 访问密码
            Shell
```

## 目录

```
server/
  QQReborn.RealServer/   网关
  QQReborn.ServerHost/   面板
  tools/                 start-server-host / start-user-gateway / build-server-msi
  installer/             WiX MSI（可选打包）
  README.md
```

## 启动

```powershell
# 面板（推荐）
powershell -ExecutionPolicy Bypass -File server\tools\start-server-host.ps1 -Publish

# 仅网关
powershell -ExecutionPolicy Bypass -File server\tools\start-user-gateway.ps1 -AccessPassword "xxx"
```

面板内点 **「一键安装并启动」** 即可：检测 QQNT → 自动下载 NapCat.Shell → 写入 OneBot 3000/3001 → 启动 NapCat → 启动网关。

也可手动：本机 NTQQ + NapCat 已登录（HTTP 3000 / 事件 3001）后只点「启动网关」。

## RealServer 源码要点

- `Program.cs` — WS 接入与鉴权  
- `WireRpc.cs` — Shell 请求路由  
- `SessionHub` / `NapCat/*` — 会话与 OneBot  

出门只映射 **8765**，见 `docs/USER-GATEWAY-SAKURAFRP.md`。
