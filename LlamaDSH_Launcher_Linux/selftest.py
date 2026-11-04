# -*- coding: utf-8 -*-
"""Linux 版回归自检(55 项): 同步批次 + 历轮真实 bug 的断言, 失败即非零退出。

跑法: python3 selftest.py   (托盘/服务在跑时会额外做活体宿主侧协议探针,
     未运行则打印 SKIP 而不是失败)
每项断言都对应一个真实踩过的坑, 修 bug 前先跑它, 修完再跑它。"""
import io
import json
import os
import struct
import sys
import threading
import time
import types

sys.path.insert(0, os.path.dirname(os.path.abspath(sys.argv[0] or ".")))
APP = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, APP)
os.chdir(APP)

FAILS = []


def check(name, cond, detail=""):
    print(("PASS " if cond else "FAIL ") + name + ((" | " + detail) if detail and not cond else ""))
    if not cond:
        FAILS.append(name)


# ---------------------------------------------------------------- toolchain --
import toolchain

check("asset 名白名单-放行", toolchain.is_safe_asset_name("llama-b1234-bin-ubuntu-vulkan-x64.tar.gz"))
check("asset 名白名单-穿越", not toolchain.is_safe_asset_name("../evil.tar.gz"))
check("asset 名白名单-绝对路径", not toolchain.is_safe_asset_name("/tmp/evil.tar.gz"))
check("asset 名白名单-注入字符", not toolchain.is_safe_asset_name("a;rm -rf.tar.gz"))
check("asset 名白名单-空", not toolchain.is_safe_asset_name(""))

assets_bad = [
    {"name": "../evil.tar.gz", "size": 10, "digest": "sha256:x"},
    {"name": "llama-b9999-bin-ubuntu-vulkan-x64.tar.gz", "size": 100, "digest": "sha256:good"},
    {"name": "llama-b9999-bin-ubuntu-cuda-12-x64.tar.gz", "size": 100, "digest": "sha256:cuda"},
]
pick = toolchain.pick_assets(assets_bad, "vulkan")
check("pick_assets 跳过非法名", pick["main"] == "llama-b9999-bin-ubuntu-vulkan-x64.tar.gz", str(pick))

# download_asset 摘要缺失 fail-closed
called = {"n": 0}
orig_urlopen = toolchain.urllib.request.urlopen


def _spy(*a, **k):
    called["n"] += 1
    raise AssertionError("不应该发起网络请求")


toolchain.urllib.request.urlopen = _spy
try:
    toolchain.download_asset("http://x/y.tar.gz", "/tmp/nope.part", 100, expected_digest="")
    ok_fc = False
except IOError as ex:
    ok_fc = "无 sha256 摘要" in str(ex) and called["n"] == 0
finally:
    toolchain.urllib.request.urlopen = orig_urlopen
check("download_asset 无摘要 fail-closed", ok_fc)

os.environ["LLAMADSH_ALLOW_UNVERIFIED_UPDATE"] = "1"
called2 = {"n": 0}


def _spy2(*a, **k):
    called2["n"] += 1
    raise IOError("stop after attempt")


toolchain.urllib.request.urlopen = _spy2
try:
    toolchain.download_asset("http://x/y.tar.gz", "/tmp/nope.part", 100, expected_digest="")
except Exception:
    pass
finally:
    toolchain.urllib.request.urlopen = orig_urlopen
    del os.environ["LLAMADSH_ALLOW_UNVERIFIED_UPDATE"]
check("应急环境变量可放行到下载阶段", called2["n"] >= 1)

# 摘要锚定: 镜像 digest 被直连覆盖
rel = {"asset_name": "llama-b9999-bin-ubuntu-vulkan-x64.tar.gz",
       "asset_url": "https://github.com/ggml-org/llama.cpp/releases/download/b9999/llama-b9999-bin-ubuntu-vulkan-x64.tar.gz",
       "digest": "sha256:mirror-suspicious", "cudart_name": "", "cudart_digest": ""}
toolchain._gh_api_direct = lambda path, timeout=15: {"assets": [
    {"name": rel["asset_name"], "digest": "sha256:official"},
    {"name": "other", "digest": ""}]}
toolchain.anchor_release_digests(rel)
check("直连 GitHub 摘要锚定覆盖镜像值", rel["digest"] == "sha256:official", rel["digest"])

rel2 = {"asset_name": "x.tar.gz", "asset_url": "http://mirror.example/x.tar.gz", "digest": "sha256:m"}
toolchain.anchor_release_digests(rel2)
check("非 github.com 资产 URL 不误锚定", rel2["digest"] == "sha256:m")

bad_rel = {"asset_name": "../evil.tar.gz", "asset_url": "http://x", "digest": "", "size": 0}
ok_upd, msg_upd = toolchain.apply_llama_update("/tmp/llama-nonexistent", bad_rel)
check("apply_llama_update 拒绝非法资产名", not ok_upd and "非法资产名" in msg_upd)

# 并发下载互删: 两个在途时 finally 不清空缓存目录(以计数器行为断言)
with toolchain._dl_lock:
    base = toolchain._active_downloads
check("下载计数器初始为 0", base == 0, str(base))

# ---------------------------------------------------------------- nodetool --
import nodetool

bad_payload = json.dumps([{"version": "v24.10.0"}, {"version": "v24.1.1;rm"}, {"version": "v24.9.9-../../x"}]).encode()
nodetool._v24_cache.update(t=0.0, v=(None, None))
orig_http = nodetool._http_get
nodetool._http_get = lambda url, timeout=30: bad_payload
try:
    base_v, ver = nodetool.latest_v24()
finally:
    nodetool._http_get = orig_http
    nodetool._v24_cache.update(t=0.0, v=(None, None))
check("node 版本号形态白名单(仅 v24.10.0 存活)", ver == "v24.10.0", str(ver))

# ---------------------------------------------------------------- llapimon --
import llapimon

mon = llapimon.LlamaApiMonitor(lambda: 8080)


class _SlowHttp:
    def __init__(self):
        self.calls = 0
        self.ev = threading.Event()

    def _get_json(self, port, path, timeout=2.0):
        self.calls += 1
        self.ev.wait(5)
        return None


sh = _SlowHttp()
mon._get_json = sh._get_json
t0 = time.monotonic()
mon.poll_slow_if_needed()          # 启动后台线程
gap = time.monotonic() - t0
check("慢轮询不阻塞调用线程", gap < 0.2, "%.3fs" % gap)
busy_now = mon._slow_busy
mon.poll_slow_if_needed()          # 在途保护: 不应再启动第二个
check("慢轮询在途保护", busy_now and sh.calls == 1, "calls=%d" % sh.calls)
sh.ev.set()
time.sleep(0.2)
check("慢轮询结束后清 busy", not mon._slow_busy)

# 日志状态机 + 差分端到端
mon2 = llapimon.LlamaApiMonitor(lambda: 8080)
for line in [
    "0.00.013.157 I launch_slot_: id  0 | task 12 | processing task, is_child = 0",
    "0.00.100.000 I slot print_timing: id  0 | task 12 | prompt processing, n_tokens = 100, progress = 0.50, t = 1.00 s / 200.00 tokens per second",
    "0.00.200.000 I slot print_timing: id  0 | task 12 | n_decoded = 10, tg = 30.00 t/s, tg_3s = 31.00 t/s",
    "0.00.300.000 I print_timing: id  0 | task 12 | prompt eval time = 500.00 ms / 100 tokens (   5.00 ms per token,   200.00 tokens per second)",
    "0.00.300.000 I print_timing: id  0 | task 12 |        eval time = 300.00 ms /  10 tokens (  30.00 ms per token,    33.33 tokens per second)",
    "0.00.300.000 I print_timing: id  0 | task 12 |       total time = 800.00 ms /  10 tokens",
    "0.00.300.100 I slot print_timing: id  0 | task 12 | graphs reused = 7",
    "0.00.300.200 I slot release: id  0 | task 12 | stop processing: n_tokens = 110, truncated = 0",
]:
    mon2.feed_log_line(line)
lt = mon2.last_task or {}
check("任务落账(prompt/eval/total)", lt.get("prompt_ms") == 500.0 and lt.get("eval_ms") == 300.0
      and lt.get("total_ms") == 800.0 and lt.get("decoded_tokens") == 10, str(lt))
check("相位收尾与 graphs", mon2.phase == "idle" and mon2.graphs_reused == 7)
check("ctx 兜底来自 release", mon2.log_ctx_used == 110)

# slots 差分
mon3 = llapimon.LlamaApiMonitor(lambda: 8080)


def _slots(port, path, timeout=2.0):
    return [{"id": 0, "is_processing": True, "id_task": 5, "n_decoded": 0,
             "n_prompt_tokens": 10, "n_prompt_tokens_processed": 10,
             "n_prompt_tokens_cache": 0, "n_remain": -1, "n_ctx": 4096}]


mon3._get_json = _slots
mon3.poll_fast()
mon3._get_json = lambda port, path, timeout=2.0: [
    {"id": 0, "is_processing": True, "id_task": 5, "n_decoded": 10,
     "n_prompt_tokens": 10, "n_prompt_tokens_processed": 10,
     "n_prompt_tokens_cache": 0, "n_remain": -1, "n_ctx": 4096}]
mon3._diff[0]["last_at"] = time.monotonic() - 1.0
mon3.poll_fast()
sp = mon3.slots[0]["gen_speed"]
check("逐槽差分算速(≈10 t/s)", 8.0 <= sp <= 12.0, str(sp))

# -------------------------------------------------------------- cmdline ----
import cmdline


def _gguf_string(s):
    b = s.encode("utf-8")
    return struct.pack("<Q", len(b)) + b


def _write_gguf(path, n_tensors, corrupt=False):
    with open(path, "wb") as f:
        f.write(b"GGUF")
        f.write(struct.pack("<I", 3))
        f.write(struct.pack("<Q", n_tensors))
        f.write(struct.pack("<Q", 0))
        if corrupt:
            f.write(b"\x00garbage")
            return
        for i in range(n_tensors):
            f.write(_gguf_string("blk.%d.ffn_down.weight" % i))
            f.write(struct.pack("<I", 2))
            f.write(struct.pack("<Q", 32))
            f.write(struct.pack("<Q", 32))
            f.write(struct.pack("<I", 10))  # f16
            f.write(struct.pack("<Q", 0))


d = "/tmp/gguf_shard_test"
os.makedirs(d, exist_ok=True)
first = os.path.join(d, "model-00001-of-00002.gguf")
second = os.path.join(d, "model-00002-of-00002.gguf")
_write_gguf(first, 2)
_write_gguf(second, 2)
st = cmdline.read_gguf_tensor_stats(first, 2)
check("分片全解析 → ok 无 partial", st["ok"] and not st["partial"], str(st))
_write_gguf(second, 2, corrupt=True)
st2 = cmdline.read_gguf_tensor_stats(first, 2)
check("分片部分解析 → 不冒充 ok, 标 partial", (not st2["ok"]) and st2["partial"], str(st2))

# ------------------------------------------------------------------ core ---
import core

hub = core.LogHub()
conv = core.Converter(hub)
logp = "/tmp/conv_tail_test.log"
with open(logp, "w") as f:
    f.write("line1\npart")
conv._log_pos = 0
done = {}
state = {"n": 0}


def fake_sleep(_s):
    state["n"] += 1
    if state["n"] == 2:
        with open(logp, "a") as f:
            f.write("ial2\nEXITCODE:0\n")
    if state["n"] > 2:
        raise StopIteration("test-end")


orig_sleep = core.time.sleep
core.time.sleep = fake_sleep
try:
    conv._poll(logp, 1000, "f16", "/tmp/conv_out.gguf", lambda p: done.setdefault("p", p))
except StopIteration:
    pass
finally:
    core.time.sleep = orig_sleep
logged = [t for (_seq, t) in hub.since(0)[0] if "[转换]" in t]
joined = "\n".join(logged)
check("转换日志不拆半行", "part" not in joined.replace("partial2", "")
      and "partial2" in joined and "[转换] line1" in joined, joined[-300:])
check("转换完成判定仍生效", done.get("p") is None and not conv.running, str(done))

# ------------------------------------------------------- monitor 磁盘快照 --
import monitor

rows_a = monitor._disk_rows_cached()
time.sleep(0.3)
rows_b = monitor._disk_rows_cached()
check("磁盘快照后台刷新返回行", isinstance(rows_b, list), str(rows_b)[:120])
check("磁盘快照返回副本(装饰不污染缓存)",
      rows_a is not monitor._disk_rows["rows"] and rows_b is not monitor._disk_rows["rows"])

# ---------------------------------------------------------------- appicon ----
import re as _re
import shutil as _sh
import subprocess as _sp
import appicon

fake = "/tmp/fakehome_icon_test"
_sh.rmtree(fake, ignore_errors=True)
os.makedirs(fake)
_home = os.environ["HOME"]
os.environ["HOME"] = fake
appicon._theme_path = None
tp = appicon.install_user_icons()
check("图标自举返回主题根目录", tp == os.path.join(fake, ".local/share/icons"), tp)
check("hicolor/index.theme 就位(缺它 Gtk/Shell 整目录忽略, 图标铺了也查不到)",
      os.path.isfile(os.path.join(tp, "hicolor", "index.theme")))
check("32x32 图标按主题布局落位",
      os.path.isfile(os.path.join(tp, "hicolor", "32x32", "apps", "llamadsh-launcher.png")))
appicon._theme_path = None
tp2 = appicon.install_user_icons()
check("自举幂等(同一路径)", tp2 == tp, tp2)
os.environ["HOME"] = _home

pm = appicon.icon_pixmaps()
check("SNI IconPixmap 非空且字节数=宽*高*4",
      len(pm) >= 2 and all(len(b) == w * h * 4 for (w, h, b) in pm),
      str([(w, h, len(b)) for (w, h, b) in pm]))
check("IconPixmap 含非透明像素(不是空图)",
      any(any(b[i] for i in range(0, len(b), 997)) for (_w, _h, b) in pm))
try:
    import gi
    gi.require_version("GdkPixbuf", "2.0")
    from gi.repository import GdkPixbuf
    pb = GdkPixbuf.Pixbuf.new_from_file(os.path.join(APP, "assets", "icon32.png"))
    ref, rs, n = pb.get_pixels(), pb.get_rowstride(), pb.get_n_channels()
    w, h, argb = appicon._png_to_argb(os.path.join(APP, "assets", "icon32.png"))
    bad = sum(1 for y in range(h) for x in range(w)
              if (argb[(y * w + x) * 4 + 1], argb[(y * w + x) * 4 + 2],
                  argb[(y * w + x) * 4 + 3], argb[(y * w + x) * 4]) !=
              (ref[y * rs + x * n], ref[y * rs + x * n + 1], ref[y * rs + x * n + 2],
               ref[y * rs + x * n + 3] if n == 4 else 255))
    check("PNG 解码与 GdkPixbuf 逐像素一致", bad == 0, "差异 %d/%d" % (bad, w * h))
except ImportError:
    print("SKIP PNG 解码逐像素比对(无 python3-gi)")

# 按名查找必须在"干净 HOME"下也命中(子进程才反映默认主题搜索路径)
probe = _sp.run(["python3", "-I", "-c",
                 "import gi,sys;gi.require_version('Gtk','3.0');from gi.repository import Gtk;"
                 "t=Gtk.IconTheme.get_default();"
                 "i=t.lookup_icon('llamadsh-launcher',22,Gtk.IconLookupFlags.GENERIC_FALLBACK);"
                 "print(i.get_filename() if i else 'NONE')"],
                env=dict(os.environ, HOME=fake), capture_output=True, text=True, timeout=60)
check("默认主题按 IconName 查找命中(22px→32x32)",
      "llamadsh-launcher.png" in probe.stdout, probe.stdout.strip() + probe.stderr[-200:])

# --------------------------------------------------------- 按钮禁用态 CSS ---
css = open(os.path.join(APP, "webui", "style.css"), encoding="utf-8").read()
check("CSS: 禁用态特异性压过 .primary/.danger 且写在其后",
      css.index("button.primary {") < css.index("button:disabled,")
      and "button.primary:disabled" in css and "button.danger:disabled" in css)
check("CSS: hover/active 只对可用按钮生效",
      "button:not(:disabled):hover" in css and "button:not(:disabled):active" in css
      and not _re.search(r"^button:hover", css, _re.M))

# ------------------------------------------- 托盘 D-Bus 签名(规范对齐) ----
# 教训: 签名不合规范时 python-dbus 自己一切正常, 只有真实宿主(GNOME 扩展)会
# 静默失败 —— Activate 无参 → TypeError(左键永远打不开窗口);
# GetLayout/GetGroupProperties 签名或回包形状不符 → 右键弹出空菜单。
src = open(os.path.join(APP, "tray.py"), encoding="utf-8").read()
SIGS = {
    'def Activate(self, x, y)': 'in_signature="ii"',          # KDE SNI: Activate(x,y) 带坐标
    'def SecondaryActivate(self, x, y)': 'in_signature="ii"',
    'def ContextMenu(self, x, y)': 'in_signature="ii"',
    'def Scroll(self, delta, orientation)': 'in_signature="is"',
    'def ProvideXdgActivationToken(self, token)': 'in_signature="s"',
    'def GetLayout(self, parent, depth, props)': 'in_signature="iias"',   # (i parent, i depth, as props)
    'def GetGroupProperties(self, ids, props)': 'in_signature="aias"',    # (ai ids, as props)
    'def Event(self, _id, event, _data, _ts)': 'in_signature="isvu"',
}
check("dbusmenu 回包形状是结构体数组 a(ia{sv})(不是 a{ia{sv}})",
      'out_signature="a(ia{sv})"' in src)
for anchor, sig in SIGS.items():
    hit = None
    for line in src.splitlines():
        if anchor in line:
            hit = line
            break
    if sig is None:
        check("dbusmenu 回包形状 a(ia{sv})", anchor in src)
        continue
    ok = hit is not None and sig in hit
    # 声明行可能在 def 上一行, 容错: 找 anchor 前后 2 行
    if not ok and hit is not None:
        idx = src.splitlines().index(hit)
        window = "\n".join(src.splitlines()[max(0, idx - 2):idx + 2])
        ok = sig in window
    check("tray 签名符合规范: %s" % anchor, ok, (hit or "未找到").strip()[:90])

# --------------------------------------- DSH 管理员模式(sudo)预检与降级 ---
class _FakeR:
    def __init__(self, rc, err=b""):
        self.returncode, self.stderr = rc, err


class _Hub2:
    def __init__(self):
        self.lines = []

    def log(self, m):
        self.lines.append(m)

    def error_ex(self, e, t):
        self.lines.append("ERR " + t)


hub2 = _Hub2()
ctl = core.DshRunner(hub2, None)
orig_run = core.subprocess.run
core.subprocess.run = lambda *a, **k: _FakeR(1, b"sudo: a password is required\n")
r1 = ctl._sudo_preflight()
check("sudo 需密码 → 预检点名并给 NOPASSWD 指引", bool(r1) and "NOPASSWD" in r1, str(r1))
core.subprocess.run = lambda *a, **k: _FakeR(1, b'sudo: The "no new privileges" flag is set\n')
r2 = ctl._sudo_preflight()
check("NNP 位 → 预检点名 NoNewPrivs 并给重启指引", bool(r2) and "NoNewPrivs" in r2, str(r2))
core.subprocess.run = lambda *a, **k: _FakeR(0)
check("sudo -n 可无人值守 → 预检放行", ctl._sudo_preflight() is None)
core.subprocess.run = orig_run

# 预检失败必须当场降级: 不能再拼 sudo, 也不能白等 30s 超时重试
core.subprocess.run = lambda *a, **k: _FakeR(1, b'sudo: The "no new privileges" flag is set\n')
spawned = {}
ctl._spawn = lambda argv, detached=True, cwd=None: spawned.setdefault("argv", argv) or True
ctl._start_tailer = lambda: None
orig_thread = core.threading.Thread
core.threading.Thread = lambda *a, **k: type("_N", (), {"start": lambda s: None})()
ok, msg = ctl._start_inner({"DshAdminMode": True, "DshDir": "/tmp/no-such-dsh-dir",
                            "DshMode": "compiled-js"})
core.threading.Thread = orig_thread
core.subprocess.run = orig_run
argv0 = str((spawned.get("argv") or [""])[0])
check("管理员模式不可用 → 自动降级普通启动(argv 不含 sudo)",
      ok and argv0 != "sudo", str(spawned))
check("降级日志含原因 + 可操作指引",
      any("管理员模式不可用" in l for l in hub2.lines)
      and any("NoNewPrivs" in l for l in hub2.lines)
      and any("不需要 root" in l for l in hub2.lines),
      " | ".join(hub2.lines[-3:])[:200])
check("降级后仍报告启动成功(不返回 sudo 启动失败)", ok and msg is None, str(msg))

# --- 6) 宿主侧协议一致性(活体探针: 用 GNOME 扩展自带的 interfaces-xml 复现它的代理) ---
# 本轮根因: SNI 的 Menu 声明是 object path(**o**), 我们按字符串(**s**)返回 →
# GDBus 把类型不符的属性从缓存里丢掉(宿主 journal: "Received property Menu with type s
# does not match expected type o") → 扩展 this._proxy.Menu 为 null → menuPath 空 →
# 永不创建 dbusmenu 客户端 → 右键只有空菜单("右键无交互")。
BASE_XML = ("/usr/share/gnome-shell/extensions/"
            "ubuntu-appindicators@ubuntu.com/interfaces-xml/")
tray_pid = None
# 不能用 pgrep -f: 包裹 shell 的命令行里含 "tray.py" 字面量, 字符类也挡不住
# (正则 [t]ray.py 命中的正是那段字面量), 会拿到 bash 的 PID 而不是托盘的。
for _p in os.listdir("/proc"):
    if not _p.isdigit():
        continue
    try:
        with open("/proc/%s/comm" % _p) as fh:
            comm = fh.read().strip()
        with open("/proc/%s/cmdline" % _p, "rb") as fh:
            args = fh.read().split(b"\0")
        if comm.startswith("python") and any(
                os.path.basename(a.decode("utf-8", "ignore")) == "tray.py" for a in args):
            tray_pid = int(_p)
            break
    except OSError:
        continue

if tray_pid is None:
    print("SKIP 宿主侧协议探针(未运行 tray.py)")
else:
    import subprocess
    import gi
    gi.require_version("Gio", "2.0")
    from gi.repository import Gio, GLib
    NAME = "org.kde.StatusNotifierItem-%d" % tray_pid
    conn = Gio.bus_get_sync(Gio.BusType.SESSION, None)

    def _iface(fname, iname):
        info = Gio.DBusNodeInfo.new_for_xml(
            "<node>" + open(BASE_XML + fname).read() + "</node>")
        return next(i for i in info.interfaces if i.name == iname)

    def _call(method, params, psig, rsig):
        r = conn.call_sync(NAME, "/MenuBar", "com.canonical.dbusmenu", method,
                           GLib.Variant(psig, params), GLib.VariantType(rsig),
                           Gio.DBusCallFlags.NONE, 3000, None)
        return r.unpack()

    mism, cached = [], {}
    for fname, iname, path in (
            ("StatusNotifierItem.xml", "org.kde.StatusNotifierItem", "/StatusNotifierItem"),
            ("DBusMenu.xml", "com.canonical.dbusmenu", "/MenuBar")):
        inf = _iface(fname, iname)
        proxy = Gio.DBusProxy.new_sync(conn, Gio.DBusProxyFlags.NONE, inf,
                                       NAME, path, iname, None)
        for pr in inf.properties:
            v = proxy.get_cached_property(pr.name)
            if v is None:
                continue      # 未导出的属性由宿主按规范默认值处理
            cached[pr.name] = v
            if v.get_type_string() != pr.signature:
                mism.append("%s: XML 声明 %s, 实际 %s"
                            % (pr.name, pr.signature, v.get_type_string()))
    check("SNI+dbusmenu 已导出属性类型逐一对上宿主 XML", not mism, "; ".join(mism))
    check("Menu 以 object path(o) 导出并指向 /MenuBar",
          "Menu" in cached and cached["Menu"].get_type_string() == "o"
          and cached["Menu"].unpack() == "/MenuBar", repr(cached.get("Menu")))
    rev, layout = _call("GetLayout", (0, -1, []), "(iias)", "(u(ia{sv}av))")
    ids = [c[0] for c in layout[2]]
    check("GetLayout 严格回包可解且含 4 项(显示/轻量化/分隔线/退出)",
          len(ids) == 4, str(ids))
    check("复选项带 toggle-type + toggle-state",
          any("toggle-type" in c[1] and "toggle-state" in c[1] for c in layout[2]),
          str(layout[2]))
    rows = _call("GetGroupProperties", (ids, []), "(aias)", "(a(ia{sv}))")[0]
    check("GetGroupProperties(ids, []) 按规范返回全部属性",
          len(rows) == len(ids) and all(r[1] for r in rows), str(rows))
    # LayoutUpdated 必须是 (u, i): parent 是 int32, 不是 uint32
    sig = subprocess.run(["busctl", "--user", "introspect", NAME, "/MenuBar"],
                         capture_output=True, text=True).stdout
    line = [l for l in sig.splitlines() if "LayoutUpdated" in l]
    check("LayoutUpdated 信号签名符合规范(uu→ui)",
          bool(line) and "ui" in line[0], str(line))

print()
if FAILS:
    print("FAILED: %d 项: %s" % (len(FAILS), FAILS))
    sys.exit(1)
print("ALL REGRESSION CHECKS PASSED")
