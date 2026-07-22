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

## 里程碑 2:应用服务与工具层(移植 + 适配)

**目标**:从旧仓库 `zh667/Mes-Agent` 移植可用资产,按新领域模型适配,消灭旧项目的"造假点"。

- [ ] 移植四组工具(生产/质量/OEE/知识库)与对应服务,适配新实体;OEE 与"今日工单"改走工厂日历。
- [ ] 移植 FactVerifier 框架;**校验查询走独立代码路径**(独立的 VerificationQueryService,禁止复用工具同一查询)。
- [ ] DebugInfo 的 SQL 改由 EF Core 拦截器捕获真实语句,删除一切手写 SQL 字符串。
- [ ] 移植 i18n 双语目录与 DeviceSimulator(模拟器数据打来源标记)。

**验收**:每个工具有单元测试 + 对应 VerificationRule;`dotnet test` 全绿;grep 不到手写的假 SQL。

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
