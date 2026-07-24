# LocalSignProxy

本地签名代理：对 RealServer / Lagrange 暴露与官方相同的

```http
POST /api/sign/sec-sign
Authorization: Bearer <token>
```

接口，在本机串行转发到上游签名服务，并对 401/429/5xx 自动重试。

## 为什么需要它

公共 `https://sign.lagrangecore.org` 在**并发**签名时容易返回 **HTTP 401**。  
打开大量会话拉历史时会把签名打爆，随后 `MessageSvc.PbSendMsg` 全部失败，表现为「谁都发不出去」。

本代理把所有签名**串成一条队列**，从源头消掉并发 401。

> 这不是逆向出来的本地 energy 引擎，仍然依赖上游（公共签名或你自己的 qsign）。  
> 当你有真正的本地 qsign 时，改 `UpstreamUrl` 指向它即可，App 不用再改。

## 启动

```powershell
# 使用 appsettings.json 里已写好的 token
powershell -ExecutionPolicy Bypass -File tools\start-local-sign.ps1

# 或显式指定 token
powershell -ExecutionPolicy Bypass -File tools\start-local-sign.ps1 -Token "你的-API-Key"
```

默认监听：`http://127.0.0.1:18488`

健康检查：

```text
http://127.0.0.1:18488/health
http://127.0.0.1:18488/stats
```

## App 配置

设置 → 账号：

| 项 | 值 |
|----|-----|
| 使用自建签名服务器 | **开** |
| 签名服务器 URL | `http://127.0.0.1:18488` |
| API Key | 可留空（代理注入 UpstreamToken），或填同一 token |
| QQ 号 | 你的 QQ |

然后重新「登录 QQ」。

## 换成真正的本地 qsign

1. 自行部署兼容 `POST /api/sign/sec-sign` 的服务（需对应协议版本的 energy 实现）。
2. 编辑 `tools/LocalSignProxy/appsettings.json`：

```json
"UpstreamUrl": "http://127.0.0.1:你的端口",
"UpstreamToken": ""
```

3. 重启本代理。

## 说明

- 真正的「离线本地签名」依赖特定 QQ 版本的 native 库（unidbg 等），维护成本高，且与 **Linux/NT 协议** 的 sec-sign 形态不一定一致。
- 本仓库当前 RealServer 使用 **Linux 协议 + sec-sign**，与公共 Lagrange 签名服务对齐；本代理是稳妥、可维护的本地入口。
