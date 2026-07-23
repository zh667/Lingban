import { defineConfig } from "@playwright/test";

// E2E 前置(本地;两个服务不由 Playwright 拉起,见 web/e2e/README.md):
//   1. pgvector 容器 + Web(Development,Llm:Mode=scripted)监听 5199
//   2. next dev 监听 3000,NEXT_PUBLIC_API_BASE=http://localhost:5199
export default defineConfig({
  testDir: "./e2e",
  timeout: 60_000,
  use: {
    baseURL: process.env.E2E_BASE_URL ?? "http://localhost:3000",
    screenshot: "only-on-failure",
  },
});
