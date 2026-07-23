"use client";

import { useRef, useState } from "react";
import { t, type Locale } from "@/lib/i18n";
import type { components } from "@/lib/api/schema";

const API = process.env.NEXT_PUBLIC_API_BASE ?? "http://localhost:5000";

// REST 请求/响应类型一律来自 OpenAPI 生成(AGENTS.md §4);SSE 事件载荷不在 OpenAPI 内,在下方手工声明。
type LoginRequest = components["schemas"]["LoginRequest"];
type AccessTokenResponse = components["schemas"]["AccessTokenResponse"];
type ChatRequest = components["schemas"]["ChatRequest"];
type ConfirmRequest = components["schemas"]["ConfirmRequest"];

type Verification = { status: string; summary: string };
type ToolResult = {
  callId: string; tool: string; data: unknown; verification: Verification;
  toolSql: string[]; verificationSql: string[]; elapsedMs: number;
};
type Hitl = { actionId: number; actionType: string; summary: string; state?: "approved" | "rejected"; error?: string };
type Audit = { passed: boolean; unverifiedNumbers: string[]; nonVerifiedTools: string[]; invalidCitations: string[] };
type Turn = {
  role: "user" | "assistant"; text: string;
  tools: ToolResult[]; hitl: Hitl[]; audit?: Audit; error?: string;
  // 语义澄清(九审 #1):幂等键只挡"同一次提交"的传输级重复(双击/代理重发);
  // 服务端对同键一律拒绝,所以"重发"是带新键的新一次提交,不是断点续传。
  retryMessage?: string;
};

// SSE 按行解析(八审 #7):支持 LF/CRLF、多行 data(按规范以 \n 拼接)、注释行与跨 chunk 残帧。
function parseSseLines(
  lines: string[],
  pending: { event: string; data: string[] },
  emit: (event: string, data: string) => void,
) {
  for (const raw of lines) {
    const line = raw.endsWith("\r") ? raw.slice(0, -1) : raw;
    if (line === "") {
      if (pending.data.length > 0) emit(pending.event || "message", pending.data.join("\n"));
      pending.event = "";
      pending.data = [];
    } else if (line.startsWith(":")) {
      // 注释帧,忽略。
    } else if (line.startsWith("event:")) {
      pending.event = line.slice(6).trimStart();
    } else if (line.startsWith("data:")) {
      pending.data.push(line.slice(5).replace(/^ /, ""));
    }
  }
}

const andonOf = (turn: Turn): "green" | "amber" | "red" => {
  if (turn.error || turn.audit?.passed === false) return "red";
  const worst = turn.tools.map((x) => x.verification.status);
  if (worst.includes("Discrepancy") || worst.includes("Failed")) return "red";
  if (worst.includes("Unverified") || turn.hitl.some((h) => !h.state)) return "amber";
  return "green";
};

export default function Home() {
  const locale: Locale = "zh-CN";
  const m = t(locale);
  const [token, setToken] = useState<string | null>(null);
  const [email, setEmail] = useState("administrator@localhost");
  const [password, setPassword] = useState("");
  const [loginError, setLoginError] = useState(false);
  const [turns, setTurns] = useState<Turn[]>([]);
  const [input, setInput] = useState("");
  const [busy, setBusy] = useState(false);
  const conversationId = useRef<number | null>(null);

  const login = async (e: React.FormEvent) => {
    e.preventDefault();
    setLoginError(false);
    const res = await fetch(`${API}/api/Users/login`, {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ email, password } satisfies LoginRequest),
    });
    if (!res.ok) { setLoginError(true); return; }
    const tokens: AccessTokenResponse = await res.json();
    setToken(tokens.accessToken);
  };

  // 更新函数必须纯(StrictMode 双调用):只重建,不原地改。
  const patch = (fn: (last: Turn) => Turn) =>
    setTurns((prev) => [...prev.slice(0, -1), fn(prev[prev.length - 1])]);

  // 单条消息的完整流式回合;retry 时传入原键复用幂等语义。
  const streamTurn = async (message: string, clientKey: string) => {
    setBusy(true);
    // 流异常必须闭合(八审 #6):任何 fetch/读取/解析失败都标红本回合并归还输入框。
    let terminal = false; // 收到 done 或 error 才算正常终止;裸 EOF 也是失败。
    try {
      const res = await fetch(`${API}/api/agentchat/chat`, {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
        body: JSON.stringify({
          conversationId: conversationId.current, message,
          clientMessageId: clientKey,
        } satisfies ChatRequest),
      });
      if (!res.ok || !res.body) {
        patch((x) => ({ ...x, error: m.streamError, retryMessage: message }));
        return;
      }

      const handle = (ev: string, dataRaw: string) => {
        const data = JSON.parse(dataRaw);
        if (ev === "token") patch((x) => ({ ...x, text: x.text + data.text }));
        else if (ev === "tool_result") patch((x) => ({ ...x, tools: [...x.tools, data] }));
        else if (ev === "hitl_pending") patch((x) => ({ ...x, hitl: [...x.hitl, data] }));
        else if (ev === "answer_audit") patch((x) => ({ ...x, audit: data }));
        else if (ev === "error") {
          terminal = true;
          // 服务端错误(含模型流失败)同样给重发入口(九审 #1 场景一)。
          patch((x) => ({ ...x, error: data.message, retryMessage: message }));
        }
        else if (ev === "done") { terminal = true; conversationId.current = data.conversationId; }
      };

      const reader = res.body.getReader();
      const decoder = new TextDecoder();
      let buffer = "";
      const pending = { event: "", data: [] as string[] };
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split("\n");
        buffer = lines.pop() ?? "";
        parseSseLines(lines, pending, handle);
      }
      buffer += decoder.decode();
      parseSseLines([...buffer.split("\n"), ""], pending, handle);

      if (!terminal) {
        patch((x) => ({ ...x, error: m.streamError, retryMessage: message }));
      }
    } catch {
      patch((x) => ({ ...x, error: m.streamError, retryMessage: message }));
    } finally {
      setBusy(false);
    }
  };

  const send = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!input.trim() || busy || !token) return;
    const message = input.trim();
    setInput("");
    setTurns((prev) => [...prev,
      { role: "user", text: message, tools: [], hitl: [] },
      { role: "assistant", text: "", tools: [], hitl: [] }]);
    await streamTurn(message, crypto.randomUUID());
  };

  const retry = async (turnIndex: number) => {
    const failed = turns[turnIndex];
    if (busy || !failed.retryMessage) return;
    const { retryMessage } = failed;
    // 重发 = 新键的新一次提交(九审 #1):服务端对同键一律拒绝,复用旧键只会得到 DUPLICATE。
    // 幂等结果重放(同键返回既有结果)记为债,触发=需要断点续传时。
    setTurns((prev) => prev.map((turn, i) =>
      i !== turnIndex ? turn : { role: "assistant", text: "", tools: [], hitl: [] }));
    await streamTurn(retryMessage, crypto.randomUUID());
  };

  const confirm = async (turnIndex: number, hitlIndex: number, approve: boolean) => {
    const action = turns[turnIndex].hitl[hitlIndex];
    const setHitl = (change: Partial<Hitl>) =>
      setTurns((prev) => prev.map((turn, i) => i !== turnIndex ? turn : {
        ...turn,
        hitl: turn.hitl.map((h, j) => j !== hitlIndex ? h : { ...h, ...change }),
      }));

    try {
      const res = await fetch(`${API}/api/agentchat/actions/${action.actionId}/confirm`, {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
        body: JSON.stringify({ approve } satisfies ConfirmRequest),
      });
      if (res.ok) {
        setHitl({ state: approve ? "approved" : "rejected", error: undefined });
        return;
      }
      // 失败不静默(八审 #9):409=已处理,403=无写权限,其余给通用提示。
      const problem = await res.json().catch(() => null);
      setHitl({
        error: problem?.detail
          ?? (res.status === 409 ? m.confirmConflict : res.status === 403 ? m.confirmForbidden : m.confirmFailed),
      });
    } catch {
      setHitl({ error: m.confirmFailed });
    }
  };

  if (!token) {
    return (
      <main className="min-h-screen flex items-center justify-center p-6">
        <form onSubmit={login} className="tool-card p-8 w-full max-w-sm space-y-4">
          <h1 className="brand text-3xl">{m.appName}</h1>
          <p className="text-sm" style={{ color: "var(--text-dim)" }}>{m.tagline}</p>
          <label className="block text-sm">{m.email}
            <input value={email} onChange={(e) => setEmail(e.target.value)} type="email"
              className="mt-1 w-full rounded-sm bg-[var(--bg)] border border-[#2c3747] p-2" />
          </label>
          <label className="block text-sm">{m.password}
            <input value={password} onChange={(e) => setPassword(e.target.value)} type="password"
              className="mt-1 w-full rounded-sm bg-[var(--bg)] border border-[#2c3747] p-2" />
          </label>
          {loginError && <p className="andon-red text-sm">{m.loginFailed}</p>}
          <button className="w-full py-2 rounded-sm font-medium" style={{ background: "var(--brass)", color: "#171d26" }}>
            {m.login}
          </button>
        </form>
      </main>
    );
  }

  return (
    <main className="min-h-screen max-w-3xl mx-auto flex flex-col p-4">
      <header className="py-3 flex items-baseline gap-3">
        <h1 className="brand text-xl">{m.appName}</h1>
        <span className="text-xs" style={{ color: "var(--text-dim)" }}>{m.tagline}</span>
      </header>

      <section className="flex-1 space-y-6 py-4" aria-live="polite">
        {turns.length === 0 && (
          <p className="brand text-lg leading-relaxed opacity-80">{m.emptyState}</p>
        )}
        {turns.map((turn, ti) =>
          turn.role === "user" ? (
            <p key={ti} className="ml-auto max-w-[85%] w-fit rounded-md px-3 py-2 text-sm"
              style={{ background: "var(--panel-2)" }}>{turn.text}</p>
          ) : (
            <div key={ti} className="msg-assistant space-y-3" data-andon={andonOf(turn)}>
              {turn.tools.map((tool, i) => (
                <details key={i} className="tool-card px-3 py-2 text-xs">
                  <summary className="flex items-center gap-2">
                    <span className={
                      tool.verification.status === "Verified" ? "andon-green" :
                      tool.verification.status === "Unverified" ? "andon-amber" : "andon-red"}>●</span>
                    <span className="font-medium">{tool.tool}</span>
                    <span className="badge">{
                      tool.verification.status === "Verified" ? m.verified :
                      tool.verification.status === "Discrepancy" ? m.discrepancy :
                      tool.verification.status === "Unverified" ? m.unverified : m.failedCheck}</span>
                    <span style={{ color: "var(--text-dim)" }}>{m.elapsed} {tool.elapsedMs}ms</span>
                  </summary>
                  <div className="mt-2 space-y-2">
                    <p style={{ color: "var(--text-dim)" }}>{tool.verification.summary}</p>
                    <p className="font-medium">{m.toolSql}</p>
                    {tool.toolSql.map((sql, k) => <pre key={k} className="sql">{sql}</pre>)}
                    <p className="font-medium">{m.verificationSql}</p>
                    {tool.verificationSql.map((sql, k) => <pre key={k} className="sql">{sql}</pre>)}
                  </div>
                </details>
              ))}
              {turn.text && <p className="whitespace-pre-wrap leading-relaxed">{turn.text}</p>}
              {turn.hitl.map((h, hi) => (
                <div key={hi} className="hitl-card p-3 text-sm space-y-2">
                  <p className="brand">{m.confirmTitle}</p>
                  <p>{h.summary}</p>
                  {h.state ? (
                    <p className={h.state === "approved" ? "andon-green" : "andon-red"}>
                      {h.state === "approved" ? m.actionApproved : m.actionRejected}
                    </p>
                  ) : (
                    <div className="space-y-2">
                      {/* 流未终结前不开闸(八审 #9):等审计与 done 落定,写操作不与半截回合绑定。 */}
                      <div className="flex gap-2">
                        <button onClick={() => confirm(ti, hi, true)} disabled={busy}
                          className="px-3 py-1 rounded-sm disabled:opacity-40"
                          style={{ background: "var(--brass)", color: "#171d26" }}>
                          {m.approve}
                        </button>
                        <button onClick={() => confirm(ti, hi, false)} disabled={busy}
                          className="px-3 py-1 rounded-sm border border-[#2c3747] disabled:opacity-40">{m.reject}</button>
                      </div>
                      {busy && <p className="text-xs" style={{ color: "var(--text-dim)" }}>{m.confirmWaitStream}</p>}
                      {h.error && <p className="andon-red text-xs">{h.error}</p>}
                    </div>
                  )}
                </div>
              ))}
              {turn.audit && !turn.audit.passed && (
                <p className="andon-red text-xs">
                  {m.auditFailed}:{turn.audit.unverifiedNumbers.length > 0 && `${m.unverifiedNumbers} ${turn.audit.unverifiedNumbers.join(", ")}`}
                  {turn.audit.invalidCitations.length > 0 && ` ${m.invalidCitations} ${turn.audit.invalidCitations.join(", ")}`}
                </p>
              )}
              {turn.error && (
                <div className="flex items-center gap-3">
                  <p className="andon-red text-sm">{turn.error}</p>
                  {turn.retryMessage && ti === turns.length - 1 && !busy && (
                    <button onClick={() => retry(ti)}
                      className="px-2 py-0.5 text-xs rounded-sm border border-[#2c3747]">{m.retry}</button>
                  )}
                </div>
              )}
            </div>
          )
        )}
      </section>

      <form onSubmit={send} className="sticky bottom-0 py-3 flex gap-2" style={{ background: "var(--bg)" }}>
        <input value={input} onChange={(e) => setInput(e.target.value)}
          placeholder={m.inputPlaceholder} maxLength={4000} disabled={busy}
          className="flex-1 rounded-sm bg-[var(--panel)] border border-[#2c3747] px-3 py-2 text-sm" />
        <button disabled={busy || !input.trim()}
          className="px-4 rounded-sm font-medium disabled:opacity-40"
          style={{ background: "var(--brass)", color: "#171d26" }}>{m.send}</button>
      </form>
    </main>
  );
}
