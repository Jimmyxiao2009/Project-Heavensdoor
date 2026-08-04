# QQ Reborn

Windows 10 Mobile 上的 QQ **瘦客户端**：号挂在家里电脑的 **NTQQ + NapCat**，手机只连网关。

```
server/  RealServer 网关 + ServerHost 面板
shell/   UWP App 本体
```

## 架构

```
┌─────────────────────────────┐
│  PC · ServerHost 面板         │
│    └─ RealServer :8765        │── OneBot ──► NapCat + NTQQ
└──────────────┬──────────────┘
               │ ws + 访问密码
               ▼
         shell · QQReborn.App
```

## 工程

| 工程 | 路径 |
|------|------|
| 网关 | `server/QQReborn.RealServer` |
| 面板 | `server/QQReborn.ServerHost` |
| 客户端 | `shell/QQReborn.App` |
| 测试 | `server/QQReborn.RealServer.Tests`、`shell/QQReborn.Shell.Logic.Tests` |

解决方案：`QQReborn.sln`。约定见 [`docs/MAINTAINABILITY.md`](docs/MAINTAINABILITY.md)、[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)。

```powershell
dotnet test server/QQReborn.RealServer.Tests
dotnet test shell/QQReborn.Shell.Logic.Tests
```

## 快速开始

### 电脑

1. 登录 NTQQ + [NapCat](https://github.com/NapNeko/NapCatQQ)（HTTP 3000 / WS 3001）
2. 启动面板：
   ```powershell
   powershell -ExecutionPolicy Bypass -File server\tools\start-server-host.ps1 -Publish
   ```
3. 设置访问密码，确认网关在线  
4. 出门只映射 **8765**（见 `docs/USER-GATEWAY-SAKURAFRP.md`）

### 手机

1. 侧载 `shell\QQReborn.App\AppPackages\` 下 ARM 包  
2. 设置：服务器 / 端口 / 访问密码 → 连接  

```powershell
powershell -ExecutionPolicy Bypass -File tools\deploy-wdp.ps1 -Ip <手机IP> -Build
```

## 开发

```powershell
# 网关
dotnet run --project server/QQReborn.RealServer

# 面板
dotnet run --project server/QQReborn.ServerHost

# Shell（VS / MSBuild x86 或 ARM）
```

更多：[`server/README.md`](server/README.md) · [`shell/README.md`](shell/README.md) · [`HANDOFF.md`](HANDOFF.md) · [`docs/MAINTAINABILITY.md`](docs/MAINTAINABILITY.md)

## 安全

- 访问密码保护网关，不是 QQ 密码；公网映射务必设强密码  
- 不要把 NapCat 3000/3001 暴露公网  
