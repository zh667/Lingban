"use client";

import { useRef, useState } from "react";
import { t, type Locale } from "@/lib/i18n";

const API = process.env.NEXT_PUBLIC_API_BASE ?? "http://localhost:5000";

type Verification = { status: string; summary: string };
type ToolResult = {
  callId: string; tool: string; data: unknown; verification: Verification;
  toolSql: string[]; verificationSql: string[]; elapsedMs: number;
};
type Hitl = { actionId: number; actionType: string; summary: string; state?: "approved" | "rejected" };
type Audit = { passed: boolean; unverifiedNumbers: string[]; nonVerifiedTools: string[]; invalidCitations: string[] };
type Turn = {
  role: "user" | "assistant"; text: string;
  tools: ToolResult[]; hitl: Hitl[]; audit?: Audit; error?: string;
};

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
      body: JSON.stringify({ email, password }),
    });
    if (!res.ok) { setLoginError(true); return; }
    setToken((await res.json()).accessToken);
  };

  const patch = (fn: (last: Turn) => void) =>
    setTurns((prev) => { const next = [...prev]; fn(next[next.length - 1]); return next; });

  const send = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!input.trim() || busy || !token) return;
    const message = input.trim();
    setInput("");
    setBusy(true);
    setTurns((prev) => [...prev,
      { role: "user", text: message, tools: [], hitl: [] },
      { role: "assistant", text: "", tools: [], hitl: [] }]);

    const res = await fetch(`${API}/api/agentchat/chat`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
      body: JSON.stringify({
        conversationId: conversationId.current, message,
        clientMessageId: crypto.randomUUID(),
      }),
    });
    if (!res.ok || !res.body) { patch((x) => { x.error = m.streamError; }); setBusy(false); return; }

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      const frames = buffer.split("\n\n");
      buffer = frames.pop() ?? "";
      for (const frame of frames) {
        const ev = /^event: (.+)$/m.exec(frame)?.[1];
        const dataRaw = /^data: (.+)$/m.exec(frame)?.[1];
        if (!ev || !dataRaw) continue;
        const data = JSON.parse(dataRaw);
        if (ev === "token") patch((x) => { x.text += data.text; });
        else if (ev === "tool_result") patch((x) => { x.tools.push(data); });
        else if (ev === "hitl_pending") patch((x) => { x.hitl.push(data); });
        else if (ev === "answer_audit") patch((x) => { x.audit = data; });
        else if (ev === "error") patch((x) => { x.error = data.message; });
        else if (ev === "done") { conversationId.current = data.conversationId; }
      }
    }
    setBusy(false);
  };

  const confirm = async (turnIndex: number, hitlIndex: number, approve: boolean) => {
    const action = turns[turnIndex].hitl[hitlIndex];
    const res = await fetch(`${API}/api/agentchat/actions/${action.actionId}/confirm`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
      body: JSON.stringify({ approve }),
    });
    if (res.ok) setTurns((prev) => {
      const next = [...prev];
      next[turnIndex].hitl[hitlIndex].state = approve ? "approved" : "rejected";
      return next;
    });
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
                    <div className="flex gap-2">
                      <button onClick={() => confirm(ti, hi, true)}
                        className="px-3 py-1 rounded-sm" style={{ background: "var(--brass)", color: "#171d26" }}>
                        {m.approve}
                      </button>
                      <button onClick={() => confirm(ti, hi, false)}
                        className="px-3 py-1 rounded-sm border border-[#2c3747]">{m.reject}</button>
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
              {turn.error && <p className="andon-red text-sm">{turn.error}</p>}
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
