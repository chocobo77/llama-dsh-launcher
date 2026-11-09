/* LlamaDSH Launcher(Linux 复刻版)前端逻辑 — 2026-10 同步版。
 * 复刻 MainWindow.xaml 终版交互: 左栏三标签(运行环境/参数配置/命令预览) +
 * 右栏运行控制 + 监测页(Strata 风格)/运行日志 + 逐参数回显 + 通知条。 */
"use strict";

const $ = (id) => document.getElementById(id);
let SCHEMA = { groups: [], params: [] };
let CFG = { Profiles: {}, CurrentProfile: "默认方案" };
let PROF = null;
let LOG_AFTER = 0, LOG_LINES = [], LOG_BOOT = "";
let CFG_DIR = "";
let FLAG_ARGS = {};          // 逐参数发射片段(preview 下发)
let LAST_TOTAL_EVENTS = -1;  // 事件流增量指纹(4bf9416: 单调序号)
let MON_TAB = "mon";
// 轮询 in-flight 保护: 上一轮未返回则跳过本轮(服务卡顿时请求/线程不堆积)
const POLL_INFLIGHT = {};
function pollGuard(name, fn) {
  if (POLL_INFLIGHT[name]) return;
  POLL_INFLIGHT[name] = true;
  Promise.resolve().then(fn).catch(() => {}).finally(() => { POLL_INFLIGHT[name] = false; });
}
let NAV_SEL = null;          // {grp, sub}
/* 参数 UI 状态表(等价 CS _pbw/_pw 控件字典): 导航式渲染下只有当前子组挂载 DOM,
 * collect/apply/依赖评估一律读写状态表, DOM 仅作展示层 */
const UI_BOOL = {};   // flag -> bool
const UI_VAL = {};    // flag -> {Enabled, Value}

function initState() {
  for (const d of SCHEMA.params) {
    if (d.hidden || d.flag === "model" || d.flag === "model-draft") continue;
    if (d.type === "Bool") {
      UI_BOOL[d.flag] = d.default === "true";
    } else {
      UI_VAL[d.flag] = { Enabled: !!d.enabled, Value: d.default || "" };
    }
  }
}
const GIB = 1073741824;

async function api(path, body, timeoutMs = 12000) {
  let r;
  try {
    r = await fetch("/api/" + path, {
      method: "POST", headers: { "Content-Type": "application/json", "X-Auth": window.__DSH_TOKEN || "" },
      body: JSON.stringify(body || {}),
      signal: AbortSignal.timeout(timeoutMs)   // 服务卡顿时请求不无限挂起
    });
  } catch (ex) {
    // 浏览器中止的原文案是 "signal timed out": 直接显示等于没说明, 用户只会再点一次。
    // 上游查询这类慢调用由调用方带更大的 timeoutMs(与服务端预算同量级), 这里只负责说清。
    if (ex && ex.name === "TimeoutError") {
      throw new Error(`请求 ${path} 超过 ${Math.round(timeoutMs / 1000)}s 未完成(网络候选都慢?), 可稍后重试`);
    }
    throw ex;
  }
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
const mEsc = (s) => String(s ?? "").replace(/&/g, "&amp;").replace(/</g, "&lt;")
  .replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&#39;");

/* ================================ 通知条 ================================= */
let noticeTimer = null;
function showNotice(text, isError) {
  if (text === undefined || text === null || String(text).trim() === "") {
    text = isError ? "(操作失败,详情见运行日志)" : "已完成";
  }
  $("noticeText").textContent = String(text);
  const bar = $("noticeBar");
  bar.classList.toggle("error", !!isError);
  bar.classList.add("show");
  clearTimeout(noticeTimer);
  noticeTimer = setTimeout(() => bar.classList.remove("show"), isError ? 12000 : 8000);
}

/* ============================ 目录/文件浏览 ============================= */
async function browsePicker(mode) {
  let cur = null, sel = null;
  const box = document.createElement("div");
  box.innerHTML = `<input type="text" id="browserPath"><div id="browser"></div>
    <div class="tiny" id="browserSel" style="margin-top:4px"></div>`;
  const list = box.querySelector("#browser");
  const pathIn = box.querySelector("#browserPath");
  const selInfo = box.querySelector("#browserSel");
  async function load(p) {
    const r = await api("browse", { path: p }, 30000);   // 大目录(网络盘)扫描可能要好几秒
    if (r.error) { selInfo.textContent = r.error; return; }
    cur = r.path; sel = null; selInfo.textContent = "";
    pathIn.value = cur;
    list.innerHTML = "";
    if (r.files_truncated) {
      const t = document.createElement("div");
      t.className = "tiny"; t.textContent = "⚠ 文件过多,仅显示前 500 个(可用上方路径框直接输入)";
      list.appendChild(t);
    }
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
      const isTgz = /\.(tgz|tar\.gz)$/i.test(f.name);
      if (mode === "dir") continue;
      if (mode === "plugin_src" && !isTgz) continue;
      const d = document.createElement("div");
      d.className = "item";
      const sz = f.size > 1073741824 ? (f.size / GIB).toFixed(2) + " GB"
        : (f.size / 1048576).toFixed(1) + " MB";
      d.innerHTML = `<span>${isTgz ? "📦" : "📄"} ${mEsc(f.name)}</span><span class="tiny">${sz}</span>`;
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
  await load("");
  const v = await showModal({
    title: mode === "plugin_src" ? "选择插件包(.tgz)或插件目录" : "选择文件 / 目录",
    body: mode === "plugin_src" ? "选中一个 .tgz 文件后点确定;或直接点「选择当前目录」安装目录形态的插件。" : "",
    extra: box,
    buttons: [{ label: "取消", value: null },
      { label: mode === "dir" ? "选择当前目录" : "确定", cls: "primary", value: "__ok__" },
      ...(mode === "plugin_src" ? [{ label: "选择当前目录", value: "__dir__" }] : [])]
  });
  if (v === null) return null;
  if (v === "__dir__") return cur;
  if (mode === "dir") return cur;
  return sel || pathIn.value;
}

/* ============================== 方案编辑 ================================ */
function collectProfile() {
  const bp = {}, vp = {};
  for (const d of SCHEMA.params) {
    if (d.hidden || d.flag === "model" || d.flag === "model-draft") continue;
    if (d.type === "Bool") {
      bp[d.flag] = !!UI_BOOL[d.flag];
    } else {
      const rec = UI_VAL[d.flag] || { Enabled: false, Value: "" };
      vp[d.flag] = { Enabled: !!rec.Enabled, Value: String(rec.Value ?? "").trim() };
    }
  }
  // 以现有方案为底(保留 LlamaVariants/ActiveLlamaVariant/CustomArgs/VramMeasured* 等
  // 服务器侧字段), UI 管理的字段覆盖其上
  const base = (typeof PROF === "object" && PROF) ? JSON.parse(JSON.stringify(PROF)) : {};
  return Object.assign(base, {
    ModelPath: $("inModel").value.trim(), MtpPath: $("inMtp").value.trim(),
    MmprojPath: $("inMmproj").value.trim(), MmprojUrl: $("inMmprojUrl").value.trim(),
    NoMmprojOffload: $("chkNoMmprojOffload").checked,
    LlamaDir: $("inLlamaDir").value.trim(), DshDir: $("inDshDir").value.trim(),
    DshMode: parseInt($("comboDshMode").value) || 0,
    DshAdminMode: $("chkDshAdmin").checked,
    MinimizeToTray: false,
    BoolParams: bp, ValueParams: vp,
    VramBreakerEnabled: $("chkBreaker").checked,
    VramBreakerType: parseInt($("comboBreakerType").value) || 0,
    VramBreakerValue: parseFloat($("txtBreakerVal").value) || 90,
    LlamaBackend: $("comboBackend").value,
  });
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
  $("comboBackend").value = (p.LlamaBackend || "").toLowerCase();
  for (const d of SCHEMA.params) {
    if (d.hidden || d.flag === "model" || d.flag === "model-draft") continue;
    if (d.type === "Bool") {
      const on = (p.BoolParams || {})[d.flag];
      UI_BOOL[d.flag] = on === undefined ? d.default === "true" : !!on;
    } else {
      const rec = (p.ValueParams || {})[d.flag];
      UI_VAL[d.flag] = rec
        ? { Enabled: !!rec.Enabled, Value: rec.Value ?? "" }
        : { Enabled: !!d.enabled, Value: d.default || "" };
    }
  }
  renderCurrentSubgroup();
  updateCustomPreview();
  depEval();
  updateArgEcho();
}

function renderCurrentSubgroup() {
  if (!NAV_SEL) return;
  const entry = NAV_INDEX.find(x => x.g.id === NAV_SEL.grp && x.sub === NAV_SEL.sub);
  if (entry) {
    const host = $("paramContent");
    host.innerHTML = "";
    for (const d of entry.items) buildParamRow(d, host);
  }
}

const cssId = (s) => s.replace(/[^a-zA-Z0-9_-]/g, "_");

/* ------------------- 参数导航(组→子组)与渲染 ------------------- */
function buildNav() {
  const host = $("paramNavPanel");
  host.innerHTML = "";
  const navIndex = [];
  for (const g of SCHEMA.groups) {
    const params = SCHEMA.params.filter(p => p.group === g.id && !p.hidden);
    if (!params.length) continue;
    const gt = document.createElement("div");
    gt.className = "navGrpTitle";
    gt.textContent = g.title;
    host.appendChild(gt);
    const bySub = new Map();
    for (const d of params) {
      const k = d.subgroup || "";
      if (!bySub.has(k)) bySub.set(k, []);
      bySub.get(k).push(d);
    }
    for (const [sub, items] of bySub) {
      const item = document.createElement("div");
      item.className = "navItem";
      const label = sub || "其它";
      item.innerHTML = `<span>${mEsc(label)}</span><span class="navBadge">${items.length}</span>`;
      item.onclick = () => selectSubgroup(g, sub, items);
      item.dataset.key = g.id + "|" + sub;
      host.appendChild(item);
      navIndex.push({ g, sub, items, el: item });
    }
  }
  NAV_INDEX = navIndex;
  if (navIndex.length) selectSubgroup(navIndex[0].g, navIndex[0].sub, navIndex[0].items);
}
let NAV_INDEX = [];

function selectSubgroup(g, sub, items) {
  NAV_SEL = { grp: g.id, sub };
  document.querySelectorAll(".navItem").forEach(x =>
    x.classList.toggle("active", x.dataset.key === g.id + "|" + sub));
  const label = sub || "其它";
  // 层级化面包屑: 组名(一级, accent 加粗) ▸ 子组(二级, 亮色) · 计数(弱色, 靠右)
  $("paramBreadcrumb").innerHTML =
    `<span class="bcGrp">${mEsc(g.title)}</span><span class="bcSep">▸</span>` +
    `<span class="bcSub">${mEsc(label)}</span><span class="bcCnt">${items.length} 个参数</span>`;
  const host = $("paramContent");
  host.innerHTML = "";
  for (const d of items) buildParamRow(d, host);
  depEval();
  updateArgEcho();
}


function buildParamRow(d, container) {
  const row = document.createElement("div");
  row.className = "prow" + (d.wide ? " wide" : "");
  row.id = "pr_" + cssId(d.flag);
  const tip = (d.tooltip || "") + (d.deprecated ? "\n⚠ 已废弃" : "");
  if (d.type === "Bool") {
    const on = UI_BOOL[d.flag];
    row.innerHTML = `<span><input type="checkbox" id="pb_${cssId(d.flag)}"${on ? " checked" : ""}></span>
      <span class="plabel" title="${mEsc(tip)}">${mEsc(d.label)}</span>`;
    const cb = row.querySelector("input");
    cb.addEventListener("change", () => { UI_BOOL[d.flag] = cb.checked; depEval(); schedulePreview(); });
    const echo = document.createElement("div");
    echo.className = "argEcho boolEcho";
    echo.id = "ae_" + cssId(d.flag);
    row.appendChild(echo);
  } else {
    const rec = UI_VAL[d.flag] || { Enabled: false, Value: d.default || "" };
    row.innerHTML = `<span><input type="checkbox" id="pe_${cssId(d.flag)}"${rec.Enabled ? " checked" : ""}></span>
      <span class="plabel" title="${mEsc(tip)}">${mEsc(d.label)}</span><span id="pw_${cssId(d.flag)}" style="flex:1;display:flex"></span>`;
    const wrap = row.querySelector("#pw_" + cssId(d.flag));
    let inp;
    if (d.type === "Combo" && (d.options || []).length) {
      inp = document.createElement("select");
      for (const o of d.options) {
        const op = document.createElement("option"); op.value = o; op.textContent = o;
        inp.appendChild(op);
      }
      inp.value = rec.Value || d.default || (d.options[0] || "");
    } else {
      inp = document.createElement("input");
      inp.type = "text";
      inp.value = rec.Value ?? (d.default || "");
      inp.classList.add("wrap");
    }
    inp.id = "pv_" + cssId(d.flag);
    inp.title = mEsc(tip);
    inp.addEventListener("input", () => { UI_VAL[d.flag].Value = inp.value.trim(); depEval(); schedulePreview(); });
    inp.addEventListener("change", () => { UI_VAL[d.flag].Value = inp.value.trim(); depEval(); schedulePreview(); });
    wrap.appendChild(inp);
    const en = row.querySelector("#pe_" + cssId(d.flag));
    en.addEventListener("change", () => { UI_VAL[d.flag].Enabled = en.checked; depEval(); schedulePreview(); });
    const echo = document.createElement("div");
    echo.className = "argEcho";
    echo.id = "ae_" + cssId(d.flag);
    row.appendChild(echo);
  }
  container.appendChild(row);
}

/* --------------------- 参数过滤 --------------------- */
function applyParamFilter() {
  const q = ($("inputParamFilter").value || "").trim().toLowerCase();
  $("btnClearParamFilter").style.display = q ? "" : "none";
  const matches = (d) => !q
    || d.flag.toLowerCase().includes(q)
    || (d.label || "").toLowerCase().includes(q)
    || (d.subgroup || "").toLowerCase().includes(q)
    || (d.tooltip || "").toLowerCase().includes(q);
  const host = $("paramContent");
  if (q) {
    // 过滤模式: 跨分类命中列表
    const hits = SCHEMA.params.filter(d => !d.hidden && matches(d));
    const cats = new Set(hits.map(d => d.group));
    $("paramBreadcrumb").innerHTML =
      `🔍 "${mEsc(q)}" · 命中 ${hits.length} 个参数 / ${cats.size} 个分类`;
    host.innerHTML = "";
    for (const d of hits) {
      const div = document.createElement("div");
      div.className = "filterHit";
      div.innerHTML = `<b>${mEsc(d.label)}</b> <code>${mEsc(d.flag)}</code>
        <span class="fhit-sub">· ${mEsc(groupTitle(d.group))}${d.subgroup ? " ▸ " + mEsc(d.subgroup) : ""}</span>`;
      div.onclick = () => { jumpToParam(d); };
      host.appendChild(div);
    }
    return;
  }
  // 非过滤: 回到当前导航选择
  const cur = NAV_INDEX.find(x => NAV_SEL && x.g.id === NAV_SEL.grp && x.sub === NAV_SEL.sub);
  if (cur) selectSubgroup(cur.g, cur.sub, cur.items);
}

function groupTitle(gid) {
  const g = SCHEMA.groups.find(x => x.id === gid);
  return g ? g.title : gid;
}

function jumpToParam(d) {
  const entry = NAV_INDEX.find(x => x.g.id === d.group && x.sub === (d.subgroup || ""));
  if (entry) selectSubgroup(entry.g, entry.sub, entry.items);
  const el = $("pr_" + cssId(d.flag));
  if (el) {
    el.scrollIntoView({ block: "center" });
    el.style.outline = "1px solid var(--accent)";
    setTimeout(() => { el.style.outline = ""; }, 1200);
  }
}

/* --------------------- 依赖评估(EvaluateDepUI 复刻) --------------------- */
function isOn(d) {
  if (d.type === "Bool") return !!UI_BOOL[d.flag];
  const rec = UI_VAL[d.flag] || {};
  return !!rec.Enabled && String(rec.Value ?? "").trim().length > 0;
}
function effVal(d) {
  if (d.type === "Bool") return UI_BOOL[d.flag] ? "true" : "false";
  const rec = UI_VAL[d.flag] || {};
  const en = !!rec.Enabled, v = String(rec.Value ?? "").trim();
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
function requiresOk(d) {
  if (!d.requires || !d.requires.length) return true;
  return d.requires.every(ruleMet);
}
function depEval() {
  for (const d of SCHEMA.params) {
    if (d.hidden || !d.implies || !isOn(d)) continue;
    for (const t of d.implies) {
      const tb = $("pb_" + cssId(t)), te = $("pe_" + cssId(t));
      if (tb) tb.checked = true;
      if (te) te.checked = true;
    }
  }
  for (const d of SCHEMA.params) {
    if (d.hidden || !d.requires || !d.requires.length) continue;
    const ok = requiresOk(d);
    const row = $("pr_" + cssId(d.flag));
    if (row) {
      row.classList.toggle("dep-off", !ok);
      const why = d.requires.map(r => describeRule(r)).join(" 且 ");
      for (const id of ["pb_" + cssId(d.flag), "pe_" + cssId(d.flag), "pv_" + cssId(d.flag)]) {
        const el = $(id); if (el) { el.disabled = !ok; el.title = ok ? (d.tooltip || "") : (d.tooltip || "") + "\n⚠ 前置未满足: " + why; }
      }
    }
  }
}
const describeRule = (r) => (r.op === "enabled" ? `--${r.flag} 已启用` :
  r.op === "disabled" ? `--${r.flag} 已停用` : `--${r.flag} ${r.op} ${r.value}`);

/* ------------------- 逐参数真实启动参数回显(48a9a2f) ------------------- */
function updateArgEcho() {
  // 只遍历当前挂载的回显行(导航式渲染下其余子组本就不在 DOM)
  const byId = {};
  for (const d of SCHEMA.params) byId[cssId(d.flag)] = d;
  for (const el of document.querySelectorAll(".argEcho")) {
    const d = byId[el.id.slice(3)];
    if (!d) continue;
    const emitted = FLAG_ARGS[d.flag];
    if (emitted && emitted.length) {
      el.textContent = emitted.join(" ");
      el.classList.add("on");
    } else {
      const ok = requiresOk(d);
      let txt;
      if (d.type === "Bool") {
        txt = d.offFlag && !ok ? "" : "--" + d.flag;
      } else {
        const v = effVal(d);
        txt = "--" + d.flag + (v ? " " + v : "");
      }
      el.textContent = txt;
      el.classList.remove("on");
    }
  }
}

/* ============================== 命令预览 ================================ */
let previewTimer = null, previewSeq = 0;
function schedulePreview() {
  clearTimeout(previewTimer);
  previewTimer = setTimeout(refreshPreview, 300);
}
async function refreshPreview() {
  const seq = ++previewSeq;   // 乱序保护: 旧响应不得覆盖新结果(estimate 走子进程, 延迟真实存在)
  try {
    const r = await api("preview", { profile: collectProfile() }, 20000);
    if (seq !== previewSeq) return;
    $("cmdPreview").value = r.fmt_cmd || "";
    FLAG_ARGS = r.flag_args || {};
    updateArgEcho();
  } catch (e) {
    if (seq === previewSeq) $("cmdPreview").value = "Error:" + e.message;
  }
}

/* ------------------- 自定义参数(仅追加) ------------------- */
function updateCustomPreview() {
  const args = (PROF && PROF.CustomArgs) || [];
  const el = $("cmdCustomPreview");
  if (args.length) {
    el.style.display = "";
    el.textContent = "+ " + args.join(" ");
    $("btnCancelCmd").style.display = "";
  } else {
    el.style.display = "none";
    $("btnCancelCmd").style.display = "none";
  }
}
async function editCustomArgs() {
  const box = document.createElement("div");
  const cur = (PROF && PROF.CustomArgs || []).join("\n");
  box.innerHTML = `<div class="tiny" style="margin-bottom:6px">每行一条,原样追加到命令行末尾(示意 --x=y 或位置参数):</div>
    <textarea id="customArgs" class="mono" style="width:100%;height:120px;resize:vertical">${mEsc(cur)}</textarea>`;
  const v = await showModal({
    title: "✏ 编辑命令(仅追加自定义参数)", body: "", extra: box,
    buttons: [{ label: "取消", value: null }, { label: "保存", cls: "primary", value: "__ok__" }]
  });
  if (v !== "__ok__") return;
  PROF.CustomArgs = box.querySelector("#customArgs").value.split("\n")
    .map(s => s.trim()).filter(Boolean);
  CFG.Profiles[CFG.CurrentProfile] = PROF;
  updateCustomPreview();
  refreshPreview();
}

/* ================================ 日志 ================================== */
async function pollLogs() {
  try {
    const r = await api("logs", { after: LOG_AFTER });
    // 游标 seq 每次服务端重启都从 1 重来: 手上的 after 会永远大于新的 seq_last,
    // 于是日志面板静默冻结(看不出是没日志还是收不到了)。boot 变了或 gap=true
    // 都表示游标作废, 归零重收并清掉面板, 免得把旧内容再堆一份。
    if ((r.boot && r.boot !== LOG_BOOT) || r.gap) {
      LOG_BOOT = r.boot || "";
      LOG_AFTER = 0;
      LOG_LINES = [];
      $("logArea").value = "";
      return;
    }
    if (r.lines && r.lines.length) {
      LOG_AFTER = r.after;
      for (const l of r.lines) LOG_LINES.push((l.level && l.level !== "INFO") ? ("[" + l.level + "] " + l.text) : l.text);
      if (LOG_LINES.length > 4000) LOG_LINES = LOG_LINES.slice(-3500);
      $("logArea").value = LOG_LINES.join("\n");
      if ($("chkAutoScroll").checked) { $("logArea").scrollTop = $("logArea").scrollHeight; }
    }
  } catch (e) { /* ignore */ }
}

/* ============================ 服务状态轮询 ============================== */
function setStatus(el, st) {
  const map = {
    running: ["● 运行中", "st-run"], running_admin: ["● 运行中(提权)", "st-run"],
    taken_over: ["● 接管运行中", "st-run"], starting: ["● 启动中...", "st-start"],
    rebuilding: ["🔨 Rebuilding...", "st-start"], stopped: ["● 已停止", "st-stop"],
    failed: ["● 启动失败", "st-fail"], crashed: ["● 启动失败/崩溃", "st-fail"],
    // 进程还在(杀不掉, 常见于以 root 启动): 不能显示成"已停止", 也不能点亮 Start
    stop_failed: ["⚠ 停止失败(进程仍在)", "st-fail"],
  };
  const [t, c] = map[st] || map.stopped;
  el.textContent = t; el.className = "status " + c;
}
async function pollStatus() {
  let s;
  try { s = await api("status"); } catch (e) { return; }
  setStatus($("lblLlamaStatus"), s.llama.status);
  setStatus($("lblDshStatus"), s.dsh.rebuilding ? "rebuilding" : s.dsh.status);
  const llamaBusy = ["running", "starting", "taken_over", "stop_failed"].includes(s.llama.status);
  $("btnLlamaStart").disabled = llamaBusy;
  $("btnLlamaStop").disabled = !llamaBusy;
  const dshBusy = ["running", "running_admin", "starting", "taken_over", "rebuilding",
                   "stop_failed"].includes(s.dsh.status);
  $("btnDshStart").disabled = dshBusy;
  $("btnDshStop").disabled = !dshBusy;
  $("btnOpenDsh").disabled = !["running", "running_admin", "taken_over"].includes(s.dsh.status);
  const lw = $("lblLightweight");
  if (lw) lw.style.display = s.lightweight ? "" : "none";
}

/* ============================== 监测页 ================================= */
const FT = (t) => (t == null ? null : (t < 10 ? (Math.round(t * 10) / 10) : Math.round(t)));
const fmtN = (x, d = 1) => (x == null ? "—" : Number(x).toFixed(d));
const fmtTok = (n) => (n == null ? "—" :
  n >= 1e9 ? (n / 1e9).toFixed(1) + "B" :
  n >= 1e6 ? (n / 1e6).toFixed(1) + "M" :
  n >= 1e3 ? (n / 1e3).toFixed(1) + "K" : String(Math.round(n)));
const fmtEta = (sec) => {
  if (sec == null || sec < 0) return "—";
  sec = Math.round(sec);
  if (sec < 60) return sec + "s";
  if (sec < 3600) return Math.floor(sec / 60) + "m" + String(sec % 60).padStart(2, "0") + "s";
  return Math.floor(sec / 3600) + "h" + String(Math.floor((sec % 3600) / 60)).padStart(2, "0") + "m";
};
const setTxt = (id, t) => { $(id).textContent = t; };

let monTimer = null;
async function pollMonitor() {
  if (MON_TAB !== "mon") return;  // 页面不可见由采样端降频(这里不主动拉)
  let m;
  try { m = await api("monitor", { visible: true }); } catch (e) { return; }
  renderMonitor(m);
}

function renderMonitor(m) {
  /* ---- 帧龄: 轻量化驻留时服务端故意不重新采样, 旧帧必须自己说明自己多旧,
     否则几小时前的显存/CPU 会被读成"现在"。阈值取正常 3s 采样的若干倍。 ---- */
  const age = (typeof m.sampled_at === "number") ? (Date.now() / 1000 - m.sampled_at) : null;
  setTxt("monStale", (age != null && age > 15)
    ? `本帧已 ${fmtEta(age)}未刷新${m.lightweight ? "(轻量化驻留)" : "(采样可能已停)"}` : "");
  /* ---- 摘要条 ---- */
  const gpus = m.gpus || [];
  const utils = gpus.map(g => g.util).filter(v => v != null);
  const mems = gpus.map(g => g.vram_used).filter(v => v != null);
  const temps = gpus.map(g => g.temp).filter(v => v != null);
  const pws = gpus.map(g => g.power_w).filter(v => v != null);
  setTxt("stripGpu", utils.length ? fmtN(utils.reduce((a, b) => a + b, 0) / utils.length, 0) + "%" : "—");
  setTxt("stripMem", mems.length ? fmtN(mems.reduce((a, b) => a + b, 0) / GIB) + " GB" : "—");
  setTxt("stripTemp", temps.length ? fmtN(Math.max(...temps), 0) + "°C" : "—");
  setTxt("stripPower", pws.length ? fmtN(pws.reduce((a, b) => a + b, 0), 0) + " W" : "—");

  /* ---- 核心指标卡 ---- */
  const gen = m.speeds?.gen ?? 0, prompt = m.speeds?.prompt ?? 0;
  const gd = m.gen_detail || {};
  setTxt("genSub", m.phase === "decoding" ? "decoding" : "");
  setTxt("pfSub", m.phase === "prompt_processing" ? "prefilling" : "");
  setTxt("genVal", FT(gen) != null ? FT(gen) : "—");
  setTxt("pfVal", FT(prompt) != null ? FT(prompt) : "—");
  setTxt("genFoot", (m.phase === "decoding" && gd.tg != null)
    ? `tg ${fmtN(gd.tg, 2)} · tg3 ${fmtN(gd.tg3, 2)} tok/s`
    : (m.online ? "来源 " + (m.speeds.source === "log" ? "日志" : "/slots 差分") : ""));
  setTxt("pfFoot", (m.phase === "prompt_processing" && m.progress?.log)
    ? "" : (gd.last_prefill != null ? `上次预填充 ${fmtN(gd.last_prefill)} tok/s` : ""));
  if (m.progress && m.progress.prompt_elapsed != null) {
    setTxt("pfFoot", `已耗时 ${fmtN(m.progress.prompt_elapsed)}s · 总量 ${fmtTok(m.progress.prompt_total)} tok`);
  }
  const actSlot = (m.slots || []).find(s => s.processing);
  if (actSlot && actSlot.n_decoded != null && actSlot.n_remain > 0) {
    const pct = Math.min(100, actSlot.n_decoded * 100 / (actSlot.n_decoded + actSlot.n_remain));
    $("genBar").style.width = pct.toFixed(1) + "%";
    $("genMax").textContent = fmtTok(actSlot.n_decoded + actSlot.n_remain) + " tok";
  } else { $("genBar").style.width = "0"; $("genMax").textContent = "—"; }
  if (m.progress && m.progress.prompt_progress != null) {
    $("pfBar").style.width = (m.progress.prompt_progress * 100).toFixed(1) + "%";
    $("pfMax").textContent = fmtTok(m.progress.prompt_total) + " tok";
  } else { $("pfBar").style.width = "0"; $("pfMax").textContent = "—"; }

  // 上下文卡
  const cu = m.ctx || {};
  const pct = cu.pct;
  const cv = $("ctxVal");
  cv.style.color = pct == null ? "var(--bright)" : pct >= 90 ? "var(--err)" : pct >= 70 ? "var(--warn)" : "var(--bright)";
  cv.innerHTML = `<span>${cu.used >= 0 ? fmtTok(cu.used) : "—"}</span><span class="munit">tok</span>`;
  $("ctxBar").style.width = pct != null ? Math.min(100, pct).toFixed(1) + "%" : "0";
  $("ctxBar").style.background = pct == null ? "var(--ok)" : pct >= 90 ? "var(--err)" : pct >= 70 ? "var(--warn)" : "var(--ok)";
  setTxt("ctxMax", cu.total > 0 ? fmtTok(cu.total) + " tok" : "—");
  setTxt("ctxFoot", pct != null ? `${fmtN(pct)}% · 剩余 ${fmtTok(Math.max(0, cu.total - cu.used))} tokens`
    : (m.online ? "无请求记录" : ""));
  $("ctxBadge").style.display = pct != null && pct >= 90 ? "" : "none";

  // MTP 环
  const acc = m.mtp?.acc;
  const ring = $("mtpRing");
  ring.setAttribute("pathLength", 100);
  if (acc != null) {
    ring.style.strokeDasharray = `${Math.max(0.02, 24.74 * acc).toFixed(2)} 100`;
    ring.style.stroke = acc < 0.65 ? "var(--err)" : acc < 0.8 ? "var(--warn)" : "var(--cyan)";
    ring.style.opacity = 1;
    setTxt("mtpVal", (acc * 100).toFixed(1) + "%");   // b3d3527: 显示统一 ×100
    let sub = `${m.mtp.accepted} / ${m.mtp.generated} · mean ${fmtN(m.mtp.mean_len, 2)}`;
    if (m.mtp.graphs != null) sub += ` · graphs ${m.mtp.graphs}`;
    setTxt("mtpSub", sub);
  } else {
    ring.style.strokeDasharray = "0 100";
    ring.style.opacity = 0.25;
    setTxt("mtpVal", "—");
    setTxt("mtpSub", "启用投机解码后显示");
  }
  drawSpark($("sparkGen"), (m.hists?.tps || [[], []])[0], (m.hists?.tps || [[], []])[1], null, "#3aa9ff", "t/s");
  drawSpark($("sparkPf"), (m.hists?.pf || [[], []])[0], (m.hists?.pf || [[], []])[1], null, "#34d399", "t/s");
  drawSpark($("sparkCtx"), (m.hists?.ctx || [[], []])[0], (m.hists?.ctx || [[], []])[1], cu.total > 0 ? cu.total : null, "#f5b53d", "tok");
  drawSpark($("sparkMtp") || null, [], [], null, "#3aa9ff", "");

  /* ---- GPU 按卡面板 ---- */
  renderGpuPanels(gpus);

  /* ---- 相位卡 ---- */
  renderPhaseCard(m);

  /* ---- 事件流(按单调序号判增量) ---- */
  if (m.total_events !== LAST_TOTAL_EVENTS || !$("monEventFeed").childElementCount) {
    LAST_TOTAL_EVENTS = m.total_events;
    const feed = $("monEventFeed");
    feed.innerHTML = "";
    for (const ev of (m.events || []).slice(0, 200)) {
      const div = document.createElement("div");
      div.className = "evt evt-" + (ev.level || "info");
      div.textContent = `${ev.ts}  ${ev.msg}`;
      feed.appendChild(div);
    }
    setTxt("monEventCount", `${m.total_events} / 200`);
  }

  /* ---- 系统资源 ---- */
  renderSystem(m.sys || {});

  /* ---- 模型与 Slot ---- */
  renderModelSlots(m);

  /* ---- 趋势图 ---- */
  renderTrends(m);
}

function renderGpuPanels(gpus) {
  const host = $("gpuPanelsHost");
  if (!gpus.length) {
    host.innerHTML = `<div class="gpuNone">▣ 数据不可用</div>`;
    return;
  }
  host.innerHTML = "";
  for (const g of gpus) {
    const card = document.createElement("div");
    card.className = "gpuCard";
    const util = g.util;
    const vu = g.vram_used, vt = g.vram_total;
    const vpct = (vu != null && vt > 0) ? vu / vt * 100 : null;
    const ringColor = util == null ? "var(--muted)" : util >= 90 ? "var(--err)" : util >= 70 ? "var(--warn)" : "var(--ok)";
    card.innerHTML = `
      <div class="gpuHead">${mEsc("GPU" + g.index + " · " + (g.name || "GPU"))}</div>
      <div class="gpuBody">
        <div class="utilRingWrap">
          <svg viewBox="0 0 104 104">
            <circle cx="52" cy="52" r="44" pathLength="100" fill="none" stroke="var(--track)" stroke-width="8"
              stroke-dasharray="75 100" transform="rotate(135 52 52)" stroke-linecap="round"/>
            <circle cx="52" cy="52" r="44" pathLength="100" fill="none" stroke="${ringColor}" stroke-width="8"
              stroke-dasharray="${(0.75 * (util || 0)).toFixed(2)} 100" transform="rotate(135 52 52)" stroke-linecap="round"/>
          </svg>
          <div class="utilCenter"><div class="utilVal">${util == null ? "—" : Math.round(util)}</div><div class="utilSub">利用率</div></div>
        </div>
        <div>
          <div class="gvram">显存 ${vu != null ? fmtN(vu / GIB) : "—"} / ${vt > 0 ? fmtN(vt / GIB) : "—"} GB${vpct != null ? " (" + vpct.toFixed(1) + "%)" : ""}</div>
          <div class="mbar" style="margin-top:4px"><div class="mbarfill" style="width:${vpct != null ? Math.min(100, vpct).toFixed(1) : 0}%;
            background:${vpct != null && vpct >= 90 ? "var(--err)" : vpct != null && vpct >= 70 ? "var(--warn)" : "var(--ok)"}"></div></div>
          <div class="gpuKv">
            <div><span class="mlbl">温度</span><span class="mvalS" style="${g.temp != null && g.temp >= 85 ? "color:var(--err)" : ""}">${g.temp != null ? Math.round(g.temp) + "°C" : "—"}</span></div>
            <div><span class="mlbl">功耗</span><span class="mvalS">${g.power_w != null ? fmtN(g.power_w) + (g.power_limit_w != null ? " / " + fmtN(g.power_limit_w) : "") : "—"} W</span></div>
            <div><span class="mlbl">风扇</span><span class="mvalS">${g.fan_pct != null ? Math.round(g.fan_pct) + "%" : "—"}</span></div>
            <div><span class="mlbl">频率</span><span class="mvalS">${g.clock_mhz != null ? Math.round(g.clock_mhz) + " MHz" : "—"}</span></div>
            <div><span class="mlbl">PCIe</span><span class="mvalS">${g.pcie_gen != null ? "gen " + g.pcie_gen + " x" + (g.pcie_width ?? "?") : "—"}</span></div>
            <div><span class="mlbl">P-State</span><span class="mvalS">${mEsc(g.pstate || "—")}</span></div>
            <div><span class="mlbl">驱动</span><span class="mvalS">${mEsc(g.driver || "—")}</span></div>
            <div><span class="mlbl">显存占比</span><span class="mvalS">${vpct != null ? vpct.toFixed(1) + "%" : "—"}</span></div>
            <div><span class="mlbl">来源</span><span class="mvalS">${mEsc(g.src || "—")}${g.synthetic ? "(合成)" : ""}</span></div>
          </div>
        </div>
      </div>`;
    host.appendChild(card);
  }
}

function renderPhaseCard(m) {
  // 模型名: /props 别名 > boot.model 基名
  const mi = m.model || {};
  const name = mi.name || (m.boot?.model || "").split(/[\\/]/).pop() || "";
  setTxt("monModelName", name || "未加载模型");
  const pill = $("monPhasePill");
  setTxt("monPhaseText", m.pill?.text || "—");
  pill.style.background = m.pill?.bg || "#1a8e979f";
  pill.style.color = m.pill?.fg || "#8e979f";

  const api = m;
  const phase = m.phase;
  const act = (m.slots || []).find(s => s.processing);
  const s1l = $("stat1Lbl"), s1v = $("stat1Val"), s2l = $("stat2Lbl"), s2v = $("stat2Val"),
        s3l = $("stat3Lbl"), s3v = $("stat3Val");
  const bar = $("monBar");
  const cacheHit = act && act.n_prompt_tokens > 0 && act.n_prompt_cache > 0
    ? ` · 缓存命中 ${Math.min(100, act.n_prompt_cache * 100 / act.n_prompt_tokens).toFixed(1)}%` : "";
  const ctxTxt = `上下文 ${m.ctx.used >= 0 ? fmtTok(m.ctx.used) : "—"}/${m.ctx.total > 0 ? fmtTok(m.ctx.total) : "—"}${m.ctx.pct != null ? " (" + m.ctx.pct + "%)" : ""}`;
  const t0 = m.task_started_at ? (Date.now() / 1000 - m.task_started_at) : null;
  let s1 = 0, s2 = 0, s3 = 0;
  if (phase === "prompt_processing") {
    s1l.textContent = "已处理 Prompt"; s2l.textContent = "Prompt 速度"; s3l.textContent = "已耗时";
    s1 = (act && act.n_prompt_processed > 0) ? act.n_prompt_processed :
      (m.progress?.prompt_total && m.progress?.prompt_progress != null
        ? Math.round(m.progress.prompt_total * m.progress.prompt_progress) : 0);
    s2 = (m.gen_detail?.log_prompt ?? 0);
    s3 = m.progress?.prompt_elapsed ?? 0;
    s1v.className = "statval c-cyan"; s2v.className = "statval c-cyan"; s3v.className = "statval c-light";
    s1v.textContent = fmtTok(s1); s2v.textContent = fmtN(s2) + " t/s"; s3v.textContent = fmtN(s3) + "s";
    const prog = (m.progress?.prompt_progress ?? 0) * 100;
    bar.style.width = prog.toFixed(1) + "%";
    bar.style.background = "#f5b53d";
    const eta = (m.progress?.prompt_progress != null && m.progress.prompt_progress > 0 && m.gen_detail?.log_prompt)
      ? fmtEta((1 - m.progress.prompt_progress) * m.progress.prompt_total / m.gen_detail.log_prompt) : "—";
    setTxt("monPhaseLabel", `Prompt 处理中 · 任务 #${m.task ?? "—"}`);
    setTxt("monPhaseDetail", `prompt ${fmtTok(m.progress?.prompt_total)} tokens · ${ctxTxt} · 预计剩余 ${eta}${cacheHit}`);
  } else if (phase === "decoding") {
    s1l.textContent = "已解码"; s2l.textContent = "实时速度 (3s)"; s3l.textContent = "任务均速";
    s1 = m.task?.decoded_tokens ?? 0; // 日志解码数由 detail 通道提供
    s1 = m.gen_detail?.decoded ?? s1;
    s2 = m.gen_detail?.tg3 ?? 0; s3 = m.gen_detail?.tg ?? 0;
    s1v.className = "statval c-cyan"; s2v.className = "statval c-cyan"; s3v.className = "statval c-light";
    s1v.textContent = fmtTok(s1); s2v.textContent = fmtN(s2) + " t/s"; s3v.textContent = fmtN(s3) + " t/s";
    if (act && act.n_remain > 0 && act.n_decoded != null) {
      const pct = Math.min(100, act.n_decoded * 100 / (act.n_decoded + act.n_remain));
      bar.style.width = pct.toFixed(1) + "%";
      const eta = m.speeds?.gen > 0 ? fmtEta(act.n_remain / m.speeds.gen) : "—";
      setTxt("monPhaseDetail", `剩余 ${fmtTok(act.n_remain)} tokens · ${ctxTxt} · 预计完成 ${eta}${cacheHit}`);
    } else {
      bar.style.width = "40%"; bar.style.background = "var(--cyan)";  // 不定态
      setTxt("monPhaseDetail", `${ctxTxt}${cacheHit}`);
    }
    setTxt("monPhaseLabel", `生成中 · 任务 #${m.task?.task_id ?? act?.task_id ?? "—"}`);
  } else {
    s1l.textContent = "上次输出"; s2l.textContent = "任务均速"; s3l.textContent = "上下文占用";
    s1 = m.task ? (m.task.total_tokens ?? m.task.decoded_tokens ?? 0) : 0;
    s2 = m.task ? (m.task.gen_speed ?? m.task.avg_tps ?? 0) : 0;
    s3 = m.ctx.used >= 0 ? m.ctx.used : 0;
    s1v.className = "statval c-cyan"; s2v.className = "statval c-cyan"; s3v.className = "statval c-light";
    s1v.textContent = fmtTok(s1); s2v.textContent = fmtN(s2) + " t/s"; s3v.textContent = fmtTok(s3);
    bar.style.width = "0";
    if (m.breaker_triggered) {
      setTxt("monPhaseLabel", "已熔断");
      setTxt("monPhaseDetail", "显存使用超限,已自动停止 Llama");
    } else if (!m.llama_alive) {
      setTxt("monPhaseLabel", "llama 未启动");
      setTxt("monPhaseDetail", ctxTxt);
    } else {
      setTxt("monPhaseLabel", "空闲 · 等待新请求");
      const lt = m.task;
      setTxt("monPhaseDetail", lt ? `上次 #${lt.task_id}: ${fmtTok(lt.total_tokens ?? lt.decoded_tokens ?? 0)} tokens · ${lt.duration ?? 0}s · 均速 ${fmtN(lt.avg_tps)} t/s · 预填充 ${fmtN(lt.prompt_speed)} t/s${lt.truncated ? " · 已截断" : ""} · ${ctxTxt}` : ctxTxt);
    }
  }
  // 已运行后缀
  if (m.online && phase !== "idle" && t0 != null) {
    $("monPhaseLabel").textContent += ` · 已运行 ${fmtEta(t0)}`;
  }
}

function renderSystem(sys) {
  setTxt("cpuHeadSub", sys.cpu_model || "");
  const cpu = sys.cpu;
  const cv = $("cpuBigVal");
  setTxt("cpuBigVal", cpu != null ? cpu.toFixed(0) + "%" : "—");
  cv.className = "bigVal" + (cpu >= 90 ? " err" : cpu >= 70 ? " warn" : "");
  const host = $("cpuCoresHost");
  const cores = sys.cpu_cores || [];
  const dense = cores.length > 16;
  if (host.childElementCount !== cores.length) {
    host.innerHTML = "";
    for (let i = 0; i < cores.length; i++) {
      const col = document.createElement("div");
      col.className = "coreCol" + (dense ? " dense" : "");
      col.innerHTML = `<div class="fill"></div>`;
      host.appendChild(col);
    }
  }
  [...host.children].forEach((col, i) => {
    const v = cores[i] ?? 0;
    col.firstChild.style.height = Math.min(100, v).toFixed(0) + "%";
    col.firstChild.style.background = v >= 90 ? "var(--err)" : v >= 70 ? "var(--warn)" : "var(--ok)";
  });
  // 内存
  const mu = sys.mem_used, mt = sys.mem_total;
  const mpct = mt > 0 ? mu / mt * 100 : 0;
  const mv = $("memBigVal");
  setTxt("memBigVal", mt > 0 ? mpct.toFixed(0) + "%" : "—");
  mv.className = "bigVal" + (mpct >= 90 ? " err" : mpct >= 70 ? " warn" : "");
  $("memStackUsed").style.width = mpct.toFixed(1) + "%";
  $("memStackUsed").style.background = mpct >= 90 ? "var(--err)" : mpct >= 70 ? "var(--warn)" : "var(--ok)";
  setTxt("memUsedSub", mu != null ? `${fmtN(mu / GIB)} / ${fmtN((mt || 0) / GIB)} GB` : "");
  setTxt("memLegendUsed", `已用 ${fmtN((mu || 0) / GIB)} GB`);
  setTxt("memLegendAvail", `可用 ${fmtN(((mt || 0) - (mu || 0)) / GIB)} GB`);
  // 磁盘
  const disks = sys.disks || [];
  // 表头速率用服务端按全部盘算的 io_total(服务端只截显示行, 不截这些盘的读写)
  const ioAll = sys.io_total || [disks.reduce((a, d) => a + (d.read_mb || 0), 0),
                                 disks.reduce((a, d) => a + (d.write_mb || 0), 0)];
  setTxt("diskHeadSub", disks.length ? `读 ${fmtN(ioAll[0])} MB/s · 写 ${fmtN(ioAll[1])} MB/s` : "—");
  const dh = $("diskRowsHost");
  dh.innerHTML = "";
  for (const d of disks) {
    const row = document.createElement("div");
    row.className = "drow";
    const color = d.pct >= 90 ? "var(--err)" : d.pct >= 70 ? "var(--warn)" : "var(--ok)";
    row.innerHTML = `<span class="dname">${mEsc(d.dev)}</span>
      <div class="dbar"><div class="dbarfill" style="width:${Math.min(100, d.pct).toFixed(1)}%;background:${color}"></div></div>
      <span class="dinfo">${fmtN(d.used / GIB, 0)}G / ${fmtN(d.total / GIB, 0)}G (${d.pct}%)</span>`;
    dh.appendChild(row);
  }
  if (!disks.length) dh.innerHTML = `<div class="tiny">${sys.disks_pending ? "磁盘信息还没采到…" : "—"}</div>`;
  else if (sys.disks_dropped > 0) {
    const more = document.createElement("div");
    more.className = "tiny";
    more.textContent = `另有 ${sys.disks_dropped} 块挂载未列出(按根挂载与容量排序后截断)`;
    dh.appendChild(more);
  }
  // 网络
  const net = sys.net || {};
  const keys = Object.keys(net);
  setTxt("netHeadSub", keys.length ? `${keys.length} 个网卡` : "—");
  const nh = $("netRowsHost");
  nh.innerHTML = "";
  for (const k of keys.slice(0, 6)) {
    const row = document.createElement("div");
    row.className = "nrow";
    row.innerHTML = `<span class="nname">${mEsc(k)}</span>
      <span class="nrx">↓ ${fmtN(net[k][0], 2)} MB/s</span><span class="ntx">↑ ${fmtN(net[k][1], 2)} MB/s</span>`;
    nh.appendChild(row);
  }
  if (!keys.length) nh.innerHTML = `<div class="tiny">—</div>`;
}

function renderModelSlots(m) {
  const mi = m.model || {};
  setTxt("miName", mi.name || mi.path ? (mi.name || (mi.path || "").split("/").pop()) : "—");
  setTxt("miPath", mi.path ? "路径 " + mi.path : "");
  setTxt("miFtype", mi.ftype || "");  // C# 同语义恒 "", 空时隐藏徽章
  $("miFtype").style.display = mi.ftype ? "" : "none";
  setTxt("miParams", mi.n_params != null ? fmtTok(mi.n_params) : "—");
  setTxt("miSize", mi.size != null ? (mi.size >= GIB ? fmtN(mi.size / GIB, 2) + " GB" : fmtN(mi.size / 1048576, 0) + " MB") : "—");
  setTxt("miEmbd", mi.n_embd ?? "—");
  setTxt("miVocab", mi.n_vocab ?? "—");
  const nctx = mi.n_ctx ?? m.boot?.n_ctx_slot ?? (m.slots || []).find(s => s.n_ctx > 0)?.n_ctx;
  setTxt("miCtx", nctx ?? "—");
  setTxt("miCtxTrain", mi.n_ctx_train ?? (nctx ? "≥ " + nctx : "—"));
  setTxt("miVocabType", mi.vocab_type || "—");
  const mods = [mi.vision && "vision", mi.video && "video", mi.audio && "audio"].filter(Boolean);
  setTxt("miModal", mods.length ? mods.join(" · ") : (mi.capabilities || []).join(" · ") || "—");
  // Slot 卡
  const host = $("slotCardsHost");
  const slots = m.slots || [];
  const proc = slots.filter(s => s.processing).length;
  setTxt("monSlotSummary", !m.online ? "llama 未启动" : slots.length ? `${slots.length} 槽 · ${proc} 处理中` : "无活动槽位");
  host.innerHTML = "";
  for (const s of slots) {
    const box = document.createElement("div");
    box.className = "slotBox";
    const ctxTxt = `${s.ctx_used} / ${s.n_ctx}${s.ctx_pct != null ? " (" + s.ctx_pct + "%)" : ""}`;
    const ctxCls = s.ctx_pct != null ? (s.ctx_pct >= 90 ? "ctxErr" : s.ctx_pct >= 70 ? "ctxWarn" : "") : "";
    box.innerHTML = `
      <div class="slotHead"><span class="slotTitle">Slot ${s.id}</span>
        <span class="slotPill ${s.processing ? "busy" : "idle"}">${s.processing ? "处理中" : "空闲"}</span></div>
      <div class="slotKv">
        <div><span class="mlbl">任务</span><span class="mvalS mono">#${s.task_id ?? "—"}</span></div>
        <div><span class="mlbl">n_ctx</span><span class="mvalS mono">${s.n_ctx}</span></div>
        <div><span class="mlbl">Prompt</span><span class="mvalS mono">${fmtTok(s.n_prompt_tokens)}</span></div>
        <div><span class="mlbl">已处理·缓存</span><span class="mvalS mono">${fmtTok(s.n_prompt_processed)} · ${fmtTok(s.n_prompt_cache)}</span></div>
        <div><span class="mlbl">已解码</span><span class="mvalS mono">${fmtTok(s.n_decoded)}</span></div>
        <div><span class="mlbl">剩余</span><span class="mvalS mono">${s.n_remain > 0 ? fmtTok(s.n_remain) : "不限"}</span></div>
        <div><span class="mlbl">上下文占用</span><span class="mvalS mono ${ctxCls}">${ctxTxt}</span></div>
        <div><span class="mlbl">投机解码</span><span class="mvalS mono">${s.speculative ? "on" : "—"}</span></div>
      </div>
      <div class="slotFoot">gen ${fmtN(s.gen_speed)} t/s · prompt ${fmtN(s.prompt_speed)} t/s</div>`;
    host.appendChild(box);
  }
}

/* --------------------------- 图表(SVG) --------------------------- */
function svgEl(tag, attrs) {
  const e = document.createElementNS("http://www.w3.org/2000/svg", tag);
  for (const k in attrs) e.setAttribute(k, attrs[k]);
  return e;
}

/* 时间映射(584f243 真实时间轴): stamps 跨度>0.01s 才按时间映射 */
function xAt(i, n, stamps, w) {
  if (stamps && stamps.length === n && stamps.length > 1 && stamps[stamps.length - 1] - stamps[0] > 0.01) {
    return (stamps[i] - stamps[0]) / (stamps[stamps.length - 1] - stamps[0]) * w;
  }
  return n > 1 ? i / (n - 1) * w : 0;
}

function drawSpark(svg, values, stamps, max, tone, unit) {
  if (!svg) return;
  const W = 100, H = 32, base = 30;
  svg.innerHTML = "";
  const n = values.length;
  let top = max;
  if (top == null || top <= 0) {
    top = Math.max(0, ...values.filter(v => v != null));
    if (top <= 0) top = 1e-9;
  }
  // 面积 + 折线(按 null 断点分段; 面积**每段独立**闭合 —— 修前把整条多段 path 首尾
  // 连成一个大多边形, 跨 null 段画出横贯全图的大斜面, 是"曲线紊乱"的图形侧根因之一)
  let dLine = "";
  let started = false;
  const segs = [];
  let seg = [];
  for (let i = 0; i < n; i++) {
    const v = values[i];
    if (v == null) {
      if (seg.length > 1) segs.push(seg);
      seg = [];
      started = false;
      continue;
    }
    const x = xAt(i, n, stamps, W);
    const y = base - (v / top) * 26;
    seg.push([x, y]);
    dLine += (started ? "L" : "M") + x.toFixed(2) + " " + y.toFixed(2) + " ";
    started = true;
  }
  if (seg.length > 1) segs.push(seg);
  for (const sg of segs) {
    const first = sg[0], last = sg[sg.length - 1];
    const dArea = `M ${first[0].toFixed(2)} ${base} L ` +
      sg.map(p => `${p[0].toFixed(2)} ${p[1].toFixed(2)}`).join(" L ") +
      ` L ${last[0].toFixed(2)} ${base} Z`;
    svg.appendChild(svgEl("path", { d: dArea, fill: tone, class: "area" }));
  }
  if (dLine.trim()) {
    svg.appendChild(svgEl("path", { d: dLine, stroke: tone, class: "series" }));
  } else {
    svg.appendChild(svgEl("line", { x1: 0, y1: base, x2: W, y2: base, stroke: "#2a3036", "stroke-dasharray": "3 3" }));
  }
}

function drawTrend(svg, unit, series, stamps, yMin, yMax, legendHost) {
  const W = 320, H = 160;
  svg.innerHTML = "";
  const pad = 3;
  const all = series.flatMap(s => s.data.filter(v => v != null));
  let top = yMax, bot = yMin;
  if (top == null || isNaN(top)) top = all.length ? Math.max(...all) : 1;
  if (bot == null || isNaN(bot)) bot = Math.min(0, all.length ? Math.min(...all) : 0);
  if (top - bot <= 1e-9) top = bot + 1;
  // 3 条网格线
  for (let i = 1; i <= 3; i++) {
    const y = H * i / 4;
    svg.appendChild(svgEl("line", { x1: 0, y1: y, x2: W, y2: y, class: "gridline" }));
  }
  for (const s of series) {
    const vals = s.data;
    let dLine = "", started = false;
    const segs = [];
    let seg = [];
    for (let i = 0; i < vals.length; i++) {
      const v = vals[i];
      if (v == null) {
        if (seg.length > 1) segs.push(seg);
        seg = [];
        started = false;
        continue;
      }
      const x = xAt(i, vals.length, stamps, W);
      const y = H - ((v - bot) / (top - bot)) * (H - 6) - pad;
      seg.push([x, y]);
      dLine += (started ? "L" : "M") + x.toFixed(2) + " " + y.toFixed(2) + " ";
      started = true;
    }
    if (seg.length > 1) segs.push(seg);
    for (const sg of segs) {
      const first = sg[0], last = sg[sg.length - 1];
      const dArea = `M ${first[0].toFixed(2)} ${H - pad} L ` +
        sg.map(p => `${p[0].toFixed(2)} ${p[1].toFixed(2)}`).join(" L ") +
        ` L ${last[0].toFixed(2)} ${H - pad} Z`;
      svg.appendChild(svgEl("path", { d: dArea, fill: s.color, class: "area" }));
    }
    if (dLine.trim()) svg.appendChild(svgEl("path", { d: dLine, stroke: s.color, class: "series" }));
  }
  if (!all.length) {
    svg.appendChild(svgEl("line", { x1: 0, y1: H - pad, x2: W, y2: H - pad, stroke: "#2a3036", "stroke-dasharray": "3 3" }));
  }
  // 底部时间标签(真实跨度) + 悬停十字线
  let windowSec = 60;
  if (stamps && stamps.length > 1 && stamps[stamps.length - 1] - stamps[0] > 0.01) {
    windowSec = stamps[stamps.length - 1] - stamps[0];
  }
  const box = svg.closest(".trendBox");
  let axis = box.querySelector(".taxis");
  if (!axis) {
    axis = document.createElement("div");
    axis.className = "taxis";
    box.appendChild(axis);
  }
  axis.innerHTML = `<span>-${Math.round(windowSec)}s</span><span>-${Math.round(windowSec / 2)}s</span><span>now</span>`;
  // 悬停十字线 + 读数
  svg.onmousemove = (ev) => {
    const rect = svg.getBoundingClientRect();
    const fx = (ev.clientX - rect.left) / rect.width * W;
    const frac = Math.max(0, Math.min(1, fx / W));
    let hover = svg.querySelector(".hoverline");
    if (!hover) {
      hover = svgEl("line", { y1: 0, y2: H, class: "hoverline" });
      svg.appendChild(hover);
    }
    hover.setAttribute("x1", frac * W);
    hover.setAttribute("x2", frac * W);
    // 反查最近样本点
    // frac 是"时间占比": 样本节奏混档(3s/30s)时按线性序号反查会指到错的样本,
    // 必须换算回时刻再取最近的一条 stamps(与 xAt 的时间映射同口径)。
    const timedAxis = stamps && stamps.length > 1 && stamps[stamps.length - 1] - stamps[0] > 0.01;
    const tHover = timedAxis ? stamps[0] + frac * (stamps[stamps.length - 1] - stamps[0]) : null;
    let parts = [];
    for (const s of series) {
      const vals = s.data;
      if (!vals.length) continue;
      let idx;
      if (timedAxis && stamps.length === vals.length) {
        idx = 0;
        let best = Infinity;
        for (let i = 0; i < stamps.length; i++) {
          const dd = Math.abs(stamps[i] - tHover);
          if (dd < best) { best = dd; idx = i; }
        }
      } else {
        idx = Math.max(0, Math.min(vals.length - 1, Math.round(frac * (vals.length - 1))));
      }
      let v = vals[idx], step = 1;
      while (v == null && idx >= 0 && idx < vals.length) { v = vals[idx - step] ?? vals[idx + step]; step++; if (step > 5) break; }
      parts.push(`${s.name} ${v == null ? "—" : (Math.round(v * 10) / 10) + " " + unit}`);
    }
    let ago = "";
    if (stamps && stamps.length > 1) {
      const t = stamps[0] + frac * (stamps[stamps.length - 1] - stamps[0]);
      const diff = stamps[stamps.length - 1] - t;
      ago = diff < 1.5 ? "now" : `-${Math.round(diff)}s`;
    }
    if (legendHost) legendHost.textContent = parts.join("  ") + (ago ? "  · " + ago : "");
  };
  svg.onmouseleave = () => {
    const h = svg.querySelector(".hoverline");
    if (h) h.remove();
  };
}

function renderTrends(m) {
  const H = m.hists || {};
  const pair = (k) => H[k] || [[], []];
  const cfgs = [
    ["trendGen", "Token 生成速度", "tok/s", [["生成", "#7cd7f7", pair('tps')[0]]], pair('tps')[1], null, null],
    ["trendPf", "预填充速度", "tok/s", [["预填充", "#4ade80", pair('pf')[0]]], pair('pf')[1], null, null],
    ["trendCtx", "上下文占用", "tokens", [["上下文", "#f5b53d", pair('ctx')[0]]], pair('ctx')[1], 0, (m.ctx?.total || null)],
    ["trendMtp", "MTP 接受率", "%", [["接受率", "#7cd7f7", pair('mtp')[0]]], pair('mtp')[1], 0, 100],
    ["trendGpuUtil", "GPU 利用率", "%", [["利用率", "#f0883e", pair('gpu_util')[0]]], pair('gpu_util')[1], 0, 100],
    ["trendGpuMem", "GPU 显存", "GB", [["显存", "#f0883e", pair('gpu_mem')[0]]], pair('gpu_mem')[1], 0, null],
    ["trendGpuTemp", "GPU 温度", "°C", [["温度", "#f5b53d", pair('gpu_temp')[0]]], pair('gpu_temp')[1], 0, 110],
    ["trendGpuPower", "GPU 功耗", "W", [["功耗", "#d29916", pair('gpu_power')[0]]], pair('gpu_power')[1], 0, null],
    ["trendCpu", "CPU", "%", [["CPU", "#58a6ff", pair('cpu')[0]]], pair('cpu')[1], 0, 100],
    ["trendMem", "内存", "GB", [["内存", "#58a6ff", pair('ram')[0]]], pair('ram')[1], 0, null],
    ["trendNet", "网络", "MB/s", [["下行", "#7cd7f7", pair('net_rx')[0]], ["上行", "#4ade80", pair('net_tx')[0]]],
      pair('net_rx')[1], 0, null],
  ];
  for (const [id, title, unit, series, stamps, ymin, ymax] of cfgs) {
    const svg = $(id);
    if (!svg) continue;
    const box = svg.closest(".trendBox");
    const tt = box.querySelector(".tt");
    // 头部: 标题 + 单位 + 图例(各序列最后一个非 null 值)
    let legend = "";
    for (const [name, color, data] of series) {
      let v = null;
      for (let i = data.length - 1; i >= 0; i--) { if (data[i] != null) { v = data[i]; break; } }
      legend += `${name} ${v == null ? "—" : Math.round(v * 10) / 10 + " " + unit}  `;
    }
    tt.innerHTML = `<span style="font-weight:700;color:#eef1f3">${mEsc(title)}</span>
      <span><span class="tlegend">${mEsc(legend.trim())}</span> · ${mEsc(unit)}</span>`;
    drawTrend(svg, unit, series.map(([name, color, data]) => ({ name, color, data })), stamps, ymin, ymax,
      tt.querySelector(".tlegend"));
  }
}

/* --------------------------- Node 环境面板 --------------------------- */
async function pollNodeEnv() {
  try {
    const st = await api("node/status", {}, 45000);   // latest_v24 首查要走网络(两个源各 20s)
    const n = st.node24 || {};
    setTxt("lblNodeStatus", `node: ${n.path ? (n.version || "版本未知") + " (" + n.path + ")" : "未安装"}${st.version_unknown ? " ⚠ 探测失败" : ""}`);
    const p = st.pnpm || {};
    setTxt("lblPnpmStatus", `pnpm: ${p.path ? (p.version || "?") + " (" + p.path + ")" : "未安装"}`);
    setTxt("lblLatestStatus", `最新 v24: ${st.latest_v24 || "—(网络不可达)"}`);
  } catch (e) { /* ignore */ }
}
async function pollJobInto(id, logEl, seen) {
  for (;;) {
    const j = await api("job", { id });
    for (const l of j.log || []) {
      if (!seen.has(l)) { seen.add(l); logEl.value += l + "\n"; logEl.scrollTop = logEl.scrollHeight; }
    }
    if (j.status !== "running") {
      logEl.value += (j.status === "done" ? "✔ 完成: " : "✘ 失败: ") + j.result + "\n";
      logEl.scrollTop = logEl.scrollHeight;
      await pollNodeEnv();
      return j;
    }
    await new Promise(r2 => setTimeout(r2, 1500));
  }
}

/* --------------------------- 变体面板 --------------------------- */
async function refreshVariants() {
  try {
    const r = await api("toolchains/variants", {}, 30000);   // 每个变体要跑一次 llama-server --version(各 8s 上限)
    const sel = $("comboVariant");
    sel.innerHTML = "";
    for (const v of r.variants || []) {
      const o = document.createElement("option");
      o.value = v.name;
      o.textContent = `${v.name}${v.installed ? "(b" + (v.local_build ?? "?") + ")" : "(未安装)"}`;
      sel.appendChild(o);
    }
    sel.value = r.active || "";
    const cb = $("comboBackend");
    cb.value = (r.backend_choice || "").toLowerCase();
    $("backendHint").dataset.auto = r.backend_auto || r.backend || "vulkan";
    $("backendHint").textContent = r.backend_choice
      ? "生效: " + r.backend + "(手动)"
      : "生效: " + (r.backend || "?") + "(自动检测)";
  } catch (e) { /* ignore */ }
}
async function installVariantDialog() {
  const box = document.createElement("div");
  box.innerHTML = `
    <div class="tiny" style="margin-bottom:4px">仓库链接(完整 URL,自动解析 owner/repo,兼容 .git 与 /tree//releases 链接):</div>
    <input type="text" id="vRepo" class="mono" style="width:100%">
    <div class="tiny" style="margin:6px 0 4px">名称(自动带出可改):</div>
    <input type="text" id="vName" style="width:100%">
    <div class="tiny" style="margin:6px 0 4px">目录(自动建议,与现有 llama.cpp 并排):</div>
    <div style="display:flex;gap:6px">
      <input type="text" id="vDir" class="mono fill">
      <button id="vBrowse">浏览…</button>
    </div>
    <div class="tiny" id="vHint" style="margin-top:6px"></div>`;
  const repoIn = box.querySelector("#vRepo"), nameIn = box.querySelector("#vName"),
        dirIn = box.querySelector("#vDir"), hint = box.querySelector("#vHint");
  repoIn.addEventListener("change", async () => {
    if (!repoIn.value.trim()) return;
    try {
      const r = await api("toolchains/parse_repo", { url: repoIn.value.trim() });
      if (r.error) { hint.textContent = r.error; return; }
      nameIn.value = r.name;
      dirIn.value = r.dir;
      hint.textContent = `仓库 ${r.repo} · 后端 ${r.backend}`;
    } catch (e) { hint.textContent = e.message; }
  });
  box.querySelector("#vBrowse").onclick = async () => {
    const v = await browsePicker("dir");
    if (v) dirIn.value = v;
  };
  const v = await showModal({
    title: "＋ 安装新变体", body: "", extra: box,
    buttons: [{ label: "取消", value: null }, { label: "安装", cls: "primary", value: "__ok__" }]
  });
  if (v !== "__ok__") return;
  const job = await api("toolchains/variants/install", {
    repo: repoIn.value.trim(), name: nameIn.value.trim(), dir: dirIn.value.trim()
  });
  const logEl = $("nodeLog");
  const seen = new Set();
  const r = await pollJobInto(job.job, logEl, seen);
  await refreshVariants();
  if (r.status === "done" && r.result?.ok) showNotice(String(r.result.msg).split("\n")[0]);
  else if (r.status !== "done") showNotice("变体安装失败", true);
}

/* ============================ 启动/停止流程 ============================= */
let START_BUSY = false;
async function startLlama() {
  if (START_BUSY) return;
  START_BUSY = true;
  try { await _startLlamaInner(); }
  catch (e) { showNotice("启动失败: " + (e.message || e), true); }
  finally { START_BUSY = false; }
}
async function _startLlamaInner() {
  const prof = collectProfile();
  let r;
  try { r = await api("llama/start", { profile: prof }); }
  catch (e) { showNotice("启动失败: " + e.message, true); return; }
  if (r.need_confirm) {
    const k = r.need_confirm.kind;
    if (k === "safetensors") {
      const v = await showModal({
        title: "自动转换 safetensors → GGUF",
        body: mEsc(r.need_confirm.message),
        buttons: [{ label: "取消", value: "cancel" }, { label: "Q8_0(省一半)", value: "q8_0" }, { label: "F16(精度优先)", cls: "primary", value: "f16" }]
      });
      if (v === "cancel") { logLine("--- 已取消:模型为 safetensors,未转换 ---"); return; }
      const rr = await api("llama/start", { profile: prof, confirm: { done: true, convert_dir: r.need_confirm.dir, outtype: v } });
      if (rr.error) showNotice("启动失败: " + rr.error, true);
      return;
    }
    if (k === "build_error") { showNotice(r.need_confirm.message, true); return; }
    if (k === "empty") { showNotice("Empty", true); return; }
    // confirm:参数剔除 → 显存 → qwen35(一次汇总,顺序与 CS 一致;装得下也确认,信息级)
    const nc = r.need_confirm;
    let msg = "";
    if (nc.violations && nc.violations.length) {
      msg += "以下参数未通过依赖/校验检查,将被剔除:\n\n" + nc.violations.map(v => " • " + v).join("\n") + "\n\n";
    }
    // vram_fits 三态: true=在范围内 / false=超出 / null|undefined=探针没读到显存(不得替用户
    // 断言"装得下", 也不能报"超出" —— 对齐 CS 的"未取到显卡显存信息,无法判断")
    const vramUnknown = !!nc.vram && nc.vram_fits !== true && nc.vram_fits !== false;
    const vramOver = !!nc.vram && nc.vram_fits === false;
    if (nc.vram) {
      msg += nc.vram + "\n\n判断: " + (vramOver
        ? "超出可用范围,llama-server 很可能因显存耗尽而崩溃(bad allocation / GGML_ASSERT)"
        : (vramUnknown ? "未取到显卡显存信息,无法判断(按下面的估算自行确认)"
                       : "在可用范围内")) + "\n\n";
    }
    if (nc.qwen35) msg += "⚠ " + nc.qwen35 + "\n\n";
    const v = await showModal({
      title: vramOver ? "显存不足" : "显存预算",
      body: mEsc(msg.trim()),
      buttons: [{ label: "否,取消启动", value: "no" }, { label: vramOver ? "仍然启动" : "继续启动", cls: vramOver ? "danger" : "primary", value: "yes" }]
    });
    if (v !== "yes") { logLine("--- 启动已取消 ---"); return; }
    const rr = await api("llama/start", { profile: prof, confirm: { done: true } });
    if (rr.error) showNotice("启动失败: " + rr.error, true);
  }
}

async function startDsh() {
  if (START_BUSY) return;
  START_BUSY = true;
  try { await _startDshInner(); }
  catch (e) { showNotice("DSH 启动失败: " + (e.message || e), true); }
  finally { START_BUSY = false; }
}
async function _startDshInner() {
  const prof = collectProfile();
  let r;
  try { r = await api("dsh/start", { profile: prof }, 45000); }   // 端口/git 探测最坏 25s
  catch (e) { showNotice("DSH 启动失败: " + e.message, true); return; }
  if (r.need_rebuild) {
    const v = await showModal({
      title: "DSH 仓库已升级",
      body: mEsc(r.need_rebuild.message),
      buttons: [{ label: "否,跳过重建直接启动", value: "no" }, { label: "是,自动重建后启动", cls: "primary", value: "yes" }]
    });
    const rr = await api(v === "yes" ? "dsh/rebuild_and_start" : "dsh/start_inner", { profile: prof }, 45000);
    if (rr.error) showNotice("DSH: " + rr.error, true);
    return;
  }
  if (r.error) showNotice("DSH: " + r.error, true);
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
  await saveProfileToCfg(false);
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
  showNotice("正在查询上游版本(GitHub API 直连+镜像, 通常几秒)…");
  // 服务端一次上游查询的预算约 22s, 前端必须比它更耐心, 否则先被浏览器中止
  const r = await api("toolchains/check_update", {}, 30000);
  if (r.error) {
    logLine("[更新] llama.cpp 检查失败: " + r.error);
    if (r.hint) {
      await showModal({
        title: "llama.cpp 无法检查更新",
        body: mEsc(r.error + "\n\n" + r.hint),
        buttons: [{ label: "知道了", cls: "primary", value: "ok" }]
      });
    } else {
      showNotice(r.error, true);
    }
    return;
  }
  if (r.up_to_date && !r.mismatch) { showNotice(`llama.cpp 已是最新(build ${r.local} ≥ ${r.latest})`); return; }
  let extra = "";
  if (r.mismatch) extra = `\n\n⚠ 本地 build ${r.local} 与上游 ${r.latest} 不一致(可能是分支/CI 构建)。若加载模型报 ggml type 错误,请删除该目录后重新安装。`;
  const v = await showModal({
    title: "llama.cpp 更新(" + (r.repo || "") + ")",
    body: mEsc(`本地 build: ${r.local ?? "未知"}\n上游最新: ${r.latest}(${r.tag})\n后端: ${r.backend}\n资产: ${r.asset_name}(${fmtN((r.size || 0) / GIB, 1)} GB)${extra}\n\n下载并整目录覆盖(只动二进制,不碰模型)?`),
    buttons: [{ label: "取消", value: "no" }, { label: "更新", cls: "primary", value: "yes" }]
  });
  if (v !== "yes") return;
  const j = await api("toolchains/update");
  if (j.error) { showNotice(j.error, true); return; }
  const rr = await pollJobInto(j.job, $("nodeLog"), new Set());
  if (rr.status === "done" && rr.result?.ok) showNotice(String(rr.result.msg).split("\n")[0]);
}
async function checkUpdateDsh() {
  const dir = $("inDshDir").value.trim();
  const r = await api("update/check", { target: "dsh", dir }, 30000);
  if (r.error) {
    // "查不到" 不等于 "网络坏了": cause=not_dir/not_repo/no_remote/detached 时给出出路
    // (含该仓库的构建基线 commit), 否则用户会反复点按钮, 看起来就是功能不稳定。
    logLine("[更新] DSH 检查失败(cause=" + (r.cause || "?") + "): " + r.error);
    if (r.hint) {
      await showModal({
        title: "DSH 无法检查更新",
        body: mEsc(r.error + "\n\n" + r.hint),
        buttons: [{ label: "知道了", cls: "primary", value: "ok" }]
      });
    } else {
      showNotice("git 检查失败: " + r.error, true);
    }
    return;
  }
  if (r.behind === 0) { logLine("[更新] DSH 已是最新(branch " + r.branch + ")"); return; }
  if (r.ahead > 0) { showNotice(`本地有 ${r.ahead} 个未推送提交,不自动更新;请先处理本地改动。`, true); return; }
  const v = await showModal({
    title: "DSH 更新",
    body: mEsc(`branch ${r.branch}:落后 ${r.behind} 个提交。\n\n执行 git pull --ff-only?(升级后可能需要"🔨 重建 DSH")`),
    buttons: [{ label: "取消", value: "no" }, { label: "拉取", cls: "primary", value: "yes" }]
  });
  if (v !== "yes") return;
  // 服务端是同步 git pull(--ff-only 240s + 镜像 fetch 回退), 12s 默认必然前端先弃单
  const rr = await api("update/do", { target: "dsh", dir }, 900000);
  logLine("[更新] DSH pull: " + (rr.msg || rr.ok));
}

/* ============================== 插件管理 ================================ */
async function pluginManager() {
  const prof = collectProfile();
  const r = await api("dsh/plugins/audit", { profile: prof });
  if (r.error) { showNotice(r.error, true); return; }
  const items = r.plugins || [];
  const box = document.createElement("div");
  const runningHint = r.dsh_running
    ? '<span class="mbadge warnBadge">⚠ DSH 运行中(升级/卸载/安装被拒)</span>' : "";
  // 表格化(用户反馈"乱糟糟"): 插件 | 版本 | 状态 | 说明 | 操作, thead 吸顶
  const rows = [];
  for (const p of items) {
    const peers = (p.peer_issues || []).slice(0, 2)
      .map(x => `${x.dep} ${x.range}(装 ${x.installed})`).join("; ");
    const notes = [];
    if (peers) notes.push("peer 不兼容: " + peers);
    if ((p.dead_peers || []).length) notes.push("死依赖: " + p.dead_peers.join(", "));
    if ((p.peer_unknown || []).length) notes.push("未判定(需 DSH 源码树): " + p.peer_unknown.join(", "));
    if ((p.missing_specs || []).length) notes.push("缺依赖: " + p.missing_specs.join(", "));
    if ((p.errors || []).length) notes.push(p.errors.join("; "));
    const stateCls = p.state_label === "已启用" ? ""
      : p.state_label === "被外部重新激活" ? "warnBadge" : "";
    rows.push(`<tr>
      <td class="pmName"><b>${mEsc(p.name)}</b>
        <div class="pmSpec" title="${mEsc(p.spec || "")}">${mEsc(p.spec || "(bundle)")}</div></td>
      <td>${mEsc(p.version || "?")}${String(p.spec || "").startsWith("file:") ? ' <span class="mbadge">本地</span>' : ""}</td>
      <td><div class="pmBadges">
        <span class="mbadge ${stateCls}">${mEsc(p.state_label)}</span>
        ${p.compatible ? "" : '<span class="mbadge warnBadge">peer 不兼容</span>'}
        ${p.exempted ? '<span class="mbadge">已豁免</span>' : ""}
        ${(p.dead_peers || []).length ? `<span class="mbadge warnBadge">死依赖 ${p.dead_peers.length}</span>` : ""}
      </div></td>
      <td class="pmNote">${notes.length ? mEsc(notes.join("\n")) : "—"}</td>
      <td class="pmOps"></td>
    </tr>`);
  }
  box.innerHTML = `<div class="tiny" style="margin-bottom:6px;display:flex;flex-wrap:wrap;gap:4px">
      <span class="mbadge">插件 ${items.length} 个</span>${runningHint}</div>
    <div style="max-height:360px;overflow:auto;border:1px solid var(--border);border-radius:6px">
    <table class="pmTable"><thead><tr>
      <th>插件</th><th>版本</th><th>状态</th><th>说明</th><th>操作</th>
    </tr></thead><tbody>${rows.join("")}</tbody></table></div>`;
  const list = box.querySelector("tbody");
  const trs = list.querySelectorAll("tr");
  items.forEach((p, i) => {
    const ops = trs[i].querySelector(".pmOps");
    const mk = (label, fn, cls, cond = true) => {
      if (!cond) return;
      const b = document.createElement("button");
      b.textContent = label; b.style.padding = "2px 8px"; b.style.fontSize = "11px";
      if (cls) b.className = "pmbtn " + cls;
      b.onclick = async () => {
        try { await fn(); } catch (e) { showNotice(e.message, true); }
        pluginManager();
      };
      ops.appendChild(b);
    };
    mk("激活", async () => {
      const rr = await api("dsh/plugins/set_active", { profile: prof, name: p.name, active: true });
      showNotice(rr.msg || "已激活");
    }, "primary", !p.in_bundles && !p.locked);
    if (p.locked) {
      mk("启用并解锁", async () => {
        const rr = await api("dsh/plugins/set_active", { profile: prof, name: p.name, active: true });
        showNotice(rr.msg || "已启用");
      });
    } else {
      mk("停用并锁定", async () => {
        const v2 = await showModal({
          title: "停用并锁定", body: mEsc(`停用并锁定 "${p.name}"?\n\n将从 dsh.profile.bundles 移除并写入锁定列表:\n· 即使被 DSH 网页端插件管理/市场插件写回,启动器审计也会自动移除\n· 适合 peer 版本错位、client 模块加载失败等需要长期停用的插件`),
          buttons: [{ label: "取消", value: null }, { label: "确认停用并锁定", cls: "danger", value: 1 }]
        });
        if (!v2) return;
        const rr = await api("dsh/plugins/set_active", { profile: prof, name: p.name, active: false });
        showNotice(rr.msg || "已停用");
      });
    }
    mk("升级", async () => {
      const pv = await api("dsh/plugins/preview_update", { profile: prof, name: p.name }, 45000);   // registry 两个源各 15s
      if (pv.error) { showNotice(pv.error, true); return; }
      if (pv.up_to_date) { showNotice(`已是最新(${pv.installed})`); return; }
      let confirmMsg = `升级 ${p.name}: ${pv.installed} → ${pv.latest}?`;
      if (pv.gate_blocked) confirmMsg += `\n\n⚠ ${pv.concern || "可能被 DSH 门禁拒绝"}`;
      else if (pv.concern) confirmMsg += `\n\n⚠ ${pv.concern}`;
      const vv = await showModal({ title: "升级插件", body: mEsc(confirmMsg),
        buttons: [{ label: "取消", value: null }, { label: "升级", cls: "primary", value: 1 }] });
      if (!vv) return;
      const rr = await api("dsh/plugins/upgrade", { profile: prof, name: p.name }, 900000);   // pnpm add 上限 900s
      showNotice(rr.ok ? rr.msg : rr.error, !rr.ok);
    }, "primary", p.spec && !String(p.spec).startsWith("file:"));
    mk("豁免本版", async () => {
      const rr = await api("dsh/plugins/exempt", { profile: prof, name: p.name, version: p.version });
      showNotice(rr.msg || "已豁免");
    }, null, p.compatible === false && !p.exempted);
    mk("卸载", async () => {
      if (!confirm(`卸载 ${p.name}?(需要 DSH 已停止)`)) return;
      const rr = await api("dsh/plugins/uninstall", { profile: prof, name: p.name }, 620000);   // pnpm remove 上限 600s
      showNotice(rr.ok ? rr.msg : rr.error, !rr.ok);
    });
    mk("打开目录", async () => {
      const rr = await api("dsh/plugins/open_dir", { dir: p.dir });
      if (rr.error) showNotice(rr.error, true);
    });
  });
  await showModal({
    title: "🗂 插件管理", body: "", extra: box,
    buttons: [
      { label: "📥 安装本地插件", value: "install" },
      { label: "🔄 刷新", value: "refresh" },
      { label: "关闭", cls: "primary", value: 1 }
    ]
  }).then(async v => {
    if (v === "refresh") { pluginManager(); return; }
    if (v === "install") {
      try {
        const v2 = await browsePicker("plugin_src");
        if (!v2) { pluginManager(); return; }
        const rr = await api("dsh/plugins/install_local", { profile: prof, source: v2 }, 1500000);   // pnpm pack 600s + add 900s
        showNotice(rr.ok ? (rr.msg || "已安装") : (rr.error || rr.msg || "安装失败"), !(rr.ok));
        pluginManager();
      } catch (e) { showNotice("安装失败: " + (e.message || e), true); pluginManager(); }
    }
  });
}

/* ============================== 事件绑定 ================================ */
function wire() {
  // 浏览器式标签切换
  document.querySelectorAll(".btab").forEach(b => b.addEventListener("click", () => {
    document.querySelectorAll(".btab").forEach(x => x.classList.remove("active"));
    b.classList.add("active");
    for (const sec of ["path", "param", "cmd"]) {
      $("sec" + sec.charAt(0).toUpperCase() + sec.slice(1)).style.display = b.dataset.sec === sec ? "" : "none";
    }
    if (b.dataset.sec === "cmd") refreshPreview();
  }));
  // 主标签(监测/日志)
  document.querySelectorAll(".mtab").forEach(b => b.addEventListener("click", () => {
    document.querySelectorAll(".mtab").forEach(x => x.classList.remove("active"));
    b.classList.add("active");
    MON_TAB = b.dataset.tab;
    $("tabMon").style.display = MON_TAB === "mon" ? "" : "none";
    $("tabLog").style.display = MON_TAB === "log" ? "" : "none";
    if (MON_TAB === "mon") pollMonitor();
    else api("monitor", { visible: false }).catch(() => {});
  }));
  // 路径输入 → 预览
  for (const id of ["inModel", "inMtp", "inMmproj", "inMmprojUrl", "inLlamaDir", "inDshDir"]) {
    $(id).addEventListener("input", schedulePreview);
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
    } catch (err) { showNotice("导入失败: " + err.message, true); }
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
    if (r.error) showNotice(r.error, true);
    else { await loadAll(); logLine("[配置] 配置目录: " + r.dir); }
  };
  // 方案操作
  $("comboProfile").onchange = () => switchProfile($("comboProfile").value);
  $("btnProfSave").onclick = () => saveProfileToCfg();
  $("btnProfNew").onclick = async () => {
    const name = prompt("新方案名称:", "方案 " + (Object.keys(CFG.Profiles).length + 1));
    if (!name) return;
    if (CFG.Profiles[name]) { showNotice("同名方案已存在", true); return; }
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
    if (CFG.Profiles[name]) { showNotice("同名方案已存在", true); return; }
    CFG.Profiles[name] = CFG.Profiles[old];
    delete CFG.Profiles[old];
    CFG.CurrentProfile = name;
    renderProfileCombo();
    await saveConfigFile();
  };
  $("btnProfDel").onclick = async () => {
    if (Object.keys(CFG.Profiles).length <= 1) { showNotice("至少保留一个方案", true); return; }
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
    initState();
    // 保留当前 UI 编辑值(schema 重载不丢改动)
    const saved = prof;
    buildNav();
    applyProfile(Object.assign({}, saved, { BoolParams: collectProfile().BoolParams, ValueParams: collectProfile().ValueParams }));
    refreshPreview();
    logLine("[参数] 配置已重载,来源: " + r.schema.source + ",参数 " + r.schema.params.length + " 项 / 组 " + r.schema.groups.length + " 个");
    if (r.messages && r.messages.length) {
      showModal({ title: "参数配置提示", body: mEsc(r.messages.join("\n")), buttons: [{ label: "确定", cls: "primary", value: 1 }] });
    }
  };
  $("btnCopyCmd").onclick = async () => {
    try { await navigator.clipboard.writeText($("cmdPreview").value); logLine("Copied"); }
    catch (e) { const t = $("cmdPreview"); t.select(); document.execCommand("copy"); logLine("Copied"); }
  };
  $("btnEditCmd").onclick = editCustomArgs;
  $("btnCancelCmd").onclick = () => {
    PROF.CustomArgs = [];
    CFG.Profiles[CFG.CurrentProfile] = PROF;
    updateCustomPreview(); refreshPreview();
  };
  // 服务按钮
  $("btnLlamaStart").onclick = startLlama;
  $("btnLlamaStop").onclick = () => api("llama/stop", {}, 30000);   // 停止链最坏 ≈13s(端口探针 10s + 杀树 3s)
  $("btnDshStart").onclick = startDsh;
  $("btnDshStop").onclick = () => api("dsh/stop", {}, 30000);   // 停止链最坏 ≈19s(杀树 + sudo kill 15s)
  $("btnOpenDsh").onclick = async () => {
    // 主窗口是 WebKit2 客户端, 弹窗会被吞掉 → 优先让服务端交系统默认浏览器;
    // 服务端交不出去(无桌面/无浏览器)时才回落到前端 window.open(浏览器模式下可用)。
    try {
      const r = await api("open/dsh");
      if (!r.opened && r.url && !window.open(r.url, "_blank")) {
        showNotice("打不开浏览器, 请手动访问 " + r.url, true);
      }
    } catch (e) { showNotice("打开 DSH 失败: " + e.message, true); }
  };
  $("btnRebuildDsh").onclick = async () => {
    const v = await showModal({
      title: "重建 DSH(全量三步)",
      body: "1/3 pnpm clean\n2/3 pnpm build:lib\n3/3 vite build(apps/web)\n\nDSH 运行中会先停止(等 ≤20s)。确认开始?",
      buttons: [{ label: "取消", value: "no" }, { label: "开始重建", cls: "primary", value: "yes" }]
    });
    if (v !== "yes") return;
    const r = await api("dsh/rebuild", { profile: collectProfile() });
    if (r.error) showNotice(r.error, true);
  };
  // 维护工具
  $("btnCompat").onclick = () => showCompatModal().catch(e => showNotice(e.message, true));

/* ===================== 维护工具: 结构化兼容性报告 ===================== */
const CATS_ORDER = ["环境", "配置", "插件", "补丁", "链接", "豁免", "注入", "检查器"];
const LV_ICON = { error: "✗", warn: "⚠", ok: "✓", info: "·" };
const LV_CLS = { error: "evt-error", warn: "evt-warn", ok: "evt-ok", info: "" };
function compatReportDom(rep) {
  const box = document.createElement("div");
  const probes = rep.probes || {};
  const counts = rep.counts || {};
  const badge = (label, val) => {
    const bad = val === false || val === null || val === undefined;
    const shown = val === true ? "✓" : val === false ? "✗" : (val == null ? "?" : String(val));
    return '<span class="' + (bad ? "mbadge warnBadge" : "mbadge") + '">' + mEsc(label + " " + shown) + "</span>";
  };
  let head = '<div class="tiny" style="margin:0 0 6px;display:flex;flex-wrap:wrap;gap:4px">' +
    badge("DSH " + (rep.version || "?"), true) +
    badge("git", probes.git) + badge("编译产物", probes.bin) +
    badge("node24", probes.node24 || false) + badge("pnpm", probes.pnpm || false) +
    (probes.dump === null || probes.dump === undefined ? "" : badge("dump-config", probes.dump)) +
    '<span class="mbadge">✗ ' + (counts.errors || 0) + "</span>" +
    '<span class="mbadge warnBadge">⚠ ' + (counts.warns || 0) + "</span>" +
    '<span class="mbadge">🔧 ' + (counts.fixables || (rep.fixables || []).length) + "</span>" +
    ((counts.unreadable || 0) ? '<span class="mbadge warnBadge">未核对 ' + counts.unreadable + "</span>" : "") +
    "</div>";
  box.innerHTML = head;
  const byCat = new Map();
  for (const it of (rep.items || [])) {
    const c = it.cat || "其它";
    if (!byCat.has(c)) byCat.set(c, []);
    byCat.get(c).push(it);
  }
  const cats = [...byCat.keys()].sort((a, b) => {
    const ia = CATS_ORDER.indexOf(a), ib = CATS_ORDER.indexOf(b);
    return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib);
  });
  for (const c of cats) {
    const sec = document.createElement("div");
    sec.style.cssText = "margin:8px 0 2px;font-weight:700;font-size:12px;color:var(--tone-info)";
    sec.textContent = c;
    box.appendChild(sec);
    for (const it of byCat.get(c)) {
      const row = document.createElement("div");
      row.className = LV_CLS[it.level] || "";
      row.style.cssText = "display:flex;gap:6px;padding:2px 0;font-size:12px;align-items:baseline";
      row.innerHTML = '<span style="flex:none">' + (LV_ICON[it.level] || "·") + "</span>" +
        '<span style="min-width:0;word-break:break-all">' + mEsc(it.text) + "</span>";
      box.appendChild(row);
    }
  }
  return box;
}
async function showCompatModal() {
  let r = await api("dsh/compat", { profile: collectProfile() }, 240000);   // audit 里 --dump-config 上限 180s
  if (r.error) { showNotice(r.error, true); return; }
  for (const l of r.log || []) logLine(l);
  for (;;) {
    const rep = r.report || {};
    const nFix = (rep.fixables || []).length;
    const box = compatReportDom(rep);
    const btns = [];
    if (nFix) btns.push({ label: "🔧 一键修复(" + nFix + " 项)", value: "fix" });
    btns.push({ label: "🗂 插件管理", value: "mgr" });
    btns.push({ label: "关闭", cls: "primary", value: "close" });
    const act = await showModal({ title: "🩺 DSH 维护工具 · 兼容性报告", body: "", extra: box, buttons: btns });
    if (act === "mgr") { pluginManager(); return; }
    if (act !== "fix") return;
    const rr = await api("dsh/compat/repair", { profile: collectProfile() }, 480000);
    if (rr.error) { showNotice(rr.error, true); return; }
    for (const l of (rr.log || [])) logLine(l);
    // 修复后仍不兼容的插件: 放行与否由用户逐次决定(工具不代替用户接受风险)
    const stillBad = ((rr.report || {}).plugins || []).filter(
      p => p.peer_issues && p.peer_issues.length && !p.exempted && !String(p.spec || "").startsWith("file:"));
    if (stillBad.length) {
      const exBox = document.createElement("div");
      exBox.innerHTML = '<div class="tiny" style="margin-bottom:6px">修复 ' + (rr.fixed || 0) +
        ' 项后, 以下插件仍与 DSH ' + mEsc(rr.report.version || "?") +
        ' 不兼容。放行 = 写入精确版本豁免(只跳过版本校验, 真不兼容仍可能崩);保持现状 = 稍后在插件管理里处理。</div>';
      for (const p of stillBad) {
        const row = document.createElement("div");
        row.style.cssText = "display:flex;gap:6px;align-items:center;padding:3px 0;font-size:12px";
        row.innerHTML = '<span style="flex:1;min-width:0;word-break:break-all">' +
          mEsc(p.name + "@" + (p.version || "?")) + "</span>";
        const bYes = document.createElement("button");
        bYes.textContent = "放行"; bYes.style.padding = "2px 8px"; bYes.style.fontSize = "11px";
        bYes.onclick = async () => {
          try {
            const x = await api("dsh/plugins/exempt", { profile: {}, name: p.name, version: p.version });
            showNotice(x.msg || (x.ok ? "已放行" : "失败"));
            bYes.disabled = true; bYes.textContent = "已放行";
          } catch (e) { showNotice(e.message, true); }
        };
        row.appendChild(bYes);
        exBox.appendChild(row);
      }
      const v = await showModal({
        title: "🩺 修复完成(" + (rr.fixed || 0) + " 项) · 复检" +
          ((rr.report.counts || {}).errors ? "仍有错误 " + rr.report.counts.errors : "无错误"),
        body: "", extra: exBox,
        buttons: [{ label: "再看报告", value: "again", cls: "primary" }, { label: "关闭", value: "close" }]
      });
      if (v === "again") { r = { report: rr.report, log: rr.log || [] }; continue; }
      return;
    }
    r = { report: rr.report, log: rr.log || [] };
    showNotice("兼容性修复完成 " + (rr.fixed || 0) + " 项 · 复检" +
      ((rr.report.counts || {}).errors ? "仍有错误 " + rr.report.counts.errors : "无错误") +
      " —— 报告已刷新");
  }
}
  $("btnPlugins").onclick = async () => {
    const r = await api("dsh/plugins", { profile: collectProfile() });
    const c = r.counts || {};
    const html = r.report.items.map(i => {
      const cls = i.level === "error" ? "evt-error" : i.level === "warn" ? "evt-warn" : "";
      const badge = i.level === "error" ? '<span class="mbadge warnBadge">✗</span> '
        : i.level === "warn" ? '<span class="mbadge warnBadge">⚠</span> ' : '<span class="mbadge">✓</span> ';
      return '<div style="display:flex;gap:6px;align-items:baseline;padding:2px 0;font-size:12px" class="' + cls + '">' +
        badge + '<span style="font-weight:600;flex:none">' + mEsc(i.name) + "</span>" +
        '<span class="tiny" style="min-width:0;word-break:break-all">' + mEsc(i.note || "") + "</span></div>";
    }).join("");
    const head = '<div class="tiny" style="margin-bottom:6px;display:flex;flex-wrap:wrap;gap:4px">' +
      '<span class="mbadge">插件 ' + r.report.count + " 个</span>" +
      '<span class="' + (c.abnormal ? "mbadge warnBadge" : "mbadge") + '">异常 ' + (c.abnormal || 0) + "</span>" +
      '<span class="mbadge">市场停用 ' + (c.market < 0 ? "?" : c.market) + "</span>" +
      '<span class="' + (c.links ? "mbadge warnBadge" : "mbadge") + '">失效链接 ' + (c.links || 0) + "</span></div>";
    const box = document.createElement("div");
    box.innerHTML = head + '<div style="max-height:340px;overflow:auto">' + (html || "(无插件)") + "</div>";
    await showModal({ title: "🧩 插件智能检测", body: "", extra: box,
                      buttons: [{ label: "关闭", cls: "primary", value: 1 }, { label: "🗂 打开插件管理", value: "mgr" }] })
      .then(v => { if (v === "mgr") pluginManager(); });
    for (const l of r.log) logLine(l);
  };
  $("btnPluginMgr").onclick = () => pluginManager().catch(e => showNotice(e.message, true));
  // 检索/更新
  $("btnSearchLlama").onclick = async () => {
    logLine("[检索] 正在检索 llama-server(最长 45s)...");
    const r = await api("search", { target: "llama" }, 60000);   // 服务端 BFS 上限 45s
    if (r.result) { $("inLlamaDir").value = r.result; logLine("[检索] 找到: " + r.result); refreshPreview(); }
    else showNotice("未找到 llama-server", true);
  };
  $("btnSearchDsh").onclick = async () => {
    logLine("[检索] 正在检索 DSH 项目(最长 45s)...");
    const r = await api("search", { target: "dsh" }, 60000);
    if (r.result) { $("inDshDir").value = r.result; logLine("[检索] 找到: " + r.result); refreshPreview(); }
    else showNotice("未找到 DSH 项目", true);
  };
  $("btnUpdLlama").onclick = () => checkUpdateLlama().catch(e => showNotice(e.message, true));
  $("btnUpdDsh").onclick = () => checkUpdateDsh().catch(e => showNotice(e.message, true));
  // 变体
  $("comboVariant").onchange = async () => {
    const r = await api("toolchains/variants/switch", { name: $("comboVariant").value });
    if (r.error) { showNotice(r.error, true); refreshVariants(); return; }
    if (r.ok) {
      PROF.LlamaDir = r.new_dir ?? PROF.LlamaDir;
      $("inLlamaDir").value = PROF.LlamaDir || "";
      showNotice(r.msg);
      refreshPreview();
    }
  };
  $("btnAddVariant").onclick = () => installVariantDialog().catch(e => showNotice(e.message, true));
  $("btnRemoveVariant").onclick = async () => {
    const name = $("comboVariant").value;
    if (!confirm(`从列表移除变体 "${name}"?(不删磁盘文件)`)) return;
    const r = await api("toolchains/variants/remove", { name });
    if (r.error) showNotice(r.error, true);
    else { await refreshVariants(); await loadAll(); }
  };
  // Node 环境
  $("btnInstallNodeEnv").onclick = async () => {
    const job = await api("node/install");
    await pollJobInto(job.job, $("nodeLog"), new Set());  // seen 随 job 新建(全局 Set 会吞合法重复行)
  };
  $("btnRefreshNodeEnv").onclick = pollNodeEnv;
  $("btnCopyNodeDiag").onclick = async () => {
    const st = await api("node/status", {}, 45000);
    const txt = JSON.stringify(st, null, 2);
    try { await navigator.clipboard.writeText(txt); showNotice("诊断已复制"); }
    catch (e) { $("nodeLog").value += txt + "\n"; }
  };
  // 后端选择 → 提示即时更新(生效 = 手动选择或自动检测)
  $("comboBackend").addEventListener("change", () => {
    const auto = ($("backendHint").dataset.auto || "vulkan");
    const v = $("comboBackend").value;
    $("backendHint").textContent = v ? ("生效: " + v + "(手动)") : ("生效: " + auto + "(自动检测)");
  });
  // 参数过滤
  $("inputParamFilter").addEventListener("input", applyParamFilter);
  $("btnClearParamFilter").addEventListener("click", () => { $("inputParamFilter").value = ""; applyParamFilter(); });
  // 日志
  $("btnClearLog").onclick = async () => { await api("logs/clear"); LOG_LINES = []; $("logArea").value = ""; };
  $("btnOpenLogDir").onclick = async () => {
    const r = await api("open/logs");
    if (!r.ok) logLine("[日志] 目录: " + (await api("bootstrap")).log_dir);
  };
  $("noticeClose").onclick = () => $("noticeBar").classList.remove("show");
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
  initState();
  buildNav();
  applyProfile(PROF);
  await refreshPreview();
  await refreshVariants();
  pollNodeEnv();
  if (b.schema.errors && b.schema.errors.length) {
    logLine("[参数] ⚠ schema 错误: " + b.schema.errors.join("; "));
  }
}

(async function init() {
  wire();
  await loadAll();
  pollStatus(); pollLogs(); pollMonitor();
  setInterval(() => pollGuard("status", pollStatus), 2000);
  setInterval(() => pollGuard("logs", pollLogs), 1000);
  setInterval(() => pollGuard("mon", pollMonitor), 3000);
  setInterval(() => pollGuard("node", pollNodeEnv), 15000);
})();
