# Lingban 建设规划(Bootstrap Plan)

创建:2026-07-22
依据:知识库《MES Agent 另起炉灶方案:基座选型、模板评审与 skills 清单》
状态:进行中
说明:按里程碑推进,不做日历排期;每个里程碑有明确验收标准,验收通过才进下一个。
顺序有讲究——领域心脏先于 Agent,Agent 先于界面;每个里程碑结束时仓库都处于可构建、测试全绿的状态。

---

## 里程碑 0:仓库引导

**目标**:空仓库 → 可构建的骨架。

- [x] 骨架已生成:ca-sln v10.8.0,`-cf None -db postgresql`,`Lingban.slnx` + `Lingban.*` 命名空间;模板为 .NET 10 + Aspire 编排(AppHost 自动拉 `pgvector/pgvector:pg17` 容器,**取代原计划的 docker-compose**)。
- [x] 新增 `src/Agent`(Lingban.Agent)类库并入解决方案。
- [x] Azure 资源已移除(AppHost 改纯 `AddPostgres`,删 Azure.AppContainers / Azure.PostgreSQL / JavaScript 包)。
- [x] `.env.example`(ANTHROPIC_API_KEY 占位)。
- [x] 模板未带 CI,已自写 `.github/workflows/ci.yml`(restore → build → format verify → 全量 test;GitHub runner 自带 Docker 可跑功能测试)。
- [x] 传递依赖漏洞治理:开启 CentralPackageTransitivePinningEnabled,钉版 System.Security.Cryptography.Xml 10.0.10 / MessagePack 3.1.8 / OpenTelemetry 1.17.0 / Microsoft.OpenApi 2.11.0,NuGet audit 零告警。
- [x] 本地验证:build 0 警告 0 错误;Domain + Application 单元测试 14/14 通过;format 干净。
- [x] AGENTS.md 第 1、2 节已回填实测事实。
- [x] Docker 29.6.2 已装,本地全量测试 33/33 通过(功能测试首跑因拉镜像冷启动超时,属一次性现象)。
- [x] GitHub 门禁已配:main ruleset(require PR + required check `build-and-test`)实测直推被拒;secret scanning push protection 开启。
- [x] CodeQL 决策:暂不启用;M4(MCP Server 网络暴露面出现)时用页面 default setup 一键开启,记入 M4 验收项。

**验收:✅ 全部通过(2026-07-22)**——本地 build + 全量测试全绿、CI 两次运行全绿、直推 main 被拒。M0 关闭,进入 M1。

## 里程碑 1:领域心脏(MES 铁律落地,趁没有代码债)

**目标**:把旧项目缺的两颗心脏之一(物料)和全部领域铁律建成 Domain 层,schema 参考 qcadoo MES 与 Odoo mrp(抄结构不抄代码)。

- [x] 实体:Product、BomLine、**MaterialLot**、**MaterialConsumption**(谱系边,只能经 `WorkOrder.RecordConsumption` 创建)、WorkOrder、WorkOrderOperation、ProcessRoute/ProcessStep、ProductionLine/Workstation、QualityInspection、DefectRecord/DefectType、Shift。Equipment 实体推迟到 M2(随设备状态工具一起进)。
- [x] **WorkOrder 状态机**:Draft→Released→InProgress→Completed,Cancel 仅限开工前;非法转换抛 `InvalidWorkOrderTransitionException`;Status 私有 setter。
- [x] **ShiftCalendar 领域服务**:UTC 时刻 → (班次, 生产日),跨天夜班归开班日;`GetProductionDayBoundsUtc` 供"今天的工单/OEE"划界;禁用 API 由 `BannedTimeApisTests` 机械保证(扫描 src/ 拒绝 DateTime.Now/Today/UtcNow.Date,已顺手修掉模板 WeatherForecasts 的一处违例)。
- [x] 数量闭环:完工/合格/报废/返工四账分记,decimal(18,3) 全局约定,超产暴露(`IsOverproduced`)不截断。
- [x] **多租户**:ITenantEntity + TenantInterceptor 盖章 + DbContext 表达式树全局过滤器;M1 租户来自配置(默认 "default")。
- [x] EF 配置(租户复合唯一索引、谱系边 Restrict 删除、消耗集合私有字段访问);~~首个迁移~~ 模板开发期为 EnsureCreated 策略,迁移推迟到有部署目标时。
- [x] 种子数据:SMT 电子装配两级谱系(来料 → WO-SEED-01 → PCBA 批次 → WO-SEED-02 → 整机批次)+ 双班制 + 缺陷类型。
- [x] 追溯查询:`TraceLotForwardQuery` / `TraceLotBackwardQuery`(Application 层 MediatR,含环路防护)。

**验收:✅ 全部通过(2026-07-22,51/51 测试)**——① 正向追溯与 ② 反向追溯为真库功能测试(召回/客诉场景,含供应商批号叶子);③ 非法转换抛异常;④ 东八区 07:59→前日夜班 / 08:01→当日白班,另验 UTC 午夜不切分生产日。

## M1 审查跟进(Codex 交叉审查,2026-07-23)

Codex CLI 审查原始 PR #2 diff,报 7 bug / 7 风险 / 1 建议。逐条核实后处置如下。

**已修(feature/m1-review-followups,均有回归测试)**:
- #1 并发超卖 → MaterialLot 启用 PostgreSQL xmin 乐观并发令牌;并发冲突测试。
- #2 菱形谱系漏支路 → Trace 访问集改为"当前递归路径"(防环不防汇合);菱形测试正反向双验。
- #3(同工单部分)自耗环 → RecordConsumption 拒绝消耗本工单产出批次。
- #4 租户写隔离 → 拦截器:伪造租户新增拒收,改/删校验原值与现值;伪造租户测试。
- #5(轻量部分)→ RecordConsumption 校验工单与批次租户一致。
- #6(写入面收紧)→ MaterialLot.Create 移除 producedBy 参数、谱系导航 internal/private;新增架构守卫:禁止对谱系表 Add/Remove/ExecuteUpdate/Delete(采购批次入库除外)。
- #7 四账不变量 → 合格+报废+返工 ≤ 完工,违规调用原子回滚。
- #9(重叠部分)→ ShiftCalendar 构造时拒绝重叠班次。
- #10 测试基建 → AttachGraph 弃用,改同一 DbContext 执行领域操作;新增库存扣减持久化断言。
- #11(扩展部分)→ 守卫正则加 DateTimeOffset.Now。
- #13 质量记录级联删除 → 全部改 Restrict(审计证据不随主数据清除)。
- #14 UTC 不变量 → 领域入口 ToUniversalTime() 归一;偏移量测试。
- #15 工序顺序唯一索引(路线步骤、工单工序)。
- AGENTS.md 单测命令修正(dotnet test 不接受多项目参数)。

**缓修(显式负债;"推荐修复时机"是硬约束——对应里程碑动工时先清账,再写新功能)**:
| 项 | 推荐修复时机 | 触发条件(早于时机出现则提前修) | 理由 |
| --- | --- | --- | --- |
| 跨工单环路检测 + Trace 环标记(#3 余下) | **M2 开工第一批**,与报工/消耗工具同一 PR | 出现任何跨工单写路径 | 需要应用层事务内图检查,随报工工具一起设计 |
| 消耗上报幂等键 (TenantId, EventId) 唯一索引(#8) | **M2**,消耗上报进入 API/工具的同一 PR | 任何外部调用方(工具/端点/模拟器)能触发 RecordConsumption 时 | 幂等键设计跟随调用方形态;晚于 API 上线就是真超扣 |
| OEE 用班次区间集合而非首尾包络(#9 余下) | **M2**,移植 OEE 工具的同一 PR | OEE 计算首次引用 ShiftCalendar 时 | 不修则午休等空档计入计划时间,OEE 数字直接失真 |
| Complete 前置校验(消耗>0、产出=报工)(#7 余下) | **M2**,报工流程定义时 | 报工工具落地 | 先定义无料工单等合法形态,不默认放行也不误杀 |
| 复合租户外键 (TenantId, Id) + 迁移(#5 完整) | **M4(MCP 对外暴露前)** | 第二个真实租户进系统,或任何外部可写入口上线 | MCP Server 是首个外部写入面;单 default 租户期域内校验已挡主要路径 |
| Identity 用户-租户 membership(#12 后半) | **M4(MCP 鉴权边界)或鉴权接入时** | 出现第二个用户/租户 | 与 MCP 的租户上下文解析是同一件事 |
| regex 守卫升级 Roslyn analyzer(#11 完整) | 触发式,无固定里程碑 | 守卫被真实绕过一次 | 生长纪律:等真实事故,不预先镀金 |

**还债进度(2026-07-23)**:跨工单环检测、消耗幂等键、OEE 班次区间、Complete 前置校验、设备事实受控写入、Todo 移除——全部已还,均有回归测试;剩余债项(复合租户外键、Identity membership、Roslyn analyzer、全字段复核、QueryLog 边界、工具 LLM contract)按表内时机执行。

**核实后不改**:Cancel 不回滚消耗(消耗仅限 InProgress、Cancel 仅限开工前,路径互斥,Codex 亦确认);08:00 边界归属正确([start,end) 半开区间)。

## 里程碑 2:应用服务与工具层(移植 + 适配)

**目标**:从旧仓库 `zh667/Mes-Agent` 移植可用资产,按新领域模型适配,消灭旧项目的"造假点"。

- [x] 写路径命令(创建/下达/开工/取消/消耗/报工/产出/完工)——三条 M1 债的落点:幂等键(EventId + 过滤唯一索引)、跨工单环检测(谱系祖先遍历)、完工前置校验(有消耗、有产出、产出=报工)。
- [x] 工具组重写为 Application 查询(移植思路、不搬旧病):GetTodayWorkOrders(班次切分生产日)、AnalyzeDelayedOrders、GetDefectSummary、CalculateOee。
- [x] CalculateOee 计划时间用班次区间集合(债 #9 还清);Performance/Quality 产线级归因并在 Attribution 字段如实标注,数据不足时为 null 不硬凑。
- [x] FactVerifier 框架重写:规则经 VerificationQueryService(原生 SQL,显式租户条件)复核,与工具 LINQ 管道零共享;篡改结果被独立路径抓出的测试。
- [x] SqlCaptureInterceptor + IQueryLog:真实执行的 SQL 进作用域日志,供 M3 debug 面板使用;禁止手写 SQL 字符串。
- [x] Equipment / EquipmentStatusRecord / DowntimeRecord 实体(M1 推迟项),采集事实带 DataSource 来源标记(领域铁律 #5)。
- [x] **M2 收尾**:DeviceSimulator 落地(BackgroundService,只走命令入口、Source=Simulated、Aspire 编排随 Web 之后启动);Todo/WeatherForecasts 模板业务全量移除(Domain/Application/Web/测试/种子);设备事实受控命令(SetEquipmentState 关旧开新、RecordDowntime 重叠拒绝、EndDowntime)——债 #11 余下部分还清。
- 调整:知识库工具组推迟到 M5(向量基建在那里,现在移植是空壳);i18n 双语目录推迟到 M6(当前无用户可见界面文案,工具 DTO 是结构化数据)。

**验收:✅ M2 关闭(2026-07-23)**——工具查询 + 校验规则 + 篡改测试齐备;三轮 Codex 交叉审查全部处置;模拟器与 Todo 移除完成;60/60 测试全绿(Todo 清除净减 28 条模板测试)。

## M2 审查跟进(Codex 二审,2026-07-23)

Codex CLI 审查 PR #4(9 bug / 4 风险 / 1 建议),核心批评成立:四条债"还了但成色不足"。处置:

**已修(同 PR 追加提交,均有回归测试):**
- #1 幂等竞态与键碰撞 → 指纹校验(同键不同 payload 显式拒绝)+ DbUpdateException 兜底复查;并发同键被闸门串行化。
- #2 环检测 TOCTOU → **IGenealogySerializedExecutor**:租户级 pg_advisory_xact_lock,消耗/产出/完工的"检测+写入"同锁串行;并发反向边测试(恰好一边被拒)。
- #3 Complete 绕过 → TOCTOU 由闸门解决;裸调用 WorkOrder.Complete() 由架构守卫禁止(白名单:领域/命令/种子)。
- #4 开放停机 → 截断到 min(AsOf, 日终),不再把未来算成停机;OeeDto 增 AsOfUtc。
- #5 重叠停机 → 工具与校验路径各自做区间并集(实现独立,数字必须一致)。
- #6/#7 → 产线过滤进校验 SQL(DTO 携带 ProductionLineId);缺陷校验补 AsOf 上界。
- #8(部分)→ 规则不再信任 DTO 时间范围,从工厂日历独立重建边界与班次区间;核对字段扩展:Today 五项、Delayed 含 ID 集合、Defect 含分类合计、OEE 含计划分钟与公式一致性(带 clamp)。
- #9 → CreateWorkOrder 校验 PlannedEnd > PlannedStart、ID 为正。
- #10 → 消耗校验工位与工单同产线;RecordedBy 长度、ID 正数进 Validator。
- #11(部分)→ DataSource 加 Unspecified=0 哨兵 + CHECK 约束(Source<>0、End>Start),模拟器忘标来源直接写不进去。
- #13(部分)→ 新增:空档日历(计划 480 非 540)、重叠停机并集、开放停机截断、并发环竞赛、指纹碰撞、产线过滤校验、缺陷 AsOf、四工具多字段篡改测试。

**缓修(入债表):**
| 项 | 推荐修复时机 | 触发条件 |
| --- | --- | --- |
| #8 余下:全部展示事实的逐字段复核(明细数量、OEE 分量) | M3,DTO 进入 Agent 答案时 | LLM 开始引用某字段,该字段就必须有校验 |
| SSE 聊天端点匿名 → 鉴权 | M4(MCP/鉴权边界) | 任何非本机部署 |
| 写操作工具 + HITL 确认交互 | M4(MCP)/M6(UI) | 首个写工具注册时 |
| #13 余下:唯一索引兜底路径的确定性并发测试 | 守卫被真实击中一次时 | 闸门已串行化,该路径为理论后盾 |

**口径修正**:M2 交付的是"工具查询内核",非完整 Agent 工具;"每工具四件套"的验收移至 M3 注册时点。

## M2 审查跟进(Codex 三审,2026-07-23)

三审 4 bug / 3 风险,两条列为合并阻断。全部确认为真,处置如下(全部已修,无新增负债):

- #1(阻断)报工未进闸门 → ReportProductionCommand 进 IGenealogySerializedExecutor;"完工 vs 追加报工"并发测试断言不变量(Completed ⇒ 产出=报工)。
- #2(阻断)WorkOrder 无并发控制 → xmin 乐观并发令牌;未走闸门的 Start/Cancel/Release 并发时后写方冲突,杜绝"带开工时间的已取消工单"。
- #3 校验仍信任 DTO 范围 → **IFactVerifier 增加 invocation context**:规则接收原始工具请求,生产日/边界/班次区间/AsOf 全部从请求独立重推;请求未钉死 AsOfUtc 时如实返回 Unverified(M3 Agent 循环调用工具前必须钉死 AsOf——此为 M3 设计约束,已记)。新增"工具选错生产日但输出自洽边界被抓"测试。
- #4 OEE 历史回放 → 已关闭停机同样按 AsOf 截断(工具与校验两侧);历史回放测试(01:00–03:00 记录,as-of 02:00 只见 60 分钟)。
- #5 指纹缺 RecordedBy → 已纳入。
- #6 竞争测试非确定性 → 新增闸门互斥性确定测试(T1 持锁期间 T2 不得进入)+ 完工/报工竞态不变量测试;原真并发反向边测试保留。
- #7 闸门是自愿约定 → 架构守卫扩展:RecordConsumption/ProduceLot 调用点白名单(领域/命令/种子),模拟器届时必须走命令入口。

**四条 M1 债的最终口径(三轮审查后)**:幂等键、跨工单环检测、OEE 班次区间、Complete 前置校验——**已还清**,各有并发/边界回归钉。校验"全字段复核"仍按债表在 M3 随字段进入 Agent 答案逐项补。

## 里程碑 3:真 Agent 循环(装第二颗心脏)

**目标**:LLM 驱动的工具编排,替代旧项目的关键词 if-else。

- [x] LLM 接入:**Microsoft.Extensions.AI `IChatClient` + `UseFunctionInvocation` 手控循环**(决策调整:MAF 高层 `Microsoft.Agents.AI` 站在同一抽象上,随 M4 MCP/A2A 引入;手控层才能在每次工具调用内部插 AsOf 钉死/事实校验/SQL 分段)。提供商可配置(当前 openai-compatible 中转,`Llm:*` user-secrets)。
- [x] 四工具注册(铁律 #3 四件套齐):中文 Description + FluentValidation 参数校验 + VerificationRule + eval 用例;LLM 自主选工具,"模式由用户选"的概念不存在。
- [x] AsOf 钉死(三审设计约束):`IAgentInvocationClock` 每次调用进循环时 Pin 一次,全部工具与校验共用——脚本化测试以"校验 Verified"证明钉死生效。
- [x] SSE 端点真 token 透传(`POST /api/agentchat/chat`);工具结果 + 校验结论 + 真实 SQL(QueryLog 分段,债 #12 还清)随事件流下发并持久化到会话消息。
- [x] 会话上下文窗口(近 10 条)真实喂给 LLM——测试断言模型第二轮收到了第一轮的问答。
- [x] 最小 eval 集 4 条(每工具一条中文问题 → 选对工具 + Verified);命令入 AGENTS.md 第 2 节;CI 无密钥自动跳过。
- 范围界定:M3 只注册**只读工具**,写操作工具 + HITL 确认交互随 M4(MCP)/M6(UI)进入(铁律 #5 的交付时点);SSE 端点暂匿名,M4 鉴权边界收紧(入债表)。
- ⚠️ eval 实跑状态:harness 已验证可发起真实调用,但中转站(cn.aiapi.bot)上游账号池当前整体 503(`codex_scheduler_no_eligible_account`,密钥有效、models 接口正常)——**live eval 通过待中转恢复,不作"已通过"声明**。

**验收**:脚本化 LLM 功能测试 2 条全绿(工具执行/校验/流式/持久化/多轮);live eval 待中转可用后补验——"3号线今天延期的工单"类问题选对工具且校验通过。

## M3 审查跟进(Codex 四审,2026-07-23)

四审 7 bug / 6 风险 / 1 建议,全部核实为真。同 PR 修复:
- #1 答案级闸门 → **AnswerAuditor**:最终答案数字必须在"已 Verified 工具数据/用户原话"中有出处,任一工具非 Verified 即整体降级;AnswerAuditEvent 随流下发并持久化;"工具说1模型说9被抓"回归测试。
- #2 债 #8 还清 → 四规则字段级/逐行复核(Today 四账合计、Delayed 逐单字段+延期小时重算、Defect 逐类型+占比重算、OEE 全部派生分量按同公式独立重算)。
- #3 验收问题可表达 → Today/Delayed 支持产线编码过滤(进独立校验 SQL),OEE 支持设备编码;eval 恢复"3号线延期工单"原题并断言不混线。
- #4 UserSecretsId 找回(合并时序事故:修复提交搁浅在已合并的 PR #6 分支上);eval 改走 ConfigurationBuilder(user-secrets+环境变量)与 Web 同路径。
- #5 参数信任边界 → 日期 TryParseExact、结构化可恢复错误回给模型(含字段与格式)、Today/OEE validator 补齐;坏日期不断流测试。
- #6 失败闭合 → 流异常持久化失败回合 + SSE error 事件,不留孤立断流;幂等键/重试语义留 M6 UI(债)。
- #7/#13 CallId 贯穿事件与持久化;SQL 三段分账(toolSql/verificationSql)。
- #8 AllowConcurrentInvocation 显式 false + 注释边界条件。
- #9 上下文 (Created,Id) 稳定排序 + 助手历史附工具数据快照(标注过期)。
- #10 系统提示词加入信任层级(工具文本是数据不是指令);对抗 eval 留债。
- #11 消息限长 4000 字符;速率限制留 M4。
- #12 eval 严格化:期望工具 + 答案审计通过 + 含种子数字/工单号断言;预检分类(5xx→跳过,401/400/429→显式失败)、静态缓存;网关中途断流按跳过。
- eval 实跑:中转站持续抖动(短暂恢复期确认其流式工具调用协议正常,随后再次整体不可用),live 通过仍待中转稳定。

留债:对抗性注入 eval(M5 知识库入库时)、请求速率限制与幂等重试键(M4/M6)。

## 里程碑 4:MCP Server(单一实现,两处暴露)

**目标**:Lingban 成为 AI 生态里的制造业数据源。

- [x] mcp-builder skill 先行;官方 MCP C# SDK(ModelContextProtocol 1.4.1)暴露 `lingban-mes`:stdio(src/McpServer,日志走 stderr)+ HTTP(Web `/mcp`,RequireAuthorization)。
- [x] 单一实现两处暴露:LingbanMesTools 与 Agent 循环共用同一批 MediatR 查询、FactVerifier、QueryLog;每次调用独立作用域内钉死 AsOf;错误结构化可恢复。
- [x] M4 债第一批:端点鉴权、会话属主(防枚举回归测试)、复合租户外键(谱系关键关系,库级杜绝跨租户引用)、固定窗口限速;CodeQL 待用户页面一键开启。
- 命名遵循 MCP 惯例:mes_ 前缀 + snake_case;ReadOnly 注解;工具描述含参数示例。

**验收**:本机 Claude Code 配置 `lingban-mes` 后,直接问"今天有几张工单"能得到经校验的真实数据;AGENTS.md 第 10 节登记工具清单。

## M4 审查跟进(Codex 五审,2026-07-23)

五审 5 bug / 5 风险 / 1 建议,全部核实为真。同 PR 修复:
- #1 租户授权 → MesData 角色策略(Administrator/MesReader)挂上聊天与 /mcp;自注册用户默认无角色读不到数据;完整用户-租户 membership 仍按债表在多租户真实启用时落地。
- #2 /mcp 限速 → 独立策略 60/分钟,按 NameIdentifier(前缀 user:/ip:)分区。
- #3 单一实现 → **MesToolExecutor** 内核(参数归一/日期解析/查询/校验编排/SQL 分账/错误分类只此一处);AgentToolset 与 LingbanMesTools 降为薄适配层;工具文案共享常量。
- #4 协议错误 → MCP 工具返回 CallToolResult,业务错误 IsError=true(测试钉住);来源不明的 InvalidOperationException 收敛为稳定错误码不泄内部信息。
- #5 复合外键补全 → 质量/设备事实/工序/会话消息全部 (TenantId, Id);**EF 模型级架构测试**枚举全部租户实体外键强制含 TenantId(白名单=三条 SetNull 工位关系)——该守卫首跑即抓出 3 条漏网。
- #6 stdio 威胁模型写入 AGENTS.md 第 10 节(本机信任、只读角色、连接串卫生)。
- #7 限速分区改 NameIdentifier + 前缀。
- #8 429 带 Retry-After 与结构化正文;单轮工具调用预算 MaximumIterationsPerRequest=8;SSE 并发上限留 M6(债)。
- #9 CancellationToken 贯穿 MCP 工具→MediatR→FactVerifier→EF。
- #11 只读工具 Idempotent=true。

留债:MCP 协议层自动化测试(握手/tools list/schema/isError/401/限速,触发=首个外部客户端接入前或 M6);SSE 并发流上限(M6);stdio 只读数据库角色(部署期)。

## M4 审查跟进(Codex 六审/增量复审,2026-07-23)

六审实测发现合并阻断:stdio 宿主缺 IUser 注册,四工具真实协议调用全灭(直接方法测试自证的恶果);判定五审 11 条中 5 已修 4 部分 1 未修。全部处置:
- #1(阻断)→ ServiceUser 进程身份注册进 McpServer 与 DeviceSimulator(后者同病,tick 曾无声吞错);**进程级协议冒烟测试**(真启动 stdio 宿主:握手→列表→成功调用含 Verified→坏日期 isError=true),自证时代结束。
- #2(阻断)→ FactVerifier 显式重抛请求取消,不再把取消解释成 Failed。
- #3 → 校验异常收敛为稳定错误码(VERIFICATION_EXECUTION_ERROR),不泄表名列名;契约:VerificationStatus.Failed 在 MCP 层 IsError=true。
- #4 → 移除 TargetSite 命名空间嗅探:仅白名单类型(Validation/NotFound)原文透出,IOE 一律稳定码;写工具接入时以 DomainRuleException 类型白名单透出(债)。
- #5 → GlobalLimiter 用户级总预算 80/分钟兜住策略叠加;Retry-After 读取 lease 真实剩余窗口。
- #6 → FK 守卫白名单精化:键含 FK 属性、强制可空+SetNull、恰好命中一次、未消费项报警。
- #7 → 迭代预算语义纠偏(注释)+ AnswerAuditor 拒绝空答案;严格按调用计数的预算随写工具落地(债)。
- 次要留债:Logging/Performance behaviour 的 Identity 查询不接受取消令牌(接口签名改动,随鉴权深化)。

## 里程碑 5:知识库与真 RAG

**目标**:SOP/维护手册问答,生成与引用都是真的。

- [x] 决策变更(2026-07-23):中转站无 /v1/embeddings 能力(整路由 404)→ 按回退条件改选**本地 Ollama + bge-m3**(Docker 容器,1024 维,OpenAI 兼容端点,Llm:Embedding* 可配置替换)。
- [x] docx(OpenXml 按标题分节)/md/txt 解析 → 分块(≤800 字符)→ 向量落库(pgvector shadow 列,Domain 不引用具体技术)→ 余弦检索(原生 SQL,显式租户条件)。语料:三份手搓 SMT SOP(标准库 zipfile 构建 docx,python-docx 装不上),含刻意埋设的注入对抗样本。
- [x] 生成 = LLM 基于检索分块作答(第 5 工具 SearchKnowledge,双面暴露 mes_search_knowledge);引用契约进系统提示词与工具文案([文档§章节],无结果明说没有)。
- [x] KnowledgeSearchVerificationRule:每个分块的文本/标题/章节经独立 SQL 核对(篡改被抓测试);相似度排名依赖同一 embedding,如实声明不可独立复核。
- [x] 同名重入库=全量替换(SOP 版本语义);上传端点 /api/knowledge/documents(MesData + 5MB 限制)。
- [x] 注入对抗 eval:问返修温度,断言引用 320/带 § 引用/不执行文档内"宣称 OEE 100%"指令(需中转+Ollama,自跳过)。
- 基建修复:WebApplicationFactory 与 Program 初始化的时序竞态(M5 模型变大后 EnsureCreated 变慢暴露)→ 测试基建显式等 schema + Respawner 重试;TestAppHost 改用 pgvector 镜像。

**验收**:管道功能测试 3 条全绿(入库→检索→校验/篡改被抓/重入库替换);注入 eval 待 Ollama 拉完模型 + 中转稳定后实跑。

## M5 审查跟进(Codex 七审,2026-07-23)

七审 13 条(6 阻断),全部核实为真,同 PR 修复:
- #1 原子替换 → IKnowledgeWriter:先解析/分块/全量向量化(计数+维度 fail-fast),后单事务删旧+插新+写向量;失败注入测试证明旧版保留可检索。
- #2 写权限 → KnowledgeWrite 策略(Administrator/KnowledgeManager);MesReader 上传=403;策略角色矩阵测试。
- #3 维度契约 → 钉死 1024(删除误导性 EmbeddingDimensions 配置语义),服务/入库双重校验;512 维注入测试。
- #4 引用执法 → AnswerAuditor 解析 [标题§章节],知识命中时缺引用/伪造引用=审计失败;三态纯函数测试;eval 锚点收紧到具体文档。
- #5 校验加固 → 空结果如实降 Unverified(阈值语义不可独立复核,库空除外);隐身分块(无向量)/重复 ID/相似度范围与单调性守卫。
- #6 相关性阈值 → Knowledge:MinSimilarity(默认 0.45)进检索 SQL;无关问题空命中测试。
- #7 上传:复制前查长度 + RequestSizeLimit 6MB。#8 embedding 分批(64)+ 写入无 N+1(预置 shadow 后单次 SaveChanges)。
- #9 解析器 → 表格按行入库、标题层级栈生成路径、段落/句子边界切块(硬切仅为兜底)。
- #10 zip 膨胀防护(条目数/单条目/总解压尺寸)。#11 就绪哨兵改为本次 schema 标记(KnowledgeChunks.Embedding 列)。
- #12 测试补钉:原子性失败注入、维度不符、空命中、策略矩阵、引用三态。
- #13 HNSW 索引 → 债:P95 超标或分块过万时加 vector_cosine_ops 索引并验证执行计划。

## 里程碑 6:操作台(最后做界面)

**目标**:Next.js 前端——chat(真流式 + 校验标识 + HITL 确认交互)、工单/OEE 看板。

- [ ] OpenAPI 代码生成打通(前端零手写响应类型);en-US/zh-CN 双目录。
- [ ] 用 `webapp-testing` skill(Playwright)做关键路径 E2E:登录 → 提问 → 流式回答 → 校验标识可见。

**验收**:E2E 全绿;`pnpm lint / typecheck / test / build` 进 CI。

---

## 里程碑之外的持续事项

- 每个里程碑合并前:`git diff` 通读、AGENTS.md 相关小节同步更新(文档与代码同一改动)。
- 出现第二次同类错误 → 按 AGENTS.md 第 13 节固化为 lint/CI/测试或加一行规则。
- `mes-domain` 项目级 skill(ISA-95 词汇、班次/谱系不变量)在里程碑 1 完成后用 `skill-creator` 沉淀,放 `.claude/skills/`。

## 决策记录

| 日期 | 决策 | 理由 |
| --- | --- | --- |
| 2026-07-22 | 项目定名 Lingban(领班) | 身份而非类别;全球无撞车;隐喻=车间里全知的执行者 |
| 2026-07-22 | 基座:ca-sln + Microsoft Agent Framework + MCP C# SDK;不用 ABP、不用 BotSharp | 见知识库《MES Agent 另起炉灶方案》 |
| 2026-07-22 | 领域心脏(物料/状态机/日历)先于 Agent 循环 | 旧项目教训:基建齐全但两颗心脏缺失;趁零代码债先立铁律 |
| 2026-07-22 | M1 建模四决定:离散制造、批次级追溯、工位实记消耗、多租户即刻进模型 | 用户拍板(均采纳推荐);实记保证谱系真实,倒冲的"理论谱系"召回失真 |
| 2026-07-22 | M1 默认值:Asia/Shanghai 可配、班次是数据(种子双班制)、夜班归开班日、decimal(18,3) 无换算表、报废/返工只记账、单工厂两级层级、SMT 种子场景 | 不值得占用户决策带宽的小项,声明假设后继续 |
| 2026-07-22 | CodeQL 推迟到 M4 用页面 default setup 开启 | 现在扫模板骨架零产出;MCP Server 出现才有真攻击面;public 仓库免费 |
| 2026-07-23 | M5 embedding 改选本地 Ollama + bge-m3 | 中转站无 /v1/embeddings 路由;按拍板时声明的回退条件执行 |
| 2026-07-24 | M6 产品形态:对话为主轴,工具结果卡片嵌入对话流 | 用户拍板;"AI 领班"身份的最强表达,与传统 MES 看板差异化 |
| 2026-07-24 | M6 引入一个写工具(报工)走通 HITL 全链路 | 用户拍板;铁律 #5 的债一次还清,后续写工具只是加条目 |
