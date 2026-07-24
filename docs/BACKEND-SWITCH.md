# 多协议后端切换（Lagrange / NapCat）

UWP App 只认 **统一 wire**（`ws://host:8765/ws`）。  
RealServer 在进程启动时选择后端实现，**无需改 App**。

| Backend | 说明 | 登录 |
|---------|------|------|
| `lagrange`（默认） | 现有 LagrangeV2 + 签名服务 | App 扫码 / keystore + sign |
| `napcat` | OneBot 11 → NapCat/NTQQ | 在 NapCat 里先登录 QQ |

## 切换方式（优先级从高到低）

1. **环境变量**  
   ```powershell
   $env:QQREBORN_BACKEND = "napcat"   # 或 lagrange
   $env:NAPCAT_HTTP = "http://127.0.0.1:3000"
   $env:NAPCAT_WS   = "ws://127.0.0.1:3001"
   $env:NAPCAT_TOKEN = ""             # 若 NapCat 开了 token
   dotnet run --project server\QQReborn.RealServer -c Debug --no-launch-profile
   ```

2. **appsettings.json**  
   ```json
   {
     "QQReborn": { "Backend": "napcat" },
     "NapCat": {
       "HttpBase": "http://127.0.0.1:3000",
       "EventWs": "ws://127.0.0.1:3001",
       "AccessToken": ""
     }
   }
   ```

3. **专用配置文件**  
   ```powershell
   # 使用 appsettings.NapCat.json 覆盖
   $env:ASPNETCORE_ENVIRONMENT = "NapCat"
   dotnet run --project server\QQReborn.RealServer --no-launch-profile
   ```

启动后探测：

```text
GET http://127.0.0.1:8765/backend
GET http://127.0.0.1:8765/   → 文本里含 backend=…
```

## NapCat 侧准备

1. 安装并登录 **NTQQ + NapCat**（版本以你本机为准）。
2. 打开 **HTTP 服务**（默认常见 `3000`）与 **正向 WebSocket 事件**（常见 `3001`）。
3. 确认本机可访问：  
   - `POST http://127.0.0.1:3000/get_login_info`  
   - `ws://127.0.0.1:3001`
4. 启动 RealServer（`Backend=napcat`）。
5. 打开 QQ Reborn App → 账号：  
   - **QQ 号**填 NapCat 当前登录号（会校验一致性；可先填对的号）  
   - 签名服务器在 NapCat 模式下**可忽略**  
   - 点「开始登录」→ 实际走 `get_login_info` + 拉好友/群列表

## 能力对照（初版 NapCat 适配）

| 能力 | Lagrange | NapCat 初版 |
|------|----------|-------------|
| 文字 / 图文混排 / 多图 | ✅ | ✅ |
| 会话列表 / 联系人 / 群成员 | ✅ | ✅ |
| 收消息推送 | ✅ | ✅（event WS） |
| 历史分页 | ✅ | ⚠️ 依赖 NapCat history API |
| 撤回 / 戳一戳 / 改群名片 | ✅ | ⚠️ 已接常见 action |
| 语音编解码 / 空间动态 | ⚠️/🟡 | ❌ stub |
| 签名 LocalSignProxy | 需要 | **不需要** |

## Archive / 分支建议

你提到会 **archive Lagrange 版本**，并保留 NapCat 分支，建议：

```powershell
# 1) 打标签冻结当前 Lagrange 主线行为（可选）
git tag archive/lagrange-backend-$(Get-Date -Format yyyyMMdd)

# 2) 开长期分支做 NapCat 默认（可选；主线已支持双后端）
git checkout -b backend/napcat
# 把 appsettings.json 默认 Backend 改成 napcat 后提交

# 3) 主线 master 继续保留双后端切换，避免再分叉两套 wire
```

**推荐：** 主线只维护 **一套 wire + 两个 ISessionBackend**；用 tag/branch 标记「默认后端」和发布包，而不是复制整个 RealServer。

## 代码入口

- `ISessionBackend` — 统一会话面  
- `BackendFactory` — 选择实现  
- `BotSessionManager` — Lagrange  
- `NapCat/NapCatSessionManager` — OneBot  
- `Program.cs` — 只依赖接口  

## 故障排查

| 现象 | 处理 |
|------|------|
| `无法连接 NapCat` | 检查 HTTP 端口、防火墙、token |
| `登录号不一致` | App 里 QQ 号改成 NapCat 当前 uin |
| 收不到消息 | 检查 EventWs 端口与 access_token |
| 发图失败 | 确认 NapCat 支持 `base64://` image segment |
| `/backend` 仍是 lagrange | 环境变量未进该进程；用 ServerHost 时需注入 env |
