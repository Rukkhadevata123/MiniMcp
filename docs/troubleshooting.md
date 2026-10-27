# 排错

## 配置无法加载或服务无法启动

- 运行 `minimcp check --config <settings.json>`，检查错误对应的字段。JSON 使用 UTF-8，字段和模式大小写严格；未知字段、重复键和错误类型会被拒绝。
- 确认项目目录存在，策略与恢复目录位于允许的位置。相对路径以所属配置文件目录为基准，JSON 不展开 `~` 或环境变量。
- 端口被占用时停止占用该端口的程序或更换 `port`。`check` 只做短暂绑定探测，之后端口仍可能被其他程序占用。
- FullAccess 需要 Bash；启用受管隧道需要 cloudflared 和可读、非空 token 文件。Linux 还需 `/usr/bin/setsid`。`check` 不执行这些程序，实际启动失败时查看日志。
- 无法定位 ripgrep 或 Git 时，安装可信版本或填写 `rgPath` / `gitPath`。它们只影响对应查询，不影响其他工具启动。

## 本机正常，公网连接失败

先检查 `http://127.0.0.1:端口/healthz`，再检查桌面的隧道状态或 CLI 日志。核对 Tunnel 的 published application hostname、后端端口、DNS 和 Access 应用 hostname。进程 Running 不代表公网路由可用。

OAuth 登录失败时核对 Allowed redirect URIs、客户端显示的实际回调、Access 登录方式及邮箱策略；用 `https://域名/.well-known/oauth-authorization-server` 检查发现端点。JWT 错误时核对 `team`、应用 AUD、系统时间及网络到 Cloudflare 公钥端点的连接。`publicOrigin` 填 HTTPS 根地址，客户端 Server URL 才加 `/mcp`。具体步骤见 [Cloudflare 指南](cloudflare.md)。

隧道在运行期间退出时，MiniMcp 保留本机服务供诊断。桌面可检查错误后重试隧道；CLI 停止后的退出码会反映该次故障。token 泄漏时在 Cloudflare 轮换并更新本机文件。

## 查询结果不完整

查看返回的 `complete`、`truncated` 和 `notices`。缩小 `path`、深度或匹配范围；目录列表默认不解析 gitignore，搜索则使用选定范围内的 ignore 文件。私密路径、链接、过大文件和部分不可读取的内容会跳过或触发错误。

Git 工具要求项目根是标准工作树，`.git` 为实际目录。linked worktree、外部 gitdir、partial clone、助手、include 等配置不在支持范围；使用普通仓库及受支持配置，或在可信环境自行使用 Git。查询 diff 不包含未跟踪文件正文，且允许截断。

## 补丁被拒绝或恢复有冲突

先查看完整预演的 `risk` 和 `decision`。ProjectEdit 会整批拒绝删除、移动、关键文件、未知文件及超限批次；ReadOnly 拒绝实际写入。修改本机策略后需要重启服务，客户端确认不能改变服务端规则。

补丁上下文必须精确且唯一。预览过大或计算预算超限时拆小补丁。记录目录不可用、已满或备份失败时，修复存储后再试。

恢复前检查预览中的冲突：当前文件可能已被外部修改、删除或替换。确认希望保留的内容后处理冲突，再重新加载预览。恢复只处理记录中的文件；Bash、网络操作和未记录目录不在恢复范围。遇到不完整记录、回滚或最终保存错误时，先核对实际文件与记录状态，避免直接重复写入。
