# Linux 运行与 systemd

Linux 使用 CLI 入口。准备 .NET 10 SDK、ASP.NET Core 运行时、Git、ripgrep 和 Bash；启用 Bash 或受管隧道时需要 util-linux 提供的 `/usr/bin/setsid`，文本搜索也需要它。cloudflared 仅在使用受管 Tunnel 时需要。依赖安装来源和版本以各发行版及官方发行页为准：[.NET](https://learn.microsoft.com/en-us/dotnet/core/install/linux)、[ripgrep](https://github.com/BurntSushi/ripgrep/releases)、[Git](https://git-scm.com/install/linux)、[cloudflared](https://developers.cloudflare.com/tunnel/downloads/)。

Arch Linux 可以按需要安装软件包：

```bash
sudo pacman -Syu --needed dotnet-sdk aspnet-runtime dotnet-targeting-pack aspnet-targeting-pack bash util-linux git ripgrep cloudflared
dotnet --info
dotnet --list-runtimes
command -v bash rg git cloudflared
test -x /usr/bin/setsid
```

确认 SDK 为 10，运行时包含 `Microsoft.NETCore.App` 和 `Microsoft.AspNetCore.App` 10.x。包名及仓库可用性随发行版变化；其他发行版使用对应安装方法。ripgrep、Git 和 cloudflared 的路径留空时会自动查找，也可以在配置中显式指定可信程序。

## 首次配置和前台运行

1. 创建实际项目目录和用户配置目录。参考 [配置说明](configuration.md) 创建 `~/.config/minimcp/settings.json`，将 `projectRoot` 改为项目的绝对路径。Local 示例位于 [examples/settings.local.json](../examples/settings.local.json)。
2. 按需要创建同目录的 `policy.json`；缺省为 ReadOnly。恢复记录默认保存在 `~/.local/state/minimcp/recovery/`，与项目目录分开。
3. 在仓库根目录构建并启动：

```bash
dotnet build src/MiniMcp.Cli/MiniMcp.Cli.csproj
dotnet run --project src/MiniMcp.Cli -- check
dotnet run --project src/MiniMcp.Cli -- run
```

如需使用别处的运行配置，在 `check` 和 `run` 后加 `--config /absolute/path/settings.json`。端口默认 8000，本机健康检查：

```bash
curl --fail http://127.0.0.1:8000/healthz
```

按 Ctrl+C 停止。CloudflareAccess 和 Tunnel 的创建、token 保存及公网客户端连接见 [Cloudflare 指南](cloudflare.md)。

## 发布 CLI

以下命令在目标 Linux 机器上生成依赖本机 .NET 运行时的目录；确认已安装相同大版本的 .NET 和 ASP.NET Core 运行时：

```bash
dotnet publish src/MiniMcp.Cli/MiniMcp.Cli.csproj -c Release --self-contained false -o "$HOME/minimcp-publish"
"$HOME/minimcp-publish/minimcp" --help
"$HOME/minimcp-publish/minimcp" check --config "$HOME/.config/minimcp/settings.json"
"$HOME/minimcp-publish/minimcp" run --config "$HOME/.config/minimcp/settings.json"
```

保留整个发布目录，不要只复制可执行文件。.NET 发布模式说明见 [Microsoft 文档](https://learn.microsoft.com/en-us/dotnet/core/deploying/)；实际机器的发行版、运行时、权限和进程清理应按 [检查](#检查)中的命令验证。

## systemd 用户服务

先用上面的前台命令检查配置和公网连接。创建 `~/.config/systemd/user/minimcp.service`，将 `YOUR_USER` 替换为实际用户名并确认所有路径：

```ini
[Unit]
Description=MiniMcp
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=/home/YOUR_USER/minimcp-publish
ExecStart=/home/YOUR_USER/minimcp-publish/minimcp run --config /home/YOUR_USER/.config/minimcp/settings.json
Restart=on-failure
RestartSec=5
TimeoutStopSec=15

[Install]
WantedBy=default.target
```

```bash
systemctl --user daemon-reload
systemctl --user enable --now minimcp.service
systemctl --user status minimcp.service
journalctl --user -u minimcp.service -f
systemctl --user restart minimcp.service
systemctl --user stop minimcp.service
```

服务使用该用户的配置、策略和文件权限。启用 `tunnel.enabled` 时，CLI 自行启动和停止 cloudflared，无需另建 Tunnel 服务。用户服务通常跟随用户会话；需要注销后继续运行时，可按系统管理策略启用该用户的 systemd linger：`loginctl enable-linger YOUR_USER`。关闭自启动使用 `systemctl --user disable --now minimcp.service`。部署前核对账户权限、XDG 目录和服务环境中的 PATH；不要在 service 文件中放 token 正文。

## 检查

在仓库根目录运行跨平台检查：

```bash
dotnet run --project tests/MiniMcp.Checks
dotnet run --project tests/MiniMcp.ProtocolChecks
dotnet run --project tests/MiniMcp.RuntimeChecks
```

运行时检查会启动测试子进程，使用 `dotnet run` 或生成的 apphost，不要通过 `dotnet 测试程序集.dll` 启动。实际 Linux 安装还应验证前台 Ctrl+C、systemd 停止和 SIGTERM、端口释放以及所需外部程序的路径。Linux 上进程被 SIGKILL 或崩溃时无法执行托管清理，应检查是否仍有相关子进程。
