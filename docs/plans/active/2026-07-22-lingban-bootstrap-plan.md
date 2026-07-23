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
| Todo 模板业务移除(#12 前半) | **M2 收尾** | 真实端点可替代演示时 | 模板遗留;M2 后它是唯一无租户过滤的业务面 |
| 复合租户外键 (TenantId, Id) + 迁移(#5 完整) | **M4(MCP 对外暴露前)** | 第二个真实租户进系统,或任何外部可写入口上线 | MCP Server 是首个外部写入面;单 default 租户期域内校验已挡主要路径 |
| Identity 用户-租户 membership(#12 后半) | **M4(MCP 鉴权边界)或鉴权接入时** | 出现第二个用户/租户 | 与 MCP 的租户上下文解析是同一件事 |
| regex 守卫升级 Roslyn analyzer(#11 完整) | 触发式,无固定里程碑 | 守卫被真实绕过一次 | 生长纪律:等真实事故,不预先镀金 |

**还债进度(2026-07-23,M2 主体 PR)**:跨工单环检测、消耗幂等键、OEE 班次区间、Complete 前置校验四条已还,均有回归测试;Todo 移除待 M2 收尾 PR;其余按表内时机执行。

**核实后不改**:Cancel 不回滚消耗(消耗仅限 InProgress、Cancel 仅限开工前,路径互斥,Codex 亦确认);08:00 边界归属正确([start,end) 半开区间)。

## 里程碑 2:应用服务与工具层(移植 + 适配)

**目标**:从旧仓库 `zh667/Mes-Agent` 移植可用资产,按新领域模型适配,消灭旧项目的"造假点"。

- [x] 写路径命令(创建/下达/开工/取消/消耗/报工/产出/完工)——三条 M1 债的落点:幂等键(EventId + 过滤唯一索引)、跨工单环检测(谱系祖先遍历)、完工前置校验(有消耗、有产出、产出=报工)。
- [x] 工具组重写为 Application 查询(移植思路、不搬旧病):GetTodayWorkOrders(班次切分生产日)、AnalyzeDelayedOrders、GetDefectSummary、CalculateOee。
- [x] CalculateOee 计划时间用班次区间集合(债 #9 还清);Performance/Quality 产线级归因并在 Attribution 字段如实标注,数据不足时为 null 不硬凑。
- [x] FactVerifier 框架重写:规则经 VerificationQueryService(原生 SQL,显式租户条件)复核,与工具 LINQ 管道零共享;篡改结果被独立路径抓出的测试。
- [x] SqlCaptureInterceptor + IQueryLog:真实执行的 SQL 进作用域日志,供 M3 debug 面板使用;禁止手写 SQL 字符串。
- [x] Equipment / EquipmentStatusRecord / DowntimeRecord 实体(M1 推迟项),采集事实带 DataSource 来源标记(领域铁律 #5)。
- [ ] **M2 收尾(下一个 PR)**:DeviceSimulator 移植(写入打 Simulated 标记)+ Todo 模板业务移除。
- 调整:知识库工具组推迟到 M5(向量基建在那里,现在移植是空壳);i18n 双语目录推迟到 M6(当前无用户可见界面文案,工具 DTO 是结构化数据)。

**验收**:每个工具查询有功能测试 + 对应 VerificationRule 且测试含"篡改被抓"路径;`dotnet test` 全绿;grep 不到手写的假 SQL。当前 70/70 绿(收尾两项待办)。

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
| #11 余下:设备事实的受控工厂方法 + 写入侧重叠拒绝 | M2 收尾(模拟器 PR) | 模拟器落地即触发 |
| #12:QueryLog 工具级边界(checkpoint/关联 ID) | M3,DebugInfo 消费端落地时 | 同一作用域出现多工具调用 |
| #14:工具 LLM contract(Description/schema/eval)与 HITL | M3 注册时 | 即 AGENTS.md 铁律 #3 的交付时点,口径已在 M2 小节修正 |
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

- [ ] 接入 Microsoft Agent Framework(`Microsoft.Agents.AI`),注册全部工具(Description 即 function schema);模型商可配置(Claude 优先)。
- [ ] 删除"模式由用户选"的设计:LLM 自主选择工具,AgentMode 降级为可选提示。
- [ ] SSE 透传模型真实 token 流;工具结果 → FactVerifier → 校验结论随消息返回并持久化。
- [ ] 会话上下文窗口真实喂给 LLM(多轮对话)。
- [ ] 写操作类工具走 Human-in-the-loop 确认;只读工具自动执行。
- [ ] **最小 eval 集**:每个工具至少 1 条"中文自然语言问题 → 应选中该工具且数字经校验"的用例;eval 命令写进 AGENTS.md 第 2 节。

**验收**:"3号线今天延期的工单有哪些?为什么?"→ LLM 正确选择工具(不把 3 当设备 ID)→ 校验通过 → 真流式中文回答;eval 全绿。

## 里程碑 4:MCP Server(单一实现,两处暴露)

**目标**:Lingban 成为 AI 生态里的制造业数据源。

- [ ] 先调用 `mcp-builder` skill,再动工;用官方 MCP C# SDK 把工具集暴露为 `lingban-mes` Server(stdio + HTTP 两种传输)。
- [ ] 工具实现与进程内 Agent 循环共用同一层,禁止两套逻辑。
- [ ] 鉴权与租户上下文在 MCP 边界处理。

**验收**:本机 Claude Code 配置 `lingban-mes` 后,直接问"今天有几张工单"能得到经校验的真实数据;AGENTS.md 第 10 节登记工具清单。

## 里程碑 5:知识库与真 RAG

**目标**:SOP/维护手册问答,生成与引用都是真的。

- [ ] docx/pdf 解析入库(docx skill 造测试语料)、分块、OpenAI/可配置 embedding、pgvector 检索(复用旧仓库 PgVectorStore 思路)。
- [ ] 生成 = LLM 基于检索上下文作答(删除旧项目"top-1 截断拼前缀"的假生成);引用标注 + KnowledgeCitation 校验规则(引用必须真实存在于检索结果)。

**验收**:上传一份 SOP → 提问 → 带真实引用的回答;引用校验规则测试通过;无上下文时明确说"知识库没有",不编。

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
