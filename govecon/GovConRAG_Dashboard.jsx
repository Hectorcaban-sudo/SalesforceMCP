import { useState, useEffect, useRef } from "react";

const NAV = [
  { id: "dashboard", icon: "⬡", label: "Dashboard" },
  { id: "ingestion", icon: "⬢", label: "Ingestion" },
  { id: "query", icon: "◈", label: "Query" },
  { id: "agents", icon: "◉", label: "Agents" },
  { id: "rfp", icon: "◎", label: "RFP / Proposals" },
  { id: "performance", icon: "◇", label: "Performance" },
  { id: "audit", icon: "▣", label: "Audit Log" },
];

const MOCK_DOCS = [
  { id: 1, title: "GSA IDIQ Contract FY2025", source: "SharePoint", domain: "contracts", status: "Indexed", size: "1.2MB", chunks: 142, ts: "2m ago" },
  { id: 2, title: "Q1 Invoice Reconciliation", source: "SharePoint", domain: "accounts", status: "Indexed", size: "540KB", chunks: 62, ts: "5m ago" },
  { id: 3, title: "CPARS Review — DHS PMO", source: "Database", domain: "performance", status: "Processing", size: "88KB", chunks: 0, ts: "just now" },
  { id: 4, title: "SOW Template Library", source: "Excel", domain: "contracts", status: "Indexed", size: "320KB", chunks: 38, ts: "12m ago" },
  { id: 5, title: "Past Perf Reference — DoD", source: "SharePoint", domain: "performance", status: "Failed", size: "2.1MB", chunks: 0, ts: "18m ago" },
  { id: 6, title: "Staffing Matrix FY25", source: "Excel", domain: "operations", status: "Indexed", size: "190KB", chunks: 21, ts: "31m ago" },
];

const MOCK_QUERIES = [
  { q: "What are the key deliverables on the DHS contract?", agent: "Contracts", ms: 1240, ts: "1m ago" },
  { q: "Show past performance for NAICS 541511", agent: "Performance", ms: 2880, ts: "4m ago" },
  { q: "Summarize Q1 invoice discrepancies", agent: "Accounts", ms: 990, ts: "8m ago" },
  { q: "Who are likely competitors on the HHS SPARC recompete?", agent: "Competitor", ms: 3120, ts: "15m ago" },
];

const MOCK_AUDIT = [
  { event: "Ingest.Completed", actor: "admin", resource: "GSA IDIQ Contract", outcome: "Success", ts: "2m ago" },
  { event: "Agent.Query", actor: "jsmith", resource: "ContractsAgent", outcome: "Success", ts: "4m ago" },
  { event: "Ingestion.Started", actor: "system", resource: "Q1 Invoice Reconciliation", outcome: "Success", ts: "5m ago" },
  { event: "Ingest.Failed", actor: "system", resource: "Past Perf Reference", outcome: "Failure", ts: "18m ago" },
  { event: "Agent.Query", actor: "alee", resource: "PerformanceAgent", outcome: "Success", ts: "22m ago" },
  { event: "Webhook.Received", actor: "SharePoint", resource: "doclib", outcome: "Success", ts: "31m ago" },
];

const CHART_DATA = [
  { label: "Mon", indexed: 2100, failed: 34 },
  { label: "Tue", indexed: 3800, failed: 12 },
  { label: "Wed", indexed: 2900, failed: 58 },
  { label: "Thu", indexed: 4200, failed: 21 },
  { label: "Fri", indexed: 5100, failed: 9 },
  { label: "Sat", indexed: 1900, failed: 44 },
  { label: "Sun", indexed: 810, failed: 6 },
];

const STATUS_COLOR = { Indexed: "#1D9E75", Processing: "#BA7517", Failed: "#E24B4A", Pending: "#888780" };
const DOMAIN_COLOR = { contracts: "#534AB7", accounts: "#185FA5", performance: "#0F6E56", operations: "#993C1D", proposal: "#D4537E", competitor: "#993556" };

function StatusBadge({ s }) {
  const c = STATUS_COLOR[s] || "#888";
  return (
    <span style={{ background: c + "22", color: c, borderRadius: 4, padding: "2px 8px", fontSize: 11, fontWeight: 500 }}>{s}</span>
  );
}

function DomainBadge({ d }) {
  const c = DOMAIN_COLOR[d] || "#888";
  return (
    <span style={{ background: c + "18", color: c, borderRadius: 4, padding: "2px 8px", fontSize: 11, fontWeight: 500 }}>{d}</span>
  );
}

function MetricCard({ label, value, sub, accent }) {
  return (
    <div style={{ background: "var(--color-background-secondary)", borderRadius: 8, padding: "14px 18px", minWidth: 120 }}>
      <div style={{ fontSize: 12, color: "var(--color-text-secondary)", marginBottom: 4 }}>{label}</div>
      <div style={{ fontSize: 24, fontWeight: 500, color: accent || "var(--color-text-primary)" }}>{value}</div>
      {sub && <div style={{ fontSize: 11, color: "var(--color-text-tertiary)", marginTop: 2 }}>{sub}</div>}
    </div>
  );
}

function MiniBar({ data }) {
  const max = Math.max(...data.map(d => d.indexed));
  return (
    <div style={{ display: "flex", alignItems: "flex-end", gap: 4, height: 60 }}>
      {data.map((d, i) => (
        <div key={i} style={{ flex: 1, display: "flex", flexDirection: "column", alignItems: "center", gap: 2 }}>
          <div style={{ width: "100%", background: "#1D9E75", borderRadius: 2, height: Math.max(4, (d.indexed / max) * 50) }} />
          <div style={{ fontSize: 9, color: "var(--color-text-tertiary)" }}>{d.label[0]}</div>
        </div>
      ))}
    </div>
  );
}

function AgentPill({ name, active }) {
  return (
    <div style={{
      padding: "8px 14px", borderRadius: 8,
      background: active ? "#534AB7" : "var(--color-background-secondary)",
      color: active ? "#fff" : "var(--color-text-secondary)",
      fontSize: 13, fontWeight: 500, cursor: "pointer", border: "0.5px solid var(--color-border-tertiary)"
    }}>{name}</div>
  );
}

function ProgressBar({ pct, color }) {
  return (
    <div style={{ background: "var(--color-background-secondary)", borderRadius: 4, height: 6, overflow: "hidden" }}>
      <div style={{ width: `${pct}%`, height: "100%", background: color || "#1D9E75", borderRadius: 4, transition: "width .6s" }} />
    </div>
  );
}

// ── DASHBOARD PAGE ────────────────────────────────────────────
function DashboardPage() {
  return (
    <div>
      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(130px,1fr))", gap: 12, marginBottom: 24 }}>
        <MetricCard label="Total documents" value="98,412" sub="↑ 2.1% today" />
        <MetricCard label="Indexed" value="95,889" accent="#1D9E75" />
        <MetricCard label="Chunks" value="4.2M" sub="avg 43/doc" />
        <MetricCard label="Queries today" value="1,831" />
        <MetricCard label="Avg latency" value="1.4s" sub="p95: 3.2s" />
        <MetricCard label="Health score" value="94%" accent="#1D9E75" />
      </div>

      <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 16, marginBottom: 24 }}>
        <div style={{ background: "var(--color-background-primary)", border: "0.5px solid var(--color-border-tertiary)", borderRadius: 12, padding: 16 }}>
          <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginBottom: 12 }}>
            <span style={{ fontSize: 13, fontWeight: 500 }}>Ingestion this week</span>
            <span style={{ fontSize: 11, color: "var(--color-text-tertiary)" }}>18,411 docs</span>
          </div>
          <MiniBar data={CHART_DATA} />
        </div>
        <div style={{ background: "var(--color-background-primary)", border: "0.5px solid var(--color-border-tertiary)", borderRadius: 12, padding: 16 }}>
          <div style={{ fontSize: 13, fontWeight: 500, marginBottom: 12 }}>Agent query distribution</div>
          {[["Contracts", 38], ["Performance", 24], ["Accounts", 19], ["Operations", 11], ["Proposal", 5], ["Competitor", 3]].map(([name, pct]) => (
            <div key={name} style={{ marginBottom: 8 }}>
              <div style={{ display: "flex", justifyContent: "space-between", fontSize: 12, marginBottom: 3 }}>
                <span style={{ color: "var(--color-text-secondary)" }}>{name}</span>
                <span style={{ fontWeight: 500 }}>{pct}%</span>
              </div>
              <ProgressBar pct={pct} color={DOMAIN_COLOR[name.toLowerCase()] || "#534AB7"} />
            </div>
          ))}
        </div>
      </div>

      <div style={{ background: "var(--color-background-primary)", border: "0.5px solid var(--color-border-tertiary)", borderRadius: 12, padding: 16, marginBottom: 24 }}>
        <div style={{ fontSize: 13, fontWeight: 500, marginBottom: 12 }}>Source breakdown</div>
        <div style={{ display: "grid", gridTemplateColumns: "repeat(4,1fr)", gap: 12 }}>
          {[["SharePoint", "81,204", "#534AB7"], ["Database", "9,482", "#185FA5"], ["Excel", "4,910", "#0F6E56"], ["Custom API", "2,816", "#993C1D"]].map(([src, ct, col]) => (
            <div key={src} style={{ textAlign: "center" }}>
              <div style={{ fontSize: 20, fontWeight: 500, color: col }}>{ct}</div>
              <div style={{ fontSize: 12, color: "var(--color-text-secondary)" }}>{src}</div>
            </div>
          ))}
        </div>
      </div>

      <div style={{ background: "var(--color-background-primary)", border: "0.5px solid var(--color-border-tertiary)", borderRadius: 12, padding: 16 }}>
        <div style={{ fontSize: 13, fontWeight: 500, marginBottom: 12 }}>Recent activity</div>
        {MOCK_AUDIT.slice(0, 4).map((e, i) => (
          <div key={i} style={{ display: "flex", justifyContent: "space-between", alignItems: "center", padding: "8px 0", borderBottom: i < 3 ? "0.5px solid var(--color-border-tertiary)" : "none" }}>
            <div>
              <span style={{ fontSize: 12, fontWeight: 500 }}>{e.event}</span>
              <span style={{ fontSize: 12, color: "var(--color-text-secondary)", marginLeft: 8 }}>{e.resource}</span>
            </div>
            <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
              <span style={{ fontSize: 11, color: e.outcome === "Success" ? "#1D9E75" : "#E24B4A", fontWeight: 500 }}>{e.outcome}</span>
              <span style={{ fontSize: 11, color: "var(--color-text-tertiary)" }}>{e.ts}</span>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

// ── INGESTION PAGE ────────────────────────────────────────────
function IngestionPage() {
  const [form, setForm] = useState({ source: "SharePoint", ref: "", domain: "contracts", priority: "5" });
  const [docs, setDocs] = useState(MOCK_DOCS);

  const handleIngest = () => {
    if (!form.ref) return;
    const newDoc = {
      id: docs.length + 1, title: form.ref.split("/").pop() || "New Document",
      source: form.source, domain: form.domain, status: "Processing",
      size: "—", chunks: 0, ts: "just now"
    };
    setDocs([newDoc, ...docs]);
    setForm({ ...form, ref: "" });
  };

  return (
    <div>
      <div style={{ background: "var(--color-background-primary)", border: "0.5px solid var(--color-border-tertiary)", borderRadius: 12, padding: 20, marginBottom: 20 }}>
        <div style={{ fontSize: 14, fontWeight: 500, marginBottom: 16 }}>Ingest document</div>
        <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr 1fr auto", gap: 10, alignItems: "end" }}>
          <div>
            <label style={{ fontSize: 11, color: "var(--color-text-secondary)", display: "block", marginBottom: 4 }}>Source</label>
            <select value={form.source} onChange={e => setForm({ ...form, source: e.target.value })} style={{ width: "100%" }}>
              <option>SharePoint</option><option>Database</option><option>Excel</option><option>Custom API</option>
            </select>
          </div>
          <div>
            <label style={{ fontSize: 11, color: "var(--color-text-secondary)", display: "block", marginBottom: 4 }}>Source reference</label>
            <input value={form.ref} onChange={e => setForm({ ...form, ref: e.target.value })} placeholder="siteId|driveId|itemId or path…" style={{ width: "100%" }} />
          </div>
          <div>
            <label style={{ fontSize: 11, color: "var(--color-text-secondary)", display: "block", marginBottom: 4 }}>Domain</label>
            <select value={form.domain} onChange={e => setForm({ ...form, domain: e.target.value })} style={{ width: "100%" }}>
              <option>contracts</option><option>accounts</option><option>operations</option><option>performance</option><option>proposal</option>
            </select>
          </div>
          <button onClick={handleIngest} style={{ background: "#534AB7", color: "#fff", border: "none", borderRadius: 8, padding: "8px 18px", fontSize: 13, cursor: "pointer", fontWeight: 500 }}>Ingest</button>
        </div>

        <div style={{ marginTop: 16, paddingTop: 16, borderTop: "0.5px solid var(--color-border-tertiary)" }}>
          <div style={{ fontSize: 12, color: "var(--color-text-secondary)", marginBottom: 8 }}>SharePoint bulk ingest (up to 100K docs)</div>
          <div style={{ display: "flex", gap: 8 }}>
            <input placeholder="Site ID" style={{ flex: 1 }} />
            <input placeholder="Drive ID" style={{ flex: 1 }} />
            <button style={{ padding: "6px 16px", fontSize: 12, borderRadius: 8, cursor: "pointer" }}>Bulk Enqueue</button>
          </div>
        </div>
      </div>

      <div style={{ display: "grid", gridTemplateColumns: "repeat(4,1fr)", gap: 12, marginBottom: 20 }}>
        <MetricCard label="Indexed" value="95,889" accent="#1D9E75" />
        <MetricCard label="Processing" value="312" accent="#BA7517" />
        <MetricCard label="Failed" value="211" accent="#E24B4A" />
        <MetricCard label="Queue depth" value="1,489" />
      </div>

      <div style={{ background: "var(--color-background-primary)", border: "0.5px solid var(--color-border-tertiary)", borderRadius: 12, overflow: "hidden" }}>
        <div style={{ padding: "12px 16px", borderBottom: "0.5px solid var(--color-border-tertiary)", fontSize: 13, fontWeight: 500 }}>Documents</div>
        <table style={{ width: "100%", fontSize: 12, borderCollapse: "collapse" }}>
          <thead>
            <tr style={{ background: "var(--color-background-secondary)" }}>
              {["Title", "Source", "Domain", "Status", "Size", "Chunks", "Indexed"].map(h => (
                <th key={h} style={{ padding: "8px 12px", textAlign: "left", color: "var(--color-text-secondary)", fontWeight: 500 }}>{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {docs.map((d, i) => (
              <tr key={d.id} style={{ borderTop: "0.5px solid var(--color-border-tertiary)" }}>
                <td style={{ padding: "10px 12px", fontWeight: 500 }}>{d.title}</td>
                <td style={{ padding: "10px 12px", color: "var(--color-text-secondary)" }}>{d.source}</td>
                <td style={{ padding: "10px 12px" }}><DomainBadge d={d.domain} /></td>
                <td style={{ padding: "10px 12px" }}><StatusBadge s={d.status} /></td>
                <td style={{ padding: "10px 12px", color: "var(--color-text-secondary)" }}>{d.size}</td>
                <td style={{ padding: "10px 12px" }}>{d.chunks || "—"}</td>
                <td style={{ padding: "10px 12px", color: "var(--color-text-tertiary)" }}>{d.ts}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

// ── QUERY PAGE ────────────────────────────────────────────────
function QueryPage() {
  const [q, setQ] = useState("");
  const [domain, setDomain] = useState("");
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState(null);
  const [history, setHistory] = useState(MOCK_QUERIES);

  const AGENTS = ["Auto-route", "Contracts", "Accounts", "Operations", "Performance", "Proposal", "Competitor"];

  const submit = () => {
    if (!q.trim()) return;
    setLoading(true);
    setTimeout(() => {
      const agentMap = { contracts: "Contracts", accounts: "Accounts", operations: "Operations", performance: "Performance", proposal: "Proposal", competitor: "Competitor" };
      const routed = agentMap[domain] || "Contracts";
      setResult({
        question: q, agent: routed,
        answer: `[${routed} Agent] Based on the indexed documents, here is a synthesized response to your query:\n\n"${q}"\n\nThe relevant contracts indicate specific deliverables aligned with PWS Section 4.1 through 4.8. Key past performance references include DHS-2023-T-001 (CPARS: 4.5/5) and DoD-2022-C-0088 (CPARS: 4.8/5). Recommend referencing these for the technical volume. [Source: SharePoint — GSA IDIQ Contract FY2025, chunk 14/142]`,
        chunks: [
          { title: "GSA IDIQ Contract FY2025", score: 0.94, content: "Section 4.1: The Contractor shall provide..." },
          { title: "SOW Template Library", score: 0.88, content: "Performance Work Statement requirements..." },
          { title: "CPARS Review DHS PMO", score: 0.81, content: "Overall rating: Exceptional. Key strengths..." },
        ],
        ms: Math.round(800 + Math.random() * 2000)
      });
      setHistory([{ q, agent: routed, ms: Math.round(800 + Math.random() * 2000), ts: "just now" }, ...history]);
      setLoading(false);
      setQ("");
    }, 1800);
  };

  return (
    <div>
      <div style={{ background: "var(--color-background-primary)", border: "0.5px solid var(--color-border-tertiary)", borderRadius: 12, padding: 20, marginBottom: 20 }}>
        <div style={{ display: "flex", gap: 8, marginBottom: 12, flexWrap: "wrap" }}>
          {AGENTS.map(a => (
            <div key={a} onClick={() => setDomain(a === "Auto-route" ? "" : a.toLowerCase())}
              style={{ padding: "4px 12px", borderRadius: 20, fontSize: 12, cursor: "pointer", fontWeight: 500, border: "0.5px solid var(--color-border-tertiary)",
                background: (a === "Auto-route" ? domain === "" : domain === a.toLowerCase()) ? "#534AB7" : "transparent",
                color: (a === "Auto-route" ? domain === "" : domain === a.toLowerCase()) ? "#fff" : "var(--color-text-secondary)" }}>{a}</div>
          ))}
        </div>
        <div style={{ display: "flex", gap: 10 }}>
          <input value={q} onChange={e => setQ(e.target.value)} onKeyDown={e => e.key === "Enter" && submit()}
            placeholder="Ask anything about your contracts, past performance, financials…" style={{ flex: 1, fontSize: 14 }} />
          <button onClick={submit} disabled={loading} style={{ padding: "8px 20px", background: loading ? "#888" : "#534AB7", color: "#fff", border: "none", borderRadius: 8, cursor: loading ? "default" : "pointer", fontSize: 13 }}>
            {loading ? "…" : "Ask ↗"}
          </button>
        </div>
      </div>

      {result && (
        <div style={{ background: "var(--color-background-primary)", border: "0.5px solid var(--color-border-tertiary)", borderRadius: 12, padding: 20, marginBottom: 20 }}>
          <div style={{ display: "flex", gap: 8, alignItems: "center", marginBottom: 12 }}>
            <DomainBadge d={result.agent.toLowerCase()} />
            <span style={{ fontSize: 11, color: "var(--color-text-tertiary)" }}>{result.ms}ms</span>
          </div>
          <div style={{ fontSize: 14, lineHeight: 1.7, color: "var(--color-text-primary)", whiteSpace: "pre-wrap", marginBottom: 16 }}>{result.answer}</div>
          <div style={{ fontSize: 12, fontWeight: 500, color: "var(--color-text-secondary)", marginBottom: 8 }}>Retrieved chunks</div>
          {result.chunks.map((c, i) => (
            <div key={i} style={{ background: "var(--color-background-secondary)", borderRadius: 8, padding: "10px 14px", marginBottom: 8 }}>
              <div style={{ display: "flex", justifyContent: "space-between", marginBottom: 4 }}>
                <span style={{ fontSize: 12, fontWeight: 500 }}>{c.title}</span>
                <span style={{ fontSize: 11, color: "#1D9E75", fontWeight: 500 }}>score: {c.score}</span>
              </div>
              <div style={{ fontSize: 12, color: "var(--color-text-secondary)" }}>{c.content}</div>
            </div>
          ))}
        </div>
      )}

      <div style={{ background: "var(--color-background-primary)", border: "0.5px solid var(--color-border-tertiary)", borderRadius: 12, padding: 16 }}>
        <div style={{ fontSize: 13, fontWeight: 500, marginBottom: 12 }}>Recent queries</div>
        {history.map((h, i) => (
          <div key={i} style={{ display: "flex", justifyContent: "space-between", padding: "8px 0", borderBottom: i < history.length - 1 ? "0.5px solid var(--color-border-tertiary)" : "none" }}>
            <div style={{ flex: 1, overflow: "hidden" }}>
              <div style={{ fontSize: 13, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{h.q}</div>
              <div style={{ fontSize: 11, color: "var(--color-text-tertiary)", marginTop: 2 }}><DomainBadge d={h.agent.toLowerCase()} /></div>
            </div>
            <div style={{ display: "flex", gap: 12, alignItems: "center", marginLeft: 12, flexShrink: 0 }}>
              <span style={{ fontSize: 11, color: "var(--color-text-secondary)" }}>{h.ms}ms</span>
              <span style={{ fontSize: 11, color: "var(--color-text-tertiary)" }}>{h.ts}</span>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

// ── RFP PAGE ──────────────────────────────────────────────────
function RfpPage() {
  const [tab, setTab] = useState("analyze");
  const [rfpText, setRfpText] = useState("");
  const [volume, setVolume] = useState("technical");
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState(null);

  const run = () => {
    setLoading(true);
    setTimeout(() => {
      setResult({
        title: "DHS EAGLE II IDIQ — IT Professional Services",
        agency: "Dept of Homeland Security", naics: "541511",
        matchScore: 0.87, winProbability: 0.68,
        competitors: ["Leidos", "Booz Allen Hamilton", "SAIC", "Perspecta", "Deloitte"],
        recommendation: "BID — Strong past performance alignment with DHS ATO and CBP cloud modernization. Key discriminator: our FedRAMP High authorization. Win theme: zero-trust architecture expertise.",
        draft: tab === "generate" ? `TECHNICAL VOLUME — EAGLE II\n\nSection 1: Technical Approach\n\nOur team brings 12+ years of DHS-specific IT modernization experience...\n\nSection 2: Management Approach\n\nOur proven PMO structure includes PMP-certified leads on all task orders...\n\nSection 3: Past Performance\n\nContract DHS-2023-T-001 (CPARS 4.5): Cloud modernization delivering 40% cost reduction...\n\nSection 4: Key Personnel\n\nProgram Manager: 15 years DHS/CBP experience, Top Secret/SCI cleared...` : null
      });
      setLoading(false);
    }, 2200);
  };

  return (
    <div>
      <div style={{ display: "flex", gap: 2, marginBottom: 20, background: "var(--color-background-secondary)", padding: 4, borderRadius: 10, width: "fit-content" }}>
        {[["analyze", "Analyze RFP"], ["generate", "Generate Volume"], ["competitor", "Competitor Intel"]].map(([id, label]) => (
          <div key={id} onClick={() => setTab(id)} style={{ padding: "6px 16px", borderRadius: 8, fontSize: 13, cursor: "pointer", fontWeight: 500,
            background: tab === id ? "var(--color-background-primary)" : "transparent",
            color: tab === id ? "var(--color-text-primary)" : "var(--color-text-secondary)",
            border: tab === id ? "0.5px solid var(--color-border-tertiary)" : "none" }}>{label}</div>
        ))}
      </div>

      <div style={{ background: "var(--color-background-primary)", border: "0.5px solid var(--color-border-tertiary)", borderRadius: 12, padding: 20, marginBottom: 20 }}>
        {tab === "generate" && (
          <div style={{ marginBottom: 12 }}>
            <label style={{ fontSize: 11, color: "var(--color-text-secondary)" }}>Volume type</label>
            <div style={{ display: "flex", gap: 8, marginTop: 6 }}>
              {["technical", "management", "sow", "price"].map(v => (
                <div key={v} onClick={() => setVolume(v)} style={{ padding: "4px 12px", borderRadius: 6, fontSize: 12, cursor: "pointer",
                  background: volume === v ? "#534AB722" : "var(--color-background-secondary)",
                  color: volume === v ? "#534AB7" : "var(--color-text-secondary)",
                  border: volume === v ? "0.5px solid #534AB7" : "0.5px solid var(--color-border-tertiary)" }}>{v}</div>
              ))}
            </div>
          </div>
        )}
        <textarea value={rfpText} onChange={e => setRfpText(e.target.value)}
          placeholder="Paste RFP text, solicitation number, or describe the opportunity…"
          style={{ width: "100%", height: 120, fontSize: 13, borderRadius: 8, padding: "10px 14px", border: "0.5px solid var(--color-border-tertiary)", background: "var(--color-background-secondary)", color: "var(--color-text-primary)", resize: "vertical", boxSizing: "border-box" }} />
        <button onClick={run} disabled={loading} style={{ marginTop: 12, padding: "8px 20px", background: loading ? "#888" : "#534AB7", color: "#fff", border: "none", borderRadius: 8, cursor: loading ? "default" : "pointer", fontSize: 13 }}>
          {loading ? "Analyzing…" : tab === "generate" ? "Generate ↗" : "Analyze ↗"}
        </button>
      </div>

      {result && (
        <>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(4,1fr)", gap: 12, marginBottom: 16 }}>
            <MetricCard label="Match score" value={`${Math.round(result.matchScore * 100)}%`} accent="#1D9E75" />
            <MetricCard label="Win probability" value={`${Math.round(result.winProbability * 100)}%`} accent="#534AB7" />
            <MetricCard label="Agency" value={result.agency} />
            <MetricCard label="NAICS" value={result.naics} />
          </div>
          <div style={{ background: "var(--color-background-primary)", border: "0.5px solid var(--color-border-tertiary)", borderRadius: 12, padding: 20, marginBottom: 16 }}>
            <div style={{ fontSize: 13, fontWeight: 500, marginBottom: 8 }}>Recommendation</div>
            <div style={{ background: "#1D9E7511", border: "0.5px solid #1D9E75", borderRadius: 8, padding: "10px 14px", fontSize: 13, color: "var(--color-text-primary)" }}>{result.recommendation}</div>
          </div>
          {tab === "competitor" && (
            <div style={{ background: "var(--color-background-primary)", border: "0.5px solid var(--color-border-tertiary)", borderRadius: 12, padding: 20, marginBottom: 16 }}>
              <div style={{ fontSize: 13, fontWeight: 500, marginBottom: 12 }}>Likely competitors</div>
              {result.competitors.map((c, i) => (
                <div key={c} style={{ display: "flex", alignItems: "center", gap: 12, padding: "8px 0", borderBottom: i < result.competitors.length - 1 ? "0.5px solid var(--color-border-tertiary)" : "none" }}>
                  <div style={{ width: 32, height: 32, borderRadius: "50%", background: "#534AB720", display: "flex", alignItems: "center", justifyContent: "center", fontSize: 12, fontWeight: 500, color: "#534AB7" }}>{c[0]}</div>
                  <div style={{ flex: 1 }}><div style={{ fontSize: 13, fontWeight: 500 }}>{c}</div></div>
                  <ProgressBar pct={Math.round(60 - i * 8)} color="#534AB7" />
                </div>
              ))}
            </div>
          )}
          {result.draft && (
            <div style={{ background: "var(--color-background-primary)", border: "0.5px solid var(--color-border-tertiary)", borderRadius: 12, padding: 20 }}>
              <div style={{ fontSize: 13, fontWeight: 500, marginBottom: 12 }}>Generated draft — {volume}</div>
              <pre style={{ fontSize: 12, lineHeight: 1.7, color: "var(--color-text-primary)", whiteSpace: "pre-wrap", fontFamily: "var(--font-mono)", background: "var(--color-background-secondary)", padding: "14px", borderRadius: 8 }}>{result.draft}</pre>
            </div>
          )}
        </>
      )}
    </div>
  );
}

// ── AUDIT PAGE ────────────────────────────────────────────────
function AuditPage() {
  const [filter, setFilter] = useState("");
  const events = MOCK_AUDIT.filter(e => !filter || e.event.toLowerCase().includes(filter.toLowerCase()) || e.resource.toLowerCase().includes(filter.toLowerCase()));
  return (
    <div>
      <div style={{ display: "grid", gridTemplateColumns: "repeat(4,1fr)", gap: 12, marginBottom: 20 }}>
        <MetricCard label="Total events" value="48,921" sub="last 30 days" />
        <MetricCard label="Success" value="48,204" accent="#1D9E75" />
        <MetricCard label="Failures" value="717" accent="#E24B4A" />
        <MetricCard label="Actors" value="31" sub="unique users" />
      </div>
      <div style={{ marginBottom: 12 }}>
        <input value={filter} onChange={e => setFilter(e.target.value)} placeholder="Filter events…" style={{ width: "100%" }} />
      </div>
      <div style={{ background: "var(--color-background-primary)", border: "0.5px solid var(--color-border-tertiary)", borderRadius: 12, overflow: "hidden" }}>
        <table style={{ width: "100%", fontSize: 12, borderCollapse: "collapse" }}>
          <thead>
            <tr style={{ background: "var(--color-background-secondary)" }}>
              {["Event", "Actor", "Resource", "Outcome", "Time"].map(h => (
                <th key={h} style={{ padding: "8px 12px", textAlign: "left", color: "var(--color-text-secondary)", fontWeight: 500 }}>{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {events.map((e, i) => (
              <tr key={i} style={{ borderTop: "0.5px solid var(--color-border-tertiary)" }}>
                <td style={{ padding: "10px 12px", fontWeight: 500 }}>{e.event}</td>
                <td style={{ padding: "10px 12px", color: "var(--color-text-secondary)" }}>{e.actor}</td>
                <td style={{ padding: "10px 12px" }}>{e.resource}</td>
                <td style={{ padding: "10px 12px" }}>
                  <span style={{ color: e.outcome === "Success" ? "#1D9E75" : "#E24B4A", fontWeight: 500 }}>{e.outcome}</span>
                </td>
                <td style={{ padding: "10px 12px", color: "var(--color-text-tertiary)" }}>{e.ts}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

// ── PERFORMANCE PAGE ──────────────────────────────────────────
function PerformancePage() {
  return (
    <div>
      <div style={{ display: "grid", gridTemplateColumns: "repeat(4,1fr)", gap: 12, marginBottom: 20 }}>
        <MetricCard label="Health score" value="94%" accent="#1D9E75" />
        <MetricCard label="P95 latency" value="3.2s" />
        <MetricCard label="Embedding rate" value="1.4k/min" />
        <MetricCard label="Fail rate" value="0.22%" accent="#E24B4A" />
      </div>
      <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 16, marginBottom: 20 }}>
        {[["Ingestion queue depth", "1,489", "#BA7517", 74], ["Vector index size", "4.2M chunks", "#534AB7", 84], ["MQTT broker lag", "12ms", "#1D9E75", 12], ["Reconciliation last run", "2h ago", "#185FA5", 62]].map(([label, val, col, pct]) => (
          <div key={label} style={{ background: "var(--color-background-primary)", border: "0.5px solid var(--color-border-tertiary)", borderRadius: 12, padding: 16 }}>
            <div style={{ fontSize: 12, color: "var(--color-text-secondary)", marginBottom: 8 }}>{label}</div>
            <div style={{ fontSize: 20, fontWeight: 500, color: col, marginBottom: 12 }}>{val}</div>
            <ProgressBar pct={pct} color={col} />
          </div>
        ))}
      </div>
      <div style={{ background: "var(--color-background-primary)", border: "0.5px solid var(--color-border-tertiary)", borderRadius: 12, padding: 20 }}>
        <div style={{ fontSize: 13, fontWeight: 500, marginBottom: 12 }}>AI performance report</div>
        <div style={{ fontSize: 13, lineHeight: 1.8, color: "var(--color-text-secondary)" }}>
          System operating at <strong>94% health</strong>. Ingestion throughput is nominal at 1,400 embeddings/min. The failure spike on Wednesday (58 docs) was traced to a SharePoint permission change — reconciliation job caught and re-queued all failed items within 60 minutes.<br /><br />
          Recommendation: Increase MQTT broker concurrency from 8 to 12 workers to reduce queue depth during peak ingestion windows (09:00–11:00 EST). Consider implementing semantic caching for the top-20 repeated query patterns to reduce p95 latency below 2s.
        </div>
      </div>
    </div>
  );
}

// ── APP SHELL ─────────────────────────────────────────────────
export default function App() {
  const [page, setPage] = useState("dashboard");
  const PAGE_TITLES = { dashboard: "Dashboard", ingestion: "Ingestion", query: "Query", agents: "Agent System", rfp: "RFP & Proposals", performance: "Performance", audit: "Audit Log" };

  const renderPage = () => {
    switch (page) {
      case "dashboard":   return <DashboardPage />;
      case "ingestion":   return <IngestionPage />;
      case "query":       return <QueryPage />;
      case "rfp":         return <RfpPage />;
      case "performance": return <PerformancePage />;
      case "audit":       return <AuditPage />;
      case "agents": return (
        <div>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(3,1fr)", gap: 12, marginBottom: 20 }}>
            {[["Router", "Auto-routes queries to the correct specialist", "#534AB7", "Active"],
              ["Contracts", "FAR/DFARS, CLINs, IDIQs, task orders", "#185FA5", "Active"],
              ["Accounts", "Billing, AR/AP, financial reporting", "#0F6E56", "Active"],
              ["Operations", "PM, staffing, deliverables, EVM", "#993C1D", "Active"],
              ["Past Performance", "CPARS, performance reviews, ratings", "#534AB7", "Active"],
              ["Proposal", "RFP analysis, SOW, technical volumes", "#D4537E", "Active"],
              ["Competitor Intel", "Competitor analysis, win probability", "#BA7517", "Active"],
              ["Performance Monitor", "System health, latency, throughput", "#1D9E75", "Active"]].map(([name, desc, col, status]) => (
              <div key={name} style={{ background: "var(--color-background-primary)", border: "0.5px solid var(--color-border-tertiary)", borderRadius: 12, padding: 16 }}>
                <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-start", marginBottom: 8 }}>
                  <div style={{ fontSize: 13, fontWeight: 500 }}>{name}</div>
                  <StatusBadge s={status} />
                </div>
                <div style={{ fontSize: 12, color: "var(--color-text-secondary)", lineHeight: 1.6 }}>{desc}</div>
                <div style={{ marginTop: 12, fontSize: 11, color: col, fontWeight: 500 }}>● Microsoft Agent Framework</div>
              </div>
            ))}
          </div>
        </div>
      );
      default: return null;
    }
  };

  return (
    <div style={{ display: "flex", height: "100vh", fontFamily: "var(--font-sans)", fontSize: 14 }}>
      <div style={{ width: 200, background: "var(--color-background-secondary)", borderRight: "0.5px solid var(--color-border-tertiary)", display: "flex", flexDirection: "column", flexShrink: 0 }}>
        <div style={{ padding: "20px 16px", borderBottom: "0.5px solid var(--color-border-tertiary)" }}>
          <div style={{ fontSize: 13, fontWeight: 500, color: "#534AB7" }}>◈ GovConRAG</div>
          <div style={{ fontSize: 11, color: "var(--color-text-tertiary)", marginTop: 2 }}>Enterprise Platform</div>
        </div>
        <nav style={{ flex: 1, padding: "8px 8px" }}>
          {NAV.map(n => (
            <div key={n.id} onClick={() => setPage(n.id)}
              style={{ display: "flex", alignItems: "center", gap: 10, padding: "8px 10px", borderRadius: 8, cursor: "pointer", marginBottom: 2,
                background: page === n.id ? "var(--color-background-primary)" : "transparent",
                color: page === n.id ? "var(--color-text-primary)" : "var(--color-text-secondary)",
                border: page === n.id ? "0.5px solid var(--color-border-tertiary)" : "none",
                fontWeight: page === n.id ? 500 : 400 }}>
              <span style={{ fontSize: 14 }}>{n.icon}</span>
              <span style={{ fontSize: 13 }}>{n.label}</span>
            </div>
          ))}
        </nav>
        <div style={{ padding: "12px 16px", borderTop: "0.5px solid var(--color-border-tertiary)", fontSize: 11, color: "var(--color-text-tertiary)" }}>
          <div>LiteGraph 5.0.2</div>
          <div>MS Agent Framework RC</div>
          <div style={{ color: "#1D9E75", marginTop: 2 }}>● MQTT connected</div>
        </div>
      </div>

      <div style={{ flex: 1, overflow: "auto", background: "var(--color-background-tertiary)" }}>
        <div style={{ padding: "16px 24px", borderBottom: "0.5px solid var(--color-border-tertiary)", background: "var(--color-background-primary)", display: "flex", justifyContent: "space-between", alignItems: "center" }}>
          <div style={{ fontSize: 16, fontWeight: 500 }}>{PAGE_TITLES[page]}</div>
          <div style={{ display: "flex", gap: 8 }}>
            <span style={{ fontSize: 11, background: "#534AB722", color: "#534AB7", padding: "3px 10px", borderRadius: 20, fontWeight: 500 }}>FedRAMP Ready</span>
            <span style={{ fontSize: 11, background: "#1D9E7511", color: "#1D9E75", padding: "3px 10px", borderRadius: 20, fontWeight: 500 }}>98,412 docs indexed</span>
          </div>
        </div>
        <div style={{ padding: 24 }}>
          {renderPage()}
        </div>
      </div>
    </div>
  );
}
