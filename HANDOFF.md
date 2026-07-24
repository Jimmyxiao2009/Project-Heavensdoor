# QQ-Reborn Handoff

**Last updated:** 2026-07-24  
**Audience:** next agent / human continuing this repo  
**North star (owner):** 电脑上只跑一个**打包好的带 UI 管理程序**；Lumia 上只填**服务器地址 + 密码**，即可当 QQ 用。

> **Runtime backend = NapCat only.** Lagrange / Signing / LocalSignProxy 已从 RealServer 与管家主路径移除（`_ref/Lagrange*` 可保留作归档）。

---

## 0. 目标产品体验（最高优先级 · 按这个做）

### 用户故事（必须达成的感觉）

```
【家里电脑 · 一次配置】
  双击「QQ Reborn 管家」安装包/绿色版
    → 图形界面：启动网关、看是否在线、复制访问地址、（可选）樱花穿透说明
    → 内部自动：RealServer(backend=napcat) + 依赖本机已登录的 NapCat/NTQQ
    → 用户设一个「访问密码」（不是 QQ 密码）

【手机 Lumia · 每天使用】
  打开 QQ Reborn Shell
    → 设置：服务器 = 樱花域名/IP（或家里局域网 IP）
    → 设置：端口 = 8765 或樱花远程端口
    → 设置：访问密码 = 上面设的密码
    → 点连接/登录
    → 直接进会话列表，收发消息（像在用 QQ）

【出门】
  电脑不关、不休眠 + SakuraFrp 映射 8765
  手机仍只填地址/端口/密码，无感
```

**用户不需要：**

- 填 Lagrange API Key / 公共 sign token  
- 懂 OneBot、RealServer、WebSocket  
- SSH / 手动 `dotnet run`  
- 在手机上装 NapCat  
- 碰云服务器（本阶段不做「云端多人 SaaS」）

**用户需要（可接受的成本）：**

- 家用 PC **常开**（和 MC 开服一样）  
- PC 上 **NTQQ + NapCat 已登录**（管家可检测/提示，不要求用户会命令行）  
- 出门用 **SakuraFrp**（管家 UI 里给「复制说明 / 本机端口」即可，不必内嵌樱花 SDK）

### 产品形态一句话

> **PC 管家 = 持号侧 + 网关 + 密码门**  
> **Lumia Shell = 瘦客户端**（只认 `ws://host:port/ws` + 访问密码）

### 目标架构图

```
                    ┌──────────────────────────────────────────┐
                    │  PC「QQ Reborn 管家」 (WPF 单文件/安装包)    │
                    │  · 启动/停止网关                          │
                    │  · 状态：NapCat / RealServer / 密码       │
                    │  · 显示：本机 ws 地址、建议 Frp 端口       │
                    │         ┌─────────────────────┐           │
                    │         │ RealServer :8765    │           │
                    │         │ backend=napcat      │           │
                    │         │ + AccessPassword    │           │
                    │         └──────────┬──────────┘           │
                    │                    │ OneBot               │
                    │         ┌──────────▼──────────┐           │
                    │         │ NapCat + NTQQ(已登录) │           │
                    │         └─────────────────────┘           │
                    └────────────────────┬─────────────────────┘
                                         │ 仅映射 8765
                                         ▼
                                   SakuraFrp（可选）
                                         │
              ┌──────────────────────────┼──────────────────────────┐
              ▼                          ▼                          ▼
         家里 Lumia                 出门 Lumia                   本机调试
      局域网 IP:8765            樱花主机:远程端口              127.0.0.1:8765
      + 访问密码                 + 访问密码                    + 访问密码
              │                          │                          │
              └──────────────────────────┴──────────────────────────┘
                                         │
                              shell/QQReborn.App (UWP 10.0.15063)
                              只显示聊天等 UI，不跑协议栈
```

### 明确不做 / 后置（避免跑偏）

| 项 | 状态 |
|----|------|
| 公共 Lagrange sign（1 token 绑 3 号）当多用户方案 | ❌ 放弃作主路径 |
| 云端一台服服务无数陌生人 SaaS | 后置（见 `docs/MULTI-TENANT.md`） |
| 自研 so 算签 / 逆向 energy | ❌ 不作为本阶段目标 |
| **好友动态 / 空间**（NapCat cookies → QZone） | ✅ 读动态已接；发表说说待做 |
| 支付 / 真音视频通话 | ❌ |
| 把 NapCat 3000/3001 映射公网 | ❌ 危险；只穿透 **8765** |

### 访问密码（已实现）

- **不是** QQ 密码，是网关访问口令。  
- 管家生成或用户自设，可复制 / 可改。  
- Shell 连接后首包 `type:auth`；错密码断开并提示「访问密码错误」。  
- 空密码 = 仅本机开发开放模式（勿映射公网）。

---

## 1. 实现路线图（按用户故事倒推）

### P0 — 体验闭环（已基本落地）

1. **访问密码** ✅ RealServer + Shell + 管家  
2. **管家 UI** ✅ NapCat-only 启停 / 密码 / 地址 / 检测 NapCat（`tools/start-server-host.ps1 -Publish`）  
3. **Shell 登录简化** ✅ 设置页三件套；连接页无扫码主路径  
4. **文档对齐** ✅ `docs/USER-GATEWAY-SAKURAFRP.md` + `docs/BACKEND-SWITCH.md`（仅 NapCat）

### P1 — 可靠出门

- 管家内嵌 SakuraFrp **操作说明**（不强制集成客户端）  
- 端口非 8765 时 Shell 已支持「服务器端口」  
- 家 PC 防休眠提示  

### P2 — 体验打磨

- 断线重连 / 密码错误明确文案  
- 托盘常驻、开机启动网关  
- 可选：检测 NapCat 是否在线并一键打开文档  

### P3 — 可选远期

- 云多租户 / SessionHub 商业化（另一条线）  
- NapCat 旁路只读空间动态  

---

## 2. 仓库现状 vs 目标差距

| 能力 | 现状 | 目标 |
|------|------|------|
| Shell 连远程 host:port | ✅ `ServerHost` + `ServerPort` | ✅ 保留 |
| RealServer `backend=napcat` | ✅ `NapCatSessionManager` | ✅ 默认 |
| 本机一键脚本 | ✅ `tools/start-user-gateway.ps1` | 被管家 UI 替代/封装 |
| SakuraFrp 文档 | ✅ `docs/USER-GATEWAY-SAKURAFRP.md` | 管家内链到它 |
| **访问密码** | ✅ `type:auth` + 长度安全比较 | 保留 |
| **打包带 UI 管家** | ✅ ServerHost NapCat-only | publish 继续打磨 |
| Shell 无感登录 | ✅ 地址+端口+密码主路径 | 继续打磨文案 |
| 动态 | ✅ getActiveFeeds 可读 | 发表/评论完善中 |
| 聊天图文混排等 | 部分已做 | 继续修消息质量 |

### 关键路径（当前默认）

| 路径 | 角色 |
|------|------|
| `shell/QQReborn.App` | Lumia/PC 瘦客户端 |
| `server/QQReborn.RealServer` | 网关 wire；**仅 napcat** |
| `server/QQReborn.ServerHost` | 「QQ Reborn 管家」 |
| 本机 NapCat + NTQQ | 持号与签名（用户侧） |
| SakuraFrp | 出门端口映射（用户侧） |
| `_ref/Lagrange*` / `QQReborn.Signing` | 归档，不进产品构建 |

---

## 3. 架构与协议（给实现者）

### 组件关系

```
Shell  --ws://host:port/ws-->  RealServer  --OneBot-->  NapCat  --NT-->  QQ
         + AccessPassword           │
                                    └── 本地 only: NapCat :3000/:3001
```

- Shell **禁止**直连腾讯。  
- RealServer **禁止**要求用户填公共 sign token（napcat 模式）。  
- 多连接：`SessionHub` 已做连接级会话隔离；本机网关通常一用户一号。

### Wire（摘要）

- Client → server: `{ "id", "type", ... }`  
- Server → client: `{ "type":"result", "id", "data"|"error" }` 或 push：`messageReceived`, `loginStatus`, …  
- **已实现：** `type: "auth", "password": "..."` → `{ ok: true }` 后才允许业务（空密码跳过强制）

Handlers：`server/QQReborn.RealServer/Program.cs`。

### 配置

```json
// appsettings 目标形态
{
  "QQReborn": {
    "Backend": "napcat",
    "Mode": "localGateway",
    "AccessPassword": "用户在管家里设置"
  },
  "NapCat": {
    "HttpBase": "http://127.0.0.1:3000",
    "EventWs": "ws://127.0.0.1:3001"
  }
}
```

---

## 4. 怎么跑（开发）

```powershell
# 本机网关（需已登录 NapCat）
powershell -ExecutionPolicy Bypass -File tools\start-user-gateway.ps1

# 管家（开发）
powershell -ExecutionPolicy Bypass -File tools\start-server-host.ps1

# Shell：MSBuild Debug|x86，设置 host/port，连 ws
```

详文：

- **用户向：** `docs/USER-GATEWAY-SAKURAFRP.md`  
- 后端切换：`docs/BACKEND-SWITCH.md`  
- 云多租户（非主路径）：`docs/MULTI-TENANT.md`  

---

## 5. 能力与限制（聊天优先）

### 已有基础

- 远程 host + **可配端口**（樱花远程口）  
- NapCat：好友/群列表、收发文字、图文混排方向、会话隔离  
- 免打扰/置顶：客户端本地  
- 群成员/@、图文/文件、转发、已读、戳一戳、资料、群通知/好友申请（有 flag 时可处理）  
- 表情回应 / 文件下载 / 语音 URL：尽力接 NapCat API  

### NapCat 路径明确缺口

- **动态 / 空间**：已接（NapCat cookies → QZone `getActiveFeeds`）；发表说说仍待做  
- **本机须安装并登录 NapCat**（外部依赖；非仓库内编译）  
- 语音 SILK 全格式播放：依赖 CDN/get_record  
- 入群/好友审批：依赖 request 事件或系统消息带 flag  
- 管家 publish 单文件 / 托盘 / 开机启动：P2  

### 历史 Lagrange

**已从产品路径移除**；`_ref/Lagrange*` 与 `server/QQReborn.Signing` 仅归档。不要再恢复公共 sign 或双后端切换。

---

## 6. 给下一任的指令（照做）

1. 确认本机 **NTQQ + NapCat** 在线（HTTP 3000 / 事件 WS 3001），再测发消息。  
2. 用 `tools/WsGatewayTest` 或管家 + Shell 做端到端。  
3. **不要**恢复 Lagrange / LocalSignProxy 主路径。  
4. 文档与文案统一：**电脑管家 + 手机地址密码 = 出门也能聊**（电脑常开 + 樱花）。

---

## 7. Repo map（速查）

| Path | Role |
|------|------|
| `shell/QQReborn.App/` | UWP Shell（15063），瘦客户端 |
| `server/QQReborn.RealServer/` | 网关；仅 `NapCat/` |
| `server/QQReborn.ServerHost/` | 用户向管家 UI |
| `server/QQReborn.Signing/` | 归档（不引用） |
| `tools/start-user-gateway.ps1` | 本机 napcat 网关 CLI |
| `tools/WsGatewayTest/` | 真 wire 鉴权/列表/发送联调客户端 |
| `tools/LocalSignProxy/` | 归档（Lagrange 用） |
| `docs/USER-GATEWAY-SAKURAFRP.md` | 樱花 + 本机网关用户文档 |
| `HANDOFF.md` | 本文件 |

### App 后端开关

`shell/QQReborn.App/App.xaml.cs`：

```csharp
private const bool UseRemoteBackend = true; // false → Mock
```

---

## 8. 成功标准（验收用）

- [x] 非开发者只装「管家」+ 已登录 NapCat，点开始，无需命令行（需本机 NapCat）  
- [ ] Lumia 只配置：**服务器、端口、密码**，能进会话并收发消息（依赖本机 NapCat 在线）  
- [x] 错误密码无法使用网关  
- [ ] 出门：樱花映射后改地址/端口仍可用（家 PC 在线）  
- [x] 用户全程不接触公共 Lagrange sign token  
- [x] 动态：可读好友动态（QZone）；无数据时显示空状态，不假装  


**Done means the Lumia feels like QQ; the PC feels like a one-click game server host.**
