# 桌面页面布局约定

这套约定用于当前的概览、设置、日志，以及后续的修改记录和恢复预览。目标是让不同任务使用同一个稳定工作区，同时保留各自适合的滚动和内容布局。

- MainWindow 负责 NavigationView、全局提示和唯一的页面 Frame。内容列居中，最大宽度 1200 有效像素；按导航之外的可用宽度使用 16/24/32 像素边距。页面不再单独声明最大宽度或外层左右留白。
- 页面统一为缓存的 Page，顶部使用 Controls/PageHeader；页头固定，正文占剩余高度。各页的标题起点、说明、正文顶边保持一致。概览与设置只滚动正文，日志保留受约束高度的 ListView，不能把列表再放进外层同向 ScrollViewer。
- 分组表面使用 App.xaml 的 WorkspaceCardStyle：系统主题画刷、8 像素圆角、20 像素内边距；卡片间隔 16，页头与正文间隔 20。分组标题使用 WorkspaceSectionTitleStyle。按实际内容宽度重排操作按钮和日志筛选控件。
- 左侧一级页面切换使用 Frame.Navigate + EntranceNavigationTransitionInfo，即原生向上进入并淡入；系统关闭动画时使用 SuppressNavigationTransitionInfo。一级导航不积累返回历史，同一菜单重复选择不重新导航。
- Page 的 NavigationCacheMode 为 Required，保留草稿控件、选择与滚动位置。Runtime、SettingsEditor 和 RuntimeMonitor 属于 App/窗口，切页不创建 Runtime 或重新订阅。页面事件只在首次缓存时接入；日志页未加载时不强行滚动。
- 缓存页可能暂时脱离可视树，主题要跟随工作区 ActualTheme 同步，避免重新进入时保留旧的控件前景色。禁止用固定黑白画刷修补主题问题。
- 日志保留级别文字，Warning 使用系统 Caution 前景色，Error/Critical 使用 Critical 前景色，Critical 另外加粗。原生模板选择器只选择级别对应的文本模板，共用字体/布局样式；浅色和深色主题将颜色应用于 TextBlock，悬停、移开或选中时保持级别颜色，容器继续负责系统背景/选择/焦点。HighContrast 主题省略文字前景色设置，继承原生容器的 WindowText/HighlightText 配对，避免选中后文字不可读。保留列表虚拟化；复制、详情及导出仍为原来的纯文本。实现依据微软的[彩色列表文字对比度说明](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes#list-items-with-colored-text)。

## 修改记录和恢复页

6.3 沿用同一个 Frame、PageHeader、内容宽度和卡片样式。宽窗口下可以在正文放记录列表与详情两列；窄窗口重排到上下结构，不能通过改变整个页面的最大宽度制造跳变。

完整反向 diff 使用独立的只读、可选择文本区域，可按正文需求提供横向/纵向滚动。记录列表摘要可以截短，但待确认的完整预览不能使用摘要、MaxLines、日志缓存或截断文本替代。冲突、无法完整生成预览、只读模式或记录状态不允许时，恢复动作应按后端结果禁用。

恢复确认属于用户主动选择某条记录后的具体操作。先完成完整预览，再明确确认并调用同一个恢复核心；不添加通用待审批页、远程 confirmed 参数或自动恢复。布局统一不改变强制备份、冲突检查和失败回滚约束。

## 参考与验证

原生进入效果采用微软的 [Page transitions](https://learn.microsoft.com/en-us/windows/apps/develop/motion/page-transitions) 和 [EntranceNavigationTransitionInfo](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.media.animation.entrancenavigationtransitioninfo) 定义。技能参考以本地 winui-design、winui-xaml-patterns 为主；也核对了微软 [Windows development skills](https://github.com/microsoft/win-dev-skills) 的维护位置，不额外安装 CLI 或引入运行依赖。

真实窗口检查覆盖三页的页头位置/宽度一致、缓存实例与滚动保留、快速切换、无返回栈增长、系统动画偏好、缓存页主题资源、日志虚拟化以及既有配置/隧道/关闭路径。测试截图只使用临时配置，不使用用户的 settings、policy 或 token。
