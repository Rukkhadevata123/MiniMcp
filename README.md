# MiniMcp

MiniMcp 正在重组为 Runtime 类库、跨平台 CLI 和 Windows WinUI 入口，HTTP 服务由入口在进程内托管。

当前已完成业务代码迁移和进程内 HTTP 启停原型：CLI 可启动仅有 `/healthz` 的 loopback 服务。正式 JSON 配置、MCP HTTP 工具注册、执行模式、审批、恢复记录和桌面界面尚未接入。

旧根项目、环境变量配置入口及其部署模板已移除。当前 `/mcp` 返回 404，不对外开放工具，也不能把已有核心代码视为已经接入只读或审批保护。

## 项目结构

```text
MiniMcp.slnx
src/
  MiniMcp.Runtime/           .NET 10 类库
    McpRuntime.cs            由调用者控制的进程内 HTTP 生命周期
    Configuration/          核心内存选项，正式 JSON 配置待实现
    Security/               Cloudflare Access JWT 验证
    Services/               文件、补丁、Bash 与进程清理
    Tools/                  三个 MCP 工具入口
  MiniMcp.Cli/               .NET 10 命令行入口
tests/
  MiniMcp.Checks/            引用 Runtime 的核心行为检查
  MiniMcp.ProtocolChecks/    实际 MCP SDK 内存流检查
  MiniMcp.RuntimeChecks/     实际 loopback HTTP 生命周期检查
```

WinUI 项目会在进程内生命周期验证的小步中加入。当前解决方案只包含跨平台项目。

## 构建与 CLI 入口

需要 .NET 10 SDK；在仓库根目录执行：

```powershell
dotnet build MiniMcp.slnx
dotnet run --project src/MiniMcp.Cli -- --help
dotnet run --project src/MiniMcp.Cli -- run --port 8000
```

无参数同样显示帮助。`run` 默认端口为 8000，`--port` 范围为 1–65535；仅监听 `127.0.0.1`，访问 `http://127.0.0.1:8000/healthz` 返回 `{"status":"ok"}`。按 Ctrl+C 正常停止；Linux 也接收 SIGTERM，但尚未在 Linux 实机验证。

正常结束返回 `0`，启动/停止失败返回 `1`，无效参数返回 `2`，启动阶段取消返回 `130`。端口冲突不会停止已有服务。`run` 目前是健康端点原型，不接受正式配置文件，也不启用 `/mcp`。

Runtime 是类库，不作为独立服务 DLL 启动。CLI 直接调用 `McpRuntime.StartAsync/StopAsync/DisposeAsync`，后续 WinUI 复用同一生命周期。内部 Host 使用空配置构建器，监听地址不取自 `ASPNETCORE_URLS`，也不安装控制终端信号的默认 Host lifetime。

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
