# 容灾 / 防错（禁止在重构中静默删掉）

重构分层、拆 partial、提可维护性时，**下列行为必须保留**。  
若改动触及这些路径，先读本文件，改完用文末清单自检。

## Shell（`RemoteChatService`）

| 机制 | 位置 / 行为 |
|------|-------------|
| Frp 冷启动重试 | `EnsureConnectedAsync`：`maxAttempts` 2～3 次，退避延迟 |
| 瞬态错误识别 | `IsTransientConnectFailure`；错密码 **不重试** |
| 半开套接字清理 | 超时/失败必须 `CleanupSocket`，禁止 `_connected==true` 残留 |
| 鉴权超时 | `AuthenticateAsync` 超时文案；0x8000000E 相关错误信息 |
| 断线重连循环 | `TryStartReconnectLoop` / `ReconnectLoopAsync` |
| 重连后重新绑号 | `RestoreAccountAfterReconnectAsync` + `EnsureAccountBoundForCurrentConnectionAsync` |
| 在途请求失败 | `FailAllPending` 在连接死亡时唤醒所有 waiter |
| 强制重连 | `ForceReconnectAsync`（生命周期 Resume 使用） |
| UI 线程调度 | `RunOnUi`；`_dispatcher` 在首次连接前捕获 |

## Server 网关（`Program` / `Wire*`）

| 机制 | 行为 |
|------|------|
| 访问密码 | `WireAuth.SafePasswordEquals` 常量时间比较 |
| 消息大小 | 单帧上限 2MB，超限关闭连接 |
| 发送串行 | `sendLock` + `ClientConnection` 发送队列保序 |
| 分发串行 | `dispatchLock` 防同连接并发 handler 乱序 |
| 分发异常 | `WireRpc`/`DispatchAsync` 捕获后回 error，不断死进程 |
| 空闲会话 | 连接断开且无订阅者时 `Dispose` NapCat backend，防事件监听堆积 |

## NapCat 适配

| 机制 | 行为 |
|------|------|
| configure 串行 | `_configureGate` 防重入清缓存 |
| 历史拉取串行 | `_historyGates` 每会话一把锁 |
| 事件 WS 重连 | `EventLoopAsync` + `ReconnectDelayMs` |
| 事件处理隔离 | 单条 `HandleEvent` 异常只打日志，不打死循环 |
| 重复 configure | 同号不整表清空 transcript |
| 出站临时文件 | 图/语音/**视频** temp 路径进列表，`finally` 删除 |
| 群头像 / 头像 temp | `SetGroupPortrait` / `SetAvatar` 同样删除 |
| 签名错误上浮 | `SetSelfProfile`：昵称成功但签名失败仍 `error`（不假 ok） |
| 非 HTTP 图 URL | 清洗 base64/本地路径，避免 Shell Image 裂图 |

## Shell 产品向防错

| 机制 | 行为 |
|------|------|
| 防撤回 | `UtilitySettings.AntiRecall` + 本地保留 |
| 免打扰 / 特别关心 | `NotificationMuteGate` |
| 生命周期会话记忆 | `App.RememberConversation` / Resume 重连 |

## 重构铁律

1. **移动代码可以，删行为不行。** partial / 换文件必须带着注释与分支。  
2. 合并 JSON 解析时，保留「bool / number / string」多形态（见 `WireJson.Flag`）。  
3. 禁止「为了干净」去掉：重试、锁、finally 删文件、错密码短路、外层 try/catch。  
4. 触碰上表路径时，优先补/跑：  
   `dotnet test server/QQReborn.RealServer.Tests`  
   `dotnet test shell/QQReborn.Shell.Logic.Tests`

## 改完自检（复制）

- [ ] 连接失败仍会重试；错密码仍只试一次  
- [ ] 重连后仍会 `configureAccount` 恢复会话  
- [ ] 发图/语音/视频后 temp 文件仍删除  
- [ ] 签名更新失败仍返回 error  
- [ ] 大包 / 错帧不会拖死进程  
