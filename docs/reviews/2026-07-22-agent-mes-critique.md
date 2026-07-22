# MES Copilot 锐评(2026-07-22,Claude Code)

> 基于 commit 7b4d950 的代码通读:AgentController、ConversationService、四个 Agent Plugin、
> Verification 规则、Domain 实体、VectorStore。每条批评均附出处。

## 一、Agent 角度:这不是 Agent,这是穿着 Agent 皮的 switch-case

**1. 整个系统里没有 LLM。一行都没有。**
README 声称 "intelligent manufacturing operations Agent platform",但 chat 的完整调用链是:
`AgentController.ExecuteToolAsync()` → `request.Mode` 的 switch → `ContainsAny(message, "delay", "延期")`
关键词匹配 → 固定工具 → 模板字符串(`AgentController.cs:306-392`)。没有规划、没有多步工具调用、
没有上下文推理——"Agent" 三大件一件没有。

**2. 假流式,表演性 AI。**
`AgentController.cs:102` 先发 `thinking: "Selecting MES tool"` SSE 事件——背后是一个正则在跑。
`AgentController.cs:136-140` 把算好的模板字符串用 `ChunkText(text, 24)` 切成 24 字符伪装 token 流。
给用户表演一个不存在的大模型正在打字。

**3. BotSharp 是挂件。**
csproj 引用了 BotSharp.Core / BotSharp.Abstraction,全库 grep 代码里零引用。
README 里 "BotSharp-style Agent Plugins" 的 "style" 一词扛下了所有。

**4. "RAG" 的 G 是假的。**
Embedding 是真 OpenAI API(`OpenAiEmbeddingClient.cs`),pgvector 检索是真的,"生成"是
`ContextualRagAnswerGenerator.cs:24-26`:取 top-1、截断 600 字符、拼上 "Based on {title}: "。
这不是 RAG,是带余弦相似度的 grep。

**5. FactVerifier 是给不存在的病人准备的药。**
`DelayedOrdersVerificationRule`:工具从数据库查出 totalCount,验证器再查同一个数据库核对同一个数字,
永远通过。这套防线防的是 LLM 幻觉——而系统里没有 LLM。
(公道话:等真接了 LLM,这个架子会立刻有价值,是全项目最超前的设计。)

**6. Debug 信息造假。**
`GetTodayWorkOrdersTool.cs` 的 `DebugInfo.SqlExecuted` 是手写的假 SQL,不是 EF Core 实际执行的语句。

**7. 意图识别玻璃级脆弱。**
- `ExtractFirstPositiveInteger` 把消息里第一个数字当 equipmentId:"3号线今天效率怎么样" → 查 equipment id=3。
- 消息含 "why" → 直接触发 5-Why,整句话塞进 symptomDescription(`AgentController.cs:332-338`)。
- `GetContextWindowAsync`(`ConversationService.cs:90`)认真实现了上下文窗口,chat 路径从未调用——
  每一轮都是无状态金鱼,对话历史只为侧边栏展示而存。

**8. Agent 的第一件事(路由)被外包给了用户。**
`AgentMode` 由前端传入,用户自己选 Production/Quality/OEE/Knowledge。

## 二、MES 角度:一个没有物料的制造执行系统

**1. 没有物料,就没有 MES。**
Domain 里有 WorkOrder、ProcessRoute、Equipment、DefectRecord,唯独没有 material lot、物料消耗记录、
库存事务。没有 lot-to-lot 消耗链(genealogy),`TraceBatchTool` 的"追溯"追不了召回。
追溯是 MES 的命根子,这里是空的。

**2. UTC 日期切"今天"。**
`DateTime.UtcNow.Date` 划天(`AgentController.cs:105`),没有班次日历、工厂时区、倒班切分。
中国工厂早上八点前的产量全算昨天;倒班工厂的 OEE 可用时间基数从根上是错的。

**3. 工单状态机没有守卫。**
`WorkOrderStatus` 是可随意赋值的 enum,Completed 可以改回 NotScheduled。MES 的本质是执行纪律。
`Progress` 用 `Math.Min(1m, ...)` 把超产静默截断成 100%——超产是需要暴露的信号,不是需要抹平的毛刺。

**4. 排程工具是复读机。**
`SuggestScheduleTool` 调用时固定 `productionLineId: null`、`UtcNow` 到 +7 天
(`AgentController.cs:311-315`),问什么都返回同一份全厂七日排程。

**5. 优先级倒挂。**
Phase 3 上了企业级多租户(表达式树全局过滤器,手艺不错)、读副本 compose、
OPC UA + Modbus + MQTT 三协议、审计、i18n——城墙固若金汤,城堡中央的"大脑"是一个 if-else。
典型简历驱动开发路径:难而有面子的都做了,难而没面子的(LLM 编排、物料模型)绕开了。

## 三、总评

精致的货物崇拜样本:Agent 目录、Plugins、Prompts、Verification、SSE、thinking 事件——
LLM 应用该有的跑道、塔台、指挥灯全造好了,唯独天上没有飞机。`Prompts/MesPromptBuilder` 里
躺着 prompt 构建器,而全系统没有任何模型会来读它,这是全项目最好的隐喻。

公道话:分层干净、测试认真、审计/ProblemDetails/i18n 纪律在线,工程素养是真的好。
问题不是写得烂,而是它声称的两个身份各缺了心脏:"Agent" 缺 LLM,"MES" 缺物料。
把 `ExecuteToolAsync` 换成真的 LLM tool-use 循环(工具的 Name/Description 都写好了,
天生就是 function schema),补上物料批次和消耗记录,就能从"演 Agent"变成"是 Agent"。
骨架已经配得上灵魂了,把灵魂装进去吧。
