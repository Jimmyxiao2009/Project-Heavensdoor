# Shell（手机 / UWP 侧）

只有本体：

```
shell/
  QQReborn.App/    UWP 瘦客户端
  README.md
```

连家里电脑的 **RealServer**（地址 + 端口 + 访问密码），不跑 QQ 协议。

## UI 壳（现代）

- **`ShellPage`**：顶栏汉堡菜单（`SplitView`）+ **底部 CommandBar**（消息 / 联系人 / 动态 / 我）
- 详情页（聊天、设置、搜索等）走根 `Frame`，不叠底栏
- 视觉：深色现代底 + QQ 蓝强调、圆角头像/搜索条、紧凑顶栏

## 构建

```powershell
# 打开仓库根 QQReborn.sln，或：
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild shell\QQReborn.App\QQReborn.App.csproj /p:Configuration=Debug /p:Platform=x86 /t:Build /v:m
```

ARM 包：`shell\QQReborn.App\AppPackages\`

## 侧载到手机

```powershell
powershell -ExecutionPolicy Bypass -File tools\deploy-wdp.ps1 -Ip <手机IP> -Build
```

（脚本在仓库根 `tools/`，依赖在 `tools/deps/ARM`。）

## App 内结构（摘要）

| 路径 | 说明 |
|------|------|
| `Services/AppServices.cs` | 组合根；`UseRemoteBackend` 开关 |
| `Services/Chat/IChatService` | 基础聊天（mock 可用） |
| `Services/Chat/IGatewayService` | 扩展 API（群管/媒体等）；`AppServices.Gateway` |
| `Services/Chat/RemoteChatService*` | WebSocket 客户端 → RealServer |
| `Views/` | 页面（会话等已 partial） |
| `ViewModels/` | 列表 / 会话状态 |

约定见 [`docs/MAINTAINABILITY.md`](../docs/MAINTAINABILITY.md)。
