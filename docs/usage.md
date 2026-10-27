# 使用 MiniMcp

## 启动与连接

准备 [运行配置和本机策略](configuration.md)后，在仓库根目录运行：

```sh
dotnet run --project src/MiniMcp.Cli -- check --config /absolute/path/settings.json
dotnet run --project src/MiniMcp.Cli -- run --config /absolute/path/settings.json
```

替换配置路径。CLI 在前台输出状态与脱敏日志，按 Ctrl+C 停止；Linux 也支持 SIGTERM。`run --mode ReadOnly`、`ProjectEdit` 或 `FullAccess` 可以覆盖本次运行的策略模式。返回码 0 表示成功或正常停止，1 表示执行或清理故障，2 表示参数、配置或权限限制，130 表示取消。

Windows 也可运行 `dotnet run --project src/MiniMcp.Desktop --no-launch-profile`。概览页启停服务和受管隧道；设置页保存配置；运行日志页可以筛选、复制和导出；修改记录页提供预览和恢复。设置修改后使用概览页重启应用新配置。

Local 模式的客户端地址为 `http://127.0.0.1:8000/mcp`，按配置端口调整，传输方式选择 Streamable HTTP。CloudflareAccess 客户端使用 `https://你的域名/mcp` 和 OAuth。健康检查为本机 `http://127.0.0.1:8000/healthz`；浏览器直接 GET `/mcp` 不能代替 MCP 客户端验证。

## 工具与参数

路径使用相对于项目根的 `/` 分隔形式。下面的 JSON 代表工具参数，具体调用界面由客户端提供。

| 工具 | 参数和默认值 | 用途 |
| --- | --- | --- |
| `read_file` | 必填 `path`；`start_line=1`、`end_line=null` | 按 1 开始的包含式行号读取 UTF-8；返回行数、正文和截断信息。 |
| `list_files` | `path="."`、`max_depth=2`、`max_entries=200`、`include_hidden=false`、`format="flat"` | 列出文件；`format="tree"` 同时提供树形文本。 |
| `search_text` | 必填 `pattern`；`path="."`、`glob=null`、`regex=false`、`case_sensitive=true`、`max_depth=16`、`max_results=100` | 使用本机 ripgrep 搜索匹配行。默认字面匹配，`regex=true` 使用 Rust 正则。 |
| `git_status` | `path="."`、`max_entries=200` | 查看暂存、未暂存和未跟踪状态。 |
| `git_diff` | `path="."`、`staged=false`、`max_files=20`、`max_lines=1000` | 比较工作区与 index；`staged=true` 比较 index 与 HEAD。 |
| `git_log` | `path="."`、`max_count=20` | 查看当前 HEAD 可达的提交摘要。 |
| `apply_patch` | 必填 `patch`；`dry_run=false` | 预演或提交文本补丁，实际提交按模式检查并保存恢复记录。 |
| `bash` | 必填 `command`；`workdir="."`、`timeout_seconds=30` | FullAccess 中执行一次 Bash；超时范围 1–600 秒。 |

例如查看两层目录并搜索 C# 文件：

```json
{"path":".","max_depth":2,"format":"tree"}
```

```json
{"pattern":"StartAsync","path":"src","glob":"*.cs","max_results":50}
```

`list_files` 深度最大 8、结果最多 2000 项；默认跳过隐藏项和常见生成目录，不解析 gitignore。`search_text` 深度最大 32、结果最多 1000 个匹配行，遵守选定根以内的 ignore 文件及 rg 对隐藏和二进制文件的过滤，不使用父级或全局 ignore。两者遍历和输出有上限；查看 `complete`、`truncated` 和 `notices`，必要时缩小路径或深度。查询输出最多 256 KiB，`read_file` 的正文最多 16000 个 UTF-16 字符单元。

例如 `list_files` 的 `max_depth=1` 会列出所选目录的直接子项，不展开其中的子目录。遇到边界目录时，返回项标记 `expanded=false`，结果同时报告 `DepthLimit`、`complete=false` 和 `truncated=true`。这说明子树未被完整遍历，直接子项仍可能已经列全；查看其他 `notices`，可以判断是否还有条目因数量、时间或访问问题被省略。`max_depth=0` 不展开所选目录。

Git 查询要求项目根本身是含真实 `.git` 目录的标准工作树。它们不遍历子模块，也不支持外部 gitdir、linked worktree 或 partial clone。查询读取本地 Git 配置及其 include；例如 `git_diff` 可能触发配置的 clean filter，外部程序的副作用不受执行模式或补丁恢复记录约束。Git 的外部 diff、textconv、fsmonitor、自动获取对象和索引刷新写入由固定参数禁用。详见 [Git 配置与外部程序](security.md#git-配置与外部程序)。

`git_diff` 不显示未跟踪文件正文，查询 diff 可能截断；补丁预演则要求完整 diff。Git 查询与搜索共用 10 秒预算和有限并发。

## 预演与提交补丁

先读取目标文件，再传入补丁。以下示例更新已有的 `README.md`，上下文必须与文件内容精确且唯一匹配：

```text
*** Begin Patch
*** Update File: README.md
@@
-旧内容
+新内容
*** End Patch
```

用 `{"patch":"...","dry_run":true}` 查看完整 diff、风险、统计和策略决定；确认后以相同补丁调用 `dry_run=false`。补丁也支持 `*** Add File: path`、`*** Delete File: path` 和更新时的 `*** Move to: path`。新增和移动目标必须不存在。单批最多 1 MiB 和 64 个操作，完整预览最多 256 KiB；超过计算或预览上限时需要拆小补丁。

实际提交会重新检查工作区并保存恢复记录。ProjectEdit 只允许限额内的普通补丁，详见 [权限与安全边界](security.md)。工具错误或连接中断后先检查实际文件和记录，避免重复提交。

## 查看与恢复记录

桌面“修改记录”页加载并选择记录，查看完整反向预览；确认恢复前会显示对话框。ReadOnly 允许查看，ProjectEdit 和 FullAccess 可在无冲突时确认恢复。运行中的桌面使用当前会话配置，服务停止时使用已保存配置打开本机维护会话。

先停止 CLI 或桌面服务，再在交互终端执行：

```sh
minimcp records list --config /absolute/path/settings.json
minimcp records show CHANGE_ID --config /absolute/path/settings.json
minimcp records restore CHANGE_ID --config /absolute/path/settings.json
```

使用 `dotnet run` 时，把上述 `minimcp` 替换为 `dotnet run --project src/MiniMcp.Cli --`。`show` 显示完整预览和冲突；`restore` 再次展示预览，要求输入界面提示的 `RESTORE` 和 32 位记录 ID。其他输入、EOF 或 Ctrl+C 取消。恢复前再次检查当前文件和记录，成功后创建新记录。维护命令不启动 HTTP 服务、Bash 或隧道。

## 日志

日志包含运行状态和诊断，敏感字段和常见凭据格式会脱敏，但不保证识别任意秘密；不要把 token 或工具正文写进日志。桌面日志支持关键词、级别和当前运行筛选，复制选中项或导出筛选结果。缓冲有限，界面会显示队列缺口和容量淘汰；导出保存点击时的筛选范围。
