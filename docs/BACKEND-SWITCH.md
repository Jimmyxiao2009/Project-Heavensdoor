# 后端说明（仅 NapCat 本机网关）

> **产品路径只有 NapCat。** 旧协议栈 / 公共 sign 相关代码与依赖已删除。

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
powershell -ExecutionPolicy Bypass -File server\tools\start-user-gateway.ps1 -AccessPassword "your-pass"

# 管家 UI
powershell -ExecutionPolicy Bypass -File server\tools\start-server-host.ps1
```

## 鉴权

非空 `AccessPassword` 时，WebSocket 首包必须是：

```json
{ "id": "...", "type": "auth", "password": "..." }
```

错密返回 `error: "访问密码错误"` 并断开。空密码 = 开发开放模式（不建议映射公网）。

## 绑定账号

```json
{ "id": "...", "type": "configureAccount", "expectedUin": "" }
```

空 `expectedUin` = 采用 NapCat 当前登录号。旧客户端若仍发 `signUin`，网关会当作 `expectedUin` 兼容读取。

## 能力（聊天优先）

| 能力 | NapCat 路径 |
|------|-------------|
| 登录 | 本机 NTQQ 已登录；Shell 只填地址/端口/访问密码 |
| 好友/群列表、文字收发 | 支持 |
| 动态 / 空间 | 可读；发表仍完善中 |
| 公共协议 sign | **不需要** |
