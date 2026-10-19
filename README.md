# MiniMcp

让 ChatGPT 通过三个 MCP 工具读取文件、应用补丁和执行一次性 Bash 命令。服务使用 .NET 10、ASP.NET Core 和官方 MCP C# SDK，通过 Cloudflare Tunnel 发布，并使用 Cloudflare Access Managed OAuth 登录。

```text
ChatGPT / MCP Client
        │ HTTPS + OAuth
Cloudflare Access（指定邮箱授权 + Managed OAuth）
        │ Tunnel + 已签名的 Cf-Access-Jwt-Assertion
cloudflared → http://127.0.0.1:8000 → MiniMcp → 文件工具 / Bash
```

MiniMcp 本身不调用 OpenAI API，无需 OpenAI API Key。

`read_file` 和 `apply_patch` 限定在 `MCP_ROOT` 内；**Bash 使用运行 MiniMcp 的账号权限，`MCP_ROOT` 只是默认工作目录，不是 shell 沙箱**。命令可以访问该账号能访问的其他文件及网络。只将服务开放给你信任的身份，并使用普通用户运行。

## 目录

- [日常使用：Arch 手动启动与停止](#arch-manual)
- [当前验证结果](#verification)
- [工具、代码位置与配置](#configuration)
- [首次部署：Arch 环境与配置文件](#arch-setup)
- [Cloudflare Dashboard 完整配置](#cloudflare)
- [在 ChatGPT 验证三个工具](#chatgpt)
- [补丁格式与一次性 Bash 用法](#tool-usage)
- [Arch：systemd 服务与自启动](#systemd)
- [Windows 11：手动运行与自启动](#windows)
- [日志解释与故障定位](#troubleshooting)
- [开发检查与本地 MCP 测试](#development)
- [实现参考](#references)
- [许可证](#license)

<a id="arch-manual"></a>

## 日常使用：Arch 手动启动与停止

适用于已经配置好 `.env`、Tunnel token 和 Cloudflare 的情况。每次使用开两个终端窗口或标签页。

**终端一：启动 MiniMcp。**

```bash
cd "$HOME/MiniMcp"
set -a
source .env
set +a
dotnet run --no-launch-profile
```

应看到 `MiniMcp mode: CloudflareAccess` 和 `Now listening on: http://127.0.0.1:8000`。ASP.NET 不自动加载 `.env`，因此每个新终端都要执行上面的导入步骤。

**终端二：启动 Tunnel。**

```bash
cloudflared tunnel --no-autoupdate run --token-file "$HOME/.config/minimcp/tunnel-token"
```

出现 `Registered tunnel connection` 后查看 Dashboard 的 Tunnel 状态，并在 ChatGPT 中使用：

```text
https://mcp.yoimiyalove.top/mcp
```

使用期间保持两个进程运行，电脑保持开机和联网。结束时在两个终端分别按 `Ctrl+C`。下次重复这两步。

修改 `.env` 后，停止终端一的进程，重新导入配置并运行。修改 C# 代码后重新运行 `dotnet run --no-launch-profile`；如果仍在同一个终端且环境变量未变，可直接重新运行。Tunnel 通常可继续保持连接。

<a id="verification"></a>

## 当前验证结果

| 项目 | 状态与证据 |
| --- | --- |
| Arch 前台启动 | 已验证；Production 环境、CloudflareAccess 模式、监听 `127.0.0.1:8000` |
| 应用依赖与运行 | 已验证；本机已还原项目依赖并成功运行服务 |
| Cloudflare Tunnel | 已验证；QUIC 连接注册成功，收到公网 hostname 到本机端口的远程配置 |
| ChatGPT 已认证调用链路 | 已验证；服务识别客户端 `openai-mcp`，成功处理工具发现及调用 |
| 三工具版本 | 27 项核心检查通过，覆盖按行读取、补丁、真实 Bash 执行、超时、取消与子进程清理；运行命令见[开发检查](#development) |
| MCP SDK 内存流协议检查 | 已通过；恰好三个工具、参数 schema、补丁回读、Bash 返回与工具错误 |
| 新旧协议互通 | 本地内存流检查通过；SDK 默认选择 `2026-07-28`，显式指定该版或 `2025-11-25` 时均可发现三个工具并调用 `read_file` |
| 新版 HTTP 端到端 | 已通过 Arch 实机复测；ChatGPT 经公网 HTTPS、Access 和 Tunnel 完成新版工具发现与实际调用 |
| 工具列表刷新 | 已验证；旧会话调用已删除工具被拒绝，重新发现工具后新版三个工具均调用成功 |
| ChatGPT 调用 `bash` | 已验证；`pwd`、`uname -s`、文本输出和算术运算成功，退出码为 `0` |
| ChatGPT 调用 `read_file` | 已验证；读取 UTF-8 文本和 `Program.cs` 前 35 行，未截断、无错误 |
| ChatGPT 调用 `apply_patch` | 已验证；`dry_run=true` 预演和实际更新均成功，修改后回读确认内容已保存 |
| 匿名拒绝、错误 AUD、过期令牌等失败分支 | 待专项验证；成功登录不能替代这些测试 |
| Arch systemd 服务、注销后运行、重启自启 | 待实机验证；指南见后文 |
| Windows 手动运行与自启动 | 待实机验证；保留完整步骤 |

已验证的版本为 .NET SDK `10.0.113`、基础运行时及 ASP.NET Core `10.0.13`、cloudflared `2026.10.0`；包版本见 [MiniMcp.csproj](MiniMcp.csproj)。

<a id="configuration"></a>

## 工具、代码位置与配置

### 工具

固定提供以下三个工具。列目录、搜索、构建和查看 Git diff 都通过 `bash` 完成。

此版本不保留旧工具兼容层。升级时以 [.env.example](.env.example) 为准清理旧配置，重启应用后刷新客户端工具列表。

| 工具 | 主要参数 | 功能 |
| --- | --- | --- |
| `read_file` | `path`、`start_line=1`、`end_line=null` | 读取 UTF-8 文本；行号从 1 开始、包含结束行，省略结束行表示读到末尾 |
| `apply_patch` | `patch`、`dry_run=false` | 用文本补丁新增、更新、移动或删除文件；可先校验并查看变更文件计划 |
| `bash` | `command`、`workdir="."`、`timeout_seconds=30` | 启动独立 Bash 执行一次命令，返回退出码和输出；超时范围 1–600 秒 |

文件工具的路径和 Bash 的 `workdir` 使用相对于 `MCP_ROOT` 的 `/` 分隔路径，例如 `Program.cs`、`Tools/WorkspaceTools.cs`。这些参数拒绝绝对路径、越界路径、符号链接与 junction，并过滤 `.git`、`.env*`、`.ssh`、`.aws`、`.config`、`.codex`、`.agents`、`.cloudflared`、`tunnel-token` 和 `.key`、`.pem`、`.pfx`、`.p12` 文件。**这些检查不约束 Bash 命令正文**，例如 `git diff` 可以正常读取 `.git`。

文件工具仅支持不含 NUL 的 UTF-8 文本，单文件最多 256 KiB（含 BOM）。`read_file` 返回 `path`、`startLine`、`endLine`、`totalLines`、`truncated`、`content`；内容不附加行号，最多 16000 个 UTF-16 字符单元，不截断代理对。`truncated` 表示所选行范围被输出上限截断；最后一行可能只返回了一部分。可缩小行范围继续读取，超长单行需通过 Bash 选取片段。

补丁先校验整批操作，再暂存新内容、备份原文件并替换；提交失败时尝试回滚。补丁和 Bash 调用共用一把锁，避免它们在服务内部同时修改文件。它们不与外部编辑器同步，也不保证断电/进程被强制终止时的多文件事务；回滚失败会报错，并保留同目录 `.minimcp-*.bak` 供恢复。路径检查不隔离硬链接，也无法消除其他本机进程替换链接的竞态。

### 代码位置

| 文件 | 职责 |
| --- | --- |
| [Program.cs](Program.cs) | 监听配置、MCP 工具注册、`/mcp` 和 `/healthz` |
| [Tools/WorkspaceTools.cs](Tools/WorkspaceTools.cs) | 三个工具的 MCP 入口、参数与权限提示 |
| [Services/ProjectWorkspace.cs](Services/ProjectWorkspace.cs) | 路径校验、UTF-8 文件读取与行范围 |
| [Services/PatchService.cs](Services/PatchService.cs) | 补丁解析、精确匹配、预演与提交 |
| [Services/BashRunner.cs](Services/BashRunner.cs) | 独立 Bash、环境变量、输出上限、超时与取消 |
| [Services/ProcessLifetime.cs](Services/ProcessLifetime.cs) | Linux 进程组与 Windows Job Object 清理 |
| [Security/CloudflareAccessAuthentication.cs](Security/CloudflareAccessAuthentication.cs) | Access JWT 校验及公钥缓存刷新 |
| [Configuration/MiniMcpOptions.cs](Configuration/MiniMcpOptions.cs) | 环境变量与启动校验 |

### 配置项

| 变量 | 值或默认值 | 说明 |
| --- | --- | --- |
| `MCP_ROOT` | 必填 | 已存在的项目目录；不能是文件系统根目录或整个用户 home |
| `MCP_AUTH_MODE` | `CloudflareAccess` | 公网使用该模式；本机无认证测试可显式使用 `Local` |
| `MCP_PUBLIC_URL` | 例如 `https://mcp.yoimiyalove.top` | Cloudflare 模式必填；填 HTTPS 根地址，省略 `/mcp` |
| `CF_ACCESS_TEAM` | 团队域名前缀 | 例如团队域名 `my-team.cloudflareaccess.com` 对应 `my-team` |
| `CF_ACCESS_AUD` | Access 应用 AUD | Cloudflare 模式必填，来自应用设置 |
| `MCP_PORT` | `8000` | 端口范围 1–65535；监听地址固定为 IPv4 loopback |
| `MCP_BASH_PATH` | Linux：`/bin/bash`；Windows：`%ProgramFiles%\Git\bin\bash.exe` | Bash 可执行文件的绝对路径；未安装或路径无效时拒绝启动 |

Cloudflare 模式缺少必要配置时拒绝启动。JWT 校验包含签名、RS256、Issuer、Audience 与有效期。公钥通过 ConfigurationManager 缓存，设置六小时自动刷新间隔，并支持未知 key ID 后的受限刷新。

### 三种地址分别填在哪里

| 位置 | 当前值 |
| --- | --- |
| Access 应用 hostname | `mcp.yoimiyalove.top`，Path 留空 |
| Tunnel 后端 Service URL | `http://127.0.0.1:8000` |
| `.env` 的 `MCP_PUBLIC_URL` | `https://mcp.yoimiyalove.top` |
| ChatGPT Server URL | `https://mcp.yoimiyalove.top/mcp` |

`/mcp` 是 `app.MapMcp("/mcp")` 定义的接口路径。Tunnel 保留请求路径，因此客户端请求最终到达 `http://127.0.0.1:8000/mcp`。

<a id="arch-setup"></a>

## 首次部署：Arch 环境与配置文件

### 1. 安装依赖

首次安装或需要补齐环境时执行：

```bash
sudo pacman -Syu --needed dotnet-sdk aspnet-runtime dotnet-targeting-pack aspnet-targeting-pack cloudflared bash util-linux git ripgrep
dotnet --info
dotnet --list-runtimes
cloudflared --version
```

确认系统运行时列表同时包含 `Microsoft.NETCore.App 10.0.x` 与 `Microsoft.AspNetCore.App 10.0.x`。Arch 的 ASP.NET Core 运行时和 targeting pack 是独立包。

Bash 执行需要 `/bin/bash` 和 util-linux 提供的 `/usr/bin/setsid`。`git`、`rg` 用于下文的命令示例。

在项目目录还原和构建：

```bash
cd "$HOME/MiniMcp"
dotnet restore MiniMcp.csproj
dotnet build MiniMcp.csproj --no-restore
```

### 2. 创建 `.env`

仅在没有自己的 `.env` 时从模板复制；已有配置直接编辑，保留实际团队与 AUD。

```bash
cp .env.example .env
chmod 600 .env
```

完整示例：

```dotenv
MCP_ROOT=/home/yoimiya/MiniMcp
MCP_AUTH_MODE=CloudflareAccess
MCP_PUBLIC_URL=https://mcp.yoimiyalove.top
CF_ACCESS_TEAM=replace-with-your-team-prefix
CF_ACCESS_AUD=replace-with-application-audience-tag
MCP_PORT=8000
MCP_BASH_PATH=/bin/bash
```

首次验证可将 `MCP_ROOT` 设为 MiniMcp 项目本身；之后改为实际准备交给模型操作的项目。替换示例中的团队与 AUD 后，再按 [手动启动步骤](#arch-manual) 导入环境并运行。

`CF_ACCESS_AUD` 用于校验 Access 应用，Tunnel token 用于连接隧道；两者来自不同配置页面。Tunnel token 单独保存在后文的文件中。

<a id="cloudflare"></a>

## Cloudflare Dashboard 完整配置

以下使用 `yoimiyalove.top`，在同一个 Cloudflare 账户中管理域名、Access 与 Tunnel。换域名时同时调整 Dashboard 和 `MCP_PUBLIC_URL`。

### 1. 确认域名与团队

在 [Cloudflare Dashboard](https://dash.cloudflare.com/) 确认域名状态为 Active。首次进入 Zero Trust 时完成团队创建。

从 **Zero Trust → Settings** 查看团队域名，例如 `my-team.cloudflareaccess.com`。`CF_ACCESS_TEAM` 只填 `my-team`。[团队域名说明](https://developers.cloudflare.com/cloudflare-one/faq/getting-started-faq/)

### 2. 配置登录方式

可以使用已有的 Cloudflare 或 Google 登录。若使用邮箱验证码，在 **Zero Trust → Integrations → Identity providers → Add new identity provider → One-time PIN** 添加，随后在应用中选用。新团队未必默认启用 OTP。[OTP 官方步骤](https://developers.cloudflare.com/cloudflare-one/integrations/identity-providers/one-time-pin/)

### 3. 创建 Access 应用与邮箱策略

打开 **Zero Trust → Access controls → Applications → Create new application**。按钮也可能显示为 Add an application。选择 **Self-hosted and private**，旧界面可能显示 Self-hosted，再选择 **Add public hostname**。

| 字段 | 内容 |
| --- | --- |
| Application name | `Personal MiniMcp` |
| Session duration | 保持默认 |
| Subdomain | `mcp` |
| Domain | `yoimiyalove.top` |
| Path | 留空，保护整个 hostname |

在 **Access policies** 创建并关联策略：

| 字段 | 内容 |
| --- | --- |
| Policy name | `Only Me` |
| Action | `Allow` |
| Rule type | `Include` |
| Selector | `Emails` |
| Value | 你自己的完整登录邮箱 |

选择配置好的登录方式，保存应用。使用完整邮箱的 Emails 规则限定身份；Path 留空使 OAuth 的 `/.well-known/` 发现地址也落在同一应用下。[应用官方步骤](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/self-hosted-public-app/)，[策略说明](https://developers.cloudflare.com/cloudflare-one/access-controls/policies/)

### 4. 开启 Managed OAuth，复制 AUD

回到应用列表，选择该应用右侧 **⋯ → Edit → Advanced settings**，打开 **Managed OAuth** 并保存。OAuth 发现和授权由 Access 提供，MiniMcp 校验转发到本机的 `Cf-Access-Jwt-Assertion`。[Managed OAuth 说明](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/managed-oauth/)

**Allowed redirect URIs** 优先填写 ChatGPT 连接管理页显示的精确地址。首次准备连接时，也可先配置以下两种回调形式，随后收敛为实际连接的精确 URL：

```text
https://chatgpt.com/connector/oauth/*
https://chatgpt.com/connector_platform_oauth_redirect
```

ChatGPT 根据授权服务器能力选择回调形式，以连接管理页显示的值为准。[OpenAI 官方回调说明](https://developers.openai.com/plugins/build/auth#redirect-url)

令牌和授权会话时长先保持默认。需要本地 Inspector 进行 OAuth 联调时，再开启 **Allow localhost clients** 与 **Allow loopback clients**。

从应用的 **Configure / Edit → Additional settings** 复制 **Application Audience (AUD) Tag**，填写 `.env` 的 `CF_ACCESS_AUD`。删除并重建应用后需重新复制 AUD。[AUD 官方说明](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/validating-json/)

### 5. 创建 Tunnel，正确保存 token

打开 **Networking → Tunnels → Create a tunnel**，命名为 `personal-mcp-arch`，按页面提示选择 cloudflared 连接器。

已有 Tunnel 可进入详情的 **Overview → Add a replica**，查看连接器安装命令。命令中以 `eyJ` 开头的完整长字符串就是 Tunnel token；文件中只保存这段值。界面展示的 UUID 是 ID，不能填入 token 文件。[官方 token 获取步骤](https://developers.cloudflare.com/tunnel/reference/tunnel-tokens/)

首次保存或需要更新 token 时执行：

```bash
install -d -m 700 "$HOME/.config/minimcp"
umask 077
systemd-ask-password '粘贴完整 Tunnel token:' > "$HOME/.config/minimcp/tunnel-token"
chmod 600 "$HOME/.config/minimcp/tunnel-token"
```

在提示框粘贴完整 token 并回车。已有有效文件时，日常启动直接使用它。通过 `--token-file` 读取，避免把凭据写进启动命令文本。

启动 MiniMcp 和 Tunnel 后，Dashboard 中查看 Tunnel 状态。[Tunnel 创建说明](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/get-started/create-remote-tunnel/)

### 6. 添加公网路由

进入 Tunnel → **Routes → Add route → Published application**：

| 字段 | 内容 |
| --- | --- |
| Subdomain | `mcp` |
| Domain | `yoimiyalove.top` |
| Path | 留空 |
| Service type | `HTTP` |
| Service URL | `127.0.0.1:8000`；合并式输入框填 `http://127.0.0.1:8000` |

保存并保留默认 Host 转发。Dashboard 会建立对应 DNS 路由；`127.0.0.1` 指运行 cloudflared 的电脑。路由收到后，cloudflared 日志应包含对应 hostname 与 service 的配置更新。[路由说明](https://developers.cloudflare.com/cloudflare-one/networks/routes/add-routes/#add-a-published-application-route)

<a id="chatgpt"></a>

## 在 ChatGPT 验证三个工具

### 1. 连接插件

打开 [ChatGPT Plugins](https://chatgpt.com/plugins)，选择 **+ → Add custom MCP server**：

| 字段 | 内容 |
| --- | --- |
| Name | `MiniMcp` |
| Server URL | `https://mcp.yoimiyalove.top/mcp` |
| Authentication | `OAuth` |
| OAuth client ID / secret | 使用自动注册时留空 |

完成 Access 登录和工具扫描，再安装生成的插件。账户/工作区须允许自定义 MCP。新对话输入 `@`，选择 MiniMcp。[OpenAI 官方连接与使用说明](https://developers.openai.com/api/docs/guides/custom-mcp-server)

更新工具名称、参数或描述后，按以下顺序刷新：

1. 重启 MiniMcp，保持 Tunnel 运行。
2. 在 [ChatGPT Plugins](https://chatgpt.com/plugins) 打开 MiniMcp 的连接详情，点击 **Refresh／刷新**。
3. 确认工具列表只包含 `read_file`、`apply_patch`、`bash`。
4. 新建对话，输入 `@` 重新选择 MiniMcp，再运行测试。[OpenAI 官方刷新步骤](https://developers.openai.com/plugins/deploy/connect-chatgpt#refresh-metadata)


### 2. 测试读取

当 `MCP_ROOT` 为 MiniMcp 项目目录时，发送：

```text
请使用 MiniMcp 插件：
1. 调用 bash，参数 {"command":"pwd; ls -la"}。
2. 调用 read_file，参数 {"path":"Program.cs","start_line":1,"end_line":40}。
3. 展示实际返回内容并解释程序功能；调用失败时展示具体错误。
```

展开工具调用详情检查输入、输出；服务终端应看到对应工具完成且 `IsError = False`。如果切换了 MCP_ROOT，将 Program.cs 换为该项目实际存在的文件。

### 3. 测试补丁、回读和清理

选择项目内尚不存在的测试文件，例如 `mcp-smoke-test.txt`，发送：

```text
请使用 MiniMcp：
1. 用 apply_patch 新增 mcp-smoke-test.txt，内容为 MiniMcp patch test 加一个换行。
   先用 dry_run=true 校验并展示变更计划，再用相同 patch、dry_run=false 执行。
2. 用 read_file 回读，确认文件内容。
3. 用 apply_patch 把这一行改为 MiniMcp updated，再回读确认。
4. 用 bash 执行 git diff --stat，展示退出码、stdout 和 stderr。
5. 最后用 apply_patch 删除刚才创建的 mcp-smoke-test.txt。
```

如果同名文件已存在，换一个测试文件名。按 ChatGPT 的工具确认设置操作。`git diff` 不显示未跟踪文件的内容，因此测试新增文件以 `read_file` 回读为准；不是 Git 仓库时该命令会返回非零退出码。

### 4. 健康与认证检查

在本机另开终端：

```bash
curl --fail http://127.0.0.1:8000/healthz
curl -i http://127.0.0.1:8000/mcp
```

CloudflareAccess 模式下预期分别为健康状态 `200` 和匿名访问 `401`；`/healthz` 只表明应用运行，不检验公网 OAuth。

公网未登录检查：

```bash
curl -i -H 'Accept: application/json' https://mcp.yoimiyalove.top/mcp
curl -i https://mcp.yoimiyalove.top/.well-known/oauth-authorization-server
```

Managed OAuth 正常配置时，非浏览器未认证请求预期得到带发现信息的 `401`，授权服务器发现地址应返回 metadata。完整联调仍以实际工具调用为准。

<a id="tool-usage"></a>

## 补丁格式与一次性 Bash 用法

### apply_patch

`patch` 是下面这样的完整字符串，使用 `*** Begin Patch` / `*** End Patch` 包裹。新增文件的每一行以 `+` 开头：

```diff
*** Begin Patch
*** Add File: notes/example.txt
+hello
+world
*** End Patch
```

更新已有文件前先读取。`@@` 开始一段修改，空格前缀表示上下文，`-` 表示删除，`+` 表示插入；空白上下文行也必须带一个空格前缀。以下示例把 `hello` 改为 `Hello` 并保留下一行：

```diff
*** Begin Patch
*** Update File: notes/example.txt
@@
-hello
+Hello
 world
*** End Patch
```

移动可与修改合并，也可以单独使用；删除会删除整个文本文件：

```diff
*** Begin Patch
*** Update File: notes/example.txt
*** Move to: notes/renamed.txt
*** End Patch
```

```diff
*** Begin Patch
*** Delete File: notes/renamed.txt
*** End Patch
```

- 最多 64 个文件操作、补丁 UTF-8 大小最多 1 MiB。新增和移动的目标必须不存在；同一文件路径在一份补丁里只能涉及一次。
- 匹配严格区分空白与大小写，必须在当前搜索范围内唯一；有重复片段时增加上下文。多个 `@@` 按原文件顺序处理，不允许重叠。
- `@@ 精确的一整行` 可定位到该行之后；定位行本身也必须在尚未消费的范围内唯一。`*** End of File` 放在最后一段末尾，可限定匹配必须到文件末尾。只有 `+` 的修改段表示追加到文件末尾。
- 不接受 `git diff` 的标准 unified diff 行号头，也不做模糊匹配。这是下文所述 Codex 补丁格式的严格子集。
- 新文件使用 UTF-8 无 BOM、LF 换行。已有文件保留 BOM、末尾是否有换行及 Unix 权限；未改行保留原换行符，新增行采用原文件首个换行符（无换行符时用 LF）。
- `dry_run=true` 完成同样的解析、路径、上下文和内容大小校验，返回文件级计划，不创建目录或写文件。返回 `dryRun` 和 `changes`，每项包含 `action`、`path`、`moveTo`、`beforeBytes`、`afterBytes`。真正执行时会重新校验，预演不会锁定文件供以后提交。

### bash

以下都是 `bash` 工具的参数示例：

```json
{"command":"pwd; ls -la"}
{"command":"rg -n 'MapMcp' --glob '*.cs' ."}
{"command":"git status --short; git diff -- Program.cs"}
{"command":"dotnet build MiniMcp.csproj --nologo","timeout_seconds":120}
{"command":"pwd; ls","workdir":"Services"}
```

返回 `exitCode`、`stdout`、`stderr`、`timedOut`、`outputTruncated`。非零退出码是命令结果，不会自动变成 MCP 工具错误；超时时 `exitCode=null`、`timedOut=true`，保留已经收集的输出。stdout、stderr 分别最多 16000 个 UTF-16 字符单元，超出部分继续读取并丢弃，避免管道堵塞。`outputTruncated` 也可能表示后代进程仍占用输出管道，清理后等待读取超过 5 秒而停止收集。

每次使用 `--noprofile --norc` 启动新 Bash，不读 shell 启动脚本；上一次的 `cd`、`export`、函数和别名不会保留，文件修改会保留。需要连续操作时放在同一个 `command` 中。stdin 关闭，不支持交互输入、PTY、长期会话或后台任务管理；不要用它运行需要持续存活的服务器。

命令正常结束、超时或取消后都会清理普通子进程：Linux 使用 `setsid` 创建进程组，Windows 使用带 `KILL_ON_JOB_CLOSE` 的 Job Object。主动脱离进程组的恶意程序不属于 Linux 清理保证范围，这些机制不提供文件或网络沙箱。

子进程仅继承 PATH、HOME/用户目录、语言、临时目录、Windows 系统目录及 HTTP(S)/ALL/NO_PROXY 等基础变量，不自动继承 MiniMcp 的配置、凭据或 `BASH_ENV`。这不阻止命令主动读取账号可访问的配置文件。依赖自定义 SDK 环境变量的命令，可在单次命令中显式设置。

<a id="systemd"></a>

## Arch：systemd 服务与自启动

本节供之后切换为后台服务时使用。先安装并手动验证 systemd 服务，再决定启用登录或开机自启动。

### 1. 发布并安装用户级服务

先在两个前台终端按 `Ctrl+C` 停止已有进程，避免端口占用和重复连接器。

```bash
cd "$HOME/MiniMcp"
dotnet publish MiniMcp.csproj -c Release -o "$HOME/apps/MiniMcp"
install -d -m 700 "$HOME/.config/minimcp"
install -m 600 .env "$HOME/.config/minimcp/environment"
install -d "$HOME/.config/systemd/user"
install -m 644 deploy/minimcp.service "$HOME/.config/systemd/user/minimcp.service"
install -m 644 deploy/cloudflared-mcp.service "$HOME/.config/systemd/user/cloudflared-mcp.service"
systemctl --user daemon-reload
```

仓库模板分别为 [minimcp.service](deploy/minimcp.service) 和 [cloudflared-mcp.service](deploy/cloudflared-mcp.service)。这一步安装文件，下一步再启动。

| 内容 | 路径与作用 |
| --- | --- |
| 源码与手动配置 | `~/MiniMcp` 和其中的 `.env` |
| 发布目录 | `~/apps/MiniMcp`；服务运行该目录的 DLL |
| systemd 配置 | `~/.config/minimcp/environment`，由项目 `.env` 复制 |
| Tunnel token | `~/.config/minimcp/tunnel-token`，沿用已有有效文件 |
| MiniMcp 服务 | 普通用户权限运行；配置 `UMask=0077`、失败重启等 |
| Tunnel 服务 | 同一用户运行；启动时拉起 MiniMcp，独立保持隧道连接 |

两个模板使用 `/usr/bin/dotnet` 和 `/usr/bin/cloudflared`；在不同发行版使用时按实际安装位置调整。服务模板中的 `%h` 由 systemd 展开为该用户的 home。

Bash 子进程继承服务的普通用户身份和 systemd 限制。模板的 `NoNewPrivileges=true` 会限制提权，`PrivateTmp=true` 会改变它看到的临时目录；它们不会将命令限制在 `MCP_ROOT`。后台服务也不会加载你的交互式 shell 配置，所需命令应在其 PATH 中可用。

### 2. 先手动启动服务并验收

在正常桌面/SSH 用户会话中执行：

```bash
systemd-analyze --user verify "$HOME/.config/systemd/user/minimcp.service" "$HOME/.config/systemd/user/cloudflared-mcp.service"
systemctl --user start minimcp.service
systemctl --user start cloudflared-mcp.service
systemctl --user status minimcp.service cloudflared-mcp.service
curl --fail http://127.0.0.1:8000/healthz
```

查看日志：

```bash
journalctl --user -u minimcp.service -u cloudflared-mcp.service -f
```

确认两个服务为 `active (running)`，Tunnel 路由更新正确，再从 ChatGPT 复测读取与测试文件写入。`systemctl start` 只启动本次服务。[systemctl 官方说明](https://www.freedesktop.org/software/systemd/man/latest/systemctl.html)

### 3. 选择何时自动运行

**用户登录后自动运行：**

```bash
systemctl --user enable minimcp.service cloudflared-mcp.service
systemctl --user is-enabled minimcp.service cloudflared-mcp.service
```

**还需要开机后无人登录、或注销后继续运行：**

```bash
sudo loginctl enable-linger "$USER"
loginctl show-user "$USER" -p Linger
```

启用 linger 后，该用户的 systemd 管理器可在开机时启动并在注销后保留；预期看到 `Linger=yes`。[loginctl 官方说明](https://www.freedesktop.org/software/systemd/man/latest/loginctl.html)

之后实际注销/重启，再检查服务日志和 ChatGPT 文件工具。磁盘、项目目录、网络与代理也需在相应时点可用；尤其要验证现有 Mihomo 网络配置是否依赖桌面登录。自启效果以这次重启测试为准。

### 4. 日常维护与更新

| 操作 | 命令 |
| --- | --- |
| 查看状态 | `systemctl --user status minimcp.service cloudflared-mcp.service` |
| 重启应用 | `systemctl --user restart minimcp.service` |
| 重启隧道 | `systemctl --user restart cloudflared-mcp.service` |
| 本次开机日志 | `journalctl --user -u minimcp.service -u cloudflared-mcp.service -b` |
| 查看生效的 unit | `systemctl --user cat minimcp.service cloudflared-mcp.service` |

**配置来源要区分：** 前台读项目 `.env`；systemd 读 `~/.config/minimcp/environment`。修改项目 `.env` 后，同步并重启：

```bash
cd "$HOME/MiniMcp"
install -m 600 .env "$HOME/.config/minimcp/environment"
systemctl --user restart minimcp.service
```

更新 C# 代码时停止服务、重新发布，再启动：

```bash
systemctl --user stop cloudflared-mcp.service minimcp.service
cd "$HOME/MiniMcp"
dotnet publish MiniMcp.csproj -c Release -o "$HOME/apps/MiniMcp"
systemctl --user start minimcp.service cloudflared-mcp.service
```

改 unit 文件后重新安装对应文件并执行 `daemon-reload`、重启。Tunnel token 轮换后更新 token 文件并重启隧道。cloudflared 通过 pacman 升级。

### 5. 回到手动运行

```bash
systemctl --user disable --now cloudflared-mcp.service minimcp.service
```

然后按 [两个终端启动步骤](#arch-manual) 运行。如果 linger 只为本项目启用，且该用户的其他服务也不需要注销后运行，可再执行：

```bash
sudo loginctl disable-linger "$USER"
```

<a id="windows"></a>

## Windows 11：手动运行与自启动

以下是保留的 Windows 实机验证方案。源码和配置逻辑相同，路径、环境变量和后台运行方式使用 Windows 对应设置。

### 1. 安装依赖，准备项目

在 PowerShell 中安装缺少的组件：

```powershell
winget install --id Microsoft.DotNet.SDK.10 --exact
winget install --id Cloudflare.cloudflared --exact
winget install --id Git.Git --exact
```

重新打开终端：

```powershell
dotnet --info
dotnet --list-runtimes
cloudflared --version
Set-Location 'D:\Projects\MiniMcp'
dotnet restore MiniMcp.csproj
dotnet build MiniMcp.csproj --no-restore
```

确认 `Microsoft.AspNetCore.App 10.0.x` 可用。下面假设代码位于 `D:\Projects\MiniMcp`；替换为实际路径。[.NET Windows 安装说明](https://learn.microsoft.com/en-us/dotnet/core/install/windows)

本项目在 Windows 使用 **Git for Windows 的 Bash**，不调用 PowerShell 或 WSL。默认路径为 `C:\Program Files\Git\bin\bash.exe`；自定义安装路径通过 `MCP_BASH_PATH` 指定。可在 PowerShell 验证 `& 'C:\Program Files\Git\bin\bash.exe' --version`。`rg` 不是 Git Bash 自带命令，使用搜索示例前另行安装并加入 PATH。[Git for Windows](https://gitforwindows.org/)

### 2. 两个 PowerShell 终端手动运行

**终端一：配置当前进程环境并运行应用。**

```powershell
Set-Location 'D:\Projects\MiniMcp'
$env:MCP_ROOT = 'D:\Projects\MiniMcp'
$env:MCP_AUTH_MODE = 'CloudflareAccess'
$env:MCP_PUBLIC_URL = 'https://mcp.yoimiyalove.top'
$env:CF_ACCESS_TEAM = 'replace-with-your-team-prefix'
$env:CF_ACCESS_AUD = 'replace-with-application-audience-tag'
$env:MCP_PORT = '8000'
$env:MCP_BASH_PATH = 'C:\Program Files\Git\bin\bash.exe'
dotnet run --no-launch-profile
```

填写实际 team 和 AUD。PowerShell 不执行 Arch 的 `source .env`；本段通过 `$env:` 显式设置，关闭终端后这些进程环境变量不保留。

**终端二：首次保存 token，再启动 Tunnel。**

```powershell
$tokenPath = Join-Path $env:USERPROFILE '.config\minimcp\tunnel-token'
New-Item -ItemType Directory -Force -Path (Split-Path $tokenPath) | Out-Null
$secureToken = Read-Host '粘贴完整 Tunnel token' -AsSecureString
$token = [System.Net.NetworkCredential]::new('', $secureToken).Password
[System.IO.File]::WriteAllText($tokenPath, $token.Trim(), [System.Text.UTF8Encoding]::new($false))
Remove-Variable token, secureToken
```

使用无 BOM 的 UTF-8 写入，避免 Windows PowerShell 编码影响 token。已有有效文件时直接运行：

```powershell
cloudflared tunnel --no-autoupdate run --token-file "$env:USERPROFILE\.config\minimcp\tunnel-token"
```

在另一个终端检查本机服务：

```powershell
curl.exe --fail http://127.0.0.1:8000/healthz
Get-NetTCPConnection -LocalPort 8000 -State Listen
```

然后在 ChatGPT 重复读取、补丁回读和 Bash 测试。文件工具路径和 `workdir` 仍用相对路径及 `/`；命令正文使用 Bash 语法，例如 `pwd`、`git diff`、`dotnet build`。结束时两个前台终端分别按 `Ctrl+C`。

### 3. 双系统和两台电脑如何配置

同一台电脑的 Arch/Windows 双启动，只有一个系统在线时，可以沿用同一个 Tunnel、hostname 和 Access 应用；各系统的 MCP_ROOT 填本机实际项目路径。

两台电脑同时运行并操作各自项目时，为它们分别配置 Tunnel 和 hostname，例如 `arch-mcp.yoimiyalove.top`、`win-mcp.yoimiyalove.top`，再设置相应 Access 应用、AUD 和 MCP_PUBLIC_URL。同一 Tunnel 的多个连接器用于副本，流量可能进入任一连接器，不能用它区分两个独立文件工作区。[Cloudflare 副本与流量说明](https://developers.cloudflare.com/reference-architecture/architectures/load-balancing/)

### 4. 可选：Windows 登录后自动运行 MiniMcp

先完成前台验证，再停止前台 MiniMcp，发布应用：

```powershell
Set-Location 'D:\Projects\MiniMcp'
dotnet publish MiniMcp.csproj -c Release -o "$env:LOCALAPPDATA\MiniMcp"
```

在发布目录创建 `start-minimcp.cmd`，填写以下内容，并替换路径、team 和 AUD：

```bat
@echo off
setlocal
set "MCP_ROOT=D:\Projects\MiniMcp"
set "MCP_AUTH_MODE=CloudflareAccess"
set "MCP_PUBLIC_URL=https://mcp.yoimiyalove.top"
set "CF_ACCESS_TEAM=replace-with-your-team-prefix"
set "CF_ACCESS_AUD=replace-with-application-audience-tag"
set "MCP_PORT=8000"
set "MCP_BASH_PATH=%ProgramFiles%\Git\bin\bash.exe"
cd /d "%~dp0"
"%ProgramFiles%\dotnet\dotnet.exe" "%~dp0MiniMcp.dll" >> "%~dp0minimcp.log" 2>&1
```

通过 `taskschd.msc` 打开任务计划程序，选择 **创建任务**：

| 设置 | 内容 |
| --- | --- |
| 名称 | `MiniMcp` |
| 运行账户 | 当前普通用户 |
| 安全选项 | 仅当用户登录时运行；保持普通权限 |
| 触发器 | 指定用户登录时 |
| 操作 | 启动程序 |
| 程序 | `C:\Windows\System32\cmd.exe` |
| 参数 | `/d /c start-minimcp.cmd` |
| 起始于 | 发布目录的实际绝对路径，例如 `C:\Users\YOUR_USER\AppData\Local\MiniMcp` |
| 执行超时 | 取消默认的运行超时停止条件，使服务能持续运行 |
| 已在运行时 | 不启动新实例 |
| 失败重试 | 可设每 1 分钟重试，按需要调整次数 |

起始目录填实际路径；可在 PowerShell 查看 `$env:LOCALAPPDATA`。创建后先右键任务 **运行**，检查 `minimcp.log`、本机 `/healthz` 和 ChatGPT 工具，再注销/登录验证触发器。[微软登录触发器说明](https://learn.microsoft.com/en-us/windows/win32/taskschd/starting-an-executable-when-a-user-logs-on)

该方案让文件服务在用户登录后运行。无人登录时的文件服务需要另行验证任务身份、目录权限和启动方式；当前不将其列为已支持的实测场景。

### 5. 可选：将 cloudflared 安装为 Windows 服务

先结束前台 Tunnel。在**管理员 PowerShell** 中执行首次安装，token 仍从当前用户的文件读取：

```powershell
$tokenPath = Join-Path $env:USERPROFILE '.config\minimcp\tunnel-token'
$tunnelToken = (Get-Content -Raw $tokenPath).Trim()
cloudflared.exe service install $tunnelToken
Remove-Variable tunnelToken
Set-Service -Name cloudflared -StartupType Automatic
Start-Service -Name cloudflared
Get-Service -Name cloudflared
```

使用拥有该 token 文件的管理员用户提升终端；若换了管理员账户，按实际原用户路径设置 tokenPath。官方安装器把连接参数写入 Windows 服务配置；轮换 token 后也需要更新该服务配置。[Cloudflare 官方服务安装说明](https://developers.cloudflare.com/tunnel/get-started/)

此时 Tunnel 可在开机时运行，MiniMcp 按上一节在用户登录后启动。Tunnel 已在线但应用尚未运行时，访问可能得到 502。完成一次 Windows 重启、登录和真实工具调用后，再更新验证状态。

### 6. Windows 更新与停用

修改 `start-minimcp.cmd` 后，在任务计划程序结束并重新运行任务；修改源码后先结束任务，再重新发布。该脚本的配置独立于 Arch `.env`。

停止并禁用任务后，可恢复前台运行。管理员 PowerShell 管理 Tunnel 服务：

```powershell
Stop-Service -Name cloudflared
Set-Service -Name cloudflared -StartupType Manual
```

如果以后确定要卸载 Windows Tunnel 服务，再执行 `cloudflared.exe service uninstall`。Windows 侧的以上流程均待实机验证。

<a id="troubleshooting"></a>

## 日志解释与故障定位

### 已记录日志说明了什么

| 记录 | 解读 |
| --- | --- |
| `Registered tunnel connection`，`protocol=quic` | 日志中 QUIC 隧道已建立 |
| 启动时找不到本地 `config.yml` | 本项目使用 Dashboard 管理配置和 token；后续应核对远程配置更新 |
| 启动早期提示没有 ingress | 随后日志已收到包含 `mcp.yoimiyalove.top` 和本机 Service URL 的配置，实际工具调用也成功；据此可判断该提示没有持续阻断本次联调 |
| `TCP Connectivity ... FAIL`，UDP/QUIC PASS | 最新预检查中 region1 的 TCP 检查失败、region2 通过；当前隧道仍使用 QUIC，完整的 HTTP/2 回退尚未实测 |
| QUIC `timeout: no recent network activity` 后再次 `Registered tunnel connection` | 已记录到连接中断后重连成功|
| 请求四条 HA 连接但最多提供两条 | 本次日志注册了两条连接；记录不足以确定限制原因，现有工具调用已成功 |
| `No XML encryptor configured` | ASP.NET Core Data Protection 的本机密钥未配置静态加密；应与 Access JWT 校验分别看待 |
| `"apply_patch" completed. IsError = False` | 补丁调用成功；还需核对 dryRun，并回读确认实际写入内容 |

Cloudflare Tunnel 的 QUIC 使用出站 UDP 7844，HTTP/2 使用出站 TCP 7844。[端口说明](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/configure-tunnels/tunnel-with-firewall/)

Data Protection 密钥的存储和静态加密由 ASP.NET Core 的运行环境及配置决定。长期运行时可按需单独配置密钥保护。[微软说明](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/default-settings?view=aspnetcore-10.0)

### 常见问题

| 现象 | 检查方向 |
| --- | --- |
| `Provided Tunnel token is not valid` | 从 Tunnel 的连接器安装命令重新取完整 `eyJ...`；核对是否误填 UUID、整条命令、引号或带 BOM 的文本 |
| 配置变量缺失 | 前台是否导入 `.env`；systemd 是否复制到 environment；Windows 是否设置当前进程/启动脚本环境 |
| 端口 8000 被占用 | 是否同时启动了前台进程和后台服务；变更端口需同时修改 MCP_PORT 与 Tunnel Service URL |
| Cloudflare 1033 | Tunnel 是否连接、DNS 是否指向正确 Tunnel |
| 502 | MiniMcp 是否运行、后端端口是否正确、cloudflared 是否在同一台电脑 |
| 一直 503 或一直没有远程 ingress | 检查 Dashboard 的 Published application route 是否保存并下发 |
| OAuth 连接拿到登录 HTML | 检查 Managed OAuth 与覆盖整个 hostname 的 Access 应用 |
| 回调被拒绝 | 检查 Allowed redirect URIs 与 ChatGPT 管理页的实际回调地址 |
| 登录后仍 401 | 检查 team prefix、应用 AUD、机器时钟及服务认证日志 |
| 403 | 检查当前登录邮箱是否匹配 Access 策略 |
| ASP.NET 返回 400 | 核对 MCP_PUBLIC_URL 的 hostname 与请求 Host；再检查 MCP 请求格式 |
| ChatGPT 看不到工具或调用旧工具报 `Unknown tool` | 重启新版服务，在插件连接详情点击 Refresh，确认工具列表后新建对话并重新选择 MiniMcp；仍显示旧列表时重新添加连接 |
| 修改配置未生效 | 确认修改的是当前启动方式实际读取的配置，并重启应用 |
| 修改代码未生效 | 前台重新运行；后台服务重新发布 DLL 并重启 |
| 工具拒绝文件 | 检查相对路径、MCP_ROOT、链接、过滤规则、UTF-8 编码及大小上限 |
| 启动提示 Bash 路径无效 | 检查 MCP_BASH_PATH；Windows 安装 Git for Windows；Linux 安装 bash 和 util-linux |
| 补丁提示 context 不匹配或歧义 | 重新读取文件并增加精确上下文，避免重叠修改段；不要直接传 git diff 的行号格式 |
| Bash 返回非零退出码 | 查看 stderr；核对 workdir、命令依赖和 PATH，服务不会加载 shell 启动脚本 |
| Bash 超时或后台程序消失 | 单次调用有超时且结束后清理子进程；长期服务应在本机终端或 systemd 中启动 |
| systemd 提示没有用户总线 | 在实际登录用户的会话里使用 `systemctl --user`，核对用户管理器和 linger |
| systemd 在 NAMESPACE 阶段失败 | 核对系统对用户服务沙箱选项的支持，结合模板的 PrivateTmp 等设置排查 |
| `NU1301` | 检查运行终端能否访问 NuGet 源 `https://api.nuget.org/v3/index.json` |

<a id="development"></a>

## 开发检查与本地 MCP 测试

核心检查不依赖第三方测试包：

```bash
dotnet run --project tests/MiniMcp.Checks
```

检查使用临时工作区和真实 Bash，覆盖读取行范围、UTF-8/字节上限、路径/链接、补丁预演与整批校验、BOM/换行/Unix 权限，以及命令退出码、输出截断、超时和取消。Linux 额外检查超时、取消和正常退出后的子进程清理。运行前需安装 Bash（Linux 还需 util-linux）；测试不替代真实 OAuth、Windows 或重启自启验证。

另有基于实际 MCP SDK 的内存流协议检查，无需监听端口，验证客户端连接、恰好三个工具、参数 schema、结构化返回及工具错误：

```bash
dotnet run --project tests/MiniMcp.ProtocolChecks
```

该检查复用主项目依赖，不新增测试框架；它不覆盖 ASP.NET HTTP 传输与 Cloudflare 认证链路。

当前 MCP C# SDK `2.2.0` 支持 `2026-07-28` 修订，也兼容 `2025-11-25`。新版移除 `initialize` 握手与 `MCP-Session-Id`，改为逐请求携带协议元数据；`server/discover` 用于可选的版本与能力查询。项目未将服务固定到旧协议。[协议版本与兼容机制](https://modelcontextprotocol.io/specification/2026-07-28/basic/versioning)、[SDK 兼容说明](https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.2.0/docs/versioning.md)

只在本机联调、需要暂时关闭认证时，先停止已有的生产进程，再在独立终端运行：

```bash
cd "$HOME/MiniMcp"
export MCP_ROOT="$PWD"
export MCP_AUTH_MODE=Local
dotnet run --no-launch-profile
```

Local 模式仅接受 loopback Host。在这次无认证本机联调期间停止公网 Tunnel；完成后结束本机测试进程，按手动启动步骤重新加载 CloudflareAccess 配置。

可用 [MCP Inspector](https://github.com/modelcontextprotocol/inspector) 的 Streamable HTTP 连接 `http://127.0.0.1:8000/mcp`。也可在 Local 模式用 curl 检查工具发现：

```bash
curl --no-buffer http://127.0.0.1:8000/mcp \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H 'MCP-Protocol-Version: 2026-07-28' \
  -H 'Mcp-Method: tools/list' \
  --data '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}'
```

这是新版协议请求，正文版本须与请求头一致。调用具体工具时还需要对应的 `Mcp-Name` 请求头；SDK 客户端会处理这些协议字段。只替换旧示例里的版本日期不够。[HTTP 请求元数据规范](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http#request-metadata)

该服务使用 stateless Streamable HTTP，无需保存 MCP session ID。主项目常规构建命令为 `dotnet restore MiniMcp.csproj` 和 `dotnet build MiniMcp.csproj --no-restore`。

<a id="references"></a>

## 实现参考

本项目的补丁引擎和进程执行代码用 C# 独立实现，参考了其公开的格式和接口设计：

- **OpenAI Codex**（Apache-2.0）：参考 [apply-patch 的语法解析器](https://github.com/openai/codex/blob/main/codex-rs/apply-patch/src/parser.rs) 的 `Begin/End Patch`、`Add/Delete/Update File`、`Move to`、`@@` 和 `End of File` 格式。本项目使用精确且唯一的上下文匹配，保留文件编码/换行，并增加 `dry_run` 文件计划；不是 Codex 补丁引擎的完整移植。
- **DeepSeek Harness**（MIT）：参考 [tool-bash 文档](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/shell/tool-bash/README.md) 的一次性 shell、工作目录、独立调用状态及命令退出结果设计。本项目使用 .NET Process，超时后终止命令，不提供上游的后台任务转交与任务管理功能。
- 进程清理依据 Linux [setsid](https://man7.org/linux/man-pages/man1/setsid.1.html) 和微软 [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects) 的平台接口。

<a id="license"></a>

## 许可证

本项目采用 [MIT 许可证](LICENSE)。
