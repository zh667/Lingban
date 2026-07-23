import { test, expect } from "@playwright/test";

// 关键路径 E2E(M6 验收):登录 → 提问 → 流式回答 → 校验标识可见 → HITL 确认。
// 后端跑 Llm:Mode=scripted(确定性脚本模型,Development 限定);
// 工具执行、事实校验、答案审计、HITL 与 SSE 管道全部是真实路径。

test("登录 → 只读问答:工具卡与已复核徽章、绿灯柱", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel(/密码|Password/).fill("Administrator1!");
  await page.getByRole("button", { name: /登录|Sign in/ }).click();

  const input = page.getByPlaceholder(/问车间任何事/);
  await expect(input).toBeVisible();

  // 八审 #11 回归钉:scripted 演示模式必须有可见标识(E2E 后端固定跑 scripted)。
  await expect(page.getByTestId("scripted-banner")).toBeVisible();

  await input.fill("今天有哪些工单?");
  await page.getByRole("button", { name: "发送" }).click();

  const assistant = page.locator(".msg-assistant").last();
  await expect(page.locator(".tool-card").first()).toBeVisible({ timeout: 30_000 });
  await expect(page.locator(".badge").first()).toHaveText("已复核");
  await expect(assistant).toContainText("独立校验", { timeout: 30_000 });
  await expect(assistant).toHaveAttribute("data-andon", "green");

  // 每次工具调用恰好一张卡(StrictMode 双调用回归钉),展开可见真实 SQL 分段。
  await expect(page.locator(".tool-card")).toHaveCount(1);
  await page.locator(".tool-card summary").first().click();
  await expect(page.locator(".tool-card").first()).toContainText("工具 SQL");
  await expect(page.locator(".tool-card").first()).toContainText("校验 SQL");
});

test("HITL:报工提议只挂起,批准后才执行", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel(/密码|Password/).fill("Administrator1!");
  await page.getByRole("button", { name: /登录|Sign in/ }).click();

  const input = page.getByPlaceholder(/问车间任何事/);
  await input.fill("给工单 WO-SEED-03 报工 5 件");
  await page.getByRole("button", { name: "发送" }).click();

  const card = page.locator(".hitl-card").last();
  await expect(card).toBeVisible({ timeout: 30_000 });
  await expect(card).toContainText("待确认的写操作");
  // 挂起中的写操作 → 黄灯柱,回答明确说未执行。
  await expect(page.locator(".msg-assistant").last()).toHaveAttribute("data-andon", "amber");
  await expect(page.locator(".msg-assistant").last()).toContainText("确认");

  await card.getByRole("button", { name: "确认执行" }).click();
  await expect(card).toContainText("已执行", { timeout: 15_000 });
});
