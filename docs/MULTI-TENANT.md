# 多租户目标架构（一云多用户 · 远端 Shell 登录）

## 产品目标

| 要求 | 含义 |
|------|------|
| **一台云服务器服务多人** | 不是一人一机装服务 |
| **用户只碰 Shell** | 装/用 UWP 客户端，填服务器地址，扫码登录 |
| **用户不接触服务器** | 不装 NTQQ、不配 NapCat、不要 GPG、不 SSH |
| **消息 / 动态 / 发消息** | 全部经云端代理，Shell 只拉数据 + 展示 |

```
  用户 A Shell ──┐
  用户 B Shell ──┼── wss/ws://cloud:8765/ws ──► RealServer (多会话)
  用户 C Shell ──┘                              │
                     ┌──────────────────────────┼──────────────────────────┐
                     ▼                          ▼                          ▼
              Session A                  Session B                  Session C
           (QQ 号 111)                 (QQ 号 222)                 (QQ 号 333)
                     │                          │                          │
                     └──────────── 共享 Sign 服务（仅服务器持有）────────────┘
```

## 关键纠正：GPG / 签名归谁

以前觉得「用 Lagrange 就要每人有 GPG」——**在多租户云端模型下不成立**。

| 角色 | 要不要 GPG / sign token |
|------|-------------------------|
| 终端用户（Shell） | **不要**。只扫码 / 看消息 |
| 云服务器 | **要一份**（或接公共/自建 sign）。所有会话共用 |
| 运维 | 配好一次 sign，和用户无关 |

所以：**阻碍「人人能用」的是「签名必须装在用户电脑上」**；  
把签名放到**云端一份**，用户侧已经和 NapCat 一样无感。

## 为什么「远端 Shell 登录」更适合多会话 Lagrange，而不是单实例 NapCat

| | **多会话 Lagrange（推荐做多租户）** | **NapCat 单实例** |
|--|-----------------------------------|-------------------|
| 登录发生在哪 | **协议层扫码**，二维码经 WS 推到 Shell | 通常在**服务器上的 NTQQ 窗口** |
| 用户是否碰服务器 | 否 | 否，但**管理员**要在服务器登录每个号 |
| 一进程多号 | 一进程内多个 `BotContext` / 会话 | 一 NapCat ≈ 一 QQ；多号 = 多进程/多容器 |
| 适合 | 公有云、SaaS 式「连上就扫码」 | 家庭单号、自用网关 |

**结论（产品选型）：**

1. **多租户 + 远端扫码 + 用户不碰服** → 主路径应是 **云端多会话协议后端（Lagrange/同类）+ 服务器统一签名**。  
2. **NapCat** 仍适合：你自己/运维在机器上登一个号、给少量可信客户端当网关；**不适合**「任意用户打开 Shell 就注册一个新 QQ 会话」 unless 你做 **每用户自动拉起一套 NapCat 容器**（成本高、难运维）。

仓库里已有的 `Backend=napcat` 保留为 **单租户网关模式**；多租户主路径走 **SessionHub + 每用户独立会话**。

## 目标会话模型

### 连接与会话

```
WebSocket 连接  ──bind──►  AccountSession (sessionId)
                              │
                              ├── ISessionBackend (独立 Bot / 独立消息缓存)
                              ├── 订阅者: 1..N 个 Shell 连接（同号多端）
                              └── keystore_{uin}.json / prefs_{uin}.json
```

- 新连接默认**无会话**。  
- `configureAccount` / `startLogin` → 创建会话或绑定已有 uin 会话 → 推送 `qrCode` / `loginStatus` **仅给该会话的订阅者**。  
- 登录成功后 `sessionId` 或 token 可复用（重连、第二台设备）。  
- **禁止**再出现「一个 Broadcast 打给所有连接」导致串号。

### 服务端配置（用户不可见）

```json
{
  "QQReborn": {
    "Mode": "multiTenant",
    "Backend": "lagrange",
    "Sign": {
      "Url": "https://your-sign-or-proxy",
      "Token": "SERVER_SIDE_ONLY"
    }
  }
}
```

Shell **不必**再填 sign URL/token（可逐步隐藏设置项）；只填：

- 服务器主机  
- （可选）要登录的 QQ 号  

### Shell 体验（目标）

1. 设置 → 服务器 `chat.example.com`  
2. 打开登录页 → 点开始  
3. 出二维码 → 手机 QQ 扫  
4. 进主界面收发消息  

全程无服务器面板、无 NapCat、无 GPG。

## 分阶段落地

| 阶段 | 内容 | 状态 |
|------|------|------|
| **P0** | 连接级会话隔离 + 每连接独立 backend 实例；广播不串号 | 进行中 |
| **P1** | 服务端默认 sign；Shell 登录可不填 token；每 uin 独立 keystore | 下一步 |
| **P2** | 会话 token / 重连恢复；同号多设备 | 计划 |
| **P3** | wss + 简易鉴权（防止公网裸连） | 计划 |
| **P4** | （可选）每用户 NapCat 容器编排 | 仅当强需求 |

## 与当前代码的差距

| 现状 | 目标 |
|------|------|
| 全局单例 `ISessionBackend` | `SessionHub` 管理多会话 |
| `Broadcast` 可能推给所有 WS | 仅推给绑定该会话的连接 |
| 单个 `keystore.json` | `keystore_{uin}.json` |
| App 配置 sign | 服务端默认 sign，App 可省略 |
| NapCat = 进程级单号 | 保留为 `Mode=singleGateway` |

## 一句话

你要的是 **SaaS 式 QQ 代理**：云端多会话、Shell 扫码、用户零运维。  
这应建成 **多租户 SessionHub + 服务端统一签名**；NapCat 是单号网关备选，不是多用户主路径。
