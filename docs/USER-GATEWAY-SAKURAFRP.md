# 本机 NapCat 网关 + 樱花 Frp（SakuraFrp）出门方案

思路和 **MC 开服** 一样：

1. 游戏/服务跑在**家里电脑**上  
2. 用 **SakuraFrp** 把家里某个端口映射到公网  
3. 人在外面用客户端连 **映射后的地址**，不碰你的云、不用公共 Lagrange 签名  

```
家里 PC（常开、勿休眠）
┌──────────────────────────────────────────────┐
│  NTQQ 已登录 + NapCat（HTTP/WS 仅本机）         │
│       ↑                                       │
│  QQReborn.RealServer  backend=napcat          │
│       监听 0.0.0.0:8765  （Wire 给 Shell）      │
└──────────────────┬───────────────────────────┘
                   │ 只映射 8765
                   ▼
            SakuraFrp 隧道
                   │
     在家：Shell → 127.0.0.1 或 局域网 IP
     出门：Shell → 樱花给你的 域名/IP:远程端口
```

**不要**把 NapCat 的 3000/3001 映射出去，只映射 **RealServer 的 8765**。

---

## 一、家里准备（一次）

### 1. 安装并登录 NapCat

1. 安装 NTQQ + NapCat（按你使用的发行版说明）。  
2. 登录要使用的 QQ。  
3. 开启 **HTTP API**（常见 `http://127.0.0.1:3000`）和 **正向事件 WebSocket**（常见 `ws://127.0.0.1:3001`）。  
4. 浏览器或 curl 测：`get_login_info` 能返回当前 QQ 号。

### 2. 启动 QQ Reborn 网关（本机 RealServer）

仓库根目录：

```powershell
powershell -ExecutionPolicy Bypass -File tools\start-user-gateway.ps1
```

成功时本机应可访问：

- 探测：`http://127.0.0.1:8765/`  
- Shell 设置里服务器填：`127.0.0.1`（在家）  
- 管家会显示并保存一个「访问密码」，不是 QQ 密码；复制到 Shell 设置里的「网关访问密码」
- 登录页：NapCat 模式不需要填写签名服务器、API Key 或 QQ 号

### 3. 开机自启（建议）

- 系统电源：**禁止休眠**（否则出门必挂，和 MC 服一样）。  
- 可选：计划任务启动 `start-user-gateway.ps1` + 开机启动 SakuraFrp 客户端。

---

## 二、SakuraFrp（樱花）——和 MC 开服同一套路

### 1. 注册 / 客户端

1. 打开 SakuraFrp 官网，注册并下载 **Windows 客户端**（或使用启动器）。  
2. 登录后创建隧道（或使用已有节点）。

### 2. 隧道怎么填（对应 MC 的「开 25565」）

| 项 | 建议值 | 说明 |
|----|--------|------|
| 隧道类型 | **TCP**（或支持 WebSocket 的 HTTP，见下） | Shell 使用 `ws://host:port/ws` |
| 本地地址 | `127.0.0.1` | 只打本机 RealServer |
| 本地端口 | **8765** | 与 RealServer 一致 |
| 远程端口 | 节点分配 / 自选 | 公网连这个端口 |
| 访问地址 | 面板显示的 `IP` 或 `域名` | 填进 Shell「服务器」 |

**TCP 隧道（推荐，和 MC 一样好懂）：**

- 本地：`127.0.0.1:8765`  
- 外面：`ws://{樱花给你的主机}:{远程端口}/ws`  
- Shell「服务器主机」填：`主机`（不要带 `ws://`）  
- Shell「服务器端口」填：樱花**远程端口**（本地永远是 8765；远程可以是别的）

**若远程端口不是 8765：** 在设置里改「服务器端口」即可，无需强行申请 8765 远程口。

### 3. 启动顺序（和 MC 一样）

1. 开 NTQQ / NapCat（已登录）  
2. 开 `start-user-gateway.ps1`（RealServer）  
3. 开 **SakuraFrp 客户端** 并启用该隧道  
4. 出门用 Shell，服务器填樱花访问主机  

关掉 2 或 3，外面就连不上——和关 MC 服一样。

---

## 三、Shell 客户端怎么填

| 场景 | 设置 → 服务器主机 | 说明 |
|------|-------------------|------|
| 在家、同机 | `127.0.0.1` | 不经过 Frp |
| 在家、手机同一 WiFi | 电脑局域网 IP，如 `192.168.1.8` | 可不走 Frp，延迟更低 |
| 出门 | 樱花面板里的 **访问 IP/域名** | 流量走隧道 |

登录：

- **网关访问密码**：填写管家显示的密码；错误密码会立即断开。  
- **签名服务器 / API Key / QQ 号**：本机 NapCat 模式均不需要填写。

---

## 四、安全注意（比 MC 更敏感）

| 建议 | 原因 |
|------|------|
| **只映射 8765** | 3000/3001 是 OneBot 管理面，公网裸奔风险大 |
| 网关访问密码 | 只有知道密码的 Shell 才能使用 QQ 网关 |
| 隧道 token / HTTPS | SakuraFrp 自身的隧道安全能力仍应按其文档配置 |
| 不要把 Frp 密钥提交 git | 与 `*.local.md` 同样对待 |

---

## 五、故障排查

| 现象 | 检查 |
|------|------|
| 出门连不上 | 家 PC 是否休眠；Frp 客户端是否绿；隧道是否启用 |
| 在家可以出门不行 | Shell 是否仍填 `127.0.0.1`；应改成樱花主机 |
| 连上无消息 | NapCat 是否仍登录；RealServer 日志 `backend=napcat` |
| 登录提示连不上 NapCat | 本机 3000/3001；仅 RealServer 被穿透，NapCat 必须在**同一台家 PC** |
| 端口不对 | App 当前默认 8765；远程端口需一致或改 App |

探测（本机）：

```text
GET http://127.0.0.1:8765/
GET http://127.0.0.1:8765/backend
```

---

## 六、和「云多用户 / Lagrange」的关系

| 模式 | 谁持号 | 签名 | 出门 |
|------|--------|------|------|
| **本机网关 + SakuraFrp（本文）** | 用户家 PC + NapCat | 无公共 token 三号限制 | Frp + 家机常开 |
| 云 Lagrange 多租户 | 云服务器 | 公共 sign 配额 / 自建 qsign | 云 7×24 |
| 云多开 NapCat | 云上每用户实例 | 无独立 sign | 云 7×24，贵 |

当前产品主推：**MC 开服式本机网关 + 樱花映射 8765**。

---

## 七、相关脚本与配置

| 路径 | 用途 |
|------|------|
| `tools/start-user-gateway.ps1` | 本机以 napcat 后端启动 RealServer |
| `tools/start-server-napcat.ps1` | 同上（开发用，参数可调） |
| `server/.../appsettings.json` | `Backend: napcat`，`Mode: localGateway` |
| `docs/BACKEND-SWITCH.md` | 后端切换说明 |
| `docs/MULTI-TENANT.md` | 云多租户（另一路线，非本文） |
