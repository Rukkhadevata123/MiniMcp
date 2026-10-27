# 通过 Cloudflare 连接

这份指南使用 Cloudflare Access 保护 `mcp.example.com`，用远程管理的 Tunnel 把请求转发到运行 MiniMcp 的电脑。将示例域名、团队名和路径换成自己的值。先创建 Access 应用和允许策略，再添加公网路由。

```text
ChatGPT / MCP 客户端 → HTTPS + OAuth → Cloudflare Access
    → Cloudflare Tunnel → cloudflared → 127.0.0.1:8000 → MiniMcp
```

需要 Cloudflare 管理的活动域名、Zero Trust 团队和可登录的身份提供商；ChatGPT 账户或工作区还需允许添加自定义 MCP。MiniMcp 不调用 OpenAI API，不需要 OpenAI API key。客户端的工具确认与 MiniMcp 的本机执行模式分别生效。

## 1. 配置团队和登录方式

在 [Cloudflare Dashboard](https://dash.cloudflare.com/) 确认域名处于 Active。进入 Zero Trust，完成团队创建并记下团队域名，例如 `my-team.cloudflareaccess.com`。MiniMcp 的 `team` 只填写前缀 `my-team`。

在 **Integrations → Identity providers** 配置已有的登录方式；也可添加 [One-time PIN](https://developers.cloudflare.com/cloudflare-one/integrations/identity-providers/one-time-pin/) 邮箱验证码。确认你要使用的邮箱能通过该身份提供商登录。

## 2. 创建 Access 应用

在 **Zero Trust → Access controls → Applications** 创建应用，选择 **Self-hosted and private**，添加公开 hostname `mcp.example.com`，Path 留空以保护整个域名。应用名称可以填写 `Personal MiniMcp`。

创建并关联一条策略：Action 选 `Allow`，Include 选 `Emails`，填写允许使用服务的完整邮箱。选择上一步配置的身份提供商并保存。创建应用前后页面名称可能调整，可对照 [Cloudflare 的自托管应用步骤](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/self-hosted-public-app/) 和 [策略说明](https://developers.cloudflare.com/cloudflare-one/access-controls/policies/)。

打开应用详情或编辑页的 Additional settings，复制 **Application Audience (AUD) Tag**。AUD 用于 MiniMcp 校验该 Access 应用的 JWT；它不是 Tunnel token。重建 Access 应用后重新复制 AUD。

## 3. 启用 Managed OAuth

在应用列表选择对应应用的 **⋯ → Edit → Advanced settings**，开启 **Managed OAuth** 并保存。Cloudflare 会处理 OAuth 登录，再向 MiniMcp 转发 Access JWT。需要支持 RFC 8707 的 OAuth 客户端，详见 [Cloudflare Managed OAuth](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/managed-oauth/)。

在 **Allowed redirect URIs** 填入客户端连接管理页面显示的准确回调地址。ChatGPT 可能使用带连接 ID 的 `https://chatgpt.com/connector/oauth/{callback_id}`，也可能使用 `https://chatgpt.com/connector_platform_oauth_redirect`，取决于授权服务器的 issuer 能力和连接类型；以 [OpenAI 的回调说明](https://developers.openai.com/plugins/build/auth#redirect-url)及实际管理页面为准。Cloudflare 支持路径末尾 `/*`，但能填写准确地址时优先使用准确地址。只有需要本机 OAuth 调试客户端时才开启 localhost 或 loopback 选项。

## 4. 创建 Tunnel 和保存 token

在 **Networking → Tunnels** 创建远程管理的 Tunnel，选择 cloudflared。已有 Tunnel 可打开详情，选择 **Add a replica** 查看安装命令；其中以 `eyJ...` 开头的完整字符串是 token，UUID 只是 Tunnel ID。参考 [Tunnel token 官方说明](https://developers.cloudflare.com/tunnel/reference/tunnel-tokens/)。

将 token 单独保存到当前用户可读、项目目录之外的文件，文件只包含完整 token。不要把 token 粘贴到终端命令、项目文件或聊天记录中。

Linux 可以用不回显的提示输入，创建文件前确认路径没有保存其他内容：

```bash
install -d -m 700 "$HOME/.config/minimcp"
umask 077
systemd-ask-password 'Tunnel token:' > "$HOME/.config/minimcp/tunnel-token"
chmod 600 "$HOME/.config/minimcp/tunnel-token"
```

Windows 可在用户配置目录创建 `tunnel-token` 文件，通过受信任的文本编辑器粘贴 token 并保存；在文件属性的“安全”页限制其他用户读取。随后在设置或 JSON 中填写绝对路径，例如 `C:/Users/你的用户名/.config/minimcp/tunnel-token`。确认日志和截图不含 token 内容。

## 5. 添加公网路由

进入 Tunnel 的 **Routes → Add route → Published application**，添加：

| 字段 | 示例 |
| --- | --- |
| Public hostname | `mcp.example.com`，Path 留空 |
| Service type | `HTTP` |
| Service URL | `http://127.0.0.1:8000` |

`127.0.0.1` 指运行 cloudflared 的电脑，端口必须与 MiniMcp 一致。Cloudflare 管理完整 DNS 时，添加路由会创建对应记录；其他 DNS 设置按 [路由文档](https://developers.cloudflare.com/cloudflare-one/networks/routes/add-routes/)处理。不要把匿名 Local 模式直接发布到公网。

## 6. 配置 MiniMcp

在 `settings.json` 中填写自己的项目路径和以下字段，其余程序路径可按 [配置说明](configuration.md)填写：

```json
{
  "schemaVersion": 1,
  "projectRoot": "/absolute/path/to/project",
  "port": 8000,
  "bashPath": "",
  "rgPath": "",
  "gitPath": "",
  "authentication": {
    "mode": "CloudflareAccess",
    "publicOrigin": "https://mcp.example.com",
    "team": "my-team",
    "audience": "replace-with-your-access-aud"
  },
  "tunnel": {
    "enabled": true,
    "executablePath": "",
    "tokenFilePath": "/absolute/path/to/tunnel-token"
  }
}
```

`publicOrigin` 只填 HTTPS 根地址，不加 `/mcp`。启用受管隧道时安装 cloudflared 并填写 token 路径；可执行程序路径留空时自动查找。Linux 还需要 `/usr/bin/setsid`。服务启动后，MiniMcp 以 token 文件启动 cloudflared，停止服务时一并清理。桌面概览页也可以单独启停受管隧道。

如果由其他进程管理 cloudflared，将 `tunnel.enabled` 设为 `false`，按 Cloudflare 指南单独启动它；MiniMcp 仍使用 CloudflareAccess 认证。

先运行 `minimcp check --config /absolute/path/settings.json`，再运行 `minimcp run --config /absolute/path/settings.json`，或者在桌面保存后启动。如果从源码运行，将 `minimcp` 换成 `dotnet run --project src/MiniMcp.Cli --`。`check` 不验证 token 是否有效或公网登录是否成功。

## 7. 连接 ChatGPT 并验证

在 [ChatGPT Plugins](https://chatgpt.com/plugins) 选择加号和 **Add custom MCP server**，填写名称、Server URL `https://mcp.example.com/mcp`，认证选择 OAuth。根据连接管理页面显示的回调更新 Access 应用；完成登录后安装插件，在对话中用 `@` 选择。ChatGPT 支持的注册方式和账户权限以 [OpenAI 自定义 MCP 指南](https://developers.openai.com/api/docs/guides/custom-mcp-server)为准。

选择一个测试项目，要求客户端调用 `list_files` 和 `read_file`，检查返回是否来自预期目录。需要测试写入时，先在客户端查看 `apply_patch` 的完整预演，再按本机模式提交并回读。客户端显示工具确认时，检查具体参数后再选择。

在运行 MiniMcp 的电脑上检查：

```sh
curl --fail http://127.0.0.1:8000/healthz
curl -i http://127.0.0.1:8000/mcp
curl -i https://mcp.example.com/.well-known/oauth-authorization-server
```

CloudflareAccess 模式下，本机健康检查应返回 200，未携带 Access JWT 的本机 `/mcp` 请求应被拒绝。OAuth 发现地址可帮助定位网关问题；最终还要用客户端完成登录和工具调用。隧道进程 Running 只表示本机进程存活，公网路由和认证故障见 [排错](troubleshooting.md)。
