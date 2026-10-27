# 配置

MiniMcp 分别读取运行配置 `settings.json` 和本机执行策略 `policy.json`。前者选择项目、端口、认证和程序路径；后者决定允许的操作及恢复记录位置。GUI 可以编辑这两个文件，也可以按下面的格式手工创建。

## 文件位置

| 系统 | 运行配置与策略默认目录 | 默认恢复目录 |
| --- | --- | --- |
| Windows | `%LOCALAPPDATA%\MiniMcp\` | `%LOCALAPPDATA%\MiniMcp\recovery\` |
| Linux | `$XDG_CONFIG_HOME/minimcp/`，默认 `~/.config/minimcp/` | `$XDG_STATE_HOME/minimcp/recovery/`，默认 `~/.local/state/minimcp/recovery/` |

XDG 变量只有绝对路径才生效。`--config` 可以选另一个运行配置；策略始终读取当前系统用户目录下的 `policy.json`。缺少策略文件时使用 ReadOnly 默认值，缺少运行配置或文件损坏时会报错。

项目目录必须存在，不能是文件系统根目录或用户主目录。策略文件必须在项目外；恢复目录和项目目录不能相互包含。配置与恢复存储路径拒绝符号链接和 Windows junction。

## 运行配置

本机测试可以复制 [示例](../examples/settings.local.json)，把 `projectRoot` 改为实际存在的项目目录：

```json
{
  "schemaVersion": 1,
  "projectRoot": "./project",
  "port": 8000,
  "bashPath": "",
  "rgPath": "",
  "gitPath": "",
  "authentication": {
    "mode": "Local",
    "publicOrigin": null,
    "team": null,
    "audience": null
  },
  "tunnel": {
    "enabled": false,
    "executablePath": "",
    "tokenFilePath": ""
  }
}
```

| 字段 | 用途 |
| --- | --- |
| `projectRoot` | 项目目录；文件工具和默认工作目录从这里开始。 |
| `port` | 本机 IPv4 loopback 端口，范围 1–65535，默认 8000。 |
| `bashPath` | Bash 程序路径；只有 FullAccess 启动时必需。 |
| `rgPath` | ripgrep 程序路径；缺失时 `search_text` 返回错误，其他工具仍可使用。 |
| `gitPath` | Git 程序路径；缺失时三个 Git 查询返回错误，其他工具仍可使用。 |
| `authentication.mode` | `Local` 或 `CloudflareAccess`。Local 不要求登录，只用于可信的本机环境。 |
| `authentication.publicOrigin` | CloudflareAccess 的 HTTPS 根地址，例如 `https://mcp.example.com`；不要填写 `/mcp`、查询参数或凭据。 |
| `authentication.team` | Cloudflare Zero Trust 团队域名前缀，如 `my-team`。 |
| `authentication.audience` | Access 应用的 AUD tag。 |
| `tunnel.enabled` | 是否由 MiniMcp 启动 cloudflared；启用时认证模式必须为 CloudflareAccess。 |
| `tunnel.executablePath` | cloudflared 程序路径。 |
| `tunnel.tokenFilePath` | 受管 Tunnel token 文件路径；启用隧道时必须可读且非空。 |

程序路径留空时，MiniMcp 查找程序目录、常见安装位置和绝对 PATH 项；自动查找会跳过项目和当前工作目录内的候选。填写明确路径时使用可信程序。仓库不附带或自动下载 Bash、ripgrep、Git 或 cloudflared；`check` 只检查文件位置，不运行程序或验证版本。实际搜索会检查 ripgrep 是否支持所需参数。

Windows 查询检查使用过 ripgrep 15.2.0 和 Git 2.56.0；其他版本需要支持工具使用的固定参数和输出格式。可以分别运行 `rg --version`、`git --version` 确认当前 PATH 中的版本；显式配置的程序以所填路径为准。

`--config` 的相对路径以启动时工作目录为基准；JSON 里的相对路径以对应配置文件所在目录为基准。JSON 不展开 `~`、环境变量或 shell 表达式；Windows 路径中的反斜杠需要写成 `\\`，也可使用正斜杠。

Local 不接受启用的受管隧道。公网配置示例和 token 保存方法见 [Cloudflare 指南](cloudflare.md)。

## 本机策略

保存以下内容到默认配置目录的 `policy.json`，或在桌面“设置”页保存。完整示例也在 [examples](../examples/policy.readonly.json)。

```json
{
  "schemaVersion": 1,
  "mode": "ProjectEdit",
  "recoveryDirectory": "",
  "projectEditLimits": {
    "maxPaths": 10,
    "maxChangedLines": 500,
    "maxChangedBytes": 65536
  }
}
```

`mode` 可为 `ReadOnly`、`ProjectEdit` 或 `FullAccess`。空的 `recoveryDirectory` 选择上表中的默认位置；非空相对路径以 `policy.json` 所在目录为基准。策略文件必须由运行 MiniMcp 的系统用户管理。

ProjectEdit 上限按整批不同路径、增删行数之和及增删内容的 UTF-8 字节数计算，移动的源和目标各占一个路径。达到上限允许，超过任一上限则整批拒绝；0 表示该维度不允许非零改动。关键文件、未知文件类型、删除和移动也会被拒绝。详细权限见 [安全边界](security.md)。

配置采用 UTF-8 JSON，允许 BOM，单文件最多 64 KiB。字段名和模式名区分大小写；未知字段、重复键、错误类型和不支持的 `schemaVersion` 会报错。省略字段使用默认值。

## 检查和应用

```powershell
dotnet run --project src/MiniMcp.Cli -- check --config C:/path/to/settings.json
dotnet run --project src/MiniMcp.Cli -- run --config C:/path/to/settings.json
```

Linux 同样可用这两个命令，替换为自己的绝对路径。`check` 校验配置和已启用依赖，短暂检查端口能否绑定；它不启动服务或隧道，也不验证公网认证。端口被占用时会给出提示，配置本身通过仍可返回 0。

运行中的会话使用启动时的配置快照。修改后停止并重新启动；CLI `run --mode ProjectEdit` 可以只覆盖本次模式，不写入策略文件。桌面首次配置先保存运行配置，再保存本机策略；策略保存使用已保存的运行配置，而不是未保存的草稿。
