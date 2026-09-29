/* LlamaDSH Launcher(Linux 复刻版)前端逻辑。
 * 复刻原版 MainWindow.xaml 的交互:方案/参数面板/命令预览/确认链/监控/日志。 */
"use strict";

const $ = (id) => document.getElementById(id);
let SCHEMA = { groups: [], params: [] };
let CFG = { Profiles: {}, CurrentProfile: "默认方案" };
let PROF = null;            // 当前编辑中的方案(引用 CFG.Profiles[名字] 的拷贝)
let LOG_AFTER = 0, LOG_LINES = [];
let MON_STOP = false;
let CFG_DIR = "";

async function api(path, body) {
  const r = await fetch("/api/" + path, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body || {})
  });
  const j = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(j.error || r.status);
  return j;
}

/* ================================ 模态框 ================================ */
function showModal({ title, body = "", buttons = [], extra = null }) {
  return new Promise((resolve) => {
    $("modalTitle").textContent = title;
    $("modalBody").innerHTML = body;
    const bx = $("modalExtra");
    bx.innerHTML = "";
    if (extra) bx.appendChild(extra);
    const bb = $("modalBtns");
    bb.innerHTML = "";
    for (const b of buttons) {
      const el = document.createElement("button");
      el.textContent = b.label;
      if (b.cls) el.className = b.cls;
      el.onclick = () => { $("overlay").classList.remove("show"); resolve(b.value); };
      bb.appendChild(el);
    }
    $("overlay").classList.add("show");
  });
}
const mEsc = (s) => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

/* ============================ 目录/文件浏览 ============================= */
async function browsePicker(mode) {
  let cur = null;
  let sel = null;
  const box = document.createElement("div");
  box.innerHTML = `<input type="text" id="browserPath"><div id="browser"></div>
    <div class="tiny" id="browserSel" style="margin-top:4px"></div>`;
  const list = box.querySelector("#browser");
  const pathIn = box.querySelector("#browserPath");
  const selInfo = box.querySelector("#browserSel");

  async function load(p) {
    const r = await api("browse", { path: p });
    if (r.error) { selInfo.textContent = r.error; return; }
    cur = r.path; sel = null; selInfo.textContent = "";
    pathIn.value = cur;
    list.innerHTML = "";
    for (const root of r.roots || []) {
      const d = document.createElement("div");
      d.className = "item dir"; d.textContent = "⌂ " + root;
      d.onclick = () => load(root);
      list.appendChild(d);
    }
    const up = document.createElement("div");
    up.className = "item dir"; up.textContent = "↩ ..";
    up.onclick = () => load(cur.replace(/\/[^/]*$/, "") || "/");
    list.appendChild(up);
    for (const dname of r.dirs) {
      const d = document.createElement("div");
      d.className = "item dir"; d.textContent = "📁 " + dname;
      d.onclick = () => load(cur.replace(/\/$/, "") + "/" + dname);
      list.appendChild(d);
    }
    for (const f of r.files || []) {
      if (mode === "dir") continue;
      const d = document.createElement("div");
      d.className = "item";
      d.innerHTML = `<span>📄 ${mEsc(f.name)}</span><span class="tiny">${(f.size / 1073741824).toFixed(2)} GB</span>`;
      d.onclick = () => {
        list.querySelectorAll(".item").forEach(x => x.classList.remove("sel"));
        d.classList.add("sel");
        sel = cur.replace(/\/$/, "") + "/" + f.name;
        selInfo.textContent = "已选: " + sel;
      };
      list.appendChild(d);
    }
  }
  pathIn.addEventListener("keydown", (e) => { if (e.key === "Enter") load(pathIn.value); });
  await load(mode === "model" ? "" : "");
  const v = await showModal({
    title: "选择文件 / 目录", body: "", extra: box,
    buttons: [{ label: "取消", value: null },
      { label: mode === "dir" ? "选择当前目录" : "确定", cls: "primary", value: "__ok__" }]
  });
  if (v === null) return null;
  if (mode === "dir") return cur;
  return sel || pathIn.value;
}

/* ============================== 方案编辑 ================================ */
function collectProfile() {
  const bp = {}, vp = {};
  for (const d of SCHEMA.params) {
    if (d.hidden || d.flag === "model" || d.flag === "model-draft") continue;
    if (d.type === "Bool") {
      bp[d.flag] = $("pb_" + cssId(d.flag)).checked;
    } else {
      vp[d.flag] = { Enabled: $("pe_" + cssId(d.flag)).checked, Value: $("pv_" + cssId(d.flag)).value.trim() };
    }
  }
  return {
    ModelPath: $("inModel").value.trim(), MtpPath: $("inMtp").value.trim(),
    MmprojPath: $("inMmproj").value.trim(), MmprojUrl: $("inMmprojUrl").value.trim(),
    NoMmprojOffload: $("chkNoMmprojOffload").checked,
    LlamaDir: $("inLlamaDir").value.trim(), DshDir: $("inDshDir").value.trim(),
    DshMode: parseInt($("comboDshMode").value) || 0,
    DshAdminMode: $("chkDshAdmin").checked,
    MinimizeToTray: false,
    BoolParams: bp, ValueParams: vp, CmdPreview: $("cmdPreview").value,
    VramBreakerEnabled: $("chkBreaker").checked,
    VramBreakerType: parseInt($("comboBreakerType").value) || 0,
    VramBreakerValue: parseFloat($("txtBreakerVal").value) || 90,
  };
}

function applyProfile(p) {
  $("inModel").value = p.ModelPath || ""; $("inMtp").value = p.MtpPath || "";
  $("inMmproj").value = p.MmprojPath || ""; $("inMmprojUrl").value = p.MmprojUrl || "";
  $("chkNoMmprojOffload").checked = !!p.NoMmprojOffload;
  $("inLlamaDir").value = p.LlamaDir || ""; $("inDshDir").value = p.DshDir || "";
  $("comboDshMode").value = String(p.DshMode || 0);
  $("chkDshAdmin").checked = !!p.DshAdminMode;
  $("chkBreaker").checked = !!p.VramBreakerEnabled;
  $("comboBreakerType").value = String(p.VramBreakerType || 0);
  $("txtBreakerVal").value = p.VramBreakerValue ?? 90;
  for (const d of SCHEMA.params) {
    if (d.hidden || d.flag === "model" || d.flag === "model-draft") continue;
    if (d.type === "Bool") {
      const el = $("pb_" + cssId(d.flag));
      const on = (p.BoolParams || {})[d.flag];
      if (el) el.checked = on === undefined ? d.default === "true" : !!on;
    } else {
      const e = $("pe_" + cssId(d.flag)), v = $("pv_" + cssId(d.flag));
      const rec = (p.ValueParams || {})[d.flag];
      if (e) e.checked = rec ? !!rec.Enabled : !!d.enabled;
      if (v) v.value = rec ? (rec.Value ?? "") : (d.default || "");
    }
  }
  depEval();
}

const cssId = (s) => s.replace(/[^a-zA-Z0-9_-]/g, "_");

function buildParamRow(d, container) {
  const row = document.createElement("div");
  row.className = "prow" + (d.wide ? " wide" : "");
  row.id = "pr_" + cssId(d.flag);
  const tip = (d.tooltip || "") + (d.deprecated ? "\n⚠ 已废弃" : "");
  if (d.type === "Bool") {
    row.innerHTML = `<span><input type="checkbox" id="pb_${cssId(d.flag)}"></span>
      <span class="plabel" title="${mEsc(tip)}">${mEsc(d.label)}</span><span></span>`;
  } else {
    row.innerHTML = `<span><input type="checkbox" id="pe_${cssId(d.flag)}"></span>
      <span class="plabel" title="${mEsc(tip)}">${mEsc(d.label)}</span><span id="pw_${cssId(d.flag)}"></span>`;
    const wrap = row.querySelector("#pw_" + cssId(d.flag));
    if (d.type === "Combo" && (d.options || []).length) {
      const sel = document.createElement("select");
      sel.id = "pv_" + cssId(d.flag);
      for (const o of d.options) {
        const op = document.createElement("option"); op.value = o; op.textContent = o;
        sel.appendChild(op);
      }
      sel.value = d.default || (d.options[0] || "");
      wrap.appendChild(sel);
    } else {
      const inp = document.createElement("input");
      inp.type = "text"; inp.id = "pv_" + cssId(d.flag);
      inp.value = d.default || "";
      inp.title = mEsc(tip);
      wrap.appendChild(inp);
    }
  }
  container.appendChild(row);
}

function renderParams() {
  const host = $("paramGroups");
  host.innerHTML = "";
  for (const g of SCHEMA.groups) {
    const params = SCHEMA.params.filter(p => p.group === g.id && !p.hidden);
    if (!params.length) continue;
    const det = document.createElement("details");
    det.className = "grp";
    det.innerHTML = `<summary>${mEsc(g.title)}</summary><div class="body"></div>`;
    const body = det.querySelector(".body");
    // 子组聚合: 同一子组的参数聚拢(即使 yml 中不连续), 子组标题可点击折叠
    const bySub = new Map();
    for (const d of params) {
      const k = d.subgroup || "";
      if (!bySub.has(k)) bySub.set(k, []);
      bySub.get(k).push(d);
    }
    for (const [sub, items] of bySub) {
      if (sub) {
        const h = document.createElement("div");
        h.className = "subgroup";
        h.style.cursor = "pointer";
        h.title = "点击折叠/展开该小节";
        h.textContent = "▾ " + sub;
        const wrap = document.createElement("div");
        wrap.className = "subwrap";
        h.addEventListener("click", () => {
          const hide = wrap.style.display !== "none";
          wrap.style.display = hide ? "none" : "";
          wrap.dataset.collapsed = hide ? "1" : "";
          h.textContent = (hide ? "▸ " : "▾ ") + sub;
        });
        body.appendChild(h);
        body.appendChild(wrap);
        for (const d of items) buildParamRow(d, wrap);
      } else {
        for (const d of items) buildParamRow(d, body);
      }
    }
    host.appendChild(det);
  }
  // 事件绑定 → 依赖评估 + 预览刷新
  host.addEventListener("change", () => { depEval(); schedulePreview(); });
  host.addEventListener("input", (e) => {
    if (e.target.tagName === "INPUT" || e.target.tagName === "SELECT") { depEval(); schedulePreview(); }
  });
}

/* --------------------- 参数过滤 --------------------- */
function applyParamFilter() {
  const q = ($("paramFilter").value || "").trim().toLowerCase();
  $("paramFilterClear").style.display = q ? "" : "none";
  const matches = (d) => !q
    || d.flag.toLowerCase().includes(q)
    || (d.label || "").toLowerCase().includes(q)
    || (d.subgroup || "").toLowerCase().includes(q)
    || (d.tooltip || "").toLowerCase().includes(q);
  for (const det of document.querySelectorAll("#paramGroups > details")) {
    let anyVisible = false;
    const subs = new Map();
    for (const row of det.querySelectorAll(".prow")) {
      const d = SCHEMA.params.find((p) => "pr_" + cssId(p.flag) === row.id);
      const show = !d || matches(d);
      row.style.display = show ? "" : "none";
      if (show) anyVisible = true;
      const subwrap = row.closest(".subwrap");
      const headerEl = subwrap ? subwrap.previousElementSibling : row.previousElementSibling;
      if (headerEl && headerEl.classList.contains("subgroup")) {
        const arr = subs.get(headerEl) || [];
        arr.push(show);
        subs.set(headerEl, arr);
        if (q && show && subwrap) subwrap.style.display = ""; // 过滤命中时强制展开所在子组
        if (!q && subwrap) subwrap.style.display = subwrap.dataset.collapsed ? "none" : ""; // 清空过滤后还原用户折叠状态
      }
    }
    for (const [h, arr] of subs) h.style.display = arr.some(Boolean) ? "" : "none";
    det.style.display = anyVisible ? "" : "none";
    if (q && anyVisible) det.open = true;
  }
}

/* --------------------- 依赖评估(EvaluateDepUI 复刻) --------------------- */
function isOn(d) {
  if (d.type === "Bool") return $("pb_" + cssId(d.flag))?.checked || false;
  return ($("pe_" + cssId(d.flag))?.checked && ($("pv_" + cssId(d.flag))?.value.trim().length > 0)) || false;
}
function effVal(d) {
  if (d.type === "Bool") return $("pb_" + cssId(d.flag))?.checked ? "true" : "false";
  const en = $("pe_" + cssId(d.flag))?.checked, v = $("pv_" + cssId(d.flag))?.value.trim() || "";
  if (en && v.length) return v;
  return d.offValue || d.default || "";
}
function ruleMet(r) {
  const d = SCHEMA.params.find(p => p.flag === r.flag);
  if (!d) return true;
  const op = r.op || "enabled";
  if (op === "enabled") return isOn(d);
  if (op === "disabled") return !isOn(d);
  const a = parseFloat(effVal(d)), b = parseFloat(r.value);
  let c;
  if (!isNaN(a) && !isNaN(b)) c = a === b ? 0 : (a < b ? -1 : 1);
  else c = (effVal(d) < r.value ? -1 : effVal(d) > r.value ? 1 : 0);
  return { "==": c === 0, "!=": c !== 0, ">": c > 0, "<": c < 0, ">=": c >= 0, "<=": c <= 0 }[op] ?? true;
}
function depEval() {
  // implies 联动
  for (const d of SCHEMA.params) {
    if (d.hidden || !d.implies || !isOn(d)) continue;
    for (const t of d.implies) {
      const tb = $("pb_" + cssId(t)), te = $("pe_" + cssId(t));
      if (tb) tb.checked = true;
      if (te) te.checked = true;
    }
  }
  // requires 灰显
  for (const d of SCHEMA.params) {
    if (d.hidden || !d.requires || !d.requires.length) continue;
    const ok = d.requires.every(ruleMet);
    const row = $("pr_" + cssId(d.flag));
    if (row) row.classList.toggle("dep-off", !ok);
    const why = d.requires.map(r => describeRule(r)).join(" 且 ");
    for (const id of ["pb_" + cssId(d.flag), "pe_" + cssId(d.flag), "pv_" + cssId(d.flag)]) {
      const el = $(id); if (el) { el.disabled = !ok; el.title = ok ? (d.tooltip || "") : (d.tooltip || "") + "\n⚠ 前置未满足: " + why; }
    }
  }
}
const describeRule = (r) => (r.op === "enabled" ? `--${r.flag} 已启用` :
  r.op === "disabled" ? `--${r.flag} 已停用` : `--${r.flag} ${r.op} ${r.value}`);

/* ============================== 命令预览 ================================ */
let previewTimer = null;
function schedulePreview() {
  clearTimeout(previewTimer);
  previewTimer = setTimeout(refreshPreview, 300);
}
async function refreshPreview() {
  try {
    const r = await api("preview", { profile: collectProfile() });
    $("cmdPreview").value = r.fmt_cmd || "";
  } catch (e) {
    $("cmdPreview").value = "Error:" + e.message;
  }
}

/* ================================ 日志 ================================== */
async function pollLogs() {
  try {
    const r = await api("logs", { after: LOG_AFTER });
    if (r.lines && r.lines.length) {
      LOG_AFTER = r.after;
      for (const l of r.lines) LOG_LINES.push(l.text);
      if (LOG_LINES.length > 4000) LOG_LINES = LOG_LINES.slice(-3500);
      $("logArea").value = LOG_LINES.join("\n");
      if ($("chkAutoScroll").checked) { $("logArea").scrollTop = $("logArea").scrollHeight; }
    }
  } catch (e) { /* ignore */ }
}

/* ================================ 状态轮询 ============================== */
function setStatus(el, st) {
  const map = {
    running: ["● 运行中", "st-run"], running_admin: ["● 运行中(提权)", "st-run"],
    taken_over: ["● 接管运行中", "st-run"], starting: ["● 启动中...", "st-start"],
    rebuilding: ["🔨 Rebuilding...", "st-start"], stopped: ["● 已停止", "st-stop"],
    failed: ["● 启动失败", "st-fail"], crashed: ["● 启动失败/崩溃", "st-fail"],
  };
  const [t, c] = map[st] || map.stopped;
  el.textContent = t; el.className = "status " + c;
}
async function pollStatus() {
  let s;
  try { s = await api("status"); } catch (e) { return; }
  setStatus($("lblLlamaStatus"), s.llama.status);
  setStatus($("lblDshStatus"), s.dsh.rebuilding ? "rebuilding" : s.dsh.status);
  $("btnLlamaStart").disabled = ["running", "starting", "taken_over"].includes(s.llama.status);
  $("btnLlamaStop").disabled = !["running", "starting", "taken_over"].includes(s.llama.status);
  const dshBusy = ["running", "running_admin", "starting", "taken_over", "rebuilding"].includes(s.dsh.status);
  $("btnDshStart").disabled = dshBusy;
  $("btnDshStop").disabled = !dshBusy;
  $("btnOpenDsh").disabled = !["running", "running_admin", "taken_over"].includes(s.dsh.status);
  const m = s.monitor || {};
  const f1 = (x, suf = "") => (x === null || x === undefined || x === 0 && suf === "") ? "—" : x + suf;
  $("pCpu").textContent = m.cpu != null ? m.cpu.toFixed(0) + "%" : "—";
  $("pMem").textContent = m.mem_pct != null ? m.mem_pct.toFixed(0) + "%" : "—";
  $("pGpu").textContent = m.gpu_util != null ? Number(m.gpu_util).toFixed(0) + "%" : "—";
  const vu = m.vram_used || 0, vt = m.vram_total || 0;
  $("pVram").textContent = vt > 0 ? (vu / 1073741824).toFixed(1) + "/" + (vt / 1073741824).toFixed(0) + "G" : "—";
  $("pVram").title = vt > 0 ? "已用 " + (100 * vu / vt).toFixed(1) + "%" : "";
  $("pTemp").textContent = m.gpu_temp ? Number(m.gpu_temp).toFixed(0) + "°C" : "—";
  $("pTemp").title = m.gpu_temp_src ? "来源: " + m.gpu_temp_src : "";
  $("pTps").textContent = m.tps != null ? m.tps : "—";
  $("pTtft").textContent = m.ttft != null ? m.ttft.toFixed(0) + "ms" : "—";
  $("pPf").textContent = m.prefill != null ? m.prefill.toFixed(0) + " t/s" : "—";
  // 熔断标签
  const lb = $("lblVramBreaker");
  if (m.breaker_triggered) lb.textContent = `⚠ ${m.breaker_pct}% 已熔断`;
  else if (vt > 0) lb.textContent = `${m.breaker_pct}% 正常`;
  else lb.textContent = "";
}

/* ============================ 启动/停止流程 ============================= */
async function startLlama() {
  const prof = collectProfile();
  let r;
  try { r = await api("llama/start", { profile: prof }); }
  catch (e) { alert("启动失败: " + e.message); return; }
  if (r.need_confirm) {
    const k = r.need_confirm.kind;
    if (k === "safetensors") {
      const v = await showModal({
        title: "自动转换 safetensors → GGUF",
        body: mEsc(r.need_confirm.message),
        buttons: [{ label: "取消", value: "cancel" }, { label: "Q8_0(省一半)", value: "q8_0" }, { label: "F16(精度优先)", cls: "primary", value: "f16" }]
      });
      if (v === "cancel") { logLine("--- 已取消:模型为 safetensors,未转换 ---"); return; }
      await api("llama/start", { profile: prof, confirm: { done: true, convert_dir: r.need_confirm.dir, outtype: v } });
      return;
    }
    if (k === "build_error") { alert(r.need_confirm.message); return; }
    if (k === "empty") { alert("Empty"); return; }
    // confirm:参数剔除 → 显存 → qwen35(一次汇总展示,与原版顺序一致)
    const nc = r.need_confirm;
    let msg = "";
    if (nc.violations && nc.violations.length) {
      msg += "以下参数未通过依赖/校验检查,将被剔除:\n\n" + nc.violations.map(v => " • " + v).join("\n") + "\n\n";
    }
    if (nc.vram) msg += "⚠ 预计显存不足,llama-server 很可能因显存耗尽而崩溃(bad allocation / GGML_ASSERT)。\n\n" + nc.vram + "\n\n(建议:减小上下文大小,或将 KV 缓存类型改为 q4_0/q4_1)\n\n";
    if (nc.qwen35) msg += "⚠ " + nc.qwen35 + "\n\n";
    const v = await showModal({
      title: "启动前确认", body: mEsc(msg.trim()),
      buttons: [{ label: "否,取消启动", value: "no" }, { label: "是,继续启动", cls: "danger", value: "yes" }]
    });
    if (v !== "yes") { logLine("--- 启动已取消 ---"); return; }
    await api("llama/start", { profile: prof, confirm: { done: true } });
  }
}

async function startDsh() {
  const prof = collectProfile();
  let r;
  try { r = await api("dsh/start", { profile: prof }); }
  catch (e) { alert("DSH 启动失败: " + e.message); return; }
  if (r.need_rebuild) {
    const v = await showModal({
      title: "DSH 仓库已升级",
      body: mEsc(r.need_rebuild.message),
      buttons: [{ label: "否,跳过重建直接启动", value: "no" }, { label: "是,自动重建后启动", cls: "primary", value: "yes" }]
    });
    const rr = await api(v === "yes" ? "dsh/rebuild_and_start" : "dsh/start_inner", { profile: prof });
    if (rr.error) alert("DSH: " + rr.error);
    return;
  }
  if (r.error) alert("DSH: " + r.error);
}

function logLine(t) { LOG_LINES.push(t); $("logArea").value = LOG_LINES.join("\n"); }

/* ============================== 方案操作 ================================ */
function renderProfileCombo() {
  const sel = $("comboProfile");
  sel.innerHTML = "";
  for (const name of Object.keys(CFG.Profiles || {})) {
    const o = document.createElement("option"); o.value = name; o.textContent = name;
    sel.appendChild(o);
  }
  sel.value = CFG.CurrentProfile;
}
async function switchProfile(name) {
  await saveProfileToCfg(false);           // 切换前静默保存 UI 到旧方案
  CFG.CurrentProfile = name;
  PROF = CFG.Profiles[name];
  await saveConfigFile(false);
  applyProfile(PROF);
  refreshPreview();
}
async function saveProfileToCfg(refresh = true) {
  const name = CFG.CurrentProfile;
  if (!CFG.Profiles[name]) CFG.Profiles[name] = {};
  CFG.Profiles[name] = collectProfile();
  if (refresh) await saveConfigFile();
}
async function saveConfigFile(notify = true) {
  const r = await api("config/save", { config: CFG });
  if (notify) logLine("[配置] 已保存: " + r.path);
}

/* ============================== 更新检查 ================================ */
async function checkUpdateLlama() {
  const dir = $("inLlamaDir").value.trim();
  const r = await api("update/check", { target: "llama", dir });
  if (r.error) { alert(r.error); return; }
  if (r.up_to_date) { alert(`llama.cpp 已是最新(build ${r.local})`); return; }
  const v = await showModal({
    title: "llama.cpp 更新",
    body: `本地 build: ${r.local ?? "未知"}\n上游最新: ${r.latest}(${r.tag})\n\n下载 ubuntu-x64 压缩包并整目录覆盖(只动二进制,不碰模型)?`,
    buttons: [{ label: "取消", value: "no" }, { label: "更新", cls: "primary", value: "yes" }]
  });
  if (v !== "yes") return;
  const j = await api("update/do", { target: "llama", dir });
  await pollJob(j.job);
}
async function checkUpdateDsh() {
  const dir = $("inDshDir").value.trim();
  const r = await api("update/check", { target: "dsh", dir });
  if (r.error) { alert("git 检查失败: " + r.error); return; }
  if (r.behind === 0) { logLine("[更新] DSH 已是最新(branch " + r.branch + ")"); return; }
  if (r.ahead > 0) { alert(`本地有 ${r.ahead} 个未推送提交,不自动更新;请先处理本地改动。`); return; }
  const v = await showModal({
    title: "DSH 更新",
    body: `branch ${r.branch}:落后 ${r.behind} 个提交。\n\n执行 git pull --ff-only?(升级后可能需要"🔨 重建 DSH")`,
    buttons: [{ label: "取消", value: "no" }, { label: "拉取", cls: "primary", value: "yes" }]
  });
  if (v !== "yes") return;
  const rr = await api("update/do", { target: "dsh", dir });
  logLine("[更新] DSH pull: " + (rr.msg || rr.ok));
}
async function pollJob(id) {
  for (;;) {
    const j = await api("job", { id });
    for (const l of j.log || []) if (!pollJob._seen.has(l)) { pollJob._seen.add(l); logLine("[更新] " + l); }
    if (j.status !== "running") {
      logLine("[更新] " + (j.status === "done" ? "完成: " + j.result : "失败: " + j.result));
      return;
    }
    await new Promise(r2 => setTimeout(r2, 1500));
  }
}
pollJob._seen = new Set();

/* ============================== 事件绑定 ================================ */
function wire() {
  // 路径相关输入 → 预览
  for (const id of ["inModel", "inMtp", "inMmproj", "inMmprojUrl", "inLlamaDir", "inDshDir"]) {
    $(id).addEventListener("input", () => { if (id === "inModel") { /* autofill 无 */ } schedulePreview(); });
  }
  for (const id of ["chkNoMmprojOffload", "comboDshMode", "chkDshAdmin", "chkBreaker", "comboBreakerType", "txtBreakerVal"]) {
    $(id).addEventListener("change", schedulePreview);
  }
  // 浏览按钮
  document.querySelectorAll("[data-br]").forEach(b => b.addEventListener("click", async () => {
    const v = await browsePicker(b.dataset.mode);
    if (v) { $(b.dataset.br).value = v; schedulePreview(); }
  }));
  // 配置按钮
  $("btnSaveCfg").onclick = () => saveProfileToCfg();
  $("btnExportCfg").onclick = async () => {
    await saveProfileToCfg(false);
    const blob = new Blob([JSON.stringify(CFG, null, 2)], { type: "application/json" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = "LlamaDSH_Launcher_config.json";
    a.click();
  };
  $("btnImportCfg").onclick = () => $("fileImport").click();
  $("fileImport").onchange = async (e) => {
    const f = e.target.files[0];
    if (!f) return;
    try {
      const data = JSON.parse(await f.text());
      const r = await api("config/save", { config: data });
      await loadAll();
      logLine("[配置] 已导入: " + r.path);
    } catch (err) { alert("导入失败: " + err.message); }
    e.target.value = "";
  };
  $("btnChangePath").onclick = async () => {
    const box = document.createElement("div");
    box.innerHTML = `<input type="text" id="cfgDir" class="mono" style="width:100%" value="${mEsc(CFG_DIR)}">`;
    const v = await showModal({
      title: "更改配置目录", body: "配置文件(LlamaDSH_Launcher_config.json)所在目录:", extra: box,
      buttons: [{ label: "取消", value: null }, { label: "确定", cls: "primary", value: "__ok__" }]
    });
    if (v !== "__ok__") return;
    const dir = box.querySelector("#cfgDir").value.trim();
    if (!dir) return;
    const r = await api("config/setdir", { dir });
    if (r.error) alert(r.error);
    else { await loadAll(); logLine("[配置] 配置目录: " + r.dir); }
  };
  // 方案操作
  $("comboProfile").onchange = () => switchProfile($("comboProfile").value);
  $("btnProfSave").onclick = () => saveProfileToCfg();
  $("btnProfNew").onclick = async () => {
    const name = prompt("新方案名称:", "方案 " + (Object.keys(CFG.Profiles).length + 1));
    if (!name) return;
    if (CFG.Profiles[name]) { alert("同名方案已存在"); return; }
    CFG.Profiles[name] = collectProfile();
    CFG.CurrentProfile = name;
    renderProfileCombo();
    await saveConfigFile();
    applyProfile(CFG.Profiles[name]);
  };
  $("btnProfRename").onclick = async () => {
    const old = CFG.CurrentProfile;
    const name = prompt("重命名方案:", old);
    if (!name || name === old) return;
    if (CFG.Profiles[name]) { alert("同名方案已存在"); return; }
    CFG.Profiles[name] = CFG.Profiles[old];
    delete CFG.Profiles[old];
    CFG.CurrentProfile = name;
    renderProfileCombo();
    await saveConfigFile();
  };
  $("btnProfDel").onclick = async () => {
    if (Object.keys(CFG.Profiles).length <= 1) { alert("至少保留一个方案"); return; }
    if (!confirm(`删除方案 "${CFG.CurrentProfile}"?`)) return;
    delete CFG.Profiles[CFG.CurrentProfile];
    CFG.CurrentProfile = Object.keys(CFG.Profiles)[0];
    renderProfileCombo();
    applyProfile(CFG.Profiles[CFG.CurrentProfile]);
    await saveConfigFile();
  };
  // 命令预览
  $("btnRefreshCmd").onclick = refreshPreview;
  $("btnReloadSchema").onclick = async () => {
    const prof = collectProfile();
    const r = await api("schema/reload", { profile: prof });
    SCHEMA = r.schema;
    renderParams();
    applyProfile(prof);
    refreshPreview();
    logLine("[参数] 配置已重载,来源: " + r.schema.source + ",参数 " + r.schema.params.length + " 项 / 组 " + r.schema.groups.length + " 个");
    if (r.messages && r.messages.length) {
      showModal({ title: "参数配置提示", body: mEsc(r.messages.join("\n")), buttons: [{ label: "确定", cls: "primary", value: 1 }] });
    }
  };
  $("btnCopyCmd").onclick = async () => {
    try { await navigator.clipboard.writeText($("cmdPreview").value); logLine("Copied"); }
    catch (e) { /* fallback */ const t = $("cmdPreview"); t.select(); document.execCommand("copy"); logLine("Copied"); }
  };
  // 服务按钮
  $("btnLlamaStart").onclick = startLlama;
  $("btnLlamaStop").onclick = () => api("llama/stop");
  $("btnDshStart").onclick = startDsh;
  $("btnDshStop").onclick = () => api("dsh/stop");
  $("btnOpenDsh").onclick = async () => { const r = await api("open/dsh"); window.open(r.url, "_blank"); };
  $("btnRebuildDsh").onclick = async () => {
    const v = await showModal({
      title: "重建 DSH(全量三步)",
      body: "1/3 pnpm clean\n2/3 pnpm build:lib\n3/3 vite build(apps/web)\n\nDSH 运行中会先停止(等 ≤20s)。确认开始?",
      buttons: [{ label: "取消", value: "no" }, { label: "开始重建", cls: "primary", value: "yes" }]
    });
    if (v !== "yes") return;
    const r = await api("dsh/rebuild", { profile: collectProfile() });
    if (r.error) alert(r.error);
  };
  // 维护工具
  $("btnCompat").onclick = async () => {
    const r = await api("dsh/compat", { profile: collectProfile() });
    const html = r.report.items.map(i => `<div class="${i.level === "error" ? "e" : i.level === "warn" ? "w" : i.level === "ok" ? "o" : "i"}">${mEsc(i.text)}</div>`).join("");
    await showModal({ title: "🩺 DSH 兼容性检查", body: html || "(无输出)", buttons: [{ label: "关闭", cls: "primary", value: 1 }] });
    for (const l of r.log) logLine(l);
  };
  $("btnPlugins").onclick = async () => {
    const r = await api("dsh/plugins", { profile: collectProfile() });
    const html = r.report.items.map(i => `<div class="i">${mEsc(i.name)} <span class="tiny">${mEsc(i.note || "")}</span></div>`).join("");
    await showModal({ title: `🧩 插件检测(${r.report.count} 个)`, body: html || "(无插件)", buttons: [{ label: "关闭", cls: "primary", value: 1 }] });
    for (const l of r.log) logLine(l);
  };
  // 检索/更新
  $("btnSearchLlama").onclick = async () => {
    logLine("[检索] 正在检索 llama-server(最长 45s)...");
    const r = await api("search", { target: "llama" });
    if (r.result) { $("inLlamaDir").value = r.result; logLine("[检索] 找到: " + r.result); refreshPreview(); }
    else logLine("[检索] 未找到 llama-server");
  };
  $("btnSearchDsh").onclick = async () => {
    logLine("[检索] 正在检索 DSH 项目(最长 45s)...");
    const r = await api("search", { target: "dsh" });
    if (r.result) { $("inDshDir").value = r.result; logLine("[检索] 找到: " + r.result); refreshPreview(); }
    else logLine("[检索] 未找到 DSH 项目");
  };
  $("btnUpdLlama").onclick = () => checkUpdateLlama().catch(e => alert(e.message));
  $("btnUpdDsh").onclick = () => checkUpdateDsh().catch(e => alert(e.message));
  // 日志
  $("btnClearLog").onclick = async () => { await api("logs/clear"); LOG_LINES = []; $("logArea").value = ""; };
  $("btnOpenLogDir").onclick = async () => {
    const r = await api("open/logs");
    if (!r.ok) logLine("[日志] 目录: " + (await api("bootstrap")).log_dir);
  };
}

/* ================================ 启动 ================================== */
async function loadAll() {
  const b = await api("bootstrap");
  SCHEMA = b.schema;
  CFG = b.config;
  CFG_DIR = b.config_dir || "";
  if (!CFG.Profiles) CFG = { Profiles: {}, CurrentProfile: "默认方案" };
  renderProfileCombo();
  PROF = CFG.Profiles[CFG.CurrentProfile] || Object.values(CFG.Profiles)[0];
  CFG.Profiles[CFG.CurrentProfile] = PROF;
  renderParams();
  applyProfile(PROF);
  $("paramFilter").addEventListener("input", applyParamFilter);
  $("paramFilterClear").addEventListener("click", () => { $("paramFilter").value = ""; applyParamFilter(); });
  await refreshPreview();
  if (b.schema.errors && b.schema.errors.length) {
    logLine("[参数] ⚠ schema 错误: " + b.schema.errors.join("; "));
  }
}

(async function init() {
  wire();
  await loadAll();
  pollStatus(); pollLogs();
  setInterval(pollStatus, 2000);
  setInterval(pollLogs, 1000);
})();
