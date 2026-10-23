# MiniMcp

MiniMcp 由 Runtime 类库、跨平台 CLI 和 Windows WinUI 入口组成，HTTP 服务在入口进程内运行。

CLI 与桌面窗口直接控制 Runtime 的启动和停止。当前 HTTP 入口仅提供本机健康检查 `/healthz`，`/mcp` 返回 404。

文件、补丁、Bash 和 Cloudflare Access 认证的核心代码由独立检查程序验证；当前 HTTP 入口不对外开放这些工具。

## 项目结构

```text
MiniMcp.slnx
src/
  MiniMcp.Runtime/           .NET 10 类库
    McpRuntime.cs            由调用者控制的进程内 HTTP 生命周期
    Configuration/          运行配置、认证和本机执行策略模型
    Security/               Cloudflare Access JWT 验证
    Services/               文件、补丁、Bash 与进程清理
    Tools/                  三个 MCP 工具入口
  MiniMcp.Cli/               .NET 10 命令行入口
  MiniMcp.Desktop/           Windows WinUI 3 本机服务窗口
examples/
  settings.local.json       本机运行配置格式示例
  policy.readonly.json      本机只读策略格式示例
tests/
  MiniMcp.Checks/            引用 Runtime 的核心行为检查
  MiniMcp.ProtocolChecks/    实际 MCP SDK 内存流检查
  MiniMcp.RuntimeChecks/     实际 loopback HTTP 生命周期检查
```

根解决方案包含全部项目，供 Windows 构建和编辑器加载。Linux 使用 CLI 项目作为构建入口，项目引用会自动构建 Runtime，不涉及 WinUI：

```bash
dotnet build src/MiniMcp.Cli/MiniMcp.Cli.csproj
```

## 构建与 CLI 入口

需要 .NET 10 SDK；Windows 上在仓库根目录构建完整解决方案：

```powershell
dotnet build MiniMcp.slnx
dotnet run --project src/MiniMcp.Cli -- --help
dotnet run --project src/MiniMcp.Cli -- run --port 8000
```

无参数同样显示帮助。`run` 默认端口为 8000，`--port` 范围为 1–65535；仅监听 `127.0.0.1`，访问 `http://127.0.0.1:8000/healthz` 返回 `{"status":"ok"}`。按 Ctrl+C 正常停止；Linux 也接收 SIGTERM，但尚未在 Linux 实机验证。

正常结束返回 `0`，启动/停止失败返回 `1`，无效参数返回 `2`，启动阶段取消返回 `130`。端口冲突不会停止已有服务。`run` 目前是健康端点原型，不接受正式配置文件，也不启用 `/mcp`。

CLI 与 WinUI 直接调用 `McpRuntime.StartAsync/StopAsync/DisposeAsync`。内部 Host 使用空配置构建器，监听地址不取自 `ASPNETCORE_URLS`，也不安装控制终端信号的默认 Host lifetime。

`examples/` 提供不含凭据的 JSON 格式示例，当前入口不读取这些文件。`AppSettings` 定义项目目录、端口、Bash、认证和隧道配置；`ExecutionPolicy` 单独定义本机执行权限和恢复目录。模型默认认证为 `CloudflareAccess`、执行模式为 `ReadOnly`、隧道关闭；本机示例显式选择 `Local` 认证。模型定义不代表工具权限检查已接入。

Runtime 的 `ConfigurationPaths` 提供共用路径规则：Windows 配置位于 `%LOCALAPPDATA%/MiniMcp/`，Linux 位于 `$XDG_CONFIG_HOME/minimcp/`（默认 `~/.config/minimcp/`）；其中分别存放 `settings.json`、`policy.json`。恢复目录在 Windows 默认为配置目录下的 `recovery/`，Linux 为 `$XDG_STATE_HOME/minimcp/recovery/`（默认 `~/.local/state/minimcp/recovery/`），相对 XDG 值忽略。配置文件选择相对调用者工作目录，JSON 内路径相对所属文件目录；路径计算不创建文件，不展开环境变量或 `~`。这些规则供后续配置加载器使用。

`ExternalProgramLocator` 提供 Bash/cloudflared 文件定位：显式路径相对配置文件解析，路径无效时报错；留空时先检查常见安装目录，再检查 PATH 中的绝对目录。Windows 通过 Git 安装结构自动识别 Bash；cloudflared 只查找 `cloudflared.exe`，Linux 查找 `bash`、`cloudflared`。定位不执行程序、不探测版本，自动查找无结果时返回空值，由配置加载器按所需功能判断依赖。

## Windows 桌面入口

需要 .NET 10 SDK、Windows SDK 和 WinUI 构建工具。单独构建和运行：

```powershell
dotnet build src/MiniMcp.Desktop/MiniMcp.Desktop.csproj
dotnet run --project src/MiniMcp.Desktop --no-launch-profile
```

窗口使用端口 8000，提供启动、停止、运行地址和错误信息。启动后访问 `http://127.0.0.1:8000/healthz`；关闭窗口会先取消进行中的启动，再停止并释放 Runtime。端口被其他程序占用时显示错误，不结束占用者。

桌面采用未打包模式，Windows App SDK 运行文件复制到输出目录；.NET 10 / ASP.NET Core 运行依赖由目标机器提供。

## 当前核心工具

| 工具 | 参数 | 行为 |
| --- | --- | --- |
| `read_file` | `path`、`start_line=1`、`end_line=null` | 读取 UTF-8 文本，行号从 1 开始，包含结束行 |
| `apply_patch` | `patch`、`dry_run=false` | 新增、更新、移动、删除；预演不修改文件 |
| `bash` | `command`、`workdir="."`、`timeout_seconds=30` | 独立 Bash 调用，超时范围 1–600 秒 |

文件路径和工作目录参数使用相对项目根的 `/` 分隔路径。文件工具拒绝越界、链接、私密目录和凭据文件；单文件最多 256 KiB，文本输出最多 16000 个 UTF-16 字符单元。

补丁使用 `*** Begin Patch` / `*** End Patch` 格式，精确匹配上下文，保留既有 BOM、换行和 Unix 权限，先校验整批再提交。现有提交失败回滚不等于尚未实现的长期恢复记录。

Bash 使用运行账号的实际权限，项目根只是默认工作目录，不是沙箱；它可以访问账号能访问的其他路径和网络。每次调用的 shell 状态独立，普通子进程在结束、超时或取消后清理。

## 检查

检查直接引用 Runtime 的真实程序集，不复制业务源码到测试项目：

```powershell
dotnet run --project tests/MiniMcp.Checks
dotnet run --project tests/MiniMcp.ProtocolChecks
dotnet run --project tests/MiniMcp.RuntimeChecks
```

运行检查需要 Bash。Windows 默认使用 `C:\Program Files\Git\bin\bash.exe`；Linux 使用 `/bin/bash` 并需要 util-linux 的 `/usr/bin/setsid`。自定义安装可通过仅供检查程序使用的 `MINIMCP_TEST_BASH_PATH` 指定 Bash 路径。

核心检查覆盖文件/路径限制、UTF-8、补丁预演与提交、格式保留、真实命令输出、超时和取消。协议检查使用实际 MCP SDK，通过内存流验证工具发现、参数、返回值和错误；它不验证 HTTP、OAuth 或公网 Tunnel。

Runtime 检查覆盖真实 HTTP 响应、loopback 绑定、端口冲突、并发启动、取消、停止后重用端口和释放后禁止启动；同时确认当前 `/mcp` 未开放。它不替代后续 MCP HTTP、WinUI 或 OAuth 的实际验证。

<a id="references"></a>

## 实现参考

本项目的补丁引擎和进程执行代码用 C# 独立实现，参考了其公开的格式和接口设计：

- **OpenAI Codex**（Apache-2.0）：参考 [apply-patch 的语法解析器](https://github.com/openai/codex/blob/main/codex-rs/apply-patch/src/parser.rs) 的 `Begin/End Patch`、`Add/Delete/Update File`、`Move to`、`@@` 和 `End of File` 格式。本项目使用精确且唯一的上下文匹配，保留文件编码/换行，并增加 `dry_run` 文件计划；不是 Codex 补丁引擎的完整移植。
- **DeepSeek Harness**（MIT）：参考 [tool-bash 文档](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/shell/tool-bash/README.md) 的一次性 shell、工作目录、独立调用状态及命令退出结果设计。本项目使用 .NET Process，超时后终止命令，不提供上游的后台任务转交与任务管理功能。
- 进程清理依据 Linux [setsid](https://man7.org/linux/man-pages/man1/setsid.1.html) 和微软 [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects) 的平台接口。

<a id="license"></a>

## 许可证

本项目采用 [MIT 许可证](LICENSE)。
