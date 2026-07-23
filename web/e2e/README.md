# E2E(Playwright)

本地三件套(CI 暂不跑 E2E,只跑 lint/build;E2E 属发版前手动关卡):

```bash
# 1. 数据库
docker run -d --name lingban-e2e-db -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=lingban \
  -p 5499:5432 pgvector/pgvector:pg17

# 2. 后端(Development + 脚本模型;不依赖外部中转站)
ASPNETCORE_ENVIRONMENT=Development \
ConnectionStrings__LingbanDb="Host=localhost;Port=5499;Database=lingban;Username=postgres;Password=postgres" \
Llm__Mode=scripted \
dotnet run --project ../src/Web --no-launch-profile --urls http://localhost:5199

# 3. 前端 + 测试
NEXT_PUBLIC_API_BASE=http://localhost:5199 pnpm dev
pnpm test:e2e
```

`Llm:Mode=scripted` 是 Development 限定的确定性脚本模型(非 Development 拒绝启动):
工具执行、事实校验、答案审计、HITL、SSE 全部真实路径,只有"模型台词"是固定的。
想验真模型,去掉 `Llm__Mode` 用 user-secrets 里的中转站配置即可,断言不变。
