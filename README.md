# MiniMcp

MiniMcp 由 Runtime 类库、跨平台 CLI 和 Windows WinUI 入口组成，HTTP 服务在入口进程内运行。

CLI 与桌面窗口直接控制 Runtime 的启动和停止。`/mcp` 提供 stateless Streamable HTTP，`/healthz` 提供本机健康检查。

MCP 提供 `read_file`、`apply_patch` 和 `bash`，三个工具使用同一个会话执行入口。所有模式允许读取和补丁预演；ProjectEdit 允许普通补丁，FullAccess 允许全部通过基础检查的补丁，每次实际提交均保存恢复记录。Bash 仅 FullAccess 允许执行。

## 项目结构

```text
MiniMcp.slnx
src/
  MiniMcp.Runtime/           .NET 10 类库
    McpRuntime.cs            由调用者控制的进程内 HTTP 生命周期
    Diagnostics/            有界脱敏日志、独立状态订阅与输出排空
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
dotnet run --project src/MiniMcp.Cli -- check --config ./settings.json
dotnet run --project src/MiniMcp.Cli -- run --config ./settings.json
dotnet run --project src/MiniMcp.Cli -- run --config ./settings.json --mode ReadOnly
```

无参数或 `--help` 显示根帮助，`run --help` 和 `check --help` 显示对应命令帮助；帮助不加载配置。`run [--config <path>] [--mode ReadOnly|ProjectEdit|FullAccess]` 从 JSON 读取端口及项目；省略 `--config` 时使用默认用户配置，策略固定来自用户目录的 `policy.json`。`--mode` 只覆盖 run 本次会话，不写文件。监听 `127.0.0.1`，访问输出的 `/healthz` 地址返回 `{"status":"ok"}`；Ctrl+C 或 Linux SIGTERM 正常停止并释放服务。

`check [--config <path>]` 校验所选 JSON、路径、认证字段、固定本机策略及已启用依赖，采用保存的执行模式，不接受 `--mode`。程序依赖只检查文件及适用的执行权限，token 文件只检查可读和非空；检查不执行程序、不启动 MCP/隧道、不写配置或创建审批/恢复目录。端口检查只在 `127.0.0.1` 短暂绑定后释放，不开始监听；当前占用或不可绑定会在标准错误输出提示，配置校验通过仍返回 `0`，也不保证后续启动一定能绑定。公网路由、Access 登录、OAuth 和程序实际运行结果需另行验证。

成功或正常结束返回 `0`，运行、清理或输出失败返回 `1`，参数、配置或依赖错误返回 `2`，配置加载/检查/启动取消且清理完成返回 `130`；清理失败优先返回 `1`。普通诊断共用 Runtime 的有界脱敏策略，终端动态文本额外转义控制字符。run 端口冲突返回 `1` 并保留已有服务；输出包含 MCP 连接地址、健康检查地址及运行模式，工具调用使用本机执行策略。配置启用受管隧道时，Runtime 在本机 MCP 就绪后自动启动 cloudflared。Linux 信号路径未实机验证。

`run` 在前台连续显示 Runtime/Host/MCP/隧道日志，包含 UTC 时间（`Z`）、级别和类别，控制字符可见转义。普通日志和连接信息写到标准输出，实际状态故障与结束错误写到标准错误；日志级别或文本不会决定服务状态及退出码。日志队列满时省略旧条目并显示缺口，状态使用独立订阅；显示由单独后台写入器串行执行，慢终端不会阻塞子进程排空或停机。

Ctrl+C/SIGTERM 通过同一条退出路径取消会话并清理 MCP、Bash、隧道及其后代。CLI 共用十秒退出预算，给最后的显示留出最多 500 毫秒；已失败或未完成的启动清理不会隐式再等一轮完整停止。只有实际释放成功才报告“已停止”；清理未完成保留所有权并返回 `1`。输出断管会主动清理运行资源；写入器不响应时仅作有界等待，撤销订阅并返回 `1`，不强制结束调用方进程。

运行中隧道意外退出后，CLI 显示故障并保留本机 MCP 供诊断，等待用户停止；此次运行即使后续清理成功也返回 `1`。显示的公网 MCP 地址来自配置，进程 Running 不代表路由或认证成功。Windows 已用独立隐藏控制台中的真实 Ctrl+C 验证正常停止、重复信号和启动取消；Linux SIGINT/SIGTERM 检查已编写，未实机验证。

Local 模式下，MCP 客户端使用启动时输出的 `http://127.0.0.1:<port>/mcp`，传输方式选择 Streamable HTTP。CloudflareAccess 模式下，客户端通过配置的 `publicOrigin` 加 `/mcp` 访问外部 Cloudflare 网关，再由网关转发至本机服务。该端点按请求处理协议，不使用 MCP 会话 ID；通过鉴权的独立 GET/DELETE 返回 405，legacy `/sse`、`/message` 路由不开放。浏览器直接 GET 不能验证 MCP 调用，健康诊断使用 `/healthz`。

CLI 与 WinUI 都创建 `McpRuntime()`，通过 `StartAsync(RuntimeConfiguration)`、`StopAsync` 和 `DisposeAsync` 控制服务；公开启动接口必须提供完整校验生成的配置。内部 Host 使用空配置构建器，监听地址不取自 `ASPNETCORE_URLS`，也不安装控制终端信号的默认 Host lifetime。

`McpRuntime.StartAsync(RuntimeConfiguration)` 按正式配置中的端口启动 MCP HTTP 和健康端点。`Snapshot` 提供停止、启动中、运行、停止中、故障和释放状态，以及本次启动的 `RunId`、配置、实际地址和错误；每次启动使用新的 ID，停止后保留最近一次的 ID/配置并清空地址。`HasOwnedResources` 表示仍保留 Host/会话/进程所有权，尚未结束的启动仍须等待其真实任务；`FaultCount` 在一次运行内累计实际生命周期/隧道故障，停止和状态合并不会清除，启动新会话时重置。快照通过实际启停结果更新，不轮询健康端点。`DisposeAsync(CancellationToken)` 支持调用方传入剩余退出预算，失败保留资源供显式重试。CLI 与桌面都使用正式配置启动。

Runtime 通过 `ILogger` 收集 Information 及以上的 Host、MCP 和生命周期诊断。`GetLogs()` 返回不可变日志快照，最多保留 1000 条、512 Ki 个 UTF-16 字符单元（消息和类别合计）；单条消息最多 4096、类别最多 256 个字符。`ClearLogs()` 清空保留缓冲，序号继续递增。日志保留跨会话历史，每条包含 `RunId`、递增序号、UTC 时间、级别、类别和事件编号；已结束会话的缓存 logger 拒绝新日志。

`SubscribeLogs()` 和 `SubscribeState()` 原子捕获 `Initial`，各自的 `Reader` 只提供订阅后的变化。每个观察者独立消费：日志队列最多 128 条，满时丢弃最旧条目，可通过序号发现缺口并用 `GetLogs()` 重新加载；状态队列合并为最新快照，不依赖日志推断。消费者自行在终端或 UI 调度显示，不在生产者线程同步调用观察者。订阅需释放或绑定取消令牌；Runtime 成功释放后，读者可排空最后内容，后续订阅仍可读取终态及保留日志。

凭据字段、认证头、Bearer/Basic、JWT 和 URL 用户凭据在进入日志缓冲及订阅前统一脱敏，故障快照的错误文本采用同一规则。包含敏感结构化字段的整条消息会省略，避免自定义格式化绕过替换；原始消息超过 64 Ki 字符也整条省略。这些规则不能识别任意无字段名的秘密，调用方不得记录 token 文件正文、工具正文或审批正文。复制和导出应直接使用已脱敏条目，终端展示还需转义控制字符。

外部输出读取器每次读取最多 4096 字符，逻辑行最多保留 8192 字符；更长的行只输出一次省略标记，继续排空至换行后恢复，不发布截断的原始片段。stdout/stderr 可分别排空，读者取消或 IO 故障由所属进程生命周期处理。

每次启动在监听前初始化独立的工具执行服务，绑定快照中的同一份配置和 `RunId`。该 Host 的请求共享执行服务、工作区和修改互斥，服务由 Host 的 DI 容器释放；停止后旧执行服务拒绝调用，重新启动使用新的会话。工作区初始化、启动或端口绑定失败会清理已创建的资源；清理失败保留所有权并阻止再次启动。

每次工具调用登记在所属会话中，并同时响应客户端和会话取消。停止先拒绝新调用，再取消在途调用并停止受管隧道，等待 Bash、排队工作、隧道后代、输出读取和退出观察完成，随后停止和释放 Host。整个流程共用 10 秒期限；超时或取消等待后保留故障状态与资源所有权，重试继续等待未完成的停止或释放任务，清理完成后才允许启动新会话。`RestartAsync(RuntimeConfiguration)` 在同一个生命周期锁内完整停止后，再使用调用方重新加载、校验的已保存配置启动新会话。

`Snapshot.Tunnel` 独立提供 Disabled、Stopped、Starting、Running、Stopping、Faulted，以及隧道尝试的 `InstanceId`、进程 ID、退出码和脱敏错误。受管隧道使用固定参数 `tunnel --no-autoupdate run --token-file <path>`，不经过 shell，不读取或打印 token 正文，不继承 `TUNNEL_*` 等隐式配置。进程 Running 仅表示本机进程存活，不代表公网路由、Cloudflare Access 或 OAuth 已成功；公网验证需实际通过配置的域名和认证网关。

初始隧道无法创建、初始化输出失败或在启动核验前退出，会清理整次启动的 Host、会话及子进程。运行中隧道退出或输出读取故障则将隧道标为 Faulted、清理其后代和输出资源，保留本机 MCP 供诊断，不自动重启。故障快照可保留最近进程 ID，不能仅凭 ID 推断进程仍存活。本机 `StopTunnelAsync()` 可以独立停止隧道；`StartTunnelAsync()` 要求 MCP 正常运行且当前会话配置已启用隧道，并在重试前完成旧隧道清理。一次 Runtime 会话内的多个隧道尝试也使用不同标识，旧退出通知不能覆盖新尝试。

Windows 隧道通过隐藏、挂起的进程创建，在创建时用 Job List 纳入独占的 kill-on-close Job Object，仅继承三条标准流的句柄，取得进程句柄后才恢复执行；清理确认 Job 中无活动进程后再释放资源。入口进程被强制结束时，关闭 Job 也会终止其中的进程，已用真实父子进程验证。[进程创建属性](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)、[Job Object](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)

Linux 使用 `/usr/bin/setsid -- <cloudflared> ...` 创建独立会话/进程组，正常停止终止自身创建的组，并等待仍能执行的成员退出；僵尸进程的最终回收由父进程或 init 负责。Linux SIGKILL/崩溃不会执行托管清理，隧道可能遗留；代码包含受控异常退出检查，但本次未实机运行 Linux。进程组和 Job Object 都不作为文件或网络安全沙盒。[setsid 行为](https://man7.org/linux/man-pages/man1/setsid.1.html)

HTTP Host 白名单在 Local 模式包含 `localhost`、`127.0.0.1` 和 `[::1]`，CloudflareAccess 模式另外包含 `publicOrigin` 的主机名；空 Host、其他域名及子域名被拒绝，转发头不改变 Host 或协议判断。Kestrel 请求体上限为 2 MiB，HTTP/1.1 分块请求的分块头尾开销也计入上限；超限读取返回 413。[Kestrel 实现](https://github.com/dotnet/aspnetcore/blob/v10.0.13/src/Servers/Kestrel/Core/src/Internal/Http/Http1ChunkedEncodingMessageBody.cs#L313)

CloudflareAccess 的 `/mcp` 要求认证，每个请求由认证/授权中间件校验单个 `Cf-Access-Jwt-Assertion`：验证 RS256 签名、团队 issuer、应用 AUD 和有效期，从团队的 HTTPS JWKS 地址缓存并受限刷新公钥。Cookie 和客户端 OAuth Bearer 不作为 Access JWT；Managed OAuth 由 Cloudflare 网关处理。JWT 声明不会改变本机执行策略。Local 模式允许匿名 MCP 请求，`/healthz` 在两种模式下均允许匿名诊断；本机健康响应不验证公网网关连通性。[Cloudflare JWT 验证说明](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/validating-json/)

`examples/` 提供不含凭据的 JSON 格式示例。`AppSettings` 定义项目目录、端口、Bash、认证和隧道配置；`ExecutionPolicy` 单独定义本机执行权限、恢复目录和补丁审批阈值。模型默认认证为 `CloudflareAccess`、执行模式为 `ReadOnly`、隧道关闭；本机示例显式选择 `Local` 认证。执行权限在共用工具入口检查。

`policy.json` 的 `patchApprovalLimits` 保存 ProjectEdit 普通补丁自动放行的上限：`maxPaths` 默认 10，`maxChangedLines` 默认 500，`maxChangedBytes` 默认 65536。口径为整批不同路径数、增删行之和、增删真实字节之和，移动包含源和目标两个路径；超过任一上限需要确认。各项接受 0 至 2147483647 的整数，0 表示该维度的非零变更需要确认；省略对象或字段使用默认值，负值、空值和错误类型报错。ProjectEdit 需确认的补丁因无本机审批入口而整批拒绝，FullAccess 免审批但遵守基础限制与强制恢复记录要求。

Runtime 的 `ConfigurationPaths` 提供共用路径规则：Windows 配置位于 `%LOCALAPPDATA%/MiniMcp/`，Linux 位于 `$XDG_CONFIG_HOME/minimcp/`（默认 `~/.config/minimcp/`）；其中分别存放 `settings.json`、`policy.json`。恢复目录在 Windows 默认为配置目录下的 `recovery/`，Linux 为 `$XDG_STATE_HOME/minimcp/recovery/`（默认 `~/.local/state/minimcp/recovery/`），相对 XDG 值忽略。配置文件选择相对调用者工作目录，JSON 内路径相对所属文件目录；路径计算不创建文件，不展开环境变量或 `~`。这些规则供后续配置加载器使用。

`ExternalProgramLocator` 提供 Bash/cloudflared 文件定位：显式路径相对配置文件解析，路径无效时报错；留空时先检查常见安装目录，再检查 PATH 中的绝对目录。Windows 通过 Git 安装结构自动识别 Bash；cloudflared 只查找 `cloudflared.exe`，Linux 查找 `bash`、`cloudflared`。定位不执行程序、不探测版本，自动查找无结果时返回空值，由配置加载器按所需功能判断依赖。

`ConfigurationJson.ParseSettings/ParsePolicy` 解析 v1 JSON 文本，字段采用 camelCase，模式名称严格区分大小写；未知字段、重复键、错误类型、非空字段的 `null`、整数枚举与其他版本均报错。省略字段使用模型默认值。解析错误保留 JSON 字段路径及可用的行号、字节位置；此接口只检查配置格式，路径、认证和依赖的业务校验由加载流程负责。

`ConfigurationJson.SerializeSettings/SerializePolicy` 按同一格式生成带缩进、LF 换行的 JSON 文本，保留相对路径、自动定位空值和用户配置的模式，中文可直接阅读。写出拒绝不支持的版本、未知枚举和非空字段的 `null`，运行配置与本机策略分别输出；此接口不写文件、不规范化路径，业务校验与保存由调用方负责。

`ConfigurationStore.ReadSettingsAsync/ReadPolicyAsync` 读取实际文件，策略来源固定为用户目录。缺少策略文件时返回只读默认值；损坏、无法读取或缺少运行配置均报错。文件仅支持 UTF-8（允许 UTF-8 BOM），最大 64 KiB；加载结果包含源文件路径及原始字节的 SHA-256 修订指纹，错误保留文件名和内部 JSON 诊断。加载不创建目录或文件，返回值只经过格式检查。

`ConfigurationValidator.NormalizePaths` 同时用于文件模型和内存草稿，返回绝对路径副本。项目必须是实际存在的目录，不能是文件系统根或用户主目录；配置、策略和恢复路径拒绝链接及 junction。策略文件必须位于项目外，恢复目录与项目互不包含；恢复路径以策略文件目录为基准，留空时采用默认状态目录。校验不创建目录或文件，认证与外部依赖检查由后续校验负责。

`ConfigurationValidator.ValidateValues` 检查配置版本、端口范围（1–65535）、认证和执行枚举，以及隧道开关组合。Cloudflare Access 要求 HTTPS origin（可带端口和末尾 `/`，不含凭据、其他路径、query 或 fragment）、团队名前缀和无空白的 AUD；Local 模式不要求 Cloudflare 字段，且不能启用受管隧道。启用隧道时必须填写 token 文件路径。此检查不访问网络、不读取 token，也不探测程序是否可用。

`ConfigurationValidator.Validate` 组合配置值、路径和启用的外部依赖检查；`ConfigurationStore.LoadAsync` 从文件调用同一流程。ReadOnly 跳过 Bash 检查，ProjectEdit/FullAccess 要求 Bash；Linux 在 Bash 或受管隧道启用时要求 `/usr/bin/setsid`。隧道启用时要求 cloudflared 以及可打开、非空的 token 文件。停用功能保留其配置，不检查对应程序或文件是否存在。程序仅检查文件存在、非目录及 Linux 执行位，token 不读取正文；不启动程序、不验证版本或公网认证，实际运行仍由启动结果判断。

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
| `apply_patch` | `patch`、`dry_run=false` | 预演返回完整 diff、统计和策略决定；实际提交按本机模式执行并强制保存恢复记录 |
| `bash` | `command`、`workdir="."`、`timeout_seconds=30` | 仅 FullAccess 允许执行，超时范围 1–600 秒 |

文件路径和工作目录参数使用相对项目根的 `/` 分隔路径。文件工具拒绝越界、链接、私密目录和凭据文件；单文件最多 256 KiB。`read_file` 的内容和 Bash 的每个输出流分别最多 16000 个 UTF-16 字符单元；补丁 diff 使用下述独立限制。

三个工具通过同一个执行服务使用固定的运行策略：所有模式允许读取和补丁预演；ReadOnly 拒绝实际补丁写入和 Bash。ProjectEdit 自动执行通过风险评估的普通源码/文档补丁，删除、移动、关键文件、未知类型及超出批量阈值的整批补丁要求本机确认，当前因无审批入口而拒绝；Bash 同样拒绝。FullAccess 允许通过基础检查的补丁和 Bash。工具参数不能批准请求、关闭备份、声明恢复就绪或修改模式和阈值。

补丁使用 `*** Begin Patch` / `*** End Patch` 格式，精确匹配上下文。内部补丁引擎的写入、BOM/换行/Unix 权限保留和提交失败回滚由核心检查验证。

实际补丁经共享执行入口调用 `CommitWithRecoveryAsync`，在同一工作区修改锁内完成计划复查、原字节备份、`Applying` 持久化、实际提交或回滚、结果核验及最终记录保存。备份失败不创建工作区目录或暂存文件；准备后、暂存后和每个操作安装前复查外部修改。`Applying` 完成核验后，即使调用取消也会核验实际结果并等待最终清单保存；保存失败保留对应记录与引擎结果，不额外回滚工作区。恢复目录固定限制为 1000 个顶层记录槽和 1 GiB 文件字节，未知或部分记录也计入；满额或无法可靠写入时拒绝新提交，不自动清除旧记录或回退为无记录写入。

`dry_run=true` 成功返回 `dryRun=true`、完整 `diff`、逐文件 `changes`、合计 `statistics`、整批 `risk`、`decision` 和 `commit=null`。每个 `changes` 元素包含 `summary`（`action`、`path`、`moveTo`、`beforeBytes`、`afterBytes`）、该文件的 `statistics` 与 `risk`；统计字段为 `addedLines`、`removedLines`、`addedBytes`、`removedBytes`。MCP 的结构化结果和文本 JSON 包含一致的完整预览和决定，不返回内部计划、原始快照对象、绝对配置路径或批准凭证。

`dry_run=false` 成功返回同一组评估字段、`dryRun=false` 以及 `commit`：包含本次 `runId`、恢复记录 `changeId`、`state="Committed"` 和 `cleanupIncomplete`。只有实际结果核验及最终清单保存完成后才返回成功；`cleanupIncomplete=true` 表示安装成功后的引擎暂存/旧备份清理有错误，不表示补丁已回滚。恢复目录中的记录按 `changeId` 命名，`manifest.json` 记录操作、状态和哈希，`before-*.bin` 保存完整原字节，包括提交前尚未提交到 Git 的内容。

本机代码可在运行会话中调用 `McpRuntime.PreviewRecoveryAsync(changeId)` 查看历史记录的恢复预览，ReadOnly 也允许查看。结果区分当前 `runId` 和原记录的 `recordRunId`，包含 `recordState`、模式、完整反向 `diff`、逐文件 `changes`、统计、`conflicts` 和 `blockers`；`requiresConfirmation=true` 表示实际恢复仍需本机确认，预览不授予恢复权限。只有 Committed 记录生成反向 diff；Prepared、Applying、RolledBack、Incomplete 返回 `RecordNotCommitted` 阻塞，ReadOnly 另外带 `ReadOnlyMode`。损坏或无法读取的记录报错，不自动修复或清除。

恢复预览核对当前文件的完整字节哈希、存在性、适用权限及路径安全，诊断外部修改、路径占用、缺失、目录/链接/特殊文件等冲突。发现冲突或预览期间记录变化时，返回具体路径/原因，整份 `diff` 和统计为 null、`changes` 为空，不返回部分可确认内容。反向操作覆盖移回原路径、恢复修改/删除及移除原新增文件，保留原始 BOM、行尾、空文件和记录的目标权限；恢复目标权限为原记录值，普通补丁新增文件的创建请求规则另按下文说明。预览复用完整 diff 的大小与计算预算，超限报错，不截断；不创建目录、暂存或备份，不修改文件/权限/记录，也不执行恢复写入。该接口只在本机进程内提供，MCP 仍只暴露三个工具。

Runtime 内部恢复协调器以完整预览生成绑定当前会话和原记录内容的一次性计划，实际提交时重新加载原记录及完整备份，并重查所有工作区路径。过期、错误归属或已使用的计划整批拒绝；准备完成后不持有修改锁等待本机决定。进入实际提交后，同一工作区修改锁覆盖恢复前备份、Applying、真实提交/回滚及最终记录，安装边界继续检查原记录变化。ReadOnly 拒绝恢复写入；备份、额度或存储占锁失败不回退为无记录恢复，晚到取消和停止等待不会丢弃真实安装、回滚或最终记录任务。

恢复保持原记录不变，使用同一版本 1 格式生成新的恢复记录，保存恢复前完整内容、权限和不存在标记；内部结果关联原 `changeId` 与新记录、实际引擎/核验结果，调用者须检查核验结果和记录终态，返回对象本身不表示成功。新记录也可被反向预览和恢复，从而撤销本次恢复。重新创建的文件按原记录的准确 Unix 权限设置并核验，最终清单保存实际观测值；清理不完整、结果核验失败及最终保存失败保留真实事实，不自动宣称成功或回滚。失败回滚删除已安装文件前会核对本次写入的字节和权限，检测到外部新内容时保留该文件及原备份，记为 Incomplete；这类检查不提供对不合作外部写入者的跨进程文件事务。恢复对象是记录中的文件，不删除未记录原始状态的目录，也不撤销 Bash 或网络副作用。

内部计划是经过核对的执行输入，人工批准由本机交互负责。公共 Runtime API 仅提供上述只读恢复预览；恢复写入没有 MCP 工具、HTTP 管理或 CLI/WinUI 入口，也不接受 `confirmed=true`、批准票据或自动批准占位。

逐文件 `risk` 包含 `kind`（`Ordinary`、`Critical`、`Unknown`）和字符串数组 `reasons`。关键规则优先于普通源码/文档扩展名，覆盖脚本、构建文件、CI/自动化目录和实际选定的配置文件；未知类型、删除与移动列入需确认原因。整批 `risk` 包含 `size`（`paths`、`changedLines`、`changedBytes`）、本机 `limits` 和所有 `reasons`。原因包括 `CriticalFile`、`UnknownFileType`、`Delete`、`Move`、`PathLimitExceeded`、`ChangedLineLimitExceeded`、`ChangedByteLimitExceeded`，不把 `None` 放入数组。

`decision` 包含本机 `mode`、策略意向 `disposition`、当前 `blockers`、`requiresRecoveryRecord` 和 `canCommit`。ReadOnly 的意向为 `Denied`，阻塞原因为 `ReadOnlyMode`；ProjectEdit 的普通补丁为 `Automatic`，含任一需确认原因时整批为 `RequiresApproval` 并由 `ApprovalUnavailable` 阻塞；FullAccess 为 `Automatic`。无阻塞时 `canCommit=true` 仅表示允许尝试带记录提交，预演不会创建或检查备份是否就绪，实际提交仍须通过恢复存储、容量和工作区重检。所有允许写入的补丁都要求恢复记录；普通文件不会从需确认的混合补丁中被单独执行。

预演和实际提交请求都先完成基础校验、完整 diff 和整批评估。被策略拒绝的请求不会准备记录或执行写入；超过固定预览大小或计算上限时返回要求拆小补丁的错误，FullAccess 也遵守这些限制。提交或回滚不完整、结果核验失败及最终清单保存失败会返回工具错误，包含实际引擎/核验状态及对应记录、会话 ID，不导出原始 IO 异常或绝对存储路径；最终保存失败可能发生在工作区已成功修改之后，不能按“工具错误”等同于“未写入”或直接重放补丁。

停止 Runtime 会拒绝新调用，取消并等待已拥有的请求，包括真实提交、回滚和最终记录保存，再释放 Host。超时后保留故障状态及资源所有权，清理完成前不允许新 Host。客户端取消 HTTP 等待可能丢失响应，服务端仍完成已开始安装的整批操作和记录；重连后应先检查实际内容及对应记录。

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

核心检查覆盖文件/路径限制、UTF-8、补丁预演与提交、提交结果及回滚/清理错误、提交取消边界、格式保留、完整 diff 重建及预算边界、风险分类与批量阈值、三模式整批决定和执行阻塞原因、内存恢复草稿的原字节/前后路径状态/权限/哈希、恢复清单的严格 JSON/必需字段/大小边界、记录归属与操作/路径/备份引用对应关系、真实命令输出、超时和取消。协议检查使用实际 MCP SDK，通过内存流和统一执行入口验证三种模式的工具输入/输出 schema、读取、预演、带记录写入、策略拒绝及 Bash 权限；交叉核对公开会话/记录 ID、真实持久化状态与原字节备份，伪造关闭恢复等参数不能绕过记录。大型 diff 在结构化及文本输出中完整保留，超限错误不暴露部分预览或内部计划，拒绝和预演不修改文件或创建恢复目录。它不验证 HTTP、OAuth 或公网 Tunnel。

Runtime 检查覆盖真实 HTTP 响应、loopback 绑定、端口冲突、并发启动、会话隔离、真实 Bash 子进程的客户端/运行时取消、等待修改锁的调用取消、停止期限及释放任务复用、停止后重用端口和释放后禁止启动；同时验证恢复目录的范围/链接/类型与容量预检、准备记录真实落盘和读回校验、准备记录归属与目录/文件身份重检、清单替换的临时容量/身份核对/刷新/读回与不确定结果、Prepared→Applying 转换及内存版本同步/失效/后续清单容量预检、执行结果与一次性上下文绑定、持锁核对实际文件及最终清单候选、最终状态可靠更新/失败重试/不确定结果及真实任务等待、取消/失败清理、并发最后一槽及跨进程占锁、记录有界加载与备份长度/哈希/UTF-8 校验、真实 JWT 签名/issuer/AUD/有效期/算法校验、单个断言头、公钥缓存与受限刷新、执行策略独立性、Host 白名单及固定长度/分块请求体上限。恢复检查使用独立临时目录，验证只读文件加载、备份存储期间工作区和旧记录保持原样、损坏/缺失/额外备份与链接拒绝、结果或回滚核验失败不能被记录为完整成功，以及取消和并发替换不会清理外部数据。加载和候选构造不授予提交或恢复权限，最终状态由绑定执行结果的内部更新入口持久化。认证检查只替换 JWKS 文档获取，密钥在内存中生成，不依赖公网或真实 Cloudflare 凭据。

真实 MCP SDK HTTP 客户端检查覆盖工具发现/schema、三个模式的读取/预演/拒绝、逐文件风险与整批决定、阈值精确边界及逐项超限、自选配置文件识别、公开输出字段限制、大型完整 diff 及超限拒绝、FullAccess Bash 的结果与超时、跨请求鉴权、客户端取消后的子进程退出、跨客户端的修改互斥与停止排空、重启后的项目/模式/阈值隔离，以及 CLI 启动后的实际 MCP 调用。伪造模式、阈值、批准、恢复状态或决定参数不改变本机策略；取消排队中的预演不影响另一客户端正在运行的 Bash，预演和拒绝前后的存储快照一致。检查确认 stateless 响应不使用会话头，默认 Host 不暴露测试探针和 legacy SSE 路由；这些检查不替代 WinUI 交互或公网 OAuth/Tunnel 的实际验证。

内部组合提交检查在混合补丁的每个安装位置注入失败或取消，交叉核对实际工作区、原字节备份、权限和持久化终态；覆盖额度增长、存储占锁、记录篡改、部分回滚、暂存清理、各终态清单保存失败及元数据重试。并发检查验证同一工作区多个提交在最终记录保存前持续互斥、排队取消和重复计划重检、不同工作区的独立执行，以及最终保存等待存储互斥时取消仍保留真实任务与工作区锁。共享入口、真实 HTTP 和 CLI 检查覆盖允许的真实写入、恢复失败零提前写入、错误输出边界、客户端取消及运行时停止期限、真实排空和重启隔离。

恢复预览检查使用实际提交记录和原字节备份，覆盖混合反向操作、格式/权限、空文件、完整大文本、64 次移动/128 路径边界、全部不确定状态、损坏记录、当前路径/内容冲突、真实链接/硬链接、只读存储、观察期间变化及显示/计算超限。取消、工作区/存储互斥和实际 Runtime 停止/重启检查验证只读观察也受会话跟踪，旧预览不能冻结文件或充当恢复授权；所有场景核对工作区及记录保持原样。

内部恢复提交检查覆盖两种可写模式的混合恢复及再次撤销、原字节/格式/权限、新记录的恢复前镜像、只读与错误计划拒绝、文件/目标/原记录变化、备份/容量/Applying 失败零提前工作区写入、各安装位置故障/取消、部分回滚与清理、所有终态的最终清单失败与安全元数据重试、一次性并发消费、同工作区串行与不同工作区独立执行，以及真实 Runtime 停止期限、实际排空和旧会话计划失效。测试仍确认公共 MCP 只有三个工具且无恢复写入端点。Runtime 检查可用 `-- --filter "检查名称片段"` 定向复现，省略参数运行完整检查。

<a id="references"></a>

## 实现参考

本项目的补丁引擎和进程执行代码用 C# 独立实现，参考了其公开的格式和接口设计：

- **OpenAI Codex**（Apache-2.0）：参考 [apply-patch 的语法解析器](https://github.com/openai/codex/blob/main/codex-rs/apply-patch/src/parser.rs) 的 `Begin/End Patch`、`Add/Delete/Update File`、`Move to`、`@@` 和 `End of File` 格式。本项目使用精确且唯一的上下文匹配，保留文件编码/换行，并增加 `dry_run` 文件计划；不是 Codex 补丁引擎的完整移植。
- **DeepSeek Harness**（MIT）：参考 [tool-bash 文档](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/shell/tool-bash/README.md) 的一次性 shell、工作目录、独立调用状态及命令退出结果设计。本项目使用 .NET Process，超时后终止命令，不提供上游的后台任务转交与任务管理功能。
- 进程清理依据 Linux [setsid](https://man7.org/linux/man-pages/man1/setsid.1.html) 和微软 [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects) 的平台接口。

<a id="license"></a>

## 许可证

本项目采用 [MIT 许可证](LICENSE)。
