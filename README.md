# MiniMcp

MiniMcp 正在重组为 Runtime 类库、跨平台 CLI 和 Windows WinUI 入口，MCP 服务将由入口在进程内托管。

当前完成的是项目骨架与业务代码迁移：Runtime 包含现有文件、补丁、Bash 工具及 Cloudflare Access 认证代码；CLI 仅提供帮助。HTTP 启停、正式 JSON 配置、执行模式、审批、恢复记录和桌面界面尚未接入。

旧根项目、环境变量配置入口及其部署模板已移除。当前不能通过 CLI 启动 MCP 服务，也不能把这次重组视为已经启用只读或审批保护。

## 项目结构

```text
MiniMcp.slnx
src/
  MiniMcp.Runtime/           .NET 10 类库
    Configuration/          核心内存选项，正式 JSON 配置待实现
    Security/               Cloudflare Access JWT 验证
    Services/               文件、补丁、Bash 与进程清理
    Tools/                  三个 MCP 工具入口
  MiniMcp.Cli/               .NET 10 命令行入口
tests/
  MiniMcp.Checks/            引用 Runtime 的核心行为检查
  MiniMcp.ProtocolChecks/    实际 MCP SDK 内存流检查
```

WinUI 项目会在进程内生命周期验证的小步中加入。当前解决方案只包含跨平台项目。

## 构建与 CLI 入口

需要 .NET 10 SDK；在仓库根目录执行：

```powershell
dotnet build MiniMcp.slnx
dotnet run --project src/MiniMcp.Cli -- --help
```

无参数同样显示帮助；未知命令返回退出码 `2`。服务启动命令尚未实现。

Runtime 是类库，不作为独立服务 DLL 启动。后续 CLI/WinUI 直接引用它，不通过子进程或管理 REST API 调用。

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
```

运行检查需要 Bash。Windows 默认使用 `C:\Program Files\Git\bin\bash.exe`；Linux 使用 `/bin/bash` 并需要 util-linux 的 `/usr/bin/setsid`。自定义安装可通过仅供检查程序使用的 `MINIMCP_TEST_BASH_PATH` 指定 Bash 路径。

核心检查覆盖文件/路径限制、UTF-8、补丁预演与提交、格式保留、真实命令输出、超时和取消。协议检查使用实际 MCP SDK，通过内存流验证工具发现、参数、返回值和错误；它不验证 HTTP、OAuth 或公网 Tunnel。

<a id="references"></a>

## 实现参考

本项目的补丁引擎和进程执行代码用 C# 独立实现，参考了其公开的格式和接口设计：

- **OpenAI Codex**（Apache-2.0）：参考 [apply-patch 的语法解析器](https://github.com/openai/codex/blob/main/codex-rs/apply-patch/src/parser.rs) 的 `Begin/End Patch`、`Add/Delete/Update File`、`Move to`、`@@` 和 `End of File` 格式。本项目使用精确且唯一的上下文匹配，保留文件编码/换行，并增加 `dry_run` 文件计划；不是 Codex 补丁引擎的完整移植。
- **DeepSeek Harness**（MIT）：参考 [tool-bash 文档](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/shell/tool-bash/README.md) 的一次性 shell、工作目录、独立调用状态及命令退出结果设计。本项目使用 .NET Process，超时后终止命令，不提供上游的后台任务转交与任务管理功能。
- 进程清理依据 Linux [setsid](https://man7.org/linux/man-pages/man1/setsid.1.html) 和微软 [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects) 的平台接口。

<a id="license"></a>

## 许可证

本项目采用 [MIT 许可证](LICENSE)。
