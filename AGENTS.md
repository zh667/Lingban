# AGENTS.md

> 本文件是仓库入口地图。先读取与任务直接相关的代码和文档,再执行修改。
> `docs/` 与可执行配置是事实来源;本文件不重复完整细则。

## 1. 项目快照

- **名称**:Lingban(领班)——https://github.com/zh667/Lingban
- **目标**:带事实校验的制造业 AI Copilot("AI 领班"):MES 数据查询、异常分析、批次追溯、SOP/RAG、OEE 与生产报表;全部 MES 工具同时以 MCP Server 对外暴露。
- **命名约定**:解决方案 `Lingban.sln`;命名空间 `Lingban.Domain / Lingban.Application / Lingban.Infrastructure / Lingban.Web / Lingban.Agent`;MCP Server 名 `lingban-mes`。
- **技术栈**:.NET 10(ASP.NET Core + EF Core)+ PostgreSQL 17(pgvector 镜像,.NET Aspire 容器编排,无 docker-compose);Agent 层 Microsoft Agent Framework(`Microsoft.Agents.AI`)+ 官方 MCP C# SDK;前端 Next.js + TypeScript + Tailwind(M6 才引入)。
- **骨架来源**:jasontaylordev/CleanArchitecture v10.8.0(已生成:`src/{Domain,Application,Infrastructure,Web,Agent,AppHost,ServiceDefaults,Shared,DeviceSimulator}`,Azure 资源与 Todo 模板业务已移除)。
- **可移植资产**:旧仓库 `zh667/Mes-Agent` 的工具类、FactVerifier 验证框架、i18n 双语目录、DeviceSimulator;教训清单见本仓库 `docs/reviews/2026-07-22-agent-mes-critique.md`。
- **包管理器**:NuGet(后端)、pnpm(前端)。
- **运行时版本**:.NET 10 SDK(`global.json` 钉 10.0.201,rollForward latestFeature)、Node 20+(M6 前才不需要)。
- **必需服务**:Docker(Aspire 自动拉起 `pgvector/pgvector:pg17`;功能测试同依赖);本地 Ollama(Docker 容器 `ollama`,模型 bge-m3,供 embedding;`Llm:Embedding*` 可配置替换,知识类 eval 缺它自动跳过)。
- **依赖治理**:NuGet 安全审计按 error 处理;传递依赖漏洞在 `Directory.Packages.props` 集中钉版(已开启 CentralPackageTransitivePinningEnabled)。
- **环境变量说明**:`.env.example`;LLM API key 走环境变量或 user-secrets,禁止入库。
- **默认分支**:main。
- **主代码**:`src/`;**测试**:`tests/`;**前端**:`web/`。

## 2. 真实命令(2026-07-22 实测)

- 安装:`dotnet restore Lingban.slnx`
- 开发(整套,Aspire dashboard,需 Docker):`dotnet run --project src/AppHost`
- 格式检查:`dotnet format Lingban.slnx --verify-no-changes --no-restore`
- 构建:`dotnet build Lingban.slnx --no-restore`
- 单元测试(无 Docker 可跑,分开执行,`dotnet test` 不接受多项目参数):`dotnet test tests/Domain.UnitTests --no-build` 与 `dotnet test tests/Application.UnitTests --no-build`
- 全量测试(功能/集成,需 Docker):`dotnet test Lingban.slnx --no-build`
- CI 等价检查:restore → build → format verify → test(`.github/workflows/ci.yml`,与上述命令一致)
- Agent eval(真 LLM + 真库,需 Docker 与 Llm 密钥;CI 无密钥自动跳过):`dotnet test tests/Application.FunctionalTests --filter Category=Eval`
- UI 真实验证:Playwright(webapp-testing skill;M6 起适用)
- 前端命令(M6 引入后补):`pnpm install / dev / lint / typecheck / test / build`

不得把未执行的命令描述为已通过。

## 3. 文档地图

| 任务 | 事实来源 |
| --- | --- |
| 旧项目教训与可移植资产 | `docs/reviews/2026-07-22-agent-mes-critique.md` |
| 基座选型与决策背景 | 知识库《MES Agent 另起炉灶方案》 |
| 领域模型参考 | qcadoo MES、Odoo mrp 的 schema(只抄结构,不抄代码) |
| 复杂任务计划 | `docs/plans/active/` |

只登记真实存在的文档;新增文档后在此登记,不建空壳。

## 4. 项目边界

- 依赖方向:`Domain ← Application ← Infrastructure ← Web/Agent`;Domain 不引用任何外层与具体技术。
- 前后端共享 API contract:OpenAPI + 代码生成(前端不得手写后端响应类型)。
- 用户可见文案一律走 `en-US` / `zh-CN` 双目录,同一改动补齐两种语言。
- 不修改生成目录、迁移历史文件和锁定文件。
- 优先复用现有 helper 与依赖,不创建平行实现。

## 5. MES 领域铁律

1. **没有物料就没有 MES**:生产写操作必须落物料批次(MaterialLot)与消耗记录;批次谱系必须支持正向/反向追溯(召回场景是验收标准)。
2. **时间**:存储一律 UTC;"今天"、班次、OEE 时段按工厂日历与时区切,禁止用 `DateTime.UtcNow.Date` 划天。
3. **工单状态**只能通过状态机方法转换,禁止直接赋值 `Status`;非法转换抛领域异常。
4. 超产、报废、返工是需要暴露的信号,禁止 `Math.Min` 式静默截断。
5. 模拟器数据与真实采集数据必须可区分来源,不允许混写同一套表而无标记。

## 6. Agent 铁律

1. 回答中的数字与事实必须来自工具结果并经 FactVerifier 规则校验;**校验查询必须与工具走不同代码路径**,否则等于自己核对自己。
2. 禁止表演性 AI:不伪造 thinking/流式/debug 信息;流式必须透传模型真实 token;DebugInfo 的 SQL 必须来自 EF Core 拦截器捕获的真实语句。
3. 每个新工具必须同时交付:LLM 可读的 Description、参数校验、对应 VerificationRule、至少一条 eval 用例。
4. 工具单一实现、两处暴露:进程内 Agent 循环 + MCP Server。
5. 写操作类工具默认需要 Human-in-the-loop 确认,只读工具可自动执行。

## 7. 验证要求

- Bug 修复先写能复现问题的失败测试;测试行为与边界,不测实现细节。
- UI 修改用 Playwright(webapp-testing skill)做真实页面验证,检查控制台与网络错误。
- 涉及 LLM 的修改跑对应 eval(建成前明确说明"eval 缺失"这一风险)。
- 完成前通读 `git diff`,排除调试代码、临时文件、敏感信息、无关格式化。
- 无法运行的检查,记录命令、阻塞原因和未覆盖风险。

## 8. 安全与数据

- 不读取、输出或提交密钥、`.env`、真实生产数据;LLM 调用日志不得含完整认证信息。
- 外部输入(含发给 LLM 的用户消息、上传的 SOP 文档)在边界做校验、权限检查和大小限制。
- 未经明确授权,不部署、不迁移数据、不执行外部写操作。

## 9. Git 红线

- **向特性分支追加提交前,先确认其 PR 仍为 OPEN**(`gh pr view <n> --json state`);PR 已合并的,追加必须走新分支新 PR——本仓库已两次发生修复提交搁浅在已合并分支上(PR #6 UserSecretsId、PR #9 CI 权限)。
- 不覆盖、回退用户未提交改动;不对共享分支强推。
- 未经明确要求不提交、不推送、不创建 PR。
- 一个任务一个清晰改动;提交约定 `type(scope): description`。

## 10. AI 能力与 harness

- **MCP Server `lingban-mes` 已建成**(与 Agent 循环共用同一实现):工具 `mes_get_today_work_orders` / `mes_analyze_delayed_orders` / `mes_get_defect_summary` / `mes_calculate_oee` / `mes_search_knowledge`(全部只读,返回数据+校验结论+真实 SQL)。stdio:`dotnet run --project src/McpServer`(连接串经 `ConnectionStrings__LingbanDb` 环境变量);HTTP:Web 的 `/mcp`(需 bearer)。**stdio 信任模型**:无协议鉴权/属主/限速,信任边界=本机用户与父 MCP 客户端;进程固定使用配置租户;数据库账号应使用只读角色;调用预算由客户端负责;连接串勿写入 shell 历史(用客户端配置文件或环境导入)。本机接入:`claude mcp add lingban-mes -e ConnectionStrings__LingbanDb="<连接串>" -- dotnet run --project <仓库>/src/McpServer`。
- 用户级 skills(已装):`mcp-builder`(MCP Server 施工)、`webapp-testing`(UI 验证)、`frontend-design`(操作台视觉设计,新建或改版 UI 前先调用)、`xlsx` / `docx`(报表与 SOP 语料)、`skill-creator`。
- 计划中的项目级 skill:`mes-domain`(ISA-95 词汇、班次日历、批次谱系不变量)→ 建成后放 `.claude/skills/` 并登记于此。

## 11. LLM 质量

- prompt 与工具 schema 是受版本控制的 contract;变更必须通过 eval(eval 目录与命令建成后登记在第 2 节)。
- LLM/Agent eval 与软件测试分开维护:软件测试验证代码行为,eval 验证模型输出稳定达标。
- 模型输出的数字与事实必须可溯源到工具结果:机制即第 6 节的 FactVerifier + 引用检查。

## 12. 计划与交付

- **动工前的决策关卡**:开始新功能/新里程碑前,先识别"用户的选择会改变走向"的决策点(产品语义、领域建模、面向谁、数据来源、成本取舍),以**带推荐项的选项**形式请用户拍板后再动工;工程惯例、已批规划内的事项、审查修复方向不设关卡,声明假设并继续。每次拍板记入规划的决策记录表。
- 复杂或跨会话任务在 `docs/plans/active/` 维护计划、进度和决策。
- 完成时用中文说明:改了什么、为什么、运行了哪些验证、结果、剩余风险;区分已验证事实、合理推断和未验证事项。

## 13. 交叉审查分诊

- 修复中含**新设计**(并发控制、安全边界、协议假设、新抽象)→ 对修复 diff 做一次**增量复审**(范围只取修复提交);修复是照方抓药(索引、校验器、配置、测试、文档)→ 一审一修直接合,不复审。
- 复审只剩"建议"级发现 → 该审查链条终止,合并。
- 依据:本仓库实测,一审修复中的新设计两次被复审抓出阻断级缺陷(幂等竞态、闸门漏报工)。

## 14. 规则维护

- 重复错误优先固化为测试、lint、CI 或脚本,而不是扩写本文件。
- 细则下沉 `docs/`;命令、架构变化时同一改动内更新本文件;定期删除失效规则。
