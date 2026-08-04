# OneBot 11 API Coverage - New Additions

## 2026-07-30 Update

新增以下 OneBot 11 标准 API，客户端/服务端/NapCat 后端全栈实现：

### 好友相关

1. **RejectFriendRequest** - 拒绝好友请求
   - 客户端：`RemoteChatService.RejectFriendRequestAsync(FriendRequest)`
   - OneBot API：`set_friend_add_request` with `approve=false`
   - 服务端路由：`rejectFriendRequest`

2. **SendLike** - 给好友 QQ 名片点赞
   - 客户端：`RemoteChatService.SendLikeAsync(long targetUin, int count)`
   - OneBot API：`send_like`
   - 参数：`user_id`, `times` (1-10，QQ 每日上限 10 次)
   - 服务端路由：`sendLike`

### 群管理相关

3. **SetGroupAdmin** - 设置/取消群管理员
   - 客户端：`RemoteChatService.SetGroupAdminAsync(string conversationId, long targetUin, bool enable)`
   - OneBot API：`set_group_admin`
   - 参数：`group_id`, `user_id`, `enable`
   - 服务端路由：`setGroupAdmin`

4. **SetGroupBan** - 群组单人禁言
   - 客户端：`RemoteChatService.SetGroupBanAsync(string conversationId, long targetUin, int durationSeconds)`
   - OneBot API：`set_group_ban`
   - 参数：`group_id`, `user_id`, `duration` (秒，0 = 解除禁言)
   - 服务端路由：`setGroupBan`

5. **SetGroupWholeBan** - 群组全员禁言
   - 客户端：`RemoteChatService.SetGroupWholeBanAsync(string conversationId, bool enable)`
   - OneBot API：`set_group_whole_ban`
   - 参数：`group_id`, `enable`
   - 服务端路由：`setGroupWholeBan`

6. **SetGroupKick** - 踢出群成员
   - 客户端：`RemoteChatService.SetGroupKickAsync(string conversationId, long targetUin, bool rejectAddRequest)`
   - OneBot API：`set_group_kick`
   - 参数：`group_id`, `user_id`, `reject_add_request` (是否拒绝再次加群)
   - 服务端路由：`setGroupKick`

## 架构说明

所有新 API 遵循统一的三层架构：

1. **客户端层** (shell/QQReborn.App/Services/RemoteChatService.cs)
   - 提供 C# 异步方法接口
   - 通过 WebSocket 向服务端发送 JSON-RPC 请求

2. **服务端网关层** (server/QQReborn.RealServer/Program.cs)
   - 处理 JSON-RPC 请求路由
   - 调用 ISessionBackend 接口方法

3. **NapCat 后端层** (server/QQReborn.RealServer/NapCat/NapCatSessionManager.cs)
   - 实现 ISessionBackend 接口
   - 调用 NapCat 的 OneBot 11 HTTP API
   - 处理参数转换和错误返回

## 2026-08-01 Batch (NapCat Docs Alignment)

已全栈接入（后端 + RPC + 主要 UI）：

- 好友：`set_friend_remark` / `delete_friend` / `send_like` / 拒绝请求
- 资料：`set_qq_profile` / `set_self_longnick` / `set_online_status` / `get_profile_like` / `nc_get_user_status`
- 群：公告读写删、群文件列表/URL/建夹/删文件、精华、荣誉、禁言列表、签到、群头像、群备注、全员禁言、管理/踢/禁
- 消息：`fetch_ptt_text`、face/dice/rps/json 段解析、视频发送、`_mark_all_as_read`
- 诊断：`get_version_info` / `get_status` / `get_group_at_all_remain`（API 可用）

快捷面板（MainPage QuickPanel）：全部已读、在线状态、个性签名。

## 刻意未接（非主路径 IM）

- AI 语音 / 频道 Guild / 群相册 / 闪传 / 在线文件
- rkey / send_packet / OCR / 收藏体系 / 小程序 Ark 完整分享
- 自定义表情增删、群待办、模型展示等

## 验证

- ✅ 客户端构建通过 (QQReborn.App.csproj, Debug/x86)
- ✅ 服务端构建通过 (QQReborn.RealServer.csproj, Debug/net10.0)
- ✅ 接口签名与 `ISessionBackend` / Program 路由一致
