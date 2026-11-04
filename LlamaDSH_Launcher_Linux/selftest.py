# -*- coding: utf-8 -*-
"""Linux 版回归自检(115 项): 同步批次 + 历轮真实 bug 的断言, 失败即非零退出。

跑法: python3 selftest.py   (托盘/服务在跑时会额外做活体宿主侧协议探针,
     未运行则打印 SKIP 而不是失败)
注意: 本机没有 Xvfb/输入注入工具, traydialog 的退出码契约只能真弹一次窗
      (约 0.2s 一闪), 否则那 4 项只能空转。
每项断言都对应一个真实踩过的坑, 修 bug 前先跑它, 修完再跑它。"""
import io
import json
import os
import shutil
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

# --- 7) "打开 Web" 与 托盘退出的二次确认 ---
import inspect
import subprocess
import tempfile
import tray as _tray


class _Fake:
    """只带被测方法所需属性的替身(不建总线对象、不跑主循环)。"""
    MENU_SHOW, MENU_LIGHT, MENU_QUIT, MENU_SEP = 1, 2, 3, 4
    BTN_SERVICES = _tray.TrayIcon.BTN_SERVICES
    BTN_CLIENT = _tray.TrayIcon.BTN_CLIENT
    BTN_CANCEL = _tray.TrayIcon.BTN_CANCEL
    lightweight = True

    def __init__(self):
        self.calls = []
        self._exit_busy_until = 0.0

    def _service_pids(self):
        return []

    def _do_exit(self, choice, rc, err):
        self.calls.append("do_exit")
        return False

    def on_show(self):
        self.calls.append("show")

    def on_quit(self):
        self.calls.append("quit")

    def _close_window(self):
        self.calls.append("close_window")

    def _stop_all_then_quit(self):
        self.calls.append("stop_all")

    def _server_alive(self):
        return False


lay = _tray.TrayIcon._layout(_Fake())
check("托盘菜单最后一行是“退出”(退出方式挪到二次确认)",
      lay[2][-1][1].get("label") == "退出", str(lay[2][-1]))
src_fire = inspect.getsource(_tray.TrayIcon._fire)
check("点“退出”走 ask_exit 而不是直接退出",
      "self.MENU_QUIT: self.ask_exit" in src_fire and "MENU_QUIT: self.on_quit" not in src_fire,
      src_fire.strip()[:120])

# ask_exit 必须把三选按约定顺序交给一次性确认框子进程
captured = {"calls": []}
orig_run = _tray.subprocess.run


def _fake_run(argv, **kw):
    captured["calls"].append(list(argv))

    class _R:
        returncode, stdout, stderr = (0, "", "") if "--check" in argv else (1, "", "")
    return _R()


_tray.subprocess.run = _fake_run
_f = _Fake()
_tray.TrayIcon.ask_exit(_f)
for _ in range(40):
    if captured["calls"]:
        break
    time.sleep(0.05)
argv = (captured["calls"][1] if len(captured["calls"]) > 1
        else (captured["calls"][0] if captured["calls"] else []))
check("确认框由一次性子进程弹(不在 D-Bus 主线程里等 GTK)",
      len(argv) > 1 and os.path.basename(argv[1]) == "traydialog.py", str(argv[:3]))
check("弹框前先 --check 探桌面/GTK3(否则 GTK 自己 exit(1) 会被当成'用户取消')",
      "--check" in (captured["calls"][0] if captured["calls"] else []),
      str(captured["calls"][:1])[:120])
check("二次确认三选顺序 = 关闭服务并退出 / 退出 / 取消",
      argv[-3:] == [_Fake.BTN_SERVICES, _Fake.BTN_CLIENT, _Fake.BTN_CANCEL], str(argv[-3:]))

# 闩: 弹框期间再点“退出”不得再起一个框(否则 45s 等待期能重复停服)
_n0 = len(captured["calls"])
_tray.TrayIcon.ask_exit(_f)
time.sleep(0.3)
check("确认框在弹时重复点“退出”被忽略(时间闩)", len(captured["calls"]) == _n0,
      "%d → %d" % (_n0, len(captured["calls"])))
# 取消之后必须放开闩, 否则这一行菜单从此再也点不动(仍在假 subprocess 下测, 不弹真框)
_tray.TrayIcon._do_exit(_f, _Fake.BTN_CANCEL, 1, "")
_tray.TrayIcon.ask_exit(_f)
for _ in range(40):
    if len(captured["calls"]) > _n0:
        break
    time.sleep(0.05)
check("取消后放开闩(否则“退出”菜单永久失效)", len(captured["calls"]) > _n0,
      str(len(captured["calls"])))
_tray.subprocess.run = orig_run

for choice, expect in ((_Fake.BTN_CANCEL, []),
                       (_Fake.BTN_CLIENT, ["close_window", "quit"]),
                       (_Fake.BTN_SERVICES, ["stop_all"])):
    g = _Fake()
    _tray.TrayIcon._do_exit(g, choice, 0, "")
    time.sleep(0.3)
    check("选择“%s” → %s" % (choice, expect or "无动作"), g.calls == expect, str(g.calls))
g = _Fake()
_buf = io.StringIO()
_stdout = sys.stdout
sys.stdout = _buf
try:
    _tray.TrayIcon._do_exit(g, "", 2, "GTK3 不可用")
finally:
    sys.stdout = _stdout
check("确认框弹不出来(rc=2)时既不退出也要把原因留在日志",
      g.calls == [] and "无法显示" in _buf.getvalue(),
      "%s | %s" % (g.calls, _buf.getvalue()[:80]))
for rc, kw in ((3, "进程异常"), (4, "按取消处理")):
    g = _Fake()
    _b = io.StringIO()
    _so = sys.stdout
    sys.stdout = _b
    try:
        _tray.TrayIcon._do_exit(g, "", rc, "模拟")
    finally:
        sys.stdout = _so
    check("确认框 rc=%d 与 rc=2 语义分开(日志里说清是哪一种)" % rc,
          g.calls == [] and kw in _b.getvalue(), "%s | %s" % (g.calls, _b.getvalue()[:80]))

# 服务存活判定: 只靠 HTTP 会在"服务正忙到连接超时"时假阴性 → 跳过兜底 → llama 变孤儿
_fp_orig = _tray._find_py_pids


def _boom(req, timeout=None):
    raise OSError("connection refused")


_urlopen_keep = _tray.urllib.request.urlopen
_tray.urllib.request.urlopen = _boom
_tray._find_py_pids = lambda script: [4242]
check("HTTP 不通但服务进程仍在 → 仍判活(不能就此跳过兜底清理)",
      _tray.TrayIcon._server_alive(_Fake()) is True, "")
_tray._find_py_pids = lambda script: []
check("HTTP 与进程都不在 → 判死", _tray.TrayIcon._server_alive(_Fake()) is False, "")
_tray.urllib.request.urlopen = _urlopen_keep
_tray._find_py_pids = _fp_orig

# 兜底 SIGTERM 的目标必须限定"本目录那份脚本": 只按文件名匹配会命中用户自己
# 从别的目录(或安装副本)跑的 main.py, 一按"关闭服务并退出"就把无辜实例杀了
_pa_dir = tempfile.mkdtemp(prefix="llamadsh_ps_a_")
_pb_dir = tempfile.mkdtemp(prefix="llamadsh_ps_b_")
for _d in (_pa_dir, _pb_dir):
    with open(os.path.join(_d, "main.py"), "w", encoding="utf-8") as _fh:
        _fh.write("import time\ntime.sleep(30)\n")
_pa = subprocess.Popen([sys.executable, os.path.join(_pa_dir, "main.py")])
_pb = subprocess.Popen([sys.executable, os.path.join(_pb_dir, "main.py")])
time.sleep(0.7)
_ad = _tray.APP_DIR
_tray.APP_DIR = _pa_dir
try:
    _hits = _tray._find_py_pids("main.py")
finally:
    _tray.APP_DIR = _ad
for _p in (_pa, _pb):
    _p.terminate()
shutil.rmtree(_pa_dir, ignore_errors=True)
shutil.rmtree(_pb_dir, ignore_errors=True)
check("_find_py_pids 只认本目录那份脚本(兜底 SIGTERM 不会误杀别的实例)",
      _pa.pid in _hits and _pb.pid not in _hits,
      "hits=%s a=%s b=%s" % (_hits, _pa.pid, _pb.pid))

# "关闭服务并退出"必须先让服务端停 llama+DSH
opened_urls = []
orig_urlopen = _tray.urllib.request.urlopen


class _Resp:
    def read(self):
        return b"{}"

    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False


def _fake_urlopen(req, timeout=None):
    opened_urls.append(getattr(req, "full_url", str(req)))
    return _Resp()


_tray.urllib.request.urlopen = _fake_urlopen
g = _Fake()
g.base_url = "http://127.0.0.1:1"
threading.Thread(target=lambda: _tray.TrayIcon._stop_all_then_quit(g)).start()
time.sleep(1.0)


class _FakeSvc(_Fake):
    """服务已退出, 但它停 llama/DSH 没成功(以 root 启动时必然如此)"""

    def _service_pids(self):
        return [os.getpid(), 999999999]


g2 = _FakeSvc()
g2.base_url = "http://127.0.0.1:1"
_b2 = io.StringIO()
_s2 = sys.stdout
sys.stdout = _b2
try:
    _tray.TrayIcon._stop_all_then_quit(g2)
finally:
    sys.stdout = _s2
_tray.urllib.request.urlopen = orig_urlopen
check("“关闭服务并退出”先 POST /api/shutdown",
      any(u.endswith("/api/shutdown") for u in opened_urls), str(opened_urls))
check("停完服务后按 PID 复核 llama.cpp/DSH, 残留进程要点名(否则只见“托盘没了”)",
      "残留进程" in _b2.getvalue() and str(os.getpid()) in _b2.getvalue(),
      _b2.getvalue()[:160])
check("复核不改变收尾动作(照样收窗口与托盘)", "close_window" in g2.calls, str(g2.calls))


class _FakeClean(_Fake):
    def _service_pids(self):
        return []


g3 = _FakeClean()
g3.base_url = "http://127.0.0.1:1"
_b3 = io.StringIO()
_s3 = sys.stdout
sys.stdout = _b3
try:
    _tray.TrayIcon._stop_all_then_quit(g3)
finally:
    sys.stdout = _s3
check("确实停干净时在日志里明确写“已停止”(不能只留一句沉默)",
      "已停止" in _b3.getvalue(), _b3.getvalue()[:120])

main_src = open(os.path.join(APP, "main.py"), encoding="utf-8").read()
check("服务端有 /api/shutdown 路由", '("shutdown",): api_shutdown' in main_src)
i0 = main_src.index("def _stop_all_and_exit")
shut_src = main_src[i0:i0 + 1300]
check("退出收尾停完 llama.cpp 与 DSH 才结束进程(顺序不能反)",
      shut_src.index("os._exit") > shut_src.index("LLAMA") and "DSH" in shut_src,
      shut_src[:60])
check("仍在跑的后台任务在退出前记进日志(os._exit 不让独立会话的长任务停下来)",
      'j["status"] == "running"' in shut_src, shut_src[:80])
check("/api/shutdown 与信号收尾共用同一条路(不在两处各写一份)",
      "_stop_all_and_exit" in main_src[main_src.index("def api_shutdown"):][:500], "")
check("SIGTERM/SIGINT 也走收尾 —— 否则服务被杀后 llama/DSH 变孤儿且留 stale lock",
      "signal.signal(sig, _on_term)" in main_src and "signal.SIGTERM" in main_src, "")
check("open/dsh 由服务端交系统浏览器(WebKit 客户端里 window.open 会被静默吞掉)",
      "def _open_external" in main_src and '"opened": opened' in main_src)
check("_open_external 只接 http/https(不把任意串交给 xdg-open)",
      'url.startswith(("http://", "https://"))' in main_src)
appjs = open(os.path.join(APP, "webui", "app.js"), encoding="utf-8").read()
j0 = appjs.index('$("btnOpenDsh").onclick')   # 第一处是 disabled 赋值, 要的是点击处理
seg = appjs[j0:j0 + 420]
check("前端不再把 window.open 当唯一出路(服务端开不成才回落, 失败要可见)",
      "r.opened" in seg and "showNotice" in seg, seg[:120])
cli = open(os.path.join(APP, "client.py"), encoding="utf-8").read()
check("客户端接了 WebKit create 信号(window.open/_blank 交系统浏览器)",
      'web.connect("create", _on_create)' in cli)
check("客户端交系统浏览器时只认 http/https(页面里可能是 mailto:/file: 等任意 scheme)",
      "def hand_off_url(uri):" in cli and 'uri.startswith(("http://", "https://"))' in cli, "")
_pol = cli[cli.index("def _on_policy"):cli.index("def _on_create")]
_pol_code = "\n".join(ln for ln in _pol.splitlines() if not ln.strip().startswith("#"))
check("decide-policy 先绑定 uri 再决策, 取不到 URI 时绝不 ignore(否则内部导航被吞成白屏)",
      _pol_code.index('uri = ""') < _pol_code.index("try:")
      and _pol_code.count("decision.ignore()") == 1 and "decision.use()" in _pol_code,
      _pol_code[:110].replace("\n", " | "))
check("窗口锁以 append 打开(用 w 会在抢锁前把别人的 PID 抹掉)",
      'open(path, "a")' in cli, "")

# traydialog 的退出码契约: 0=选中并把标签写 stdout / 2=无显示环境
td = os.path.join(APP, "traydialog.py")
r = subprocess.run([sys.executable, td, "--check"], capture_output=True, text=True, timeout=30)
check("traydialog --check: 本机 GTK3 与显示可用", r.returncode == 0, r.stderr[:120])
r = subprocess.run([sys.executable, td, "--respond", _Fake.BTN_CLIENT, "--delay", "0.2",
                    "T", "B", _Fake.BTN_SERVICES, _Fake.BTN_CLIENT, _Fake.BTN_CANCEL],
                   capture_output=True, text=True, timeout=30)
check("traydialog 回传选中标签原文且退出码 0",
      r.returncode == 0 and r.stdout.strip() == _Fake.BTN_CLIENT,
      "rc=%s out=%r" % (r.returncode, r.stdout[:60]))
check("traydialog 弹窗成功在 stderr 留痕(没有输入注入工具时唯一取证)",
      "已弹出" in r.stderr, r.stderr[:120])
r = subprocess.run([sys.executable, td, "T", "B", _Fake.BTN_CLIENT],
                   capture_output=True, text=True, timeout=30,
                   env={"HOME": os.path.expanduser("~"), "PATH": os.environ.get("PATH", "")})
check("无显示环境 → 退出码 2(调用方据此提示, 而不是'点了没反应')",
      r.returncode == 2 and "无显示" in r.stderr, "rc=%s %s" % (r.returncode, r.stderr[:80]))
r = subprocess.run([sys.executable, td, "--respond", "根本没有这个按钮", "--delay", "0.2",
                    "T", "B", _Fake.BTN_SERVICES, _Fake.BTN_CLIENT, _Fake.BTN_CANCEL],
                   capture_output=True, text=True, timeout=30)
check("--respond 标签不存在时报错退 2(静默按取消应答会让回归测试假绿)",
      r.returncode == 2 and "没有这个按钮" in r.stderr,
      "rc=%s %s" % (r.returncode, r.stderr[:80]))

# --- 8) 本地插件安装(装 dsh-infinite-context-0.8.8.tgz 报"非法包名"那一族) ---
import compat as _compat
import tempfile

_tmp = tempfile.mkdtemp(prefix="llamadsh_selftest_plugin_")
_home = os.path.join(_tmp, "dshhome")
_prof = os.path.join(_home, "profiles", "web")
os.makedirs(os.path.join(_prof, "node_modules", "demo-plugin"))
_compat.dsh_home = lambda: _home
_compat.profile_dir = lambda p="web": _prof

# 造一个 tgz: 根 package.json 是本体, examples/ 里再放一个假的干扰
import tarfile
_pkg = os.path.join(_tmp, "src")
os.makedirs(os.path.join(_pkg, "examples"))
with open(os.path.join(_pkg, "package.json"), "w", encoding="utf-8") as fh:
    json.dump({"name": "demo-plugin", "version": "9.9.9", "main": "index.js",
               "dsh": {"bundle": "index.js"}}, fh)
with open(os.path.join(_pkg, "examples", "package.json"), "w", encoding="utf-8") as fh:
    json.dump({"name": "not-the-plugin", "version": "0.0.1"}, fh)
with open(os.path.join(_pkg, "index.js"), "w", encoding="utf-8") as fh:
    fh.write("// stub\n")
_tgz = os.path.join(_tmp, "demo-plugin-9.9.9.tgz")
with tarfile.open(_tgz, "w:gz") as tf:
    tf.add(_pkg, arcname="package")
ident = _compat._tarball_identity(_tgz)
check("_tarball_identity 取包根 package.json 的 name/version(忽略 examples/ 里的)",
      ident == ("demo-plugin", "9.9.9"), str(ident))

# profile manifest + 同名旧声明(指向已删除的旧 tgz)
_manifest = {"dependencies": {"demo-plugin": "file:/gone/old/demo-plugin-1.0.0.tgz"},
             "dsh": {"profile": {"bundles": []}}}
with open(os.path.join(_prof, "package.json"), "w", encoding="utf-8") as fh:
    json.dump(_manifest, fh)
with open(os.path.join(_prof, "node_modules", "demo-plugin", "package.json"),
          "w", encoding="utf-8") as fh:
    json.dump({"name": "demo-plugin", "version": "9.9.9", "dsh": {"bundle": "index.js"}}, fh)

pnpm_calls = []
_compat.procman_which_pnpm = lambda: True
orig_compat_run = _compat.subprocess.run


def _fake_pnpm(cmd, cwd=None, **kw):
    pnpm_calls.append(list(cmd))
    if cmd[:2] == ["pnpm", "add"]:
        # 模拟 pnpm 写依赖
        m = json.load(open(os.path.join(_prof, "package.json")))
        m["dependencies"][ident[0]] = cmd[2][len("file:"):] if cmd[2].startswith("file:") else cmd[2]
        json.dump(m, open(os.path.join(_prof, "package.json"), "w"))

    class _R:
        returncode, stdout, stderr = 0, "", ""
    return _R()


_compat.subprocess.run = _fake_pnpm
ok, msg = _compat.install_local_plugin("web", _tgz)
_compat.subprocess.run = orig_compat_run
adds = [c for c in pnpm_calls if c[:2] == ["pnpm", "add"]]
rms = [c for c in pnpm_calls if c[:2] == ["pnpm", "remove"]]
check("安装 tgz 不再报“非法包名”(旧代码把路径当包名送进白名单)",
      ok and "非法包名" not in msg, msg)
check("pnpm add 收到的是 file:<<dshHome>/packages 快照路径>",
      len(adds) == 1 and adds[0][2] == "file:" + os.path.join(_home, "packages",
                                                              "demo-plugin-9.9.9.tgz"),
      str(adds))
check("tgz 已复制进 <dshHome>/packages(profile 不依赖 U 盘/临时目录)",
      os.path.isfile(os.path.join(_home, "packages", "demo-plugin-9.9.9.tgz")), "")
check("同名旧 spec 指向失效路径时先 pnpm remove 再 add",
      len(rms) == 1 and rms[0][2] == "demo-plugin" and pnpm_calls.index(rms[0]) < pnpm_calls.index(adds[-1]),
      str(pnpm_calls))
check("包声明了 dsh.bundle → 自动加入 profile bundles",
      "demo-plugin" in (((json.load(open(os.path.join(_prof, "package.json")))
                          .get("dsh") or {}).get("profile") or {}).get("bundles") or []), msg)

# 失败必须回滚, 不留半安装状态
before_manifest = open(os.path.join(_prof, "package.json"), encoding="utf-8").read()
_compat.procman_which_pnpm = lambda: True


def _fail_add(cmd, cwd=None, **kw):
    pnpm_calls.append(list(cmd))

    class _R:
        returncode, stdout, stderr = (1, "", "ERR_PNPM_ENOENT old tarball missing") if cmd[:2] == ["pnpm", "add"] else (0, "", "")
    return _R()


# 上一轮装成功后声明已经等于新 spec, 这里再把它改成不一致, 才能测到 remove + 装回
_m = json.load(open(os.path.join(_prof, "package.json")))
_m["dependencies"]["demo-plugin"] = "file:/gone/other/demo-plugin-9.9.9.tgz"
json.dump(_m, open(os.path.join(_prof, "package.json"), "w"))
_manifest["dependencies"]["demo-plugin"] = "file:/gone/other/demo-plugin-9.9.9.tgz"
before_manifest = open(os.path.join(_prof, "package.json"), encoding="utf-8").read()
_n_before = len(pnpm_calls)
_compat.subprocess.run = _fail_add
ok2, msg2 = _compat.install_local_plugin("web", _tgz)
_compat.subprocess.run = orig_compat_run
adds2 = [c for c in pnpm_calls[_n_before:] if c[:2] == ["pnpm", "add"]]
check("pnpm add 失败 → 回滚 package.json 并说明原因",
      (not ok2) and open(os.path.join(_prof, "package.json"), encoding="utf-8").read() == before_manifest
      and "已回滚" in msg2, msg2[:160])
check("同名旧依赖已被 remove 而新装又失败时, 尽力把旧 spec 装回并把话说清楚",
      len(adds2) == 2 and adds2[-1][2] == "demo-plugin@" + _manifest["dependencies"]["demo-plugin"]
      and "未能装回" in msg2, "%s | %s" % (adds2, msg2[:120]))
check("失败信息里保留 ERR_PNPM_* 行(末尾常被进度条/警告挤掉)",
      "ERR_PNPM_ENOENT" in msg2, msg2[:200])


# 超时/OSError 这类异常也必须回滚, 且不能把裸 traceback 冒到 API 层
def _boom_run(cmd, cwd=None, **kw):
    raise _compat.subprocess.TimeoutExpired(cmd=cmd, timeout=600)


_compat.subprocess.run = _boom_run
ok_e, msg_e = _compat.install_local_plugin("web", _tgz)
_compat.subprocess.run = orig_compat_run
check("pnpm 超时 → 回滚 + 可读错误(不是未捕获的 traceback)",
      (not ok_e) and "安装异常" in msg_e
      and open(os.path.join(_prof, "package.json"), encoding="utf-8").read() == before_manifest,
      msg_e[:160])

src_ilp = inspect.getsource(_compat.install_local_plugin)
check("安装新包只走 _pnpm_add_spec(tgz 路径不能送进包名白名单)",
      "_pnpm_add_spec(base, spec)" in src_ilp, src_ilp[:80])
ok3, msg3 = _compat._pnpm_add_spec(_prof, "-force")
check("以 '-' 开头的 spec 被挡掉(否则会被 pnpm 当命令行参数)",
      not ok3 and "非法安装参数" in msg3, msg3)

# review 补: 参数形态 / 文件名 / 路径 / 时间戳 这几处校验
_ok_a, _ = _compat._spec_ok("demo-plugin@^1.2.3")
_ok_b, _ = _compat._spec_ok("@scope/pkg@2.0.0")
_ok_c, _ = _compat._spec_ok("@scope/pkg")
_no_a, _ = _compat._spec_ok("--registry=http://evil")
_no_b, _ = _compat._spec_ok("demo-plugin@$(rm -rf)")
check("spec 收紧后仍放行 name@range 与 scoped 包名", _ok_a and _ok_b and _ok_c, "")
check("以 - 开头或 range 里带 shell 元字符仍被挡", not _no_a and not _no_b, "")
_fn = _compat._pkg_filename("@evil/../../etc/passwd", "1.0;rm")
check("包名/版本拼进 packages 文件名前必须消毒(否则文件能写到 packages 之外)",
      "/" not in _fn and ".." not in _fn and ";" not in _fn, _fn)
check("_rel_path 不误伤 .hidden(lstrip('./') 会把它削成 hidden)",
      _compat._rel_path("./.hidden/x") == ".hidden/x"
      and _compat._rel_path("../escape") == "" and _compat._rel_path("!nope") == "",
      repr(_compat._rel_path("./.hidden/x")))
_psrc = os.path.join(_tmp, "packsrc")
os.makedirs(os.path.join(_psrc, "dist"))
for _leaf in ("a.js", "b.js"):
    with open(os.path.join(_psrc, "dist", _leaf), "w", encoding="utf-8") as fh:
        fh.write("//x\n")
_ents = _compat._pack_entries(_psrc, ["dist/*.js"])
check("files 里的通配条目要 glob 展开(当字面路径复制 → 暂存包里没有产物)",
      sorted(_ents) == [os.path.join("dist", "a.js"), os.path.join("dist", "b.js")], str(_ents))
_tdir = os.path.join(_tmp, "tgzdir")
os.makedirs(_tdir)
_old = os.path.join(_tdir, "old-1.0.0.tgz")
with open(_old, "wb") as fh:
    fh.write(b"x")
os.utime(_old, (time.time() - 3600, time.time() - 3600))
check("_newest_tgz 能按时间过滤: 上一轮失败留下的旧包不能被当成本次产物",
      _compat._newest_tgz(_tdir) == _old
      and _compat._newest_tgz(_tdir, not_before=time.time() - 60) is None, "")
_bom = os.path.join(_tmp, "bom.json")
with open(_bom, "wb") as fh:
    fh.write(b"\xef\xbb\xbf" + b'{"name": "bom-ok"}')
check("package.json 带 BOM 也要能读(node/Windows 写的首字符会炸 json)",
      _compat.read_json(_bom, {}).get("name") == "bom-ok", str(_compat.read_json(_bom, {})))
_bare = os.path.join(_tmp, "bare.tgz")
with tarfile.open(_bare, "w:gz") as tf:
    tf.add(_pkg, arcname=".")
_ident_bare = _compat._tarball_identity(_bare)
check("_tarball_identity 认裸根布局且仍取最浅一层(examples/ 不算本体)",
      _ident_bare == ("demo-plugin", "9.9.9"), str(_ident_bare))
_k, _d = _compat.filter_bundle_activation(
    ["demo-plugin"], [{"name": "demo-plugin", "compatible": False,
                       "missing_specs": [], "errors": []}], "web")
check("装成功后有 dsh.bundle 也要先过激活守卫(审计不兼容就不自动激活)",
      _k == [] and _d and "peer" in _d[0]["reason"], str((_k, _d)))
shutil.rmtree(_tmp, ignore_errors=True)

print()
if FAILS:
    print("FAILED: %d 项: %s" % (len(FAILS), FAILS))
    sys.exit(1)
print("ALL REGRESSION CHECKS PASSED")
