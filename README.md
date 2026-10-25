# MiniMcp

MiniMcp 由 Runtime 类库、跨平台 CLI 和 Windows WinUI 入口组成，HTTP 服务在入口进程内运行。

CLI 与桌面窗口直接控制 Runtime 的启动和停止。`/mcp` 提供 stateless Streamable HTTP，`/healthz` 提供本机健康检查。

MCP 提供 `read_file`、`apply_patch` 和 `bash`，三个工具使用同一个会话执行入口。所有模式允许读取和补丁预演，实际补丁写入关闭；Bash 仅 FullAccess 允许执行。

## 项目结构

```text
MiniMcp.slnx
src/
  MiniMcp.Runtime/           .NET 10 类库
    McpRuntime.cs            由调用者控制的进程内 HTTP 生命周期
    Configuration/          运行配置、认证和本机执行策略模型
    Security/               Cloudflare Access JWT 验证
    Services/               文件、补丁、Bash 与进程清理
    Recovery/               恢复记录、准备记录持久化与受限加载
    Tools/                  三个 MCP 工具入口
  MiniMcp.Cli/               .NET 10 命令行入口
  MiniMcp.Desktop/           Windows WinUI 3 本机服务窗口
examples/
  settings.local.json       本机运行配置格式示例
  policy.readonly.json      本机只读策略格式示例
tests/
  MiniMcp.Checks/            引用 Runtime 的核心行为检查
  MiniMcp.ProtocolChecks/    实际 MCP SDK 内存流检查
  MiniMcp.RuntimeChecks/     配置、实际 HTTP 生命周期及 CLI 集成检查
```

根解决方案包含全部项目，供 Windows 构建和编辑器加载。Linux 使用 CLI 项目作为构建入口，项目引用会自动构建 Runtime，不涉及 WinUI：

```bash
dotnet build src/MiniMcp.Cli/MiniMcp.Cli.csproj
```

## 构建与 CLI 入口

需要 .NET 10 SDK；Windows 上在仓库根目录构建完整解决方案。先参考 `examples/settings.local.json` 准备 `settings.json`，将 `projectRoot` 设置为实际项目目录；相对路径以 JSON 所在目录为基准。

```powershell
dotnet build MiniMcp.slnx
dotnet run --project src/MiniMcp.Cli -- --help
dotnet run --project src/MiniMcp.Cli -- run --config ./settings.json
dotnet run --project src/MiniMcp.Cli -- run --config ./settings.json --mode ReadOnly
```

无参数或 `run --help` 显示帮助。`run [--config <path>] [--mode ReadOnly|ProjectEdit|FullAccess]` 从 JSON 读取端口及项目；省略 `--config` 时使用默认用户配置，策略固定来自用户目录的 `policy.json`。`--mode` 只覆盖本次会话，不写文件。监听 `127.0.0.1`，访问输出的 `/healthz` 地址返回 `{"status":"ok"}`；Ctrl+C 或 Linux SIGTERM 正常停止并释放服务。

正常结束返回 `0`，启动/停止失败返回 `1`，参数、配置或依赖错误返回 `2`，配置加载/启动阶段取消返回 `130`。端口冲突保留已有服务。输出包含 MCP 连接地址、健康检查地址及运行模式；HTTP 认证与请求边界见下文，工具调用使用本机执行策略，当前入口不启动受管隧道。Linux 信号路径未实机验证。

Local 模式下，MCP 客户端使用启动时输出的 `http://127.0.0.1:<port>/mcp`，传输方式选择 Streamable HTTP。CloudflareAccess 模式下，客户端通过配置的 `publicOrigin` 加 `/mcp` 访问外部 Cloudflare 网关，再由网关转发至本机服务。该端点按请求处理协议，不使用 MCP 会话 ID；通过鉴权的独立 GET/DELETE 返回 405，legacy `/sse`、`/message` 路由不开放。浏览器直接 GET 不能验证 MCP 调用，健康诊断使用 `/healthz`。

CLI 与 WinUI 都创建 `McpRuntime()`，通过 `StartAsync(RuntimeConfiguration)`、`StopAsync` 和 `DisposeAsync` 控制服务；公开启动接口必须提供完整校验生成的配置。内部 Host 使用空配置构建器，监听地址不取自 `ASPNETCORE_URLS`，也不安装控制终端信号的默认 Host lifetime。

`McpRuntime.StartAsync(RuntimeConfiguration)` 按正式配置中的端口启动 MCP HTTP 和健康端点。`Snapshot` 提供停止、启动中、运行、停止中、故障和释放状态，以及本次启动的 `RunId`、配置、实际地址和错误；每次启动使用新的 ID，停止后保留最近一次的 ID/配置并清空地址。快照通过实际启停结果更新，不轮询健康端点。CLI 与桌面都使用正式配置启动。

每次启动在监听前初始化独立的工具执行服务，绑定快照中的同一份配置和 `RunId`。该 Host 的请求共享执行服务、工作区和修改互斥，服务由 Host 的 DI 容器释放；停止后旧执行服务拒绝调用，重新启动使用新的会话。工作区初始化、启动或端口绑定失败会清理已创建的资源；清理失败保留所有权并阻止再次启动。

每次工具调用登记在所属会话中，并同时响应客户端和会话取消。停止先拒绝新调用，再取消并等待在途调用清理，包括运行中的 Bash 及等待修改锁的预演，随后停止和释放 Host。整个流程共用 10 秒期限；超时或取消等待后保留故障状态与资源所有权，重试继续等待未完成的停止或释放任务，清理完成后才允许启动新会话。

HTTP Host 白名单在 Local 模式包含 `localhost`、`127.0.0.1` 和 `[::1]`，CloudflareAccess 模式另外包含 `publicOrigin` 的主机名；空 Host、其他域名及子域名被拒绝，转发头不改变 Host 或协议判断。Kestrel 请求体上限为 2 MiB，HTTP/1.1 分块请求的分块头尾开销也计入上限；超限读取返回 413。[Kestrel 实现](https://github.com/dotnet/aspnetcore/blob/v10.0.13/src/Servers/Kestrel/Core/src/Internal/Http/Http1ChunkedEncodingMessageBody.cs#L313)

CloudflareAccess 的 `/mcp` 要求认证，每个请求由认证/授权中间件校验单个 `Cf-Access-Jwt-Assertion`：验证 RS256 签名、团队 issuer、应用 AUD 和有效期，从团队的 HTTPS JWKS 地址缓存并受限刷新公钥。Cookie 和客户端 OAuth Bearer 不作为 Access JWT；Managed OAuth 由 Cloudflare 网关处理。JWT 声明不会改变本机执行策略。Local 模式允许匿名 MCP 请求，`/healthz` 在两种模式下均允许匿名诊断；本机健康响应不验证公网网关连通性。[Cloudflare JWT 验证说明](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/validating-json/)

`examples/` 提供不含凭据的 JSON 格式示例。`AppSettings` 定义项目目录、端口、Bash、认证和隧道配置；`ExecutionPolicy` 单独定义本机执行权限、恢复目录和补丁审批阈值。模型默认认证为 `CloudflareAccess`、执行模式为 `ReadOnly`、隧道关闭；本机示例显式选择 `Local` 认证。执行权限在共用工具入口检查。

`policy.json` 的 `patchApprovalLimits` 保存 ProjectEdit 普通补丁自动放行的上限：`maxPaths` 默认 10，`maxChangedLines` 默认 500，`maxChangedBytes` 默认 65536。口径为整批不同路径数、增删行之和、增删真实字节之和，移动包含源和目标两个路径；超过任一上限需要确认。各项接受 0 至 2147483647 的整数，0 表示该维度的非零变更需要确认；省略对象或字段使用默认值，负值、空值和错误类型报错。这组阈值用于补丁预演的整批评估，实际补丁写入关闭。

Runtime 的 `ConfigurationPaths` 提供共用路径规则：Windows 配置位于 `%LOCALAPPDATA%/MiniMcp/`，Linux 位于 `$XDG_CONFIG_HOME/minimcp/`（默认 `~/.config/minimcp/`）；其中分别存放 `settings.json`、`policy.json`。恢复目录在 Windows 默认为配置目录下的 `recovery/`，Linux 为 `$XDG_STATE_HOME/minimcp/recovery/`（默认 `~/.local/state/minimcp/recovery/`），相对 XDG 值忽略。配置文件选择相对调用者工作目录，JSON 内路径相对所属文件目录；路径计算不创建文件，不展开环境变量或 `~`。这些规则供后续配置加载器使用。

`ExternalProgramLocator` 提供 Bash/cloudflared 文件定位：显式路径相对配置文件解析，路径无效时报错；留空时先检查常见安装目录，再检查 PATH 中的绝对目录。Windows 通过 Git 安装结构自动识别 Bash；cloudflared 只查找 `cloudflared.exe`，Linux 查找 `bash`、`cloudflared`。定位不执行程序、不探测版本，自动查找无结果时返回空值，由配置加载器按所需功能判断依赖。

`ConfigurationJson.ParseSettings/ParsePolicy` 解析 v1 JSON 文本，字段采用 camelCase，模式名称严格区分大小写；未知字段、重复键、错误类型、非空字段的 `null`、整数枚举与其他版本均报错。省略字段使用模型默认值。解析错误保留 JSON 字段路径及可用的行号、字节位置；此接口只检查配置格式，路径、认证和依赖的业务校验由加载流程负责。

`ConfigurationJson.SerializeSettings/SerializePolicy` 按同一格式生成带缩进、LF 换行的 JSON 文本，保留相对路径、自动定位空值和用户配置的模式，中文可直接阅读。写出拒绝不支持的版本、未知枚举和非空字段的 `null`，运行配置与本机策略分别输出；此接口不写文件、不规范化路径，业务校验与保存由调用方负责。

`ConfigurationStore.ReadSettingsAsync/ReadPolicyAsync` 读取实际文件，策略来源固定为用户目录。缺少策略文件时返回只读默认值；损坏、无法读取或缺少运行配置均报错。文件仅支持 UTF-8（允许 UTF-8 BOM），最大 64 KiB；加载结果包含源文件路径及原始字节的 SHA-256 修订指纹，错误保留文件名和内部 JSON 诊断。加载不创建目录或文件，返回值只经过格式检查。

`ConfigurationValidator.NormalizePaths` 同时用于文件模型和内存草稿，返回绝对路径副本。项目必须是实际存在的目录，不能是文件系统根或用户主目录；配置、策略和恢复路径拒绝链接及 junction。策略文件必须位于项目外，恢复目录与项目互不包含；恢复路径以策略文件目录为基准，留空时采用默认状态目录。校验不创建目录或文件，认证与外部依赖检查由后续校验负责。

`ConfigurationValidator.ValidateValues` 检查配置版本、端口范围（1–65535）、认证和执行枚举，以及隧道开关组合。Cloudflare Access 要求 HTTPS origin（可带端口和末尾 `/`，不含凭据、其他路径、query 或 fragment）、团队名前缀和无空白的 AUD；Local 模式不要求 Cloudflare 字段，且不能启用受管隧道。启用隧道时必须填写 token 文件路径。此检查不访问网络、不读取 token，也不探测程序是否可用。

`ConfigurationValidator.Validate` 组合配置值、路径和启用的外部依赖检查；`ConfigurationStore.LoadAsync` 从文件调用同一流程。ReadOnly 跳过 Bash 检查，ProjectEdit/FullAccess 要求 Bash，Linux 同时要求 `/usr/bin/setsid`；隧道启用时要求 cloudflared 以及可打开、非空的 token 文件。停用功能保留其配置，不检查对应程序或文件是否存在。程序仅检查文件存在、非目录及 Linux 执行位，token 不读取正文；不启动程序、不验证版本或公网认证，实际运行仍由启动结果判断。

完整校验返回 `RuntimeConfiguration`，只读保存规范化后的 `AppSettings`、`ExecutionPolicy` 与 `SettingsFilePath`，作为运行入口的共用配置输入。`SettingsFilePath` 为实际配置来源的绝对路径；校验内存草稿时，它表示预期保存路径，用于识别自选文件名的关键配置。`ConfigurationStore.LoadAsync(..., mode: ExecutionMode.ReadOnly)` 可为本次加载选择执行模式；该选择在两份文件成功解析后应用，并参与完整校验，不写回 `policy.json`。已生成的配置快照不随文件或编辑草稿变化。

`ConfigurationStore.SaveSettingsAsync` 保存携带修订指纹的配置草稿。新文件使用 `Revision = null`，已有文件使用读取时的指纹；保存前检查当前本机策略和完整业务规则，再写出原始草稿中的路径。输出为无 BOM 的 UTF-8，最大 64 KiB；通过同目录临时文件和系统替换操作更新目标，首次创建不覆盖已有文件。写入期间配置或策略发生变化会报告冲突，失败或取消会清理临时文件；成功返回新指纹。进程内保存串行化，外部写入通过替换前的指纹检查检测，不提供跨进程事务隔离。

`ConfigurationStore.SavePolicyAsync` 复用相同保存流程，目标固定为用户目录的 `policy.json`，拒绝其他位置。调用方同时提供已读取的 settings 上下文；存储组件按其指纹重新读取实际文件来校验策略，不使用其中未保存的草稿值。策略或 settings 在写入期间变化都会报告冲突；相对恢复路径保留原值。首次配置先保存 settings，再保存 policy；两份文件分别提交。

## Windows 桌面入口

需要 .NET 10 SDK、Windows SDK 和 WinUI 构建工具。单独构建和运行：

```powershell
dotnet build src/MiniMcp.Desktop/MiniMcp.Desktop.csproj
dotnet run --project src/MiniMcp.Desktop --no-launch-profile
```

窗口显示默认配置和策略文件路径，每次点击启动都会重新读取 `%LOCALAPPDATA%/MiniMcp/settings.json` 与同目录的 `policy.json`，复用 CLI 的校验和运行流程。界面显示最近一次启动的实际项目、端口、认证配置、执行配置、本机 MCP 和健康检查地址；缺少或损坏配置时显示文件名及错误，修正后可重试。配置文件修改在下次启动时生效。关闭窗口会取消配置加载或启动，等待停止并释放 Runtime；端口冲突保留已有服务。长路径和错误信息可换行，较小窗口可滚动查看。

桌面采用未打包模式，Windows App SDK 运行文件复制到输出目录；.NET 10 / ASP.NET Core 运行依赖由目标机器提供。

## 当前核心工具

| 工具 | 参数 | 行为 |
| --- | --- | --- |
| `read_file` | `path`、`start_line=1`、`end_line=null` | 读取 UTF-8 文本，行号从 1 开始，包含结束行 |
| `apply_patch` | `patch`、`dry_run=false` | 设置 `dry_run=true` 返回新增、更新、移动、删除的完整 diff 和统计；实际写入被拒绝 |
| `bash` | `command`、`workdir="."`、`timeout_seconds=30` | 仅 FullAccess 允许执行，超时范围 1–600 秒 |

文件路径和工作目录参数使用相对项目根的 `/` 分隔路径。文件工具拒绝越界、链接、私密目录和凭据文件；单文件最多 256 KiB。`read_file` 的内容和 Bash 的每个输出流分别最多 16000 个 UTF-16 字符单元；补丁 diff 使用下述独立限制。

三个工具通过同一个执行服务使用固定的运行策略：所有模式允许读取和补丁预演；ReadOnly 拒绝实际补丁写入和 Bash，ProjectEdit 的 Bash 因需要本机审批而拒绝，FullAccess 允许 Bash。实际补丁写入在全部模式下关闭，预演会展示策略决定与当前阻塞原因。工具参数不能批准请求、声明恢复就绪或修改模式和阈值。

补丁使用 `*** Begin Patch` / `*** End Patch` 格式，精确匹配上下文。内部补丁引擎的写入、BOM/换行/Unix 权限保留和提交失败回滚由核心检查验证。

`dry_run=true` 成功返回 `dryRun=true`、完整 `diff`、逐文件 `changes`、合计 `statistics`、整批 `risk` 和 `decision`。每个 `changes` 元素包含 `summary`（`action`、`path`、`moveTo`、`beforeBytes`、`afterBytes`）、该文件的 `statistics` 与 `risk`；统计字段为 `addedLines`、`removedLines`、`addedBytes`、`removedBytes`。MCP 的结构化结果和文本 JSON 包含一致的完整预览和决定，不返回内部计划、原始快照对象、绝对配置路径或批准凭证。

逐文件 `risk` 包含 `kind`（`Ordinary`、`Critical`、`Unknown`）和字符串数组 `reasons`。关键规则优先于普通源码/文档扩展名，覆盖脚本、构建文件、CI/自动化目录和实际选定的配置文件；未知类型、删除与移动列入需确认原因。整批 `risk` 包含 `size`（`paths`、`changedLines`、`changedBytes`）、本机 `limits` 和所有 `reasons`。原因包括 `CriticalFile`、`UnknownFileType`、`Delete`、`Move`、`PathLimitExceeded`、`ChangedLineLimitExceeded`、`ChangedByteLimitExceeded`，不把 `None` 放入数组。

`decision` 包含本机 `mode`、策略意向 `disposition`、当前 `blockers`、`requiresRecoveryRecord` 和 `canCommit`。ReadOnly 的意向为 `Denied`；ProjectEdit 的普通补丁为 `Automatic`，含任一需确认原因时整批为 `RequiresApproval`；FullAccess 为 `Automatic`。`Automatic` 表示免人工确认，实际能否提交由阻塞条件决定。当前 `canCommit` 在所有模式下均为 `false`：ReadOnly 的阻塞原因为 `ReadOnlyMode`，其余模式均有 `RecoveryUnavailable`，ProjectEdit 的需确认补丁还包含 `ApprovalUnavailable`。需要写入的两种策略意向均要求恢复记录；普通文件不会从混合补丁中被单独执行。

预演和实际提交请求都先完成基础校验、完整 diff 和整批评估；实际提交按上述原因返回工具错误，不执行写入。超过固定预览大小或计算上限时返回要求拆小补丁的错误，FullAccess 也遵守这些限制。

diff 展示全部改动和三行上下文，正文每行用 JSON 字符串表示，包含原行尾并转义控制字符；文件头展示路径、BOM、换行类型、末尾换行和适用的 Unix 权限。新增文件的 Unix 模式表示创建请求，实际权限受 umask 影响。增删字节按完整变更行的真实 UTF-8 字节计算，包含行尾，BOM 变化另计；上下文、展示转义和纯路径移动不计入修改量。

每份补丁最多 1 MiB、64 个操作。整批 diff 的 UTF-8 大小最多 256 KiB，比较矩阵累计最多 1,000,000 个单元，输入处理、比较及输出累计最多 8,000,000 个工作单位。超过任一预览限制，工具返回要求拆小补丁的错误，不返回部分 diff。预演响应调用取消和服务停止，不创建目录、暂存、备份或审批记录，也不授予写入权限。

Bash 使用运行账号的实际权限，项目根只是默认工作目录，不是沙箱；它可以访问账号能访问的其他路径和网络。每次调用的 shell 状态独立，普通子进程在结束、超时或取消后清理。

## 检查

检查直接引用 Runtime 的真实程序集，不复制业务源码到测试项目：

```powershell
dotnet run --project tests/MiniMcp.Checks
dotnet run --project tests/MiniMcp.ProtocolChecks
dotnet run --project tests/MiniMcp.RuntimeChecks
```

运行检查需要 Bash，检查程序通过共用定位器查找常见安装目录和 PATH；Windows 自动识别 Git Bash，Linux 还需要 util-linux 的 `/usr/bin/setsid`。自定义安装可通过仅供检查程序使用的 `MINIMCP_TEST_BASH_PATH` 指定 Bash 路径，显式路径无效时报错。

核心检查覆盖文件/路径限制、UTF-8、补丁预演与提交、格式保留、完整 diff 重建及预算边界、风险分类与批量阈值、三模式整批决定和执行阻塞原因、内存恢复草稿的原字节/前后路径状态/权限/哈希、恢复清单的严格 JSON/必需字段/大小边界、记录归属与操作/路径/备份引用对应关系、真实命令输出、超时和取消。协议检查使用实际 MCP SDK，通过内存流和统一执行入口验证三种模式的工具输入/输出 schema、读取、预演、写入拒绝及 Bash 权限；检查大型 diff 在结构化及文本输出中完整保留，超限错误不暴露部分预览或内部计划，拒绝和预演不修改文件或创建恢复目录。它不验证 HTTP、OAuth 或公网 Tunnel。

Runtime 检查覆盖真实 HTTP 响应、loopback 绑定、端口冲突、并发启动、会话隔离、真实 Bash 子进程的客户端/运行时取消、等待修改锁的调用取消、停止期限及释放任务复用、停止后重用端口和释放后禁止启动；同时验证恢复目录的范围/链接/类型与容量预检、准备记录真实落盘和读回校验、取消/失败清理、并发最后一槽及跨进程占锁、记录有界加载与备份长度/哈希/UTF-8 校验、真实 JWT 签名/issuer/AUD/有效期/算法校验、单个断言头、公钥缓存与受限刷新、执行策略独立性、Host 白名单及固定长度/分块请求体上限。恢复检查使用独立临时目录，验证只读文件加载、工作区和旧记录保持原样、损坏/缺失/额外备份与链接拒绝、各记录状态原样保留，以及取消和并发替换不会清理外部数据。加载结果只表示记录和原字节已核对，不授予提交或恢复权限。认证检查只替换 JWKS 文档获取，密钥在内存中生成，不依赖公网或真实 Cloudflare 凭据。

真实 MCP SDK HTTP 客户端检查覆盖工具发现/schema、三个模式的读取/预演/拒绝、逐文件风险与整批决定、阈值精确边界及逐项超限、自选配置文件识别、公开输出字段限制、大型完整 diff 及超限拒绝、FullAccess Bash 的结果与超时、跨请求鉴权、客户端取消后的子进程退出、跨客户端的修改互斥与停止排空、重启后的项目/模式/阈值隔离，以及 CLI 启动后的实际 MCP 调用。伪造模式、阈值、批准、恢复状态或决定参数不改变本机策略；取消排队中的预演不影响另一客户端正在运行的 Bash，预演和拒绝前后的存储快照一致。检查确认 stateless 响应不使用会话头，默认 Host 不暴露测试探针和 legacy SSE 路由；这些检查不替代 WinUI 交互或公网 OAuth/Tunnel 的实际验证。

<a id="references"></a>

## 实现参考

本项目的补丁引擎和进程执行代码用 C# 独立实现，参考了其公开的格式和接口设计：

- **OpenAI Codex**（Apache-2.0）：参考 [apply-patch 的语法解析器](https://github.com/openai/codex/blob/main/codex-rs/apply-patch/src/parser.rs) 的 `Begin/End Patch`、`Add/Delete/Update File`、`Move to`、`@@` 和 `End of File` 格式。本项目使用精确且唯一的上下文匹配，保留文件编码/换行，并增加 `dry_run` 文件计划；不是 Codex 补丁引擎的完整移植。
- **DeepSeek Harness**（MIT）：参考 [tool-bash 文档](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/shell/tool-bash/README.md) 的一次性 shell、工作目录、独立调用状态及命令退出结果设计。本项目使用 .NET Process，超时后终止命令，不提供上游的后台任务转交与任务管理功能。
- 进程清理依据 Linux [setsid](https://man7.org/linux/man-pages/man1/setsid.1.html) 和微软 [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects) 的平台接口。

<a id="license"></a>

## 许可证

本项目采用 [MIT 许可证](LICENSE)。
