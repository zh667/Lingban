# API contract(生成物,勿手改)

- `openapi.json`:后端 OpenAPI 快照。刷新方式(Production 环境启动不碰数据库,连接串随意):

  ```bash
  ASPNETCORE_ENVIRONMENT=Production \
  ConnectionStrings__LingbanDb="Host=localhost;Database=fake;Username=x;Password=y" \
  dotnet run --project src/Web --no-launch-profile --urls http://localhost:5199 &
  curl -sf http://localhost:5199/openapi/v1.json -o web/lib/api/openapi.json
  ```

- `schema.d.ts`:`pnpm gen:api` 从快照生成。前端 REST 请求/响应类型只能从这里取(AGENTS.md §4);
  SSE 事件载荷不在 OpenAPI 描述范围内,允许在消费处手工声明。
- 后端改了端点或 DTO:刷新快照 + 重新生成,同一提交内落库。
