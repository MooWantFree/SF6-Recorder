# 街霸 6 战绩档案（Windows）

C# / .NET 10 / WPF 原生 GUI。WebView2 负责官方登录，HttpClient 低频请求官网 JSON，SQLite 保存战绩与分数。

## 使用

双击 artifacts/app/Sf6Prototype.exe。

- **刷新**：同步排位赛、休闲赛、比赛间对战、格斗中心对战的全部当前可查页，并更新官网角色资料与当前账号头像。首次使用会打开登录窗口，成功后自动关闭。
- **战绩**：紧凑行按日期分组；按分类、角色、结果、日期筛选，搜索对手或回放 ID；筛选结果联动胜率和场次。点击一行展开详情，可复制回放 ID、对手 UID，或打开对手角色段位。
- **日期范围**：先选开始日，再选结束日；支持昨天、本月、上个月、90 天和全部日期。
- **角色分数**：头像下显示角色名，仅列出本人本地战绩中的角色。默认显示最近 20 条，也可选择 50 / 100 / 全部；每点等距表示一场对局，悬停显示时间、分数及差值。点击点会在页面内浮层显示对应对局详情，布局与战绩页一致，支持复制回放 ID、对手 UID 及查询角色段位。点击浮层外、关闭按钮或按 Esc 收起；图表聚焦后可用左右方向键查看相邻对局。
- **对手段位**：打开时实时读取当前赛季全部角色；可选择其他赛季和最高 MR。切换赛季、切换查询类型或点击刷新均重新请求；默认隐藏未定级，可勾选显示。查询结果只存在于窗口内存中，关闭即释放，不写入 SQLite 或资料缓存；通用角色头像复用本地图片。
- **账号菜单**：左下角显示昵称与圆形头像；支持添加账号、切换保存的账号。鼠标经过账号行时显示登出按钮，确认后仅移除此设备的登录凭据，保留战绩。
- **连接设置**：设置 HTTP / HTTPS / SOCKS5 无认证代理；Google HTTPS 请求测试延迟。登录、JSON 和头像请求共用代理，关闭代理时明确直连。

界面只显示刷新状态、更新时间和必要的错误。请求数、分页和底层状态保留在诊断文件中。

本地浏览、筛选和统计不产生官网请求。默认启动不自动同步。程序当前不是安装包；请保留 EXE 同目录下的 DLL。

快捷键：Ctrl+F 搜索、Ctrl+R 刷新、Ctrl+, 连接设置。

## 增量同步与请求负担

每条官方记录带有 replay_id。数据库以 (owner_id, replay_id) 为主键，防止重复保存。

四个分类各自保存上次**成功完成**的同步边界。下一次从最新页开始，遇到该边界所在页后停止，整页仍会先去重保存。中断或失败的分类不提前推进边界，避免下次因遇到部分已保存的记录而漏掉更旧的数据。已经完成的其他分类不受影响。

分页使用串行 HttpClient GET，每次请求至少间隔 2 秒。429 立即暂停；401、403、登录重定向或验证页要求重新登录；超时和 5xx 只做一次有限重试。官网 buildId 变化时刷新一次页面元数据。不会通过后台浏览器加载整页来抓取每一页战绩。

“全部”指官网当前公开给登录会话查询的窗口，不等于账号从创建至今的所有比赛；已经不再可查的历史无法恢复。

## 分数与统计口径

- 每场保存角色 ID、LP、MR、段位、双方角色与回合结果、时间及回放 ID。
- 官网字段原样保存，不推断未提供的结算后分数。
- 曲线显示该角色已保存的分数记录；差值相对于上一条已保存的同角色记录。期间可能存在未抓取的比赛，因此差值不一定等于该场加减分。
- MR 为 0 时显示为无有效 MR。战绩接口目前没有提供赛季编号；`master_league` 实际是大师段位标识。无法确认同赛季时，MR 只显示各记录的点，不连线、不计算差值；不会通过段位标识猜测赛季。LP 的点线图不受影响。数据库版本 2 会保留历史战绩与分数，并纠正旧版本误存的赛季字段。
- 胜率 = 胜场 /（胜场 + 负场 + 平局），结果未知的记录不进入分母。
- 日期、时间按 UTC+8 展示和筛选；日期区间包含开始日与结束日。

## 本地文件

默认数据目录：artifacts/app/Data。

| 文件 | 用途 |
| --- | --- |
| battles.sqlite | 战绩数据库；battles、score_snapshots、sync_state、sync_runs |
| session.dpapi | Windows DPAPI CurrentUser 加密的本站 Cookie、账号及接口版本信息 |
| Accounts/*.dpapi | 按账号分别加密保存的登录会话 |
| characters.json / Images/ | 官网角色目录与持久化头像；角色头像只在缺失时下载 |
| profile-*.json | 账号昵称、头像位置与资料更新时间 |
| WebView2/ | 官方登录窗口的独立浏览器数据 |
| settings.json | 用户代码、代理开关与地址 |
| status.json | 当前同步结果、请求次数等诊断，不含 Cookie |
| proxy-test.json | 最近一次成功的 Google 延迟测试 |

Cookie 在内存中交给 HTTP 客户端，响应更新后的 Cookie 也会加密持久化。重启可直接恢复，不需要打开登录网页；服务端让会话失效时仍需重新登录。Cookie 仅发送给 streetfighter.com 相关域名，Google 测试使用独立无 Cookie 客户端。DPAPI 文件只适用于当前 Windows 用户，不能当作跨机器的登录备份。

数据库使用 WAL。备份时先关闭应用，再复制整个 Data 目录；不要只在应用写入时复制主 SQLite 文件。

## 开发与验证

在 PowerShell 7 执行：

    pwsh -File .\build.ps1
    pwsh -File .\build.ps1 -Launch

构建优先使用项目 .tools/dotnet 下的 .NET 10 SDK。运行需要 .NET 10 Windows Desktop Runtime 和 Microsoft Edge WebView2 Runtime。

71 项检查包括：P2 身份及分数、SQLite 唯一键和事务、分页遍历、重复同步、半途失败恢复、筛选、统计口径、按角色分数差值、DPAPI 持久化、多账号隔离及登出、日期边界、最近记录窗口、新角色发现、Cookie 域隔离、代理配置和真实本机代理转发；新增对手段位解析、身份校验、MR 赛季边界与无损数据库迁移。最新实际验收见 APP_VERIFICATION.md；最初登录原型记录保留在 VERIFICATION.md。

开发诊断参数：--sync-on-start 主动启动一次同步；--capture 保存原生 GUI 截图；--data-dir 指定独立数据目录。正常使用无需参数。

## 接口依据

接口与字段最初参考公开项目，当前已使用用户自己的登录会话实测：

- https://github.com/hanydesoki/Street-Fighter-6-History-Matches/blob/main/sf6_match_collection.py
- https://github.com/tomakoon/sf6-drive-recap/blob/main/buckler/auth.py

这是 Buckler 官网内部接口，路径和结构未来可能变化。Cloudflare 对嵌入式浏览器的兼容性也可能变化，失败时应用会显示状态，不会无限重试。

对手段位接口另外核对了官网 `profile/[sid]/play` 页面 JavaScript，并使用当前登录会话实测：

- `play.json`：当前赛季、可查询赛季列表、所有角色当前段位。
- `POST /6/buckler/api/profile/play/act/leagueinfo`：指定玩家、指定赛季的全部角色段位。
- `POST /6/buckler/api/profile/play/act/highest/master_rating_info`：指定赛季的全部角色最高 MR。

历史请求体使用 `targetShortId`、`targetSeasonId`、`locale`、`peak`。一次请求包含所有角色，无需逐角色遍历。若以后要汇总所有赛季的 MR 历史，按官网提供的 `season_ids` 串行逐季读取；目前窗口按选择查询，不自动遍历历史赛季。没有发现或假设一个可直接返回所有赛季 MR 的接口。
