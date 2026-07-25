# QQ Reborn

在 **Windows 10 Mobile（Lumia）** 上用的 QQ 瘦客户端：号挂在家里电脑的 **NTQQ + NapCat**，手机只连本机网关，填 **地址 + 端口 + 访问密码** 即可收发消息。

> 当前主路径：**仅 NapCat 本地网关**。不依赖公共 Lagrange 签名、不在手机上跑协议栈。

---

## 架构

```
┌─────────────────────────────────────┐
│  PC「QQ Reborn 管家」                 │
│  · 启动 / 停止网关                    │
│  · 访问密码、本机 ws 地址              │
│         ┌──────────────────┐        │
│         │ RealServer :8765 │        │
│         │ backend=napcat   │        │
│         └────────┬─────────┘        │
│                  │ OneBot           │
│         ┌────────▼─────────┐        │
│         │ NapCat + NTQQ    │        │
│         └──────────────────┘        │
└──────────────────┬──────────────────┘
                   │ 可选：SakuraFrp 只映射 8765
                   ▼
            Lumia / 本机 Shell
         ws://host:port/ws + 访问密码
```

| 组件 | 路径 | 说明 |
|------|------|------|
| UWP Shell | `shell/QQReborn.App` | 手机 UI（WP 风格 Pivot），min SDK 10.0.15063 |
| RealServer | `server/QQReborn.RealServer` | WebSocket 网关，对接 NapCat |
| 管家 | `server/QQReborn.ServerHost` | WPF 一键启停网关 |
| 文档 | `docs/` | 穿透、后端说明等 |

---

## 快速开始（用户）

### 1. 电脑端

1. 安装并登录 **NTQQ**，安装并登录 **[NapCat](https://github.com/NapNeko/NapCatQQ)**（HTTP 默认 `3000`，事件 WS `3001`）。
2. 启动管家（推荐）：
   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\start-server-host.ps1 -Publish
   ```
   产物目录：`publish\ServerHost\`（含 `QQReborn.ServerHost.exe` 与 `RealServer\`）。
3. 在管家里设置 **访问密码**（不是 QQ 密码），确认网关在线。
4. 出门时用 SakuraFrp 等 **只映射 8765**，不要把 NapCat 3000/3001 暴露公网。说明见 [`docs/USER-GATEWAY-SAKURAFRP.md`](docs/USER-GATEWAY-SAKURAFRP.md)。

### 2. 手机端（Lumia / ARM）

1. 安装依赖（若未装）：`Microsoft.NET.CoreRuntime.1.1`（ARM），仓库内可参考  
   `tools\deps\ARM\Microsoft.NET.CoreRuntime.1.1.appx`。
2. 侧载 Shell 的 **ARM** 包，例如：  
   `shell\QQReborn.App\AppPackages\QQReborn.App_*_ARM_*_Test\*.appx`
3. 打开应用 → **设置**：
   - 服务器地址：电脑局域网 IP，或樱花主机名  
   - 端口：`8765` 或 Frp 远程端口  
   - 网关访问密码：与管家一致  
4. 点 **连接网关**，进入会话列表。

本机调试可用 x86 包 + `localhost:8765`。

---

## 功能概览

### 聊天

- 会话列表、好友/群、文字 / 图 / 文件 / 戳一戳 / 回复 / @  
- 转发（合并转发优先）  
- 多选：复制、转发、删除、会话区截图  
- 防撤回（设置里开关；对方撤回后本地可保留）  
- 免打扰、置顶、特别关注  

### 设置 · 实用功能

防撤回、撤回提示、双击戳一戳、发送前确认、复制带发送者、来消息震动等。

### 动态

通过 NapCat cookies 读 QZone 动态（发表/评论仍在完善）。

---

## 开发者：编译

环境建议：

- Visual Studio 2022+（含 UWP 工作负载 / Windows 10 SDK **10.0.15063** 或兼容）
- .NET SDK 10（`net10.0` RealServer / ServerHost）
- MSBuild（UWP）

### 服务端 + 管家（一键 publish）

```powershell
powershell -ExecutionPolicy Bypass -File tools\start-server-host.ps1 -Publish
```

输出：

- `publish\ServerHost\QQReborn.ServerHost.exe` — 管家（win-x64 自包含）
- `publish\ServerHost\RealServer\` — RealServer（win-x64 自包含）

### 服务端 MSI 安装包

需已安装 WiX 5 CLI；建议本机已有 **NapCat.Shell**（构建时打进 MSI）：

```powershell
dotnet tool install -g wix
# 可选：指定 NapCat Shell 路径
$env:NAPCAT_SHELL = "D:\NapCat.Shell"
powershell -ExecutionPolicy Bypass -File tools\build-server-msi.ps1 -Version 0.1.0.4
```

产物：

```
publish\msi\QQReborn.ServerHost-0.1.0.4-x64.msi
```

包内默认 OneBot：`127.0.0.1:3000`（HTTP）/ `3001`（事件 WS）。管家可选登录账号（默认大号），可「启动 NapCat」并自动写入配置。

安装 / 静默安装 / 卸载：

```powershell
msiexec /i publish\msi\QQReborn.ServerHost-0.1.0.2-x64.msi
msiexec /i publish\msi\QQReborn.ServerHost-0.1.0.2-x64.msi /qn
msiexec /x publish\msi\QQReborn.ServerHost-0.1.0.2-x64.msi /qn
```

安装后开始菜单与桌面有 **「QQ Reborn 管家」** 快捷方式，目录默认  
`C:\Program Files\QQ Reborn Server Host\`。

仅编 RealServer（开发用）：

```powershell
dotnet publish server\QQReborn.RealServer\QQReborn.RealServer.csproj -c Release -r win-x64 --self-contained true -o publish\RealServer
```
### UWP Shell · ARM 包（Lumia）

```powershell
# 按本机 VS 路径调整 MSBuild
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild shell\QQReborn.App\QQReborn.App.csproj `
  /p:Configuration=Release /p:Platform=ARM `
  /p:AppxBundle=Never /p:UapAppxPackageBuildMode=SideloadOnly `
  /p:AppxPackageSigningEnabled=true /t:Rebuild /v:m
```

包目录：

```
shell\QQReborn.App\AppPackages\QQReborn.App_*_ARM_*_Test\
```

x86 调试：

```powershell
& $msbuild shell\QQReborn.App\QQReborn.App.csproj /p:Configuration=Debug /p:Platform=x86 /t:Build /v:m
```

侧载到设备可参考：`tools\deploy-wdp.ps1`。

---

## 仓库结构（摘要）

```
shell/QQReborn.App/          UWP 客户端
server/QQReborn.RealServer/  NapCat 网关
server/QQReborn.ServerHost/  WPF 管家
server/QQReborn.FakeServer/  本地假后端（无 NapCat 调 UI）
docs/                        用户与架构文档
tools/                       启动脚本、WS 联调工具
HANDOFF.md                   给后续开发的交接说明
```

`_ref/`、`_reverse/`、`ACCOUNT-NOTES.local.md` 等为本地参考/私密笔记，**不要提交密钥**。

---

## 安全注意

- **访问密码** 保护的是网关，不是腾讯账号；Frp 暴露公网时务必设强密码。  
- 切勿把 NapCat 的 OneBot 端口直接映射到公网。  
- 空密码仅适合本机调试。

---

## 许可证与声明

本项目为个人/学习向客户端与网关，**非腾讯官方产品**。使用第三方协议实现（NapCat/NTQQ）请自行遵守相关服务条款与当地法律。

更多实现细节与路线图见 [`HANDOFF.md`](HANDOFF.md)。
