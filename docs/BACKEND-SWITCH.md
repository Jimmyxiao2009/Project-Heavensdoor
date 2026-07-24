# 后端说明（仅 NapCat 本机网关）

> **2026-07 起产品路径只有 NapCat。** Lagrange / LocalSignProxy / QQReborn.Signing 已从 RealServer 运行时移除（源码可仍在 `_ref/` 与 `server/QQReborn.Signing` 作历史归档，不参与网关构建）。

## 默认与唯一后端

| 值 | 含义 |
|----|------|
| `napcat`（默认） | 本机 OneBot 11 / NapCat + 已登录 NTQQ |

环境变量 / 配置：

```powershell
$env:QQREBORN_BACKEND = "napcat"   # 可省略，默认即 napcat
$env:QQREBORN_MODE = "localGateway"
$env:NAPCAT_HTTP = "http://127.0.0.1:3000"
$env:NAPCAT_WS = "ws://127.0.0.1:3001"
$env:QQREBORN_ACCESS_PASSWORD = "你的访问密码"
```

`appsettings.json`：

```json
{
  "QQReborn": {
    "Backend": "napcat",
    "Mode": "localGateway",
    "AccessPassword": ""
  },
  "NapCat": {
    "HttpBase": "http://127.0.0.1:3000",
    "EventWs": "ws://127.0.0.1:3001"
  }
}
```

## 一键启动

```powershell
# CLI 网关
powershell -ExecutionPolicy Bypass -File tools\start-user-gateway.ps1 -AccessPassword "your-pass"

# 管家 UI
powershell -ExecutionPolicy Bypass -File tools\start-server-host.ps1
```

## 鉴权

非空 `AccessPassword` 时，WebSocket 首包必须是：

```json
{ "id": "...", "type": "auth", "password": "..." }
```

错密返回 `error: "访问密码错误"` 并断开。空密码 = 开发开放模式（不建议映射公网）。

## 联调脚本

```powershell
# 错密应拒绝
dotnet run --project tools\WsGatewayTest -- --password wrong --expect-auth-fail

# 正确密码 + 绑定 NapCat + 列会话 + 发送
dotnet run --project tools\WsGatewayTest -- --password your-pass --send
```

## 能力（聊天优先）

| 能力 | NapCat 路径 |
|------|-------------|
| 登录 | 本机 NTQQ 已登录；Shell 不填公共 sign |
| 好友/群列表、文字收发 | 支持 |
| 动态 / 空间 | 不承诺（空） |
| 公共 Lagrange sign | **不需要** |

## 历史

旧的「Lagrange ↔ NapCat 双后端切换」与公共 sign 多租户方案见 git 历史与 `docs/MULTI-TENANT.md`（后置，非主路径）。
