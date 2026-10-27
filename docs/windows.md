# Windows 运行

需要 Windows、.NET 10 SDK，以及构建桌面程序所需的 Windows SDK 和 WinUI 构建工具。FullAccess 的 Bash 可使用 [Git for Windows](https://git-scm.com/install/windows) 提供的 Git Bash；`search_text` 需要 [ripgrep](https://github.com/BurntSushi/ripgrep/releases)，三个 Git 查询需要 Git，受管隧道需要 [cloudflared](https://developers.cloudflare.com/tunnel/downloads/)。这些程序需要自行安装，未安装的可选查询工具会返回错误。

## 运行桌面程序

在 PowerShell 中进入仓库根目录：

```powershell
dotnet build src/MiniMcp.Desktop/MiniMcp.Desktop.csproj
dotnet run --project src/MiniMcp.Desktop --no-launch-profile
```

在“设置”页选择或填写运行配置文件、项目目录和认证方式。Local 适合先在本机测试；公网使用 CloudflareAccess，按 [Cloudflare 指南](cloudflare.md)填写团队、AUD 和 Tunnel。首次使用先保存运行配置，再保存本机策略。返回“概览”启动服务，状态中查看本次项目、模式、地址和隧道状态。

默认配置目录为 `%LOCALAPPDATA%\MiniMcp\`，可在设置页加载其他 `settings.json`；策略仍使用该用户的默认 `policy.json`。修改策略后保存，并在概览页重启服务。路径和各字段说明见 [配置](configuration.md)。

## 运行 CLI

也可以从同一配置启动命令行程序：

```powershell
dotnet build src/MiniMcp.Cli/MiniMcp.Cli.csproj
dotnet run --project src/MiniMcp.Cli -- check
dotnet run --project src/MiniMcp.Cli -- run
```

如果使用自选配置文件，在 `check` 或 `run` 后加 `--config C:/path/to/settings.json`。按 Ctrl+C 停止 CLI。避免桌面与 CLI 同时使用相同端口；需要在 CLI 恢复记录时先停止正在运行的服务。

## 发布 CLI 和登录后启动

生成依赖本机 .NET 10 和 ASP.NET Core 10 运行时的 CLI 目录：

```powershell
dotnet publish src/MiniMcp.Cli/MiniMcp.Cli.csproj -c Release --self-contained false -o C:/MiniMcp/publish-cli
C:/MiniMcp/publish-cli/minimcp.exe --help
C:/MiniMcp/publish-cli/minimcp.exe check --config C:/path/to/settings.json
C:/MiniMcp/publish-cli/minimcp.exe run --config C:/path/to/settings.json
```

替换发布与配置路径，并保留整个发布目录。先在当前用户下手动检查和运行；运行依赖和发布方式见 [.NET 发布文档](https://learn.microsoft.com/en-us/dotnet/core/deploying/)。

桌面程序也可以发布为依赖本机 .NET 运行时的目录：

```powershell
dotnet publish src/MiniMcp.Desktop/MiniMcp.Desktop.csproj -c Release --self-contained false -o C:/MiniMcp/publish-desktop
C:/MiniMcp/publish-desktop/MiniMcp.Desktop.exe
```

发布目录包含 Windows App SDK 和桌面资源文件；复制或移动时保留整个目录。

需要登录后运行时，可用任务计划程序创建任务：触发器选“用户登录时”，操作为启动发布目录中的 `minimcp.exe`，参数填写 `run --config "C:\path\to\settings.json"`，起始位置填写发布目录。选择实际拥有配置和项目权限的用户；不要在参数中填写 Tunnel token。先手动运行任务并检查端口和日志，再决定是否启用自动运行。停止或禁用任务使用任务计划程序；启用受管隧道时由 MiniMcp 管理 cloudflared。

## 诊断与测试

桌面运行日志页支持筛选、复制和导出；CLI 在前台显示日志。端口、程序路径、Access 登录和恢复问题见 [排错](troubleshooting.md)。开发者可运行：

```powershell
dotnet run --project tests/MiniMcp.Checks
dotnet run --project tests/MiniMcp.ProtocolChecks
dotnet run --project tests/MiniMcp.RuntimeChecks
dotnet run --project tests/MiniMcp.DesktopChecks
dotnet run --project tests/MiniMcp.DesktopChecks -- --ui
```

UI 检查会打开测试窗口和系统文件选择器，短暂移动鼠标并使用临时项目；不使用用户的运行配置或 Tunnel token。完整 Runtime 检查请使用 `dotnet run` 或 apphost，测试程序需要以自身可执行文件启动受控子进程。
