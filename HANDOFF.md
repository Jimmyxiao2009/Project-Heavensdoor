# QQ-Reborn Handoff

**North star：** 电脑跑 **面板 + 网关**；Lumia 只填 **地址 + 端口 + 访问密码**。

> 后端只有 **NapCat**。仓库工程只有三个：RealServer、ServerHost、App。

## 产品路径

```
ServerHost（面板）→ RealServer :8765 → NapCat + NTQQ
                         ↑
                    Shell（UWP）
```

## 仓库地图

| 路径 | 角色 |
|------|------|
| `server/QQReborn.RealServer` | 网关 |
| `server/QQReborn.ServerHost` | 管家面板 |
| `server/tools/` | 启动 / MSI 脚本 |
| `shell/QQReborn.App` | UWP 客户端 |
| `tools/deploy-wdp.ps1` | 侧载手机 |
| `docs/` | 穿透与说明 |

## 启动

```powershell
powershell -ExecutionPolicy Bypass -File server\tools\start-server-host.ps1 -Publish
```

面板内点 **「一键安装并启动」**：自动检测 QQNT → 下载 NapCat.Shell → 写 OneBot 3000/3001 → 启 NapCat → 启网关。

Shell：`AppServices.UseRemoteBackend`；设置里填服务器 / 端口 / 访问密码。

## 约定

1. 先确认 NTQQ + NapCat 在线，再测消息  
2. 不要再引入第二套协议栈 / FakeServer 工程  
3. 出门只穿透 8765  
4. Shell 扩展能力用 `AppServices.Gateway`（`IGatewayService`），勿 `as RemoteChatService`  

可维护性（目标 9/10）与铁律：`docs/MAINTAINABILITY.md`  
架构图：`docs/ARCHITECTURE.md`  

```powershell
dotnet test server/QQReborn.RealServer.Tests
dotnet test shell/QQReborn.Shell.Logic.Tests
```
