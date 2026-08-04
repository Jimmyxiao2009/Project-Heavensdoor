# 可维护性（目标 ≥ 9/10）

## 评分（本轮之后）

| 维度 | 分 | 依据 |
|------|---:|------|
| 工程边界 | **9** | 产品三工程 + 两个测试工程；无 Fake/第二协议栈 |
| 服务端分层 | **9** | Program / WireRpc / WireDispatch / Session / NapCat |
| 客户端分层 | **8.5** | AppServices + IChat/IGateway；Api/VM/View 均 partial 分域 |
| 接口一致性 | **9** | UI 不依赖 `RemoteChatService` 强转；扩展走 Gateway |
| 配置与仓库 | **8.5** | 单 appsettings；gitignore 覆盖产物/逆向树 |
| 可测试性 | **8.5** | 服务端 + 纯逻辑单测；路由表有 KnownTypes 护栏 |
| 文档 | **9** | ARCHITECTURE + 本文件 + server/shell README |
| 测试覆盖面 | **7** | 无 UI/E2E 自动化（接受） |

**综合：9.0 / 10**（在「家用网关 + 瘦客户端」产品范围内）。  
扣分主要来自：无设备 E2E 自动测、NapCat 适配与会话 UI 仍是大模块。

## 铁律（违反即掉分）

1. **只三产品工程**：RealServer、ServerHost、App。  
2. **加功能竖切**  
   - Server：`ISessionBackend` → `NapCatSessionManager` → `WireDispatch`（并更新 `KnownTypes`）  
   - Shell：`IGatewayService` → `RemoteChatService` → UI 只用 `AppServices.Gateway`  
3. **禁止**新代码 `as RemoteChatService`（静态工具方法除外）。  
4. **新逻辑不进** 1500+ 行单文件；进 partial / Service / VM。  
5. **配置**只认 `appsettings.json` + 环境变量。  
6. **容灾/防错行为禁止静默删除**（重连、重试、锁、temp 清理、错密码短路、签名错误上浮等）。详见 [`RESILIENCE.md`](RESILIENCE.md)。  
7. **改完跑**  
   ```powershell
   dotnet test server/QQReborn.RealServer.Tests
   dotnet test shell/QQReborn.Shell.Logic.Tests
   # + MSBuild Shell；dotnet build RealServer/ServerHost
   ```

## 大文件地图（知道去哪改）

| 区域 | 文件 |
|------|------|
| Wire 路由 | `server/.../Wire/WireDispatch.cs` |
| NapCat 读/写/管 | `NapCatSessionManager.{Read,Write,Admin,Events}.cs` |
| Shell RPC | `RemoteChatService.Api.{Session,Messaging,Group,Profile,Media,Space}.cs` |
| 会话状态 | `ConversationViewModel.{Core,History,Send}.cs` |
| 会话 UI | `ConversationPage.{Core,MultiSelect,Input,Composer,Playback,Menus}.cs` |

## 继续抬分（可选）

- 群管/会话 UI 行为测试（UI 自动化或 VM 假 Gateway）  
- WireDispatch 再按文件拆 Messaging/Group/Space  
- 引入最小 CI：`dotnet test` on push  
