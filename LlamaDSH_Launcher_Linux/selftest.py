# -*- coding: utf-8 -*-
"""Linux 版回归自检(352 项): 同步批次 + 历轮真实 bug 的断言, 失败即非零退出。

跑法: python3 selftest.py   (托盘/服务在跑时会额外做活体宿主侧协议探针,
     未运行则打印 SKIP 而不是失败)
自检自己也被约束(第 17 节): applog.LOG_DIR 在**任何被测模块 import 之前**就改到临时目录,
     跑自检不得往用户的 ~/.dsh_launcher_logs 里写一行。
注意: 本机没有 Xvfb/输入注入工具, traydialog 的退出码契约只能真弹一次窗
      (约 0.2s 一闪), 否则那 4 项只能空转。
每项断言都对应一个真实踩过的坑, 修 bug 前先跑它, 修完再跑它。
注意: 源码级断言(比对字符串)挡不住运行时错误 —— 收尾/退出这一族另有行为级
      测试(第 13 节): 进程内真调收尾函数 + 隔离 HOME 起真实例打 /api/shutdown。
第 25 节是"修复动作"本身的行为级测试: 用临时 profile/dshHome 真跑审计、一键修复、
      装插件回滚与一键安装 Node, 每条守卫都配反向对照(证据在→必须改, 证据缺→必须不动)。"""
import io
import json
import os
import shutil
import struct
import sys
import tempfile
import threading
import time
import types

sys.path.insert(0, os.path.dirname(os.path.abspath(sys.argv[0] or ".")))
APP = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, APP)
os.chdir(APP)

# 自检绝不能往用户的真实日志目录里写。以前每跑一次 selftest, ~/.dsh_launcher_logs/
# error.log 里就多几条测试噪声(2026-11-05 实测: 一次跑留下 "资产名形态非法:
# ../evil.tar.gz" 与"摘要不一致"两条), 用户排障时会把它们当成真事故, 真事故的密度
# 也被稀释。必须**在任何被测模块 import 之前**改好 —— LOG_DIR 全进程只有一份。
import applog as _applog_top          # noqa: E402
_REAL_LOG_DIR = _applog_top.LOG_DIR
_SELFTEST_HOME = tempfile.mkdtemp(prefix="llamadsh_selftest_home_")
_applog_top.LOG_DIR = os.path.join(_SELFTEST_HOME, "logs")

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

# run.log 每行由 applog 统一加日期: LogHub 再带一份就变成 "[日期] [时刻] 内容"
import inspect as _insp
_hlog = _insp.getsource(core.LogHub.log)
check("run.log 不双时间戳(applog 已带日期, LogHub 只喂正文)",
      "applog.run(text)" in _hlog and 'applog.run("[%s' not in _hlog
      and 'self.run.append("[%s' in _hlog, _hlog.strip()[-120:])
# --------------- LlamaRunner.stop 的幂等 / 用户停止不被 OOM 重启推翻 --------
# 本轮 A1: 端口缓存存在而 proc is None 时, stop() 把 None 交给 kill_process_tree,
#          它的 except 里 `pids = [int(pid)]` 对 None 再抛一次同一个 TypeError,
#          异常逃出 stop() → 状态清理整段被跳过, 面板永远显示 running。
# 本轮 A2: stop() 发的 SIGTERM 退出码是 -15, 以前走"异常退出"分支; 本轮见过 OOM
#          时接着自动降层重启一个 llama-server —— 收尾场景下它就是孤儿。
import procman

_clear_real = core.state.clear_state_for
core.state.clear_state_for = lambda which, state=None: None
try:
    class _Silent:
        def log(self, text):
            pass

        def error_ex(self, *a):
            pass

    class _FakeProc:
        pid = 4321

        def wait(self):
            return -15                    # SIGTERM: 我们自己 stop() 发的

    def _drive(oom, user):
        r = core.LlamaRunner(_Silent(), {"defs": []})
        r.last_profile = {"OomRetryEnabled": True}
        r.mem_state = {"oom_seen": oom, "offloaded": 30,
                       "total_layers": 40, "buffers": {}}
        r.proc = _FakeProc()
        r.user_stopped = user
        gone = []
        r._relaunch = lambda: gone.append(1)
        r._wait_exit()
        time.sleep(0.3)                   # 降层重启在独立线程里
        return r, gone

    r_stop, gone_stop = _drive(True, True)
    check("用户/收尾要的停不被 OOM 自动降层重启推翻(否则收尾时新 llama-server 变孤儿)",
          gone_stop == [] and r_stop.status == "stopped" and r_stop.oom_retry_left == 3,
          "%s %s" % (gone_stop, r_stop.status))
    r_crash, gone_crash = _drive(True, False)
    check("对照: 真崩溃且本轮 OOM 时仍会自动降层重启(新防线没把正常功能挡死)",
          gone_crash == [1] and r_crash.status == "crashed",
          "%s %s" % (gone_crash, r_crash.status))

    r_idle = core.LlamaRunner(_Silent(), {"defs": []})
    r_idle.proc = None
    r_idle._cached_port = 8080            # 端口缓存还在, 进程早已自己退出
    r_idle.status = "running"
    try:
        r_idle.stop()
        err = ""
    except Exception as ex:
        err = "%s: %s" % (type(ex).__name__, ex)
    check("stop() 只有端口缓存没有进程时幂等收尾(原必现 TypeError 的回归)",
          not err and r_idle.status == "stopped" and r_idle.user_stopped, err)
    check("kill_process_tree(None) 干净返回 False(端口查不到属主/进程已退)",
          procman.kill_process_tree(None) is False, "")
finally:
    core.state.clear_state_for = _clear_real

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
# DshDir 必须是**存在**的目录: 第十二轮起 _start_inner 会先拒绝不存在/未指明的目录
# (旧写法带着 /tmp/no-such-dsh-dir 一路走到 Popen, 真机上就是 cwd 的 Errno 2)。
_dsh_demo = os.path.join(tempfile.mkdtemp(prefix="llamadsh_selftest_dshdir_"), "dsh")
os.makedirs(os.path.join(_dsh_demo, "apps", "cli", "lib"))
with open(os.path.join(_dsh_demo, "apps", "cli", "lib", "bin.js"), "w", encoding="utf-8") as fh:
    fh.write("//x\n")
_real_abs2 = core._abs_exe
core._abs_exe = lambda name: "/bin/true"      # 钉住环境探针: 本机有没有 pnpm 都不该影响本断言
try:
    ok, msg = ctl._start_inner({"DshAdminMode": True, "DshDir": _dsh_demo,
                                "DshMode": "compiled-js"})
finally:
    core._abs_exe = _real_abs2
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
_lockpid_keep = _tray._lock_pid
_tray.urllib.request.urlopen = _boom
_tray._lock_pid = lambda script: 0
_tray._find_py_pids = lambda script: [4242]
check("HTTP 不通但服务进程仍在 → 仍判活(不能就此跳过兜底清理)",
      _tray.TrayIcon._server_alive(_Fake()) is True, "")
_tray._find_py_pids = lambda script: []
check("HTTP 与进程都不在 → 判死", _tray.TrayIcon._server_alive(_Fake()) is False, "")
# 锁文件里的 PID 也算活: 按脚本路径扫 /proc 会漏掉异目录安装/非 .py 入口/root 进程
_tray._find_py_pids = lambda script: []
_tray._lock_pid = lambda script: 4343
check("服务锁文件里记着活 PID → 判活(/proc 扫不到时的第二条路)",
      _tray.TrayIcon._server_alive(_Fake()) is True, "")
_tray.urllib.request.urlopen = _urlopen_keep
_tray._lock_pid = _lockpid_keep
_tray._find_py_pids = _fp_orig
check("_lock_pid 会校验 cmdline 防 PID 复用(否则会给无辜进程发 SIGTERM)",
      "cmdline" in inspect.getsource(_tray._lock_pid), "")

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
# 注意: “未能确认”那行文案里本来就含“是否已停止”四个字, 所以判定不能粗到
# 只查“已停止”子串(断言自己会假失败), 要查的是那句下结论的完整文案。
_out3 = _b3.getvalue()
check("停服前没拿到 PID 时说'未能确认', 不能武断报'已停止'",
      "未能确认 llama.cpp/DSH" in _out3
      and "llama.cpp 与 DSH 已停止" not in _out3 and "残留进程" not in _out3,
      _out3[:140])


class _FakeGone(_Fake):
    """停服前抄到的是已经死掉的 PID —— 这次才允许报'已停止'"""

    def _service_pids(self):
        return [_dead_pid]


_proc_gone = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(0.2)"])
_dead_pid = _proc_gone.pid
_proc_gone.wait()
g4 = _FakeGone()
g4.base_url = "http://127.0.0.1:1"
_b4 = io.StringIO()
_s4 = sys.stdout
sys.stdout = _b4
try:
    _tray.TrayIcon._stop_all_then_quit(g4)
finally:
    sys.stdout = _s4
check("确实停干净(PID 已消失)时才写'已停止'",
      "llama.cpp 与 DSH 已停止" in _b4.getvalue(), _b4.getvalue()[:120])

main_src = open(os.path.join(APP, "main.py"), encoding="utf-8").read()
_guard_src = main_src[main_src.index("def _dsh_running_guard"):][:700]
check("服务端有 /api/shutdown 路由", '("shutdown",): api_shutdown' in main_src)
i0 = main_src.index("def _stop_all_and_exit")
shut_src = main_src[i0:main_src.index("\ndef api_shutdown", i0)]   # 按函数边界取, 不用固定长度
check("退出收尾停完 llama.cpp 与 DSH 才结束进程(顺序不能反)",
      shut_src.index("os._exit(0)") > shut_src.index("LLAMA") and "DSH" in shut_src,
      shut_src[:60])
check("收尾有幂等闩: 重复 SIGTERM/Ctrl+C 不会起第二个收尾线程互相打断",
      "_SHUTTING_DOWN" in shut_src and "if _SHUTTING_DOWN:" in shut_src, shut_src[:90])
check("收尾挂看门狗: stop() 卡在 ss/lsof 时也不会永久挂住不退出",
      "threading.Timer(_EXIT_WATCHDOG_S" in shut_src and "os._exit(1)" in shut_src, "")
check("读 JOBS 前先取快照(直接迭代会和 run_job 的插入/pop 竞态)",
      "snapshot = dict(JOBS)" in shut_src and "with JOB_LOCK" in shut_src, "")
check("_dsh_running_guard 也看服务自己的 DSH 句柄(cmdline 探测带 10s 缓存会漏启动窗口)",
      "if DSH.running:" in _guard_src
      and "rebuilding" in _guard_src and "running_admin" in _guard_src, "")
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

# --- 9) 本轮 review 补: 并发/回滚方向/打包忽略/空值守卫 --------------
# 9.1 两个安装请求并发(main.py 是 ThreadingHTTPServer, 每请求一线程): 各自的备份与
#     回滚会把对方刚写好的依赖覆盖掉 —— 等于悄悄丢掉别人装好的包。必须明确拒绝。
def _hold_lock(lk, secs):
    """在**另一个线程**里持锁: RLock 可重入, 同线程 acquire 后自己调用是测不出
    并发拒绝的(第一版断言就是这么假绿的)。"""
    lk.acquire()
    time.sleep(secs)
    lk.release()


_lk = _compat._profile_lock("web")
_h1 = threading.Thread(target=_hold_lock, args=(_lk, 1.5), daemon=True)
_h1.start()
time.sleep(0.4)
ok_conc, msg_conc = _compat.install_local_plugin("web", _tgz)
_h1.join()
check("并发本地安装被明确拒绝(两个安装不能互相覆盖 package.json)",
      (not ok_conc) and "已有插件操作正在进行中" in msg_conc, msg_conc)

# 9.2 pnpm add **成功之后**再出错(典型: 激活写 manifest 时备份失败抛 IOError):
#     回滚方向是反的, 会把已经装好的包删掉还谎报"安装失败"。只报告, 不回滚。
_m = json.load(open(os.path.join(_prof, "package.json")))
_m["dependencies"]["demo-plugin"] = "file:/gone/again/demo-plugin-9.9.9.tgz"
json.dump(_m, open(os.path.join(_prof, "package.json"), "w"))
_act_keep = _compat.set_bundle_active


def _boom_activate(profile, name, active):
    raise IOError("备份 package.json 失败")


_compat.set_bundle_active = _boom_activate
_compat.subprocess.run = _fake_pnpm
ok_ad, msg_ad = _compat.install_local_plugin("web", _tgz)
_compat.subprocess.run = orig_compat_run
_compat.set_bundle_active = _act_keep
_dep_now = str((json.load(open(os.path.join(_prof, "package.json")))
                .get("dependencies") or {}).get("demo-plugin"))
check("pnpm add 成功之后的失败不得回滚成功安装(否则删掉好包还谎报失败)",
      (not ok_ad) and "已安装成功" in msg_ad and "后续处理失败" in msg_ad
      and _dep_now.startswith(os.path.join(_home, "packages")),
      "%s | dep=%s" % (msg_ad[:150], _dep_now))

# 9.3 备份失败时不许谎称"已回滚": 那句文案会让人以为 profile 还是原样。
_orig_backup = _compat._backup
_compat._backup = lambda path: None
_compat.subprocess.run = _boom_run
ok_nb, msg_nb = _compat.install_local_plugin("web", _tgz)
_compat.subprocess.run = orig_compat_run
_compat._backup = _orig_backup
check("备份失败时文案必须说未能回滚, 不能报已回滚 package.json/lock",
      (not ok_nb) and "备份失败未能回滚" in msg_nb
      and "已回滚 package.json/lock" not in msg_nb, msg_nb[:150])

# 9.4 打包: 作者写进 files 的条目原样带(哪怕目录叫 scripts/), main 入口缺失要补上。
#     借 pnpm pack 的 cwd(就是暂存目录)直接看打进包里的文件清单。
_pstage = {"files": [], "pj": {}}
_pout = os.path.join(_tmp, "packout")
os.makedirs(_pout)


def _fake_pack(cmd, cwd=None, **kw):
    _pstage["files"] = sorted(os.path.relpath(os.path.join(r, f), cwd)
                              for r, _d, fs in os.walk(cwd) for f in fs)
    _pstage["pj"] = json.load(open(os.path.join(cwd, "package.json")))
    with open(os.path.join(_pout, "p2-1.0.0.tgz"), "wb") as fh:
        fh.write(b"x")

    class _R:
        returncode, stdout, stderr = 0, "", ""
    return _R()


def _mk_pack_src(dirname, files_declared):
    d = os.path.join(_tmp, dirname)
    os.makedirs(os.path.join(d, "scripts", "node_modules"))
    os.makedirs(os.path.join(d, "dist"))
    for leaf in ("scripts/keep.js", "scripts/node_modules/inner.js",
                 "index.js", "dist/a.js"):
        with open(os.path.join(d, leaf), "w", encoding="utf-8") as fh:
            fh.write("//x\n")
    doc = {"name": "p2", "version": "1.0.0", "main": "index.js",
           "scripts": {"prepack": "tsc", "start": "node index.js"}}
    if files_declared:
        doc["files"] = ["dist", "scripts"]
    with open(os.path.join(d, "package.json"), "w", encoding="utf-8") as fh:
        json.dump(doc, fh)
    return d


_plog = []
_compat.subprocess.run = _fake_pack
_p_decl = _compat._pack_staged(_mk_pack_src("pack_declared", True), _pout,
                               job_log=_plog.append)
_files_decl = list(_pstage["files"])
_p_plain = _compat._pack_staged(_mk_pack_src("pack_plain", False), _pout,
                                job_log=_plog.append)
_files_plain = list(_pstage["files"])
_compat.subprocess.run = orig_compat_run
check("files 里显式声明的 scripts/ 必须原样进包(_PACK_IGNORE 只甩未声明的)",
      "scripts/keep.js" in _files_decl and "dist/a.js" in _files_decl
      and "scripts/keep.js" not in _files_plain,
      "decl=%s plain=%s" % (_files_decl, _files_plain))
check("main 入口被 files 排除时也要补进包(缺入口的包装上去就 ENOENT)",
      "index.js" in _files_decl and "index.js" in _files_plain
      and any("main 入口" in str(x) for x in _plog),
      "%s | %s" % (_files_decl, _plog[-2:]))
check("暂存打包必须剥掉 prepack(否则 pnpm 会在缺 devDependencies 的机器上跑 tsc)",
      "prepack" not in _pstage["pj"].get("scripts", {})
      and _pstage["pj"].get("scripts", {}).get("start") == "node index.js",
      str(_pstage["pj"].get("scripts")))
check("_pack_staged 返回本次新打的 tgz 而不是暂存目录",
      _p_decl == os.path.join(_pout, "p2-1.0.0.tgz")
      and _p_plain == os.path.join(_pout, "p2-1.0.0.tgz"),
      "%s %s" % (_p_decl, _p_plain))

# 9.5 空 dsh_dir 守卫: join("", "package.json") 会退化成读当前工作目录里的
#     package.json —— 启动器自己的版本号会被当成 DSH 版本。
_dshv = os.path.join(_tmp, "dshroot")
os.makedirs(_dshv)
with open(os.path.join(_dshv, "package.json"), "wb") as fh:
    fh.write(b"\xef\xbb\xbf" + b'{"name": "dsh", "version": "0.9.31"}')
check("空 dsh_dir 一律返回空版本/空集合(不能拿启动器自己的 package.json 冒充)",
      _compat.get_dsh_version("") == "" and _compat.shipped_dsh_package_names("") == set()
      and _compat.shipped_dsh_package_names(None) == set(), "")
check("dsh 版本读取认带 BOM 的 package.json(utf-8 直读会炸 json)",
      _compat.get_dsh_version(_dshv) == "0.9.31",
      repr(_compat.get_dsh_version(_dshv)))

# --- 10) 兜底 SIGTERM 遇到 root 属主的服务必须点名, 不能静默 ------------
class _FakeDeny(_Fake):
    """服务收到 SIGTERM 后仍没退出(老服务没有信号处理): 走兜底杀进程那条路。"""

    def __init__(self):
        _Fake.__init__(self)
        self._probe = 0

    def _server_alive(self):
        # 探活序列: while 判定(活, 等) → while 判定(活, 等) → while 判定(死, 出循环)
        # → if 判定(活 → 进兜底) → 之后一律判死。必须交替, 否则进不了兜底分支。
        seq = (True, True, False, True)
        return seq[self._probe] if self._probe < len(seq) else False


_child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
_kill_real = _tray.os.kill
_kill_seen = []


def _kill_partial(pid, sig):
    _kill_seen.append(pid)
    if pid == 4343:
        raise PermissionError(1, "Operation not permitted")
    return _kill_real(pid, sig)


_pids_keep, _lock_keep = _tray._find_py_pids, _tray._lock_pid
_tray._find_py_pids = lambda script: [_child.pid, 4343]
_tray._lock_pid = lambda script: 0
_tray.os.kill = _kill_partial
_d4 = _FakeDeny()
_d4.base_url = "http://127.0.0.1:1"
_b5 = io.StringIO()
_s5 = sys.stdout
sys.stdout = _b5
try:
    _tray.TrayIcon._stop_all_then_quit(_d4)
finally:
    sys.stdout = _s5
    _tray.os.kill = _kill_real
    _tray._find_py_pids, _tray._lock_pid = _pids_keep, _lock_keep
_child.wait()
check("兜底清理分清已发出/杀不掉: root 属主的服务要按 PID 点名(否则用户以为已停)",
      "4343" in _b5.getvalue() and "属主不是本人" in _b5.getvalue()
      and str(_child.pid) in _b5.getvalue(), _b5.getvalue()[:220])
check("兜底 SIGTERM 后还要等服务确认死亡(否则 llama/DSH 还在跑就收了托盘)",
      "for _ in range(50)" in inspect.getsource(_tray.TrayIcon._stop_all_then_quit), "")



# --- 11) 第三轮 review: 在途重启 / DSH 停止 / 看门狗预算 / 打包黑名单分族 ---
# 11.1 OOM 降层是"sleep 1s 后再 start": 这期间用户点"停止"必须能作废那次在途重启,
#      否则 stop() 刚清完状态, 1s 后又冒出一个 llama-server(收尾时就是孤儿)。
_started1 = []
_r1 = core.LlamaRunner(_Silent(), {"defs": []})
_r1.last_profile = {"OomRetryEnabled": True}
_r1.start = lambda p, skip_vram_confirm=False: (_started1.append(1), (True, ""))[1]
_r1.user_stopped = True
_r1.oom_retry_active = True
_r1._relaunch()
check("用户已停止时, 已排下的 OOM 降层重启必须作废(单靠决策点的标志挡不住在途那次)",
      _started1 == [] and _r1.oom_retry_active is False, str(_started1))
_started2 = []
_r2 = core.LlamaRunner(_Silent(), {"defs": []})
_r2.last_profile = {"OomRetryEnabled": True}
_r2.start = lambda p, skip_vram_confirm=False: (_started2.append(1), (True, ""))[1]
_r2._relaunch()
check("对照: 没有用户停止时降层重启照常进行(防线没把正常功能挡死)",
      _started2 == [1], str(_started2))

# 11.2 接管来的 DSH 退出后 PID 会被复用: 照 tracker.pid 杀整树 = 打死无辜进程。
class _ReusedTracker:
    pid = 0

    def poll(self):
        return -1                     # StartTime 变了 → 这个 PID 已不是当时那个进程


_alive = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
_ReusedTracker.pid = _alive.pid
_clear_real2 = core.state.clear_state_for
core.state.clear_state_for = lambda which, state=None: None
try:
    _dr = core.DshRunner(_Silent(), {"defs": []})
    _dr.tracker = _ReusedTracker()
    _dr.status = "running"
    _dr.stop()
    time.sleep(0.6)
    check("PID 已被复用时不得照 tracker.pid 杀进程树(tracker 带 StartTime 校验)",
          _alive.poll() is None and _dr.tracker is None and _dr.user_stopped,
          "child=%s tracker=%s" % (_alive.poll(), _dr.tracker))
finally:
    core.state.clear_state_for = _clear_real2
    _alive.terminate()
    _alive.wait()
_vsrc = inspect.getsource(core.DshRunner._verify_loop)
check("DSH 启动失败自动重试必须认 user_stopped(否则停止/收尾后又拉一个分离 DSH)",
      "user_stopped" in _vsrc and "不再自动重试" in _vsrc, _vsrc[:80])
check("提权停止要连子进程树一起 sudo kill(C# 用 taskkill /F /T, 只杀根 PID 会留 pnpm)",
      "procman.child_pids(pid)" in inspect.getsource(core.DshRunner.stop), "")

# 11.3 收尾看门狗: 预算必须大于 stop() 最坏合法路径, 到点要补杀 + 留痕
check("收尾看门狗预算必须比 stop() 最坏合法路径长(ss 10s + sudo 15s + 两轮杀树 ≈ 32s)",
      "_EXIT_WATCHDOG_S = 45" in main_src and "threading.Timer(_EXIT_WATCHDOG_S" in main_src, "")
_wd0 = main_src.index("def _watchdog_fire")
check("看门狗到点要先补发 SIGKILL 并把这件事写进日志(以前只 os._exit, 日志戛然而止)",
      "signal.SIGKILL" in main_src[_wd0:_wd0 + 900]
      and "child_pids" in main_src[_wd0:_wd0 + 900]
      and "HUB.log" in main_src[_wd0:_wd0 + 900], main_src[_wd0:_wd0 + 120])
check("正常收尾完成后要 cancel 看门狗(消除收尾耗时≈预算时两个 os._exit 抢跑)",
      "watchdog.cancel()" in shut_src or "watchdog.cancel()" in main_src, "")
check("_dsh_running_guard 要用 poll 感知的 DSH.running(句柄残留会让插件操作永久被拒)",
      "if DSH.running:" in main_src
      and "DSH.proc is not None or DSH.tracker is not None" not in main_src, "")
check("DSH 崩掉/被外部杀掉后要有 5s 对账把状态落回(否则面板假在线且 guard 永久拒绝)",
      'DSH.status.startswith("running") and not DSH.running' in main_src, "")

# 11.4 打包忽略项分两族: 声明条目只豁免开发期目录, 结构性目录永不进包
_pg = os.path.join(_tmp, "pack_glob")
os.makedirs(os.path.join(_pg, "scripts", "node_modules", "dep"))
os.makedirs(os.path.join(_pg, "dist"))
for _leaf in ("scripts/keep.js", "scripts/node_modules/dep/x.js", "index.js", "dist/a.js"):
    with open(os.path.join(_pg, _leaf), "w", encoding="utf-8") as fh:
        fh.write("//x\n")
with open(os.path.join(_pg, "package.json"), "w", encoding="utf-8") as fh:
    json.dump({"name": "p3", "version": "1.0.0", "main": "index.js",
               "files": ["scripts/*.js", "dist"]}, fh)
_compat.subprocess.run = _fake_pack
_compat._pack_staged(_pg, _pout, job_log=_plog.append)
_f_glob = list(_pstage["files"])
_pc = os.path.join(_tmp, "pack_case")
os.makedirs(os.path.join(_pc, "Tests"))
os.makedirs(os.path.join(_pc, "Docs"))
for _leaf in ("Tests/x.txt", "Docs/y.md", "index.js"):
    with open(os.path.join(_pc, _leaf), "w", encoding="utf-8") as fh:
        fh.write("//x\n")
with open(os.path.join(_pc, "package.json"), "w", encoding="utf-8") as fh:
    json.dump({"name": "p4", "version": "1.0.0", "main": "index.js"}, fh)
_compat._pack_staged(_pc, _pout, job_log=_plog.append)
_f_case = list(_pstage["files"])
_compat.subprocess.run = orig_compat_run
check("声明的通配条目(scripts/*.js)要收到, 但 node_modules 任何情况都不能进包",
      "scripts/keep.js" in _f_glob and "dist/a.js" in _f_glob
      and not any("node_modules" in f for f in _f_glob), str(_f_glob))
check("开发期目录按大小写无关比对也要甩掉(C# 用 OrdinalIgnoreCase; Tests/Docs 曾整目录进包)",
      not any(f.split("/")[0] in ("Tests", "Docs") for f in _f_case)
      and "index.js" in _f_case, str(_f_case))

# 11.5 激活守卫: 版本检查被豁免 ≠ peer 真的对上了(C# FilterBundleActivation 2693)
_k_ex, _d_ex = _compat.filter_bundle_activation(
    ["demo-plugin"], [{"name": "demo-plugin", "compatible": True, "exempted": True,
                       "peer_issues": [{"dep": "@deepseek-ai/dsh-core", "range": "^0.9",
                                        "installed": "0.7"}],
                       "missing_specs": [], "errors": []}], "web")
check("已豁免版本检查但 peer 仍不匹配的包不得自动激活(激活后 DSH 模块加载会失败)",
      _k_ex == [] and _d_ex and "豁免" in _d_ex[0]["reason"], str((_k_ex, _d_ex)))

# 11.6 pnpm 写依赖不在 JOBS 里, 收尾退出时必须能点名
_compat.begin_profile_op("web", "本地插件安装")
check("正在写依赖的动作要能被收尾点名(同步跑在 HTTP 线程, 不在 JOBS 里)",
      any("本地插件安装" in x for x in _compat.inflight_ops()), str(_compat.inflight_ops()))
check("在途登记必须成对清理(install 的 finally 里 end)",
      "begin_profile_op(profile" in src_ilp and "end_profile_op(profile)" in src_ilp,
      src_ilp[-120:])
_compat.end_profile_op("web")
check("clear 后在途集合必须为空(否则会一直误报有任务在跑)",
      _compat.inflight_ops() == [], str(_compat.inflight_ops()))

# 11.7 托盘兜底: 等待窗与文案要对齐服务端 45s 收尾预算
_tsrc = inspect.getsource(_tray.TrayIcon._stop_all_then_quit)
check("兜底 SIGTERM 后的等待窗要盖住服务端收尾预算(否则把正常收尾误报成残留)",
      "for _ in range(50)" in _tsrc and "45s" in _tsrc, _tsrc[:100])
check("杀不掉的(root 属主)服务要点名并给出动作(在所属账号下 sudo kill)",
      "sudo kill" in _tsrc and "属主不是本人" in _tsrc, "")



# --- 12) 第三轮补: profile 锁覆盖全部写操作 + 卸载必须摘 bundle --------------
# 12.1 并发保护不能只盖 install: 激活/升级/豁免都会读改写同一份 package.json,
#      与 pnpm 的写回交错就表现为"激活没生效"或"依赖又回来了"。
_lk2 = _compat._profile_lock("web")
_h2 = threading.Thread(target=_hold_lock, args=(_lk2, 1.5), daemon=True)
_h2.start()
time.sleep(0.4)
_busy = []
for _call in ((lambda: _compat.set_bundle_active("web", "demo-plugin", True), "激活"),
              (lambda: _compat.upgrade_plugin("web", "demo-plugin"), "升级"),
              (lambda: _compat.exempt_plugin("web", "demo-plugin", "9.9.9"), "豁免"),
              (lambda: _compat.uninstall_plugin("web", "demo-plugin"), "卸载")):
    _ok_b, _msg_b = _call[0]()
    _busy.append((_call[1], _ok_b, _msg_b))
_h2.join()
check("激活/升级/豁免/卸载都要过同一把 profile 锁(与 pnpm 写回不能并发)",
      all((not _ok) and "已有插件操作正在进行中" in _m for (_n, _ok, _m) in _busy),
      str(_busy))
check("锁必须可重入(install 内部会再调 set_bundle_active, 自锁就是安装失败)",
      "threading.RLock()" in inspect.getsource(_compat._profile_lock), "")
_op_a = _compat.begin_profile_op("web", "外层动作")
_op_b = _compat.begin_profile_op("web", "内层动作")
_ops1 = _compat.inflight_ops()
_compat.end_profile_op("web")
_ops2 = _compat.inflight_ops()
_compat.end_profile_op("web")
_ops3 = _compat.inflight_ops()
check("在途登记按重入深度配对: 内层结束不能把外层的点名清掉",
      _op_a and _op_b and len(_ops1) == 1 and len(_ops2) == 1 and _ops3 == [],
      str((_ops1, _ops2, _ops3)))

# 12.2 卸载: C# 的第一步是 SetBundleActive(false)。只跑 pnpm remove 会把 bundles 留在
#      指向已删包上 —— DSH 启动/入口补丁全报 entry not found。
_before = json.load(open(os.path.join(_prof, "package.json")))
assert "demo-plugin" in ((_before.get("dsh") or {}).get("profile") or {}).get("bundles"), _before
_n_rm = len(pnpm_calls)
_compat.subprocess.run = _fake_pnpm
ok_un, msg_un = _compat.uninstall_plugin("web", "demo-plugin")
_compat.subprocess.run = orig_compat_run
_after = json.load(open(os.path.join(_prof, "package.json")))
_bundles = ((_after.get("dsh") or {}).get("profile") or {}).get("bundles") or []
_pinned = _compat.read_pinned_inactive("web")
_rm_now = [c for c in pnpm_calls[_n_rm:] if c[:2] == ["pnpm", "remove"]]
check("卸载先停用并锁定: dsh.profile.bundles 里不能留下已卸载的包",
      ok_un and "demo-plugin" not in _bundles and "demo-plugin" in _pinned,
      "%s | bundles=%s pinned=%s" % (msg_un, _bundles, _pinned))
check("停用之后照样要跑 pnpm remove(停用不等于卸载)",
      len(_rm_now) == 1 and _rm_now[0][2] == "demo-plugin", str(_rm_now))


check("pnpm 取包超时要放宽且用 rc-option 写法(pnpm 12 实测 --fetch-timeout 被拒; 参数不能插在 spec 前)",
      "--config.fetch-timeout=600000" in adds[0] and adds[0][2].startswith("file:")
      and "--config.fetch-timeout=600000" in _rm_now[0],
      "%s | %s" % (adds[0], _rm_now[0]))



# --- 13) 停止失败必须露出来(不能报"已停止"还把 Start 点亮) --------------
_kill_keep, _alive_keep, _which_keep = (core.procman.kill_process_tree,
                                        core.procman.alive, core.procman.which)
core.procman.kill_process_tree = lambda pid, timeout=3.0: False
core.procman.alive = lambda pid: True
core.procman.which = lambda name: None      # 不去真跑 sudo


class _StubProc:
    pid = 999999999

    def poll(self):
        return None


class _StubTracker:
    pid = 999999999

    def poll(self):
        return None                         # 还活着


_clear_real3 = core.state.clear_state_for
core.state.clear_state_for = lambda which, state=None: None
try:
    _lr = core.LlamaRunner(_Silent(), {"defs": []})
    _lr.proc = _StubProc()
    _lr.status = "running"
    _lr.stop()
    check("杀不掉进程时 llama 状态必须是 stop_failed, 不能落成 stopped",
          _lr.status == "stop_failed", _lr.status)
    _ok2, _msg2 = _lr._start_locked({}, cmd_list=["true"], skip_vram_confirm=True)
    check("stop_failed 下不得放行启动(否则只是换个方式的端口撞车)",
          not _ok2 and "上一次停止未成功" in _msg2, _msg2)
    _ds = core.DshRunner(_Silent(), {"defs": []})
    _ds.tracker = _StubTracker()
    _ds.status = "running"
    _ds.stop()
    check("DSH 停止未成功同样落 stop_failed(复核过还在的进程不能说已停止)",
          _ds.status == "stop_failed", _ds.status)
finally:
    core.state.clear_state_for = _clear_real3
    core.procman.kill_process_tree = _kill_keep
    core.procman.alive = _alive_keep
    core.procman.which = _which_keep

_js = open(os.path.join(APP, "webui", "app.js"), encoding="utf-8").read()
check("前端把 stop_failed 当在跑(Start 禁用 / Stop 可点)并给出可见标签",
      'stop_failed: ["' in _js and '"stop_failed"].includes(s.llama.status)' in _js
      and '"stop_failed"].includes(s.dsh.status)' in _js, _js[:120])
check("_dsh_running_guard 也把 stop_failed 视为在跑(pnpm 不能碰还没停掉的 DSH)",
      '"stop_failed"' in _guard_src, "")


# --- 13) 第五轮: 收尾路径必须能**真的执行**(源码级断言挡不住 TypeError) -----
# 95ba776 把 `with _SHUTDOWN_LATCH:` 打成 `with _SHUTTING_DOWN:`(bool 不能当上下文管理
# 器), /api/shutdown 从那次起每次都抛 TypeError 然后静默不退出 —— 而第 8 节那批断言全
# 绿, 因为它们只比对字符串。教训: 收尾/退出这一族必须有行为级测试, 而且两条都要:
# 进程内直接调用(快, 覆盖幂等闩与"真的调了 stop") + 隔离实例端到端(覆盖路由、延迟、
# 真实进程退出与 run.log 落地)。
check("收尾的闩必须是 Lock: 必须是 with _SHUTDOWN_LATCH(bool 当上下文管理器就是 TypeError)",
      "with _SHUTDOWN_LATCH:" in shut_src and "with _SHUTTING_DOWN:" not in shut_src, "")

import subprocess as _sub5
import socket as _sock5
import urllib.request as _url5
import main as _m5

# 13.1 进程内: 只把 os._exit 与两个 stop() 换掉, 其余原样跑
_exit5 = []
_stop5 = []
_log5 = []
_real5 = (os._exit, _m5.LLAMA.stop, _m5.DSH.stop)
_m5.LLAMA.stop = lambda: _stop5.append("llama")
_m5.DSH.stop = lambda: _stop5.append("dsh")
_m5.HUB.log = lambda text: _log5.append(text)
_err5 = None
try:
    os._exit = lambda code=0: _exit5.append(code)
    _m5._SHUTTING_DOWN = False
    try:
        _m5._stop_all_and_exit("自检")
        _m5._stop_all_and_exit("自检-重复")
    except Exception as _ex5:
        _err5 = "%s: %s" % (type(_ex5).__name__, _ex5)
finally:
    os._exit, _m5.LLAMA.stop, _m5.DSH.stop = _real5
    try:
        del _m5.HUB.log                     # 去掉实例属性, 恢复类方法(别把日志吞进本进程)
    except Exception:
        pass
check("收尾函数真调一次能走到底(旧代码: 闩是 bool, 每次 /api/shutdown 都抛 TypeError)",
      _err5 is None and _exit5 == [0], str((_err5, _exit5)))
check("收尾真的停了 llama.cpp 与 DSH 并留了日志(不是只 os._exit 走人)",
      _stop5 == ["llama", "dsh"] and any("服务进程退出" in t for t in _log5),
      str((_stop5, _log5[-2:])))
check("重复收尾只退出一次(两个收尾线程并发就是互相掐断 kill, 恰好又造孤儿)",
      _exit5 == [0] and _stop5 == ["llama", "dsh"], str((_exit5, _stop5)))


# 13.1b 去重不能把"代表一次请求"的收尾行也吞掉: 连点两次"关闭服务并退出", 第二次必须
#       在 run.log 里看得见, 否则现场只能看到一次请求, 会误判成"请求没到服务端"。
_h5b = core.LogHub()
_w5b = []
_realw5b = core.applog.run
core.applog.run = lambda t: _w5b.append(t)
try:
    _h5b.log("[退出] 关闭服务并退出: 停止 llama.cpp 与 DSH…")
    _h5b.log("[退出] 关闭服务并退出: 停止 llama.cpp 与 DSH…")
    _h5b.log("普通的一行")
    _h5b.log("普通的一行")
    _h5b.log("收到 SIGTERM 也要留两次")
    _h5b.log("[收到 SIGTERM] 开始收尾")
    _h5b.log("[收到 SIGTERM] 开始收尾")
finally:
    core.applog.run = _realw5b
check("重复的收尾/信号行必须照样落日志(去重吞掉就等于第二次退出请求零痕迹)",
      len([x for x in _w5b if x.startswith("[退出]")]) == 2
      and len([x for x in _w5b if x.startswith("[收到 SIGTERM]")]) == 2
      and len([x for x in _w5b if x == "普通的一行"]) == 1, str(_w5b))

# 13.2 端到端: 隔离 HOME 起一个真服务实例, 真打一次 /api/shutdown, 它必须真的退出。
# 三处必须中和, 否则测试会去动用户的真环境: takeover_on_boot(可能接管真 DSH)、
# LLAMA.get_port(stop 有"按 1234 找 llama-server"的回退)、webbrowser.open(会弹浏览器)。
_h5 = os.path.join(_tmp, "e2e_home")
os.makedirs(_h5, exist_ok=True)
_free5 = []
for _ in range(2):
    _s5 = _sock5.socket()
    _s5.bind(("127.0.0.1", 0))
    _free5.append(_s5.getsockname()[1])
    _s5.close()
_api5, _dshp5 = _free5
_code5 = ("import sys; sys.argv = ['main.py', '-p', '%d']; import main; "
          "main.DSH_PORT = %d; main.takeover_on_boot = lambda: None; "
          "main.LLAMA.get_port = lambda: 0; main.webbrowser.open = lambda *a, **k: False; "
          "main.main()") % (_api5, _dshp5)
_errf5 = open(os.path.join(_tmp, "e2e_stderr.txt"), "wb")
_proc5 = _sub5.Popen([sys.executable, "-c", _code5], cwd=APP,
                     env=dict(os.environ, HOME=_h5,
                              XDG_CACHE_HOME=os.path.join(_h5, ".cache")),
                     stdout=_errf5, stderr=_errf5)
_ok5 = None
_rc5 = None
_wait5 = -1.0
_port5 = 0
try:
    for _ in range(400):
        if _proc5.poll() is not None:
            break
        for _c5 in range(_api5, _api5 + 6):      # 端口被占时 main() 会往后挪
            try:
                _q5 = _sock5.create_connection(("127.0.0.1", _c5), 0.2)
                _q5.close()
                _port5 = _c5
                break
            except OSError:
                continue
        if _port5:
            break
        time.sleep(0.1)
    _tok5 = ""
    if _port5:
        # 端口先就绪、token 文件后落盘是正常竞态(12s 启动上限 + 一次读 → 偶发拿不到 token,
        # 于是下面四项端到端全 FAIL, 看着像收尾坏了。按条件等, 不等固定秒数。
        for _ in range(100):
            try:
                _tok5 = open(os.path.join(_h5, ".dsh_launcher_token"), encoding="utf-8").read().strip()
            except OSError:
                _tok5 = ""
            if _tok5:
                break
            time.sleep(0.1)
        if _tok5:
            _rq5 = _url5.Request("http://127.0.0.1:%d/api/shutdown" % _port5, data=b"{}",
                                 headers={"X-Auth": _tok5,
                                          "Content-Type": "application/json"})
            try:
                _ok5 = json.loads(_url5.urlopen(_rq5, timeout=5).read().decode("utf-8")).get("ok")
            except Exception as _e5:
                _ok5 = "err:%s" % _e5
    _t5 = time.time()
    try:
        _rc5 = _proc5.wait(timeout=20)
    except Exception:
        _rc5 = None
    _wait5 = time.time() - _t5
    check("端到端: 实例能起来且 POST /api/shutdown 先拿到 {ok:true}(调用方不该只看到断连)",
          bool(_port5) and _ok5 is True, "port=%s ok=%r" % (_port5, _ok5))
    check("端到端: /api/shutdown 之后服务进程真的退出且退出码 0(旧代码抛异常后一直活着)",
          _rc5 == 0, "rc=%r 等待=%.1fs" % (_rc5, _wait5))
    check("收尾要留出响应送达的时间(0.8s 延迟, 不是当场就把连接掐了)",
          _rc5 == 0 and _wait5 >= 0.6, "等位=%.2fs" % _wait5)
    _etxt5 = open(os.path.join(_tmp, "e2e_stderr.txt"), encoding="utf-8",
                  errors="replace").read()
    check("收尾全程 stderr 不得出现 traceback(出现就是收尾路径又断了)",
          "Traceback" not in _etxt5, _etxt5[-300:])
    _rlf5 = os.path.join(_h5, ".dsh_launcher_logs", "run.log")
    _rl5 = open(_rlf5, encoding="utf-8", errors="replace").read() if os.path.isfile(_rlf5) else ""
    check("收尾两行都要落到 run.log(只打到 stdout 时排障看不见发生了什么)",
          "关闭服务并退出" in _rl5 and "服务进程退出" in _rl5, _rl5[-200:])
finally:
    if _proc5.poll() is None:
        _proc5.kill()
        _proc5.wait()
    _errf5.close()

# --- 14) 第六轮: 锁误用、静态/Host 边界、停止谎报、静默吞异常 -----------------
# 这三族都由三份独立审查(锁与 with 用法 / 安全面断言级别 / except 吞点)挖出并已实测复现。

import subprocess as _sub6
import socket as _sock6
import threading as _th6
import http.client as _hc6

# 14.1 llapimon: 在 `with self._lock:` 里调 add_event(同一把非重入 Lock) = 当场自死锁。
#      触发条件是"运行中换模型", 后果是采集线程永久卡住 + 事件流与 UI 全冻, 零异常零日志。
_mon6 = llapimon.LlamaApiMonitor(lambda: 1234)
_mon6._ports = lambda _fast=False: [1234]
_payload6 = [{"props": {"model_path": "/m/a.gguf", "model_alias": "a"}},
             {"props": {"model_path": "/m/b.gguf", "model_alias": "b"}}]
_mon6._get_json = lambda port, path, timeout=2.0: (
    (_payload6.pop(0) if _payload6 else {}) if path == "/props" else {"data": []})


def _fetch6():
    for _ in range(2):
        try:
            _mon6._fetch_props_and_models()   # 第二次模型路径不同 → 走"模型已变化"那一支
        except Exception:
            pass
_t6 = _th6.Thread(target=_fetch6, daemon=True)
_t6.start()
_t6.join(5.0)
_snap6 = []
_t6b = _th6.Thread(target=lambda: _snap6.append(_mon6.events_snapshot()), daemon=True)
_t6b.start()
_t6b.join(3.0)
_msgs6 = [e["msg"] for e in (_snap6[0][0] if _snap6 else [])]
check("换模型时采集线程不得自死锁(锁内调 add_event 就是这条, 卡死后事件流与 UI 全冻)",
      not _t6.is_alive() and not _t6b.is_alive() and len(_snap6) == 1,
      "采集线程活着=%s 快照线程活着=%s" % (_t6.is_alive(), _t6b.is_alive()))
check("对照: 模型路径变化仍要留下那条 warn 事件(修法没把功能一起删掉)",
      any("模型路径已变化" in m for m in _msgs6), str(_msgs6[:4]))
_init6 = [l for l in inspect.getsource(llapimon.LlamaApiMonitor.__init__).splitlines()
          if not l.strip().startswith("#")]
check("不得再留着从未 acquire 的相似名锁(_http_lock 就是这次误用的诱因)",
      not any("_http_lock" in l for l in _init6), "")

# 14.2 child_pids 读不到时必须报告"数不清", 而不是"没有子进程": root 属主的 DSH 树会读到空,
#      旧写法只杀根 PID 就宣称"已停止", 实际 node/pnpm 还占着端口。
_kid6 = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
_tree6 = procman.child_pids(_kid6.pid)
check("同 uid 的进程树要能枚举出来(含自身), 这是停止整树的前提",
      isinstance(_tree6, list) and _kid6.pid in _tree6, str(_tree6))
check("枚举不到时必须返回 None(旧写法返回 [] 会被当成\"确实没有子进程\"从而谎报已停止)",
      procman.child_pids(999999999) is None, repr(procman.child_pids(999999999)))
_kid6.terminate()
_kid6.wait()


class _P6:
    def __init__(self, pid):
        self.pid = pid
        self.returncode = None

    def poll(self):
        return None


_live6 = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
_realcp6, _realcl6 = core.procman.child_pids, core.procman.alive
core.procman.child_pids = lambda pid: None          # 冒充 root 属主: /proc children 读不到
_lr6 = core.LlamaRunner(_Silent(), {"defs": []})
_lr6.proc = _P6(_live6.pid)
try:
    _lr6.stop()
finally:
    core.procman.child_pids, core.procman.alive = _realcp6, _realcl6
check("整树没数清时不得落成 stopped(面板会把 Start 点亮, 再点就是撞端口)",
      _lr6.status == "stop_failed", "%s pid 活着=%s" % (_lr6.status, _live6.poll() is None))
_live6.terminate()
_live6.wait()

# 14.3 静态服务与 Host 边界: 隔离实例上真发包(旧版实测 GET /../main.py 直接吐服务端源码,
#      且 GET 完全不看 Host → DNS rebinding 能同源读走注入的 API token)。
_h6 = os.path.join(_tmp, "sec_home")
_web6 = os.path.join(_tmp, "sec_webui")
os.makedirs(_h6, exist_ok=True)
shutil.copytree(os.path.join(APP, "webui"), _web6)
os.symlink("../secret_source.py", os.path.join(_web6, "sneaky"))
with open(os.path.join(_tmp, "secret_source.py"), "w", encoding="utf-8") as _fh6:
    _fh6.write("DEF_MARKER def api_shutdown():\n")
_s6 = _sock6.socket()
_s6.bind(("127.0.0.1", 0))
_p6 = _s6.getsockname()[1]
_s6.close()
_s6b = _sock6.socket()
_s6b.bind(("127.0.0.1", 0))
_dp6 = _s6b.getsockname()[1]
_s6b.close()
_code6 = ("import sys; sys.argv = ['main.py', '-p', '%d']; import main; "
          "main.DSH_PORT = %d; main.WEBUI_DIR = %r; main.takeover_on_boot = lambda: None; "
          "main.LLAMA.get_port = lambda: 0; main.webbrowser.open = lambda *a, **k: False; "
          "main.main()") % (_p6, _dp6, _web6)
_log6 = open(os.path.join(_tmp, "sec_stderr.txt"), "wb")
_pr6 = _sub6.Popen([sys.executable, "-c", _code6], cwd=APP,
                   env=dict(os.environ, HOME=_h6, XDG_CACHE_HOME=os.path.join(_h6, ".cache")),
                   stdout=_log6, stderr=_log6)


def _req6(method, path, host=None, tok=None, body=None):
    _c = _hc6.HTTPConnection("127.0.0.1", _p6, timeout=5)
    _h = {}
    if host is None:
        _c.putrequest(method, path)
    else:
        _c.putrequest(method, path, skip_host=True)
        _h["Host"] = host
    if tok:
        _h["X-Auth"] = tok
    _d = json.dumps(body) if body is not None else ""
    _h["Content-Length"] = str(len(_d))
    for _k, _v in _h.items():
        _c.putheader(_k, _v)
    _c.endheaders(_d.encode() if _d else None)
    _r = _c.getresponse()
    _t = _r.read().decode("utf-8", "replace")
    _c.close()
    return _r.status, _t


_up6 = False
try:
    for _ in range(120):
        if _pr6.poll() is not None:
            break
        try:
            _q6 = _sock6.create_connection(("127.0.0.1", _p6), 0.2)
            _q6.close()
            _up6 = True
            break
        except OSError:
            time.sleep(0.1)
    _st1, _bd1 = _req6("GET", "/", host="evil.example")
    check("GET 也要过 Host 白名单(否则 DNS rebinding 页面能同源读走注入的 API token)",
          _st1 == 403 and "__DSH_TOKEN" not in _bd1, str(_st1))
    _st2, _bd2 = _req6("GET", "/", host="127.0.0.1:%d" % _p6)
    _st2b, _ = _req6("GET", "/", host="[::1]:%d" % _p6)
    check("本机访问照常放行, 且 IPv6 字面量 [::1]:端口不被误拒",
          _st2 == 200 and "__DSH_TOKEN" in _bd2 and _st2b == 200, str((_st2, _st2b)))
    _trav = {}
    for _pp in ("/../main.py", "/x/../../main.py", "//../main.py", "/./../main.py",
                "/..%2fmain.py", "/%2e%2e/main.py", "/../../etc/passwd", "/sneaky"):
        _trav[_pp] = _req6("GET", _pp)
    _bad = {k: (v[0], "DEF_MARKER" in v[1] or "root:" in v[1]) for k, v in _trav.items()
            if v[0] != 404 or "DEF_MARKER" in v[1] or "root:" in v[1]}
    check("静态服务不得越出 webui(../ 变体、编码变体、树外符号链接一律 404 且不吐内容)",
          _up6 and not _bad, str(_bad))
    _st3, _bd3 = _req6("GET", "/app.js")
    check("对照: webui 内的正常资产照样能取(边界没把前端锁死)",
          _st3 == 200 and "stop_failed" in _bd3, str(_st3))
    _tokfile6 = os.path.join(_h6, ".dsh_launcher_token")
    _tok6 = open(_tokfile6, encoding="utf-8").read().strip() if os.path.isfile(_tokfile6) else ""
    _mode6 = os.stat(_tokfile6).st_mode & 0o777 if _tok6 else 0
    _inj6 = ""
    _mm6 = _re.search(r'window\.__DSH_TOKEN=("(?:[^"\\]|\\.)*")', _bd2)
    if _mm6:
        _inj6 = json.loads(_mm6.group(1))
    check("token 文件必须 0600 且与注入页面的那一份一致(umask 一变就退化成同机可读凭证)",
          len(_tok6) >= 16 and _mode6 == 0o600 and _inj6 == _tok6, "mode=%o" % _mode6)
    _st4 = _req6("POST", "/api/browse", body={"path": "/tmp"})[0]
    _st5 = _req6("POST", "/api/browse", tok="deadbeef", body={"path": "/tmp"})[0]
    _st6 = _req6("POST", "/api/browse", host="evil.example", tok=_tok6, body={"path": "/tmp"})[0]
    _st7 = _req6("POST", "/api/no_such_route", tok=_tok6, body={})[0]
    check("POST 无 token / 错 token 一律 401(鉴权在路由查表之前, 不存在的路由也不透露)",
          _st4 == 401 and _st5 == 401 and _st7 == 404, str((_st4, _st5, _st7)))
    check("带对 token 但 Host 不合法要 403: Host 与 token 两道各自独立生效",
          _st6 == 403, str(_st6))
    # 字段类型错(自己的前端不会发, 但手改/旧客户端会): 旧版会 prof.get(...) →
    # AttributeError → 500, 并把整段 traceback 写进 error.log, 排障时看着像服务坏了。
    _st8, _bd8 = _req6("POST", "/api/dsh/plugins/audit", tok=_tok6, body={"profile": "web"})
    _st9, _bd9 = _req6("POST", "/api/update/check", tok=_tok6, body={"target": "dsh", "dir": {"x": 1}})
    _st10, _bd10 = _req6("POST", "/api/dsh/plugins/audit", tok=_tok6, body=[1, 2])
    check("字段类型错要 400 并说清是哪个字段发成了什么(不是 500)",
          _st8 == 400 and "profile 必须是对象" in _bd8
          and _st9 == 400 and "dir 必须是字符串" in _bd9
          and _st10 == 400 and "JSON 对象" in _bd10, str((_st8, _bd8[:70], _st9, _bd9[:70])))
    _err6f = os.path.join(_h6, ".dsh_launcher_logs", "error.log")
    _err6t = io.open(_err6f, encoding="utf-8", errors="replace").read() if os.path.isfile(_err6f) else ""
    check("形状错的请求不得在服务错误日志里留下 traceback(真事故的密度不能被稀释)",
          "Traceback" not in _err6t and "AttributeError" not in _err6t, _err6t[-160:])
finally:
    if _pr6.poll() is None:
        _pr6.kill()
        _pr6.wait()
    _log6.close()

# 14.4 /tmp 下固定名的可执行脚本与日志: 共享目录里固定名 + 0755 + bash 执行, 符号链接劫持
#      面(open("w") 跟随链接 → 把构建脚本写进任意本用户可写的文件)。
_ns6 = os.path.join(_tmp, "tmpns")
os.makedirs(_ns6, exist_ok=True)
_oldenv6 = os.environ.get("TMPDIR")
os.environ["TMPDIR"] = _ns6
try:
    _d6 = core._private_tmp_ns()
    _mode_ns = os.stat(_d6).st_mode & 0o777
    _link6 = os.path.join(_ns6, "llamadsh-%d" % os.getuid())
    _canary6 = os.path.join(_ns6, "canary_dir")
    os.makedirs(_canary6, exist_ok=True)
    shutil.rmtree(_d6, ignore_errors=True)      # 撤掉真目录, 换成指向 canary 的符号链接
    os.symlink(_canary6, _link6)
    _d6b = core._private_tmp_ns()
    check("boot/rebuild 日志与脚本要落在按 uid 命名、0700 的私有命名空间里",
          _d6.startswith(_ns6) and _mode_ns == 0o700, "%s mode=%o" % (_d6, _mode_ns))
    check("命名空间被预置成符号链接时必须改用不可预测名, 且不得跟随链接写进目标目录",
          _d6b != _link6 and os.path.realpath(_d6b) != os.path.realpath(_canary6)
          and os.listdir(_canary6) == [], "%s vs %s" % (_d6b, _canary6))
    check("三个路径都必须落在命名空间内, 不再是 /tmp 根下的固定名(旧版 = /tmp/LlamaDSH_dsh_rebuild.sh)",
          all(os.path.dirname(p) == core._TMP_NS
              for p in (core.DSH_BOOT_LOG, core.DSH_REBUILD_LOG, core.DSH_REBUILD_SCRIPT))
          and core._TMP_NS not in ("/tmp", "/"),
          str([core._TMP_NS, core.DSH_REBUILD_SCRIPT]))
finally:
    if _oldenv6 is None:
        os.environ.pop("TMPDIR", None)
    else:
        os.environ["TMPDIR"] = _oldenv6

# 14.5 日志地基写不进去时必须留痕: HOME 只读/盘满/错位会让四个日志文件整体静默归零,
#      于是"到底有没有异常"都无从判断(本轮排障就被这一点坑过一次)。
_oldlog6 = core.applog.LOG_DIR
core.applog.LOG_DIR = os.path.join(_tmp, "a_file_not_a_dir")
with open(os.path.join(_tmp, "a_file_not_a_dir"), "w", encoding="utf-8") as _fh6b:
    _fh6b.write("x")
core.applog._write_warned = False
_errbuf6 = io.StringIO()
_olderr6 = sys.stderr
try:
    sys.stderr = _errbuf6
    core.applog.run("第一条")
    core.applog.run("第二条")
finally:
    sys.stderr = _olderr6
    core.applog.LOG_DIR = _oldlog6
    core.applog._write_warned = False
_etr6 = _errbuf6.getvalue()
check("日志写入失败必须往 stderr 说一次(只说一次), 而不是整族静默归零",
      "日志写入失败" in _etr6 and _etr6.count("日志写入失败") == 1, _etr6[:160])

# 14.6 profile 锁: 放锁与记账必须分开。旧写法在登记缺失时直接 return, 把 release 一起跳过。
_held6 = []


def _hold6():
    _held6.append(_compat.begin_profile_op("lockprobe", "持锁者"))
    time.sleep(1.5)                     # 真持有一段时间, 让主线程的"野 end"撞上别人的锁
    _compat.end_profile_op("lockprobe")


_th6c = _th6.Thread(target=_hold6)
_th6c.start()
time.sleep(0.3)
_imb_before6 = len(_compat.op_imbalance())
_end_ret6 = None
try:
    _end_ret6 = _compat.end_profile_op("lockprobe")      # 主线程不是持锁者: 不得崩, 也不得放别人的锁
except Exception as _ex6:
    _end_ret6 = "raised:%s" % _ex6
_after6 = _compat.begin_profile_op("lockprobe", "外人")
_ops6 = _compat.inflight_ops()
_th6c.join(3.0)
_ok_again6 = _compat.begin_profile_op("lockprobe", "收尾检查")
_compat.end_profile_op("lockprobe")
check("end 在无对应 begin 时不得崩, 也不能把别人持有的锁放掉(外人仍进不去)",
      _end_ret6 is None and len(_compat.op_imbalance()) > _imb_before6 and _after6 is False,
      str((_end_ret6, _after6)))
check("锁不能被一次失衡的 end 永久泄漏(否则之后所有插件操作都回\"正在进行的插件操作\")",
      _ok_again6 is True and _compat.inflight_ops() == [],
      str((_after6, _ops6, _ok_again6)))

# 14.7 5s 健康对账本身出错要能看见: 它是"DSH 崩了/llama 短命退出"唯一的状态回落点。
import main                                    # 第 13 节只 import 成 _m5, 这里要模块名


class _StopLoop6(Exception):
    pass


_calls6 = []
_real_api_health6 = main.api_health
_real_time6 = main.time
_real_err_ex6 = main.HUB.error_ex
main.api_health = lambda _b: (_ for _ in ()).throw(RuntimeError("对账内部炸了"))
main.HUB.error_ex = lambda ex, ctx: _calls6.append((type(ex).__name__, ctx))


def _sleep6(_s):
    if len(_calls6) >= 1:
        raise _StopLoop6()
    time.sleep(0.05)


# 只替换 main 模块看到的 time, 不动全局 time 模块(否则会波及本进程其它线程)
main.time = types.SimpleNamespace(sleep=_sleep6, time=time.time)
_loop6 = _th6.Thread(target=main._health_loop, daemon=True)
_errbuf6b = io.StringIO()
_olderr6b = sys.stderr
try:
    sys.stderr = _errbuf6b
    _loop6.start()
    _loop6.join(5.0)
finally:
    sys.stderr = _olderr6b
    main.api_health, main.time = _real_api_health6, _real_time6
    main.HUB.error_ex = _real_err_ex6
    if _loop6.is_alive():                   # 兜底: 别让这个循环带着真 api_health 继续跑
        main.api_health = lambda _b: None
check("健康对账抛异常要进 error.log(旧写法吞掉 → 面板永远显示运行中且 guard 永久拒绝插件操作)",
      len(_calls6) >= 1 and "健康对账" in _calls6[0][1], str(_calls6))
check("信号处理器注册失败也必须留痕(挂不上等于整条收尾链不存在, 服务被杀就是孤儿)",
      "信号处理器注册失败" in main_src, "")


# --- 15) 第七轮: 假绿与假报告(读不到却报"一切正常"、未核就称"已回滚") ---------
# 15.1 兼容审计: cordis.patch.yml 读不动时以前返回空清单, 于是打印"0 条补丁"并在结尾
#      给出"0 错误 / 0 可修复项" —— 坏文件被报成一切正常, patch_orphan/rename 整段跳过。
_dsh7 = os.path.join(_tmp, "dsh_tree7")
os.makedirs(os.path.join(_dsh7, ".git"), exist_ok=True)
os.makedirs(os.path.join(_dsh7, "apps", "cli", "lib"), exist_ok=True)
open(os.path.join(_dsh7, "apps", "cli", "lib", "bin.js"), "w").write("//x\n")
_rep7 = _compat.audit(_dsh7, lambda _t: None, include_dump=False)
_lv7 = [x["level"] for x in _rep7["items"]]
with open(os.path.join(_prof, "cordis.patch.yml"), "w", encoding="utf-8") as _fh7:
    _fh7.write("patch:\n  - id: a\n   bad_indent: [unclosed\n")
_rep7b = _compat.audit(_dsh7, lambda _t: None, include_dump=False)
_txt7b = "\n".join(x["text"] for x in _rep7b["items"])
_lv7b = [x["level"] for x in _rep7b["items"]]
with open(os.path.join(_prof, "cordis.patch.yml"), "w", encoding="utf-8") as _fh7:
    _fh7.write("patch:\n  - id: aaa\n  - id: bbb\n")
_rep7c = _compat.audit(_dsh7, lambda _t: None, include_dump=False)
_txt7c = "\n".join(x["text"] for x in _rep7c["items"])
_lv7c = [x["level"] for x in _rep7c["items"]]
os.remove(os.path.join(_prof, "cordis.patch.yml"))
check("审计: patch.yml 读不动必须报 error(旧版报\"0 条补丁\"并给出\"0 错误\", 是假绿)",
      "error" in _lv7b and "读不动" in _txt7b and "0 错误 / 0 可修复项" not in _txt7b,
      _txt7b[:200])
check("审计: 有项未能核对时结尾不能是\"0 错误 / 0 可修复项\", 要写明几项未能核对",
      any("未能核对" in x["text"] for x in _rep7b["items"]), _txt7b[-160:])
check("对照: patch.yml 正常时照旧报条数且不产生 error(修法没把正常路径变红)",
      "error" not in _lv7c and "cordis.patch.yml: 2 条补丁" in _txt7c
      and "复检结果:0 错误 / 0 可修复项" in _txt7c, _txt7c[:260])

# 15.2 repair 入口的重审失败要说出来: 否则修的是弹窗时的陈旧快照, 日志上却只剩"已修复 N 项"
_stale_lock7 = os.path.join(_tmp, "dsh_tree7", "ledger-v2.lock")
open(_stale_lock7, "w").close()
_real_audit7 = _compat.audit
_logs7 = []


def _audit_boom7(*a, **k):
    raise RuntimeError("审计自己炸了")


_act7, _pc7 = procman.dsh_activity, dict(procman._probe_cache)
procman.dsh_activity = lambda _p: False      # 修复现场必须是"已确认 DSH 没在跑"(见 18.3)
_compat.audit = _audit_boom7
_fixed7 = _compat.repair(_dsh7, {"fixables": [{"kind": "stale_lock", "path": _stale_lock7}]},
                         lambda t: _logs7.append(t))
_compat.audit = _real_audit7
procman.dsh_activity, procman._probe_cache = _act7, _pc7
check("修复前重审失败必须留痕并说明本次按旧清单修(否则修的是陈旧快照还报已修复)",
      _fixed7 == 1 and not os.path.exists(_stale_lock7)
      and any("重审失败" in t for t in _logs7), str(_logs7[:3]))

# 15.3 回滚文案要反映"写回是否真成功": 以前只看备份存不存在, copy2 失败也说"已回滚"
_real_copy2 = _compat.shutil.copy2
_real_run7 = _compat.subprocess.run


def _copy_boom7(src, dst, *a, **k):
    if str(dst) == os.path.join(_prof, "package.json"):
        raise OSError("只读文件系统")
    return _real_copy2(src, dst, *a, **k)


_compat.shutil.copy2 = _copy_boom7
_compat.subprocess.run = _boom_run
ok_rb7, msg_rb7 = _compat.install_local_plugin("web", _tgz)
_compat.subprocess.run = _real_run7
_compat.shutil.copy2 = _real_copy2
check("备份存在但写回失败时不得说\"已回滚\", 要说明可能停在半安装状态",
      (not ok_rb7) and "写回失败" in msg_rb7 and "已回滚 package.json/lock" not in msg_rb7,
      msg_rb7[:180])

# 15.4 状态写盘失败要有出口: LlamaPid/DshPid 落不了盘 → 重启后接管不到进程 → 面板假
#      "未运行", 用户点启动就变"端口已被占用/启动失败"。
_real_state = core.state.STATE_FILE
_msgs7 = []
core.state.set_error_sink(lambda t: _msgs7.append(t))
core.state.STATE_FILE = os.path.join(_tmp, "a_file7", "state.json")
with open(os.path.join(_tmp, "a_file7"), "w", encoding="utf-8") as _fh7b:
    _fh7b.write("x")          # 同名是个文件 → 写 state.json 必失败
core.state.update_state(LlamaPid=12345)
core.state.save_state({"DshPid": 1})
core.state.STATE_FILE = _real_state
core.state.set_error_sink(None)
check("状态写盘失败必须能看见(以前整族静默吞, 真因只能靠猜)",
      len(_msgs7) == 2 and "state.json" in _msgs7[0] and "接管" in _msgs7[0], str(_msgs7[:2]))

# 15.5 端口探针不可用时不得当成"端口空闲": find_process_by_port 只会返回 None, 那是查不到。
_real_which7, _real_pcache7 = procman.which, dict(procman._probe_cache)
_real_nh7, _real_fp7 = main.procman.node_host_running, main.procman.find_process_by_port
main.procman.node_host_running = lambda: False
main.procman.find_process_by_port = lambda *a, **k: None
procman._probe_cache = {"at": 0.0, "ok": False}
procman.which = lambda name: None              # 冒充 ss 与 lsof 都没有
procman._probe_cache = {"at": 0.0, "ok": False}
_guard_nossp = main._dsh_running_guard()
procman.which = _real_which7
procman._probe_cache = {"at": 0.0, "ok": False}
_guard_ok = main._dsh_running_guard()
main.procman.node_host_running, main.procman.find_process_by_port = _real_nh7, _real_fp7
procman._probe_cache = _real_pcache7
check("探针(ss/lsof)缺失时插件操作要被拒, 不能把\"查不到\"当成\"没在跑\"就写 node_modules",
      _guard_nossp is False and _guard_ok is True, str((_guard_nossp, _guard_ok)))


# --- 16) 第八轮: 打包/解压的边界(符号链接外带、自制 tgz 冒充身份) --------------
# 16.1 shutil.copytree 默认**跟随符号链接**: 插件里一个 dist/assets -> ../../secrets
#      就把包外的内容原样打进 tgz。这个包随后被装进别的 profile、或发给别人, 就是
#      把别人的文件外带(实测旧形态: 包外 secret 与整棵包外目录都进了暂存目录)。
_pl8 = os.path.join(_tmp, "pack_link8")
_outside8 = os.path.join(_tmp, "outside8")
os.makedirs(os.path.join(_pl8, "dist"))
os.makedirs(_outside8)
with open(os.path.join(_outside8, "secret.json"), "w", encoding="utf-8") as fh:
    fh.write("SECRET-OUT-OF-TREE\n")
with open(os.path.join(_pl8, "dist", "real.js"), "w", encoding="utf-8") as fh:
    fh.write("//real\n")
os.symlink(os.path.join(_outside8, "secret.json"), os.path.join(_pl8, "dist", "link.json"))
os.symlink(_outside8, os.path.join(_pl8, "outlink"))
os.symlink(os.path.join(_pl8, "dist", "real.js"), os.path.join(_pl8, "dist", "inlink.js"))
with open(os.path.join(_pl8, "index.js"), "w", encoding="utf-8") as fh:
    fh.write("//main\n")
with open(os.path.join(_pl8, "package.json"), "w", encoding="utf-8") as fh:
    json.dump({"name": "lk", "version": "1.0.0", "main": "index.js",
               "files": ["dist", "outlink"]}, fh)
_lklog8 = []
_compat.subprocess.run = _fake_pack
_compat._pack_staged(_pl8, _pout, job_log=_lklog8.append)
_compat.subprocess.run = orig_compat_run
_files8 = list(_pstage["files"])
check("打包不得把包外符号链接的内容打进 tgz(copytree 跟随链接 = 把别人的文件外带)",
      "dist/link.json" not in _files8 and not any("outlink" in f or "secret" in f
                                                 for f in _files8), str(_files8))
check("包内符号链接照常进包, 被甩掉的包外链接必须在日志里点名",
      "dist/inlink.js" in _files8 and "dist/real.js" in _files8
      and any("符号链接" in str(x) and "link.json" in str(x) for x in _lklog8),
      "%s | %s" % (_files8, _lklog8[-2:]))

# 16.2 只认包根的 package.json 这条, 把 `../package.json` 也算了一层包根(rel=".." 的
#      depth 恰好是 1): 自制 tgz 于是能把安装记录里的 name/version 写成任意值(实测旧版
#      返回 ('evil-root','9.9')), 卸载/清理就会瞄错条目。绝对路径成员同样不该参与。
_evil8 = os.path.join(_tmp, "evil_ident8.tgz")
with tarfile.open(_evil8, "w:gz") as tf:
    for _n8, _d8 in (("../package.json", {"name": "evil-root", "version": "9.9"}),
                     ("/abs/package.json", {"name": "abs-root", "version": "8.8"}),
                     ("package/package.json", {"name": "@real/pkg", "version": "1.2.3"})):
        _raw8 = json.dumps(_d8).encode("utf-8")
        _ti8 = tarfile.TarInfo(_n8)
        _ti8.size = len(_raw8)
        tf.addfile(_ti8, io.BytesIO(_raw8))
check("自制 tgz 里 ../ 或绝对路径的 package.json 不得冒充包身份",
      _compat._tarball_identity(_evil8) == ("@real/pkg", "1.2.3"),
      str(_compat._tarball_identity(_evil8)))
_evil_only8 = os.path.join(_tmp, "evil_only8.tgz")
with tarfile.open(_evil_only8, "w:gz") as tf:
    _raw8 = json.dumps({"name": "evil-root", "version": "9.9"}).encode("utf-8")
    _ti8 = tarfile.TarInfo("../package.json")
    _ti8.size = len(_raw8)
    tf.addfile(_ti8, io.BytesIO(_raw8))
check("整包只有越界 package.json 时身份必须为空(不能照抄越界成员)",
      _compat._tarball_identity(_evil_only8) == ("", ""),
      str(_compat._tarball_identity(_evil_only8)))

# 16.3 解压侧 filter="data" 是恶意资产包的唯一防线(绝对路径/../穿越/包外符号链接),
#      但它零回归覆盖: 谁哪天"顺手"去掉这个参数, 更新流程就会把文件写到到处。
def _malice_tgz8(tag, name, typefield=tarfile.REGTYPE, link=""):
    p = os.path.join(_tmp, "malice_%s8.tgz" % tag)
    with tarfile.open(p, "w:gz") as tf:
        ti = tarfile.TarInfo(name)
        ti.type = typefield
        ti.linkname = link
        if typefield == tarfile.REGTYPE:
            ti.size = 2
            tf.addfile(ti, io.BytesIO(b"hi"))
        else:
            ti.size = 0
            tf.addfile(ti)
    return p


for _tag8, _name8, _type8, _link8, _out8, _raise8, _in8, _badlink8, _note8 in (
        ("dotdot", "../escaped8.txt", tarfile.REGTYPE, "", os.path.join(_tmp, "escaped8.txt"),
         True, None, None, "必须报错"),
        ("abs", "/tmp/llamadsh_abs_member8.txt", tarfile.REGTYPE, "",
         "/tmp/llamadsh_abs_member8.txt", False,
         os.path.join("tmp", "llamadsh_abs_member8.txt"), None, "不得写到解压目录外"),
        ("link", "evil_link", tarfile.SYMTYPE, "/tmp/evil_link_target8",
         "/tmp/evil_link_target8", True, None, "evil_link", "必须报错")):
    _ex8 = os.path.join(_tmp, "ex8_%s" % _tag8)
    os.makedirs(_ex8)
    _why8 = ""
    try:
        toolchain._extract_tar(_malice_tgz8(_tag8, _name8, _type8, _link8), _ex8)
    except Exception as _ex8e:
        _why8 = "%s: %s" % (type(_ex8e).__name__, _ex8e)
    _badlink8 = _badlink8 is not None and os.path.islink(os.path.join(_ex8, _badlink8))
    # 绝对路径成员由 tarfile 先把 "/" 剥掉(不报错, 但被折进解压目录内);
    # .. 穿越与"指向解压目录外"的符号链接成员由 filter="data" 直接拒 —— 后者尤其要紧:
    # 没有 filter 时它会照原样在解压目录里建一个链到 /etc/passwd 之类的链接, 之后任何
    # 一次复制/覆盖(更新流程就有 copy2 + chmod)都会写到链接的目标上。
    check("恶意 tar(%s 成员)解压%s, 且不得在解压目录外落文件或留下越界链接" % (_tag8, _note8),
          (bool(_why8) == _raise8) and not os.path.exists(_out8) and not _badlink8
          and (_in8 is None or os.path.isfile(os.path.join(_ex8, _in8))),
          "%s | out_exists=%s bad_link=%s in_dest=%s" % (
              _why8[:110], os.path.exists(_out8), _badlink8,
              _in8 is not None and os.path.isfile(os.path.join(_ex8, _in8))))
_ok8 = os.path.join(_tmp, "good8.tgz")
with tarfile.open(_ok8, "w:gz") as tf:
    ti = tarfile.TarInfo("llama.cpp-build/llama-server")
    ti.size = 4
    ti.mode = 0o644
    tf.addfile(ti, io.BytesIO(b"#!/s"))
    ti2 = tarfile.TarInfo("llama.cpp-build/README.md")
    ti2.size = 2
    tf.addfile(ti2, io.BytesIO(b"hi"))
_exok8 = os.path.join(_tmp, "exok8")
os.makedirs(_exok8)
toolchain._extract_tar(_ok8, _exok8)
_found8 = toolchain._find_dir_containing(_exok8, "llama-server")
check("对照: 正常 tar 照旧能解出并给 llama-* 补上可执行位(修法没把正常路径锁死)",
      _found8 is not None
      and os.access(os.path.join(_found8, "llama-server"), os.X_OK),
      str(_found8))



# --- 18) 第九轮: "查不到"不得当成"没在跑" —— 删锁一族 ----------------------------
# 18.1 现场取证: 3080 上有 DSH 在跑、ledger-v2.lock 存在, 但这台机器上 ss 与 lsof 都
#      不可用(精简系统 / PATH 被改窄)。旧形态里 find_process_by_port 只能返回 None,
#      node_host_running 只能返回 False, 于是 stale_lock_present()=True, 而
#      _prepare_then_start 拿到 True 就 os.remove 那把锁, 再起第二个实例去抢同一本账
#      (实测旧形态确为 True; 两本账同时写就是损坏, 且面板上看起来"一切正常")。
class _DshProbe9(core.DshRunner):
    def __init__(self):            # 只为拿到方法, 不接 HUB/SCHEMA
        pass


_d9 = _DshProbe9()
_old_home_env9 = os.environ.get("DSH_HOME")
os.environ["DSH_HOME"] = _home     # 自定义 DSH 根: core 与 compat 必须看同一个锁
_lp9 = os.path.join(_home, "task-board", "ledger-v2.lock")
os.makedirs(os.path.dirname(_lp9), exist_ok=True)
_fp9, _nh9, _pc9 = procman.find_process_by_port, procman.node_host_running, dict(procman._probe_cache)

open(_lp9, "w").close()
procman.find_process_by_port = lambda *a, **k: None
procman.node_host_running = lambda: False
procman._probe_cache = {"at": time.time(), "ok": True}
_st_a9 = _d9.stale_lock_present()                     # 确认没在跑 → 是残留锁
procman._probe_cache = {"at": time.time(), "ok": False}
_st_b9 = _d9.stale_lock_present()                     # 探针不可用 → 不得说它是残留
procman._probe_cache = {"at": time.time(), "ok": True}
procman.find_process_by_port = lambda *a, **k: 4242
_st_c9 = _d9.stale_lock_present()                     # 端口上有人 → 不是残留
procman.find_process_by_port, procman.node_host_running = _fp9, _nh9
procman._probe_cache = _pc9
check("残留锁必须只在\"确认 DSH 没在跑\"时为 True(探针不可用或端口有人都必须 False)",
      _st_a9 is True and _st_b9 is False and _st_c9 is False, str((_st_a9, _st_b9, _st_c9)))
check("core 与 compat 必须看同一把锁(DSH_HOME 自定义时旧版 core 写死 ~/.dsh, 两边互相矛盾)",
      _d9.lock_path() == os.path.join(_compat.dsh_home(), "task-board", "ledger-v2.lock") == _lp9,
      "%s | %s" % (_d9.lock_path(), _compat.dsh_home()))

# 18.2 审计侧: 探针不可用时不得给出"可修复:删除"的建议(一键就会删掉活实例的账本锁),
#      但也不能默默消失 —— 要说"无法判定", 用户才知道要自己确认。
_dsh9 = os.path.join(_tmp, "dsh_tree9")
os.makedirs(os.path.join(_dsh9, ".git"), exist_ok=True)
os.makedirs(os.path.join(_dsh9, "apps", "cli", "lib"), exist_ok=True)
open(os.path.join(_dsh9, "apps", "cli", "lib", "bin.js"), "w").write("//x\n")
open(_lp9, "w").close()
procman.find_process_by_port = lambda *a, **k: None
procman.node_host_running = lambda: False
procman._probe_cache = {"at": time.time(), "ok": True}
_r9a = _compat.audit(_dsh9, lambda _t: None, include_dump=False)
procman._probe_cache = {"at": time.time(), "ok": False}
_r9b = _compat.audit(_dsh9, lambda _t: None, include_dump=False)
procman._probe_cache = _pc9
_txt9b = "\n".join(x["text"] for x in _r9b["items"])
check("审计: 探针不可用时锁不得报成\"可修复:删除\", 要说明无法判定并让人工确认",
      not any(f["kind"] == "stale_lock" for f in _r9b["fixables"])
      and "无法判定" in _txt9b, _txt9b[:220])
check("对照: 确认没在跑时照旧给出可删除的残留锁项(修法没把正常路径锁死)",
      any(f["kind"] == "stale_lock" for f in _r9a["fixables"])
      and "可修复:删除" in "\n".join(x["text"] for x in _r9a["items"]),
      "\n".join(x["text"] for x in _r9a["items"])[:200])

# 18.3 修复侧: 弹窗到点修复之间 DSH 可能已经被启动, 探针也可能失效, 所以删前再确认一次。
_lock9 = os.path.join(_tmp, "live_lock9")
open(_lock9, "w").close()
_log9 = []
_real_audit9, _act9 = _compat.audit, procman.dsh_activity
_compat.audit = lambda *a, **k: {"items": [], "version": "",
                                 "fixables": [{"kind": "stale_lock", "path": _lock9}]}
for _state9 in (True, None, False):
    procman.dsh_activity = lambda _p, _s=_state9: _s
    _n9 = _compat.repair(_dsh9, {"fixables": [{"kind": "stale_lock", "path": _lock9}]},
                         lambda t: _log9.append(t))
    _log9.append("count=%d|锁仍在=%s" % (_n9, os.path.exists(_lock9)))
_compat.audit, procman.dsh_activity = _real_audit9, _act9
check("修复不得删活实例的锁, 探针不可用时同样拒删, 并把原因写进日志",
      _log9.count("count=0|锁仍在=True") == 2
      and any("仍在运行" in t for t in _log9) and any("无法确认" in t for t in _log9),
      str(_log9[:6]))
check("对照: 确认 DSH 已停止时残留锁照旧删除并计数(不是把所有删除都堵死)",
      _log9.count("count=1|锁仍在=False") == 1 and not os.path.exists(_lock9),
      str(_log9[-3:]))
if _old_home_env9 is None:
    os.environ.pop("DSH_HOME", None)
else:
    os.environ["DSH_HOME"] = _old_home_env9


# --- 19) 第十轮: 插件扫描里的静默跳过(package.json 读不动 ⇒ 插件"不存在") -------
# 19.1 面板的 🧩 按钮走 audit_plugins: 一个被截断/带 BOM/权限不对的 package.json 以前
#      直接 continue ⇒ 该插件从列表里**消失**, 用户只会看到 DSH 启动时它加载失败。
_p19 = _prof
_dsh19 = _dsh9
os.makedirs(os.path.join(_p19, "node_modules", "good-plugin"), exist_ok=True)
os.makedirs(os.path.join(_p19, "node_modules", "broken-plugin"), exist_ok=True)
with open(os.path.join(_p19, "package.json"), "w", encoding="utf-8") as fh:
    json.dump({"name": "profile-web", "version": "1.0.0",
               "dependencies": {"good-plugin": "^1.0.0", "broken-plugin": "^1.0.0"}}, fh)
with open(os.path.join(_p19, "node_modules", "good-plugin", "package.json"), "w",
          encoding="utf-8") as fh:
    json.dump({"name": "good-plugin", "version": "1.0.0"}, fh)
_broken_pj19 = os.path.join(_p19, "node_modules", "broken-plugin", "package.json")
with open(_broken_pj19, "w", encoding="utf-8") as fh:
    fh.write('{"name": "broken-plugin", "version": "1.0.0"')      # 截断(写盘被中断)
_rows19 = {r["name"]: r for r in _compat.audit_plugins("web", _dsh19)}
check("审计必须把 package.json 读不动的已声明插件列成 error 行(旧版整个插件从列表消失)",
      "broken-plugin" in _rows19
      and any("package.json 读不动" in e for e in _rows19.get("broken-plugin", {}).get("errors", [])),
      str(sorted(_rows19)))
check("对照: 正常插件不得被牵连(good-plugin 仍无 error)",
      "good-plugin" in _rows19 and not _rows19["good-plugin"]["errors"],
      str(_rows19.get("good-plugin")))

# 19.2 .dsh-market/state.json 读不动时, 哪些插件被停用整项失去依据: 以前 read_json
#      返回 {}, 与没有被停用的情况同形 ⇒ 被市场停用的插件一律显示成已启用。
os.makedirs(os.path.join(_p19, ".dsh-market"), exist_ok=True)
with open(os.path.join(_p19, ".dsh-market", "state.json"), "w", encoding="utf-8") as fh:
    fh.write("not json at all\n")
_bad19 = {r["name"]: r for r in _compat.audit_plugins("web", _dsh19)}
with open(os.path.join(_p19, ".dsh-market", "state.json"), "w", encoding="utf-8") as fh:
    json.dump({"disabled": ["good-plugin"]}, fh)
_ok19 = {r["name"]: r for r in _compat.audit_plugins("web", _dsh19)}
check("市场停用记录读不动必须单独成行说明(不能当成没有被停用)",
      ".dsh-market/state.json" in _bad19
      and any("未能核对" in e for e in _bad19.get(".dsh-market/state.json", {}).get("errors", [])),
      str(sorted(_bad19)))
check("对照: state.json 正常时停用状态照旧反映, 且不产生未能核对行",
      ".dsh-market/state.json" not in _ok19
      and _ok19.get("good-plugin", {}).get("disabled_by_market") is True, str(sorted(_ok19)))

# 19.3 plugin_check(C# 智能检测的同名实现, 目前未接线但语义必须一致): 计数与列表都要
#      反映没扫成, 而不是把坏插件当成不存在。
_log19 = []
_res_bad19 = _compat.plugin_check(_dsh19, _log19.append)
with open(_broken_pj19, "w", encoding="utf-8") as fh:
    json.dump({"name": "broken-plugin", "version": "1.0.0"}, fh)
_res_ok19 = _compat.plugin_check(_dsh19, _log19.append)
with open(_broken_pj19, "w", encoding="utf-8") as fh:
    fh.write('{"name": "broken-plugin", "version": "1.0.0"')
with open(os.path.join(_p19, ".dsh-market", "state.json"), "w", encoding="utf-8") as fh:
    fh.write("{oops\n")
_res_ms19 = _compat.plugin_check(_dsh19, _log19.append)
with open(os.path.join(_p19, ".dsh-market", "state.json"), "w", encoding="utf-8") as fh:
    json.dump({"disabled": []}, fh)
check("检测: 读不动的插件目录要列出来并从计数里排除(修好后计数应 +1)",
      "broken-plugin" in _res_bad19.get("unreadable", [])
      and _res_ok19["count"] == _res_bad19["count"] + 1
      and any("未能核对" in t for t in _log19),
      "bad=%s ok=%s" % (_res_bad19["count"], _res_ok19["count"]))
check("检测: state.json 读不动必须报出来(market_state_unreadable + 列表里说明)",
      _res_ms19.get("market_state_unreadable") is True
      and any("state.json" in str(x["note"]) for x in _res_ms19["items"]),
      str(_res_ms19["items"][:3]))

# 19.4 本轮自己踩到的坑, 钉成断言: read_json(path, None) 的兜底是
#      `default if default is not None else {}`, 传 None 等于传默认 ⇒ 失败也给 {}。
#      想区分"文件是空的"与"文件读不动"必须用 read_json_or_none, 否则又是假绿。
_trap19 = os.path.join(_tmp, "trap19.json")
with open(_trap19, "w", encoding="utf-8") as fh:
    fh.write("{broken")
_empty19 = os.path.join(_tmp, "empty19.json")
with open(_empty19, "w", encoding="utf-8") as fh:
    fh.write("{}")
check("read_json(p, None) 读不动时仍返回 {}(传 None 不等于要 None), 必须用 read_json_or_none",
      _compat.read_json(_trap19, None) == {}
      and _compat.read_json_or_none(_trap19) is None
      and _compat.read_json_or_none(_empty19) == {}
      and _compat.read_json_or_none(os.path.join(_tmp, "nope19.json")) is None,
      str((_compat.read_json(_trap19, None), _compat.read_json_or_none(_trap19),
          _compat.read_json_or_none(_empty19))))



# --- 20) 第十一轮: 显存预算的三态 / offValue 未计入 / cudart 并入 / pnpm 的 PATH --
# 20.1 cudart 包与主包一样带一层归档根目录(cudart-llama-bNNNN-bin-…/)。以前 copytree 把
#      整棵根目录搬进安装目录 ⇒ .so 与 llama-server 不同目录(rpath=$ORIGIN)而日志写"已并入"。
_croot20 = "cudart-llama-b12114-bin-ubuntu-cuda-13.4-x64"
_cdir20 = os.path.join(_tmp, "cudart_cdir20")
_src20 = os.path.join(_tmp, "llama_dir20")
os.makedirs(os.path.join(_cdir20, _croot20))
os.makedirs(_src20)
with open(os.path.join(_src20, "llama-server"), "w", encoding="utf-8") as fh:
    fh.write("#!/bin/sh\n")
for _nm20 in ("libcublasLt.so.13", "libcublas.so.13"):
    with open(os.path.join(_cdir20, _croot20, _nm20), "w", encoding="utf-8") as fh:
        fh.write("x")
_n20, _note20 = toolchain.merge_cudart_tree(_cdir20, _src20)
check("cudart 并入必须剥掉归档根目录: .so 要与 llama-server 同目录且不留 cudart-* 子目录",
      _n20 == 2
      and all(os.path.isfile(os.path.join(_src20, f))
              for f in ("libcublasLt.so.13", "libcublas.so.13"))
      and not any(d.startswith("cudart-") for d in os.listdir(_src20)),
      str(sorted(os.listdir(_src20))))
check("确有 .so 同目录时才说\"已并入\"(并带上数量供核对)",
      "已并入" in _note20 and "2 个 .so" in _note20, _note20)
_cdir20b = os.path.join(_tmp, "cudart_empty20")
_src20b = os.path.join(_tmp, "llama_dir20b")
os.makedirs(_cdir20b)
os.makedirs(_src20b)
_n20b, _note20b = toolchain.merge_cudart_tree(_cdir20b, _src20b)
check("并入后同目录没有 .so 时必须改成警告, 不得照报已并入",
      _n20b == 0 and "⚠" in _note20b and "已并入" not in _note20b, _note20b)

# 20.2 显存探针读不到时 total_vram=0, 与"0 显存的显卡"同形: 旧版据此报"在可用范围内",
#      用户点启动然后 bad allocation。CPU-only / i915(无 mem_info_vram_*) / nvidia-smi
#      挂掉都会走到这里。C# 侧本就有第三态, Linux 移植时丢了。(整段预算此前 0 断言)
_defs20 = [
    {"flag": "cache-type-k", "type": "Combo", "role": "cache_type_k", "default": "f16"},
    {"flag": "cache-type-v", "type": "Combo", "role": "cache_type_v", "default": "f16"},
    {"flag": "ctx-size", "type": "Combo", "role": "ctx_size", "default": "4096"},
    {"flag": "n-gpu-layers", "type": "Combo", "role": "ngl", "default": ""},
    {"flag": "ubatch-size", "type": "Combo", "role": "ubatch_size", "default": "512"},
    {"flag": "flash-attn", "type": "Combo", "role": "flash_attn", "offValue": "off",
     "default": "on"},
]
_model20 = os.path.join(_tmp, "model20.gguf")
with open(_model20, "w", encoding="utf-8") as fh:
    fh.write("x")
_real_arch20, _real_shards20 = cmdline.read_gguf_arch, cmdline.gguf_shard_bytes
cmdline.read_gguf_arch = lambda _p: {
    "ok": True, "layers": 64, "kv_heads": 8, "head_dim": 128, "head_count": 40,
    "embed": 5120, "context_len": 8192, "architecture": "qwen35", "sliding_window": 0,
    "full_attention_interval": 1, "nextn_predict_layers": 0, "expert_count": 0,
    "ssm_inner_size": 0, "ssm_state_size": 0, "ssm_conv_kernel": 0, "vocab_size": 151936}
cmdline.gguf_shard_bytes = lambda _p: 20 * 1024 ** 3
_prof20 = {"ModelPath": _model20, "NoMmprojOffload": True,
           "ValueParams": {"cache-type-k": {"Enabled": True, "Value": "q8_0"},
                           "cache-type-v": {"Enabled": True, "Value": "q4_0"},
                           "ctx-size": {"Enabled": True, "Value": "32768"},
                           "n-gpu-layers": {"Enabled": True, "Value": "-1"},
                           "ubatch-size": {"Enabled": True, "Value": "512"},
                           "flash-attn": {"Enabled": False, "Value": "on"}}}
_GIB20 = 1024 ** 3
_est_nodata = cmdline.estimate_vram_needed(_prof20, _defs20,
                                           lambda: {"vram_total": 0, "vram_used": 0})
_est_roomy = cmdline.estimate_vram_needed(_prof20, _defs20,
                                          lambda: {"vram_total": 64 * _GIB20, "vram_used": _GIB20})
_est_tight = cmdline.estimate_vram_needed(_prof20, _defs20,
                                          lambda: {"vram_total": 8 * _GIB20, "vram_used": 7 * _GIB20})
check("显存探针无数据时不得断言\"装得下\": fits 必须是 None 且 detail 说未取到",
      _est_nodata is not None and _est_nodata.get("fits") is None
      and _est_nodata.get("vram_known") is False and "未取到" in _est_nodata["detail"],
      str({k: _est_nodata.get(k) for k in ("fits", "vram_known")} if _est_nodata else None))
check("对照: 有显存数据时 fits 仍是布尔(宽裕 True / 紧张 False)",
      isinstance(_est_roomy["fits"], bool) and _est_roomy["fits"] is True
      and _est_tight["fits"] is False, str((_est_roomy["fits"], _est_tight["fits"])))

# 20.3 Flash Attention 取消勾选时命令行是 --flash-attn off(build_cmd 写 offValue), 但预算用
#      raw_value 看到的是 "" ⇒ 仍按 cache-type-v 的量化计价, 而实际 V 缓存回退 f16。
_ps20 = cmdline.ParamState(_prof20, _defs20)
check("raw_value 看不到 offValue 而 eff_value 看得到(与 build_cmd 实际生效值一致)",
      _ps20.raw_value("flash-attn") == "" and _ps20.eff_value("flash-attn") == "off",
      str((_ps20.raw_value("flash-attn"), _ps20.eff_value("flash-attn"))))
_prof20_on = json.loads(json.dumps(_prof20))
_prof20_on["ValueParams"]["flash-attn"] = {"Enabled": True, "Value": "on"}
_est_fa_off = cmdline.estimate_vram_needed(_prof20, _defs20,
                                           lambda: {"vram_total": 64 * _GIB20, "vram_used": 0})
_est_fa_on = cmdline.estimate_vram_needed(_prof20_on, _defs20,
                                          lambda: {"vram_total": 64 * _GIB20, "vram_used": 0})
check("FA 关闭必须把 V 按 f16 计价: KV 明显变大且 detail 给出说明",
      _est_fa_off["kv_bytes"] > _est_fa_on["kv_bytes"] * 1.5
      and "FA 关闭" in _est_fa_off["detail"] and "FA 关闭" not in _est_fa_on["detail"],
      "off=%.2f GiB on=%.2f GiB" % (_est_fa_off["kv_bytes"] / _GIB20,
                                    _est_fa_on["kv_bytes"] / _GIB20))
cmdline.read_gguf_arch, cmdline.gguf_shard_bytes = _real_arch20, _real_shards20
# 20.4 pnpm: node 一侧认 ~/.local/bin, pnpm 一侧只认 PATH —— 而两条安装路线都把 pnpm 装进
#      ~/.local/bin, 项目里也从没把它加进 PATH。GNOME 自启/systemd-user/sudo 下就是
#      "明明装好了却报未安装", 一键安装反复点也不收敛。
_real_lbin, _real_which20 = nodetool.LOCAL_BIN, procman.which
_lbin20 = os.path.join(_tmp, "localbin20")
os.makedirs(_lbin20)
_pnpm20 = os.path.join(_lbin20, "pnpm")
with open(_pnpm20, "w", encoding="utf-8") as fh:
    fh.write("#!/bin/sh\necho 9.9.9\n")
os.chmod(_pnpm20, 0o755)
procman.which = lambda name: None               # PATH 里没有 pnpm
nodetool.LOCAL_BIN = _lbin20
_p20 = nodetool._pnpm_path()
procman.which = _real_which20
check("pnpm 装在 ~/.local/bin 且 PATH 不含它时必须能找到(否则装好了也报未安装)",
      _p20 == _pnpm20, str(_p20))
nodetool.LOCAL_BIN = _real_lbin

#      超时/空输出两族: corepack 的 subprocess.run 超时(首次运行要联网)以前 r=None,
#      紧接着 r.stderr 抛 AttributeError ⇒ 整个一键安装以 'NoneType' object has no
#      attribute 'stderr' 收场; corepack "返回 0" 但 shim 还没取到包时, 以前只看 path
#      就报 pnpm 就绪, 失败被推到后面的 DSH 安装里。
_real_ppath = nodetool._pnpm_path
_real_detect20, _real_v24_20 = nodetool.detect, nodetool.latest_v24
_real_run20 = nodetool.subprocess.run
_LOG20 = []
_fake_pnpm20 = [None]
_drop20 = [None]                            # corepack 落下哪个 shim(哑 shim / 正常 shim)
_shim20 = os.path.join(_lbin20, "pnpm-silent")
with open(_shim20, "w", encoding="utf-8") as fh:
    fh.write("#!/bin/sh\nexit 0\n")          # corepack shim 取不到包: 退出码 0、零输出
os.chmod(_shim20, 0o755)


def _detect20(force=False):
    return {"node24": {"path": os.path.join(_lbin20, "node"), "version": "v24.9.9"},
            "any_node": {"path": os.path.join(_lbin20, "node"), "version": "v24.9.9"},
            "pnpm": ({"path": _fake_pnpm20[0], "version": None} if _fake_pnpm20[0]
                     else {"path": None, "version": None}),
            "meets": False, "version_unknown": False}


def _run_timeout(cmd, *a, **k):
    raise nodetool.subprocess.TimeoutExpired(cmd=cmd, timeout=120)


nodetool.LOCAL_BIN = _lbin20
nodetool._pnpm_path = lambda: None            # 两条路线都没能让 pnpm 出现
nodetool.detect = _detect20
nodetool.latest_v24 = lambda: ("https://nodejs.org/dist", "v24.9.9")
nodetool.subprocess.run = _run_timeout
_ok20, _msg20 = nodetool.install_node_env(_LOG20.append)
check("corepack 超时不得让任务以 AttributeError 收场(要看得出 node 已装好、超时在哪)",
      _ok20 is False and "NoneType" not in (_msg20 or "")
      and any("corepack enable 超时" in t for t in _LOG20), str((_ok20, _msg20, _LOG20[:3])))


def _run_ok(cmd, *a, **k):
    cmd_s = " ".join(str(x) for x in cmd)
    if "corepack" in cmd_s:
        _fake_pnpm20[0] = _drop20[0]         # corepack 落下什么 shim 由对照决定
        return types.SimpleNamespace(returncode=0, stdout="", stderr="")
    if "install" in cmd_s:
        return types.SimpleNamespace(returncode=0, stdout="", stderr="")
    return _real_run20(cmd, *a, **k)          # pnpm --version 真跑那个哑 shim


nodetool.subprocess.run = _run_ok
_drop20[0] = _shim20                        # corepack 落下一个 exit 0、零输出的 shim
_ok20b, _msg20b = nodetool.install_node_env(_LOG20.append)
check("pnpm 文件在但 --version 无输出时不得报就绪(corepack 首次运行要联网取包)",
      _ok20b is False and "无输出" in (_msg20b or ""), str((_ok20b, _msg20b)))
_drop20[0] = _pnpm20                         # 对照: 能报出版本时必须照常判就绪
_ok20c, _msg20c = nodetool.install_node_env(_LOG20.append)
check("对照: pnpm --version 有输出时仍要判就绪(不得把新校验变成一律失败)",
      _ok20c is True and "9.9.9" in (_msg20c or ""), str((_ok20c, _msg20c)))
nodetool.subprocess.run = _real_run20
nodetool.detect, nodetool.latest_v24 = _real_detect20, _real_v24_20
nodetool._pnpm_path, nodetool.LOCAL_BIN = _real_ppath, _real_lbin


# 20.5 三态要一路走到确认弹窗: core.precheck → need_confirm → app.js。中途任何一处
#      bool(...) 都会把"没读到"变成断言(旧版 bool(None)=False ⇒ 标题"显存不足"、按钮 danger)。
import schema as _schema20
_ldir20 = os.path.join(_tmp, "llama20_dir")
os.makedirs(_ldir20)
open(os.path.join(_ldir20, "llama-server"), "w", encoding="utf-8").write("#!/bin/sh\n")
os.chmod(os.path.join(_ldir20, "llama-server"), 0o755)
_prof20b = dict(_prof20, LlamaDir=_ldir20)
_prof20b["ValueParams"] = dict(_prof20["ValueParams"], **{"port": {"Enabled": True, "Value": "34567"}})
cmdline.read_gguf_arch = lambda _p: {
    "ok": True, "layers": 64, "kv_heads": 8, "head_dim": 128, "head_count": 40,
    "embed": 5120, "context_len": 8192, "architecture": "qwen35", "sliding_window": 0,
    "full_attention_interval": 1, "nextn_predict_layers": 0, "expert_count": 0,
    "ssm_inner_size": 0, "ssm_state_size": 0, "ssm_conv_kernel": 0, "vocab_size": 151936}
cmdline.gguf_shard_bytes = lambda _p: 20 * 1024 ** 3
_run20 = core.LlamaRunner(_Silent(), {"defs": _schema20.load()["defs"]})
_qg20 = monitor.query_gpu
_seen20 = {}
for _lab20, _q20 in (("nodata", lambda: {"vram_total": 0, "vram_used": 0}),
                     ("roomy", lambda: {"vram_total": 64 * _GIB20, "vram_used": _GIB20}),
                     ("tight", lambda: {"vram_total": 8 * _GIB20, "vram_used": 7 * _GIB20})):
    monitor.query_gpu = _q20
    try:
        _seen20[_lab20] = _run20.precheck(_prof20b)
    finally:
        monitor.query_gpu = _qg20
check("确认链必须把\"探针无显存数据\"原样带到前端(vram_fits=None, 不得抹成 True/False)",
      _seen20["nodata"].get("kind") == "confirm"
      and "vram_fits" in _seen20["nodata"] and _seen20["nodata"]["vram_fits"] is None
      and "未取到" in _seen20["nodata"]["vram"],
      str({k: _seen20[k].get("vram_fits", "<无键>") for k in sorted(_seen20)}))
check("对照: 有显存数据时确认链仍给布尔(宽裕 True / 超出 False)",
      _seen20["roomy"]["vram_fits"] is True and _seen20["tight"]["vram_fits"] is False,
      str((_seen20["roomy"]["vram_fits"], _seen20["tight"]["vram_fits"])))
cmdline.read_gguf_arch, cmdline.gguf_shard_bytes = _real_arch20, _real_shards20

# 20.6 启动 DSH 时 exec 的是裸命令名(node / pnpm / npx): 一键安装把它们链进 ~/.local/bin,
#      而启动器进程(自启、systemd-user、sudo)的 PATH 常常不含它 ⇒ execvp 报"找不到命令",
#      面板上与"根本没装"同形。HOME 临时指向自检目录, 不碰用户真实的 ~/.local/bin。
_real_which20b, _real_home20 = procman.which, os.environ.get("HOME")
_home20 = os.path.join(_tmp, "home20")
_lbin_real20 = os.path.join(_home20, ".local", "bin")
os.makedirs(_lbin_real20)
_node20 = os.path.join(_lbin_real20, "node")
open(_node20, "w", encoding="utf-8").write("#!/bin/sh\necho v24.9.9\n")
os.chmod(_node20, 0o755)
os.environ["HOME"] = _home20
try:
    procman.which = lambda name: None           # PATH 里没有 node(自启/systemd/sudo 常见)
    _abs_ok20 = core._abs_exe("node")
    _abs_none20 = core._abs_exe("no-such-exe-r11-zzz")
    check("要 exec 的命令必须能在 PATH 之外解析到 ~/.local/bin(否则装了也报找不到命令)",
          _abs_ok20 == _node20 and _abs_none20 is None, str((_abs_ok20, _abs_none20)))
    _sysbin20 = os.path.join(_tmp, "sysbin20")
    os.makedirs(_sysbin20, exist_ok=True)
    _node_sys20 = os.path.join(_sysbin20, "node")
    open(_node_sys20, "w", encoding="utf-8").write("#!/bin/sh\necho v24.9.9\n")
    os.chmod(_node_sys20, 0o755)
    procman.which = lambda name: _node_sys20 if name == "node" else None
    check("对照: PATH 里有结果时必须优先用它(不得把解析变成硬编码 ~/.local/bin)",
          core._abs_exe("node") == _node_sys20
          and core._abs_exe("no-such-exe-r11-zzz") is None, str(core._abs_exe("node")))
finally:
    procman.which = _real_which20b
    os.environ["HOME"] = _real_home20


# --- 21) 第十二轮: DSH 目录看不到时不得"判死" ------------------------------------
# 21.1 is_dsh_package_dead 的三态: 面板没填 DSH 目录(或填的不是 DSH 源码树)时, 依据只有
#      "我在哪儿都没找到这个包", 而那个"哪儿"一个都没看成。旧版一律 return True ⇒
#      插件面板每个插件一行 error"死依赖", 升级预检说"最新版仍声明已删除的依赖",
#      升级流程还会把插件 package.json 里的 peer 声明删掉。
_t21dsh = os.path.join(_tmp, "dsh_tree21")
os.makedirs(os.path.join(_t21dsh, "apps", "cli"))
with open(os.path.join(_t21dsh, "package.json"), "w", encoding="utf-8") as fh:
    json.dump({"name": "@deepseek-ai/dsh", "version": "1.0.0"}, fh)
with open(os.path.join(_t21dsh, "apps", "cli", "package.json"), "w", encoding="utf-8") as fh:
    json.dump({"name": "@deepseek-ai/dsh-cli", "version": "1.0.0"}, fh)
check("看不到 DSH 安装时必须返回 None(无法判定), 不得返回 True 判死",
      _compat.is_dsh_package_dead("@deepseek-ai/dsh-anything", "", _prof) is None
      and _compat.is_dsh_package_dead("@deepseek-ai/dsh-anything", _dsh9, _prof) is None,
      str((_compat.is_dsh_package_dead("@deepseek-ai/dsh-anything", "", _prof),
           _compat.is_dsh_package_dead("@deepseek-ai/dsh-anything", _dsh9, _prof))))
check("对照: 确实是 DSH 源码树时判定照旧(没 shipped 的判死, shipped 的判活)",
      _compat.is_dsh_package_dead("@deepseek-ai/dsh-gone", _t21dsh, _prof) is True
      and _compat.is_dsh_package_dead("@deepseek-ai/dsh", _t21dsh, _prof) is False,
      str((_compat.is_dsh_package_dead("@deepseek-ai/dsh-gone", _t21dsh, _prof),
           _compat.is_dsh_package_dead("@deepseek-ai/dsh", _t21dsh, _prof))))

# 21.2 审计行: 未判定要单列 peer_unknown, 不能塞进 dead_peers
with open(os.path.join(_prof, "package.json"), "w", encoding="utf-8") as fh:
    json.dump({"name": "profile-web", "version": "1.0.0",
               "dependencies": {"good-plugin": "^1.0.0", "broken-plugin": "^1.0.0",
                                "peer-plugin": "^1.0.0"}}, fh)
os.makedirs(os.path.join(_prof, "node_modules", "peer-plugin"), exist_ok=True)
_peer_pj21 = os.path.join(_prof, "node_modules", "peer-plugin", "package.json")
with open(_peer_pj21, "w", encoding="utf-8") as fh:
    json.dump({"name": "peer-plugin", "version": "1.0.0",
               "peerDependencies": {"@deepseek-ai/dsh-gone": ">=1.0.0"}}, fh)
_rows21a = {r["name"]: r for r in _compat.audit_plugins("web", "")}
_rows21b = {r["name"]: r for r in _compat.audit_plugins("web", _t21dsh)}
check("DSH 目录未指明时该 peer 只能记为\"未判定\", 不得进 dead_peers",
      "@deepseek-ai/dsh-gone" in _rows21a["peer-plugin"]["peer_unknown"]
      and not _rows21a["peer-plugin"]["dead_peers"],
      str({k: _rows21a["peer-plugin"][k] for k in ("peer_unknown", "dead_peers")}
          if "peer-plugin" in _rows21a else sorted(_rows21a)))
check("对照: 指明且确为源码树时照旧判死(不得把新三态变成一律不判)",
      "@deepseek-ai/dsh-gone" in _rows21b["peer-plugin"]["dead_peers"]
      and not _rows21b["peer-plugin"]["peer_unknown"],
      str({k: _rows21b["peer-plugin"][k] for k in ("peer_unknown", "dead_peers")}
          if "peer-plugin" in _rows21b else sorted(_rows21b)))

# 21.3 升级流程里"清理死 peer"会改写插件的 package.json —— 未判定时必须不动用户的清单
_real_reg21, _real_add21 = _compat._registry_query, _compat._pnpm_add
_compat._registry_query = lambda name: ("https://registry.npmjs.org", "9.9.9",
                                        {"peerDependencies": {"@deepseek-ai/dsh-gone": ">=1.0.0"}})
_compat._pnpm_add = lambda base, name, spec: (True, "ok")
_log21 = []
# _dsh9 是"目录存在但不是 DSH 源码树"(没有 package.json) —— 旧版据此把全部 peer 判死
_ok21a, _msg21a = _compat._upgrade_plugin_locked("web", "peer-plugin", _dsh9, job_log=_log21.append)
_untouched21 = json.load(open(_peer_pj21, encoding="utf-8"))
_ok21b, _msg21b = _compat._upgrade_plugin_locked("web", "peer-plugin", _t21dsh, job_log=_log21.append)
_pruned21 = json.load(open(_peer_pj21, encoding="utf-8"))
_compat._registry_query, _compat._pnpm_add = _real_reg21, _real_add21
with open(_peer_pj21, "w", encoding="utf-8") as fh:
    json.dump({"name": "peer-plugin", "version": "1.0.0",
               "peerDependencies": {"@deepseek-ai/dsh-gone": ">=1.0.0"}}, fh)
check("DSH 目录看不到时升级不得删插件的 peer 依赖声明, 且要把\"未判定\"说出来",
      "@deepseek-ai/dsh-gone" in (_untouched21.get("peerDependencies") or {})
      and any("无法判定" in t for t in _log21),
      str({k: _untouched21.get(k) for k in ("peerDependencies",)} if _ok21a else (_ok21a, _msg21a)))
check("对照: 确认已删除的依赖照旧清理(不得把三态变成永远不清理)",
      "@deepseek-ai/dsh-gone" not in (_pruned21.get("peerDependencies") or {}),
      str(_pruned21.get("peerDependencies")))

# 21.4 DSH 目录为空时旧写法兜底成**相对**目录 "deepseek-harness": 模式判定对着幽灵路径做,
#      Popen 的 cwd 也用它 ⇒ "[Errno 2] No such file or directory: 'deepseek-harness'",
#      与"用户把目录写错了"同形。现在改成已知位置扫描 + 启动前明确拒绝。
_real_home21 = os.environ.get("HOME")
_home21 = os.path.join(_tmp, "home21_nodsh")
os.makedirs(_home21)
os.environ["HOME"] = _home21
try:
    _dr21 = core.DshRunner(_Silent(), {"defs": []})
    _dr21._spawn = lambda *a, **k: False           # 对照里也不允许真的拉起进程
    _d21 = _dr21.dsh_dir_of({})
    _ok21c, _msg21c = _dr21._start_inner({"DshDir": ""})
    _ok21d, _msg21d = _dr21._start_inner({"DshDir": _t21dsh})
finally:
    os.environ["HOME"] = _real_home21
check("DSH 目录没填且已知位置扫不到时, 必须直接说明缺什么(不是 Popen 的 Errno 2)",
      _d21 == "" and _ok21c is False and "未指定有效的 DSH 目录" in (_msg21c or "")
      and "Errno 2" not in (_msg21c or ""), str((_d21, _ok21c, _msg21c)))
check("对照: 指明有效目录时不得被这道新守卫拦住",
      "未指定有效" not in (_msg21d or ""), str((_ok21d, _msg21d)))


# --- 22) 第十三轮: 数字算错 / 缺证据却下结论 --------------------------------------
# 22.1 磁盘 used 把 root 保留块算成"已用"(f_bavail 是"非 root 可用", f_bfree 才是"空闲"):
#      ext4 默认保留 5%, 2T 盘白多约 100G, 进度条提前进红区, 且与 df/文件管理器对不上。
_real_statvfs22 = monitor.os.statvfs
_blk22 = 1024
_fake_st22 = types.SimpleNamespace(f_blocks=1000, f_bfree=300, f_bavail=250, f_frsize=_blk22)
monitor.os.statvfs = lambda _p: _fake_st22
_rows22 = monitor._disk_statvfs()
monitor.os.statvfs = _real_statvfs22
check("磁盘 used 必须是 blocks-bfree、pct 必须是 used/(used+avail)(与 df 同口径)",
      _rows22 and _rows22[0]["used"] == 700 * _blk22 and _rows22[0]["avail"] == 250 * _blk22
      and abs(_rows22[0]["pct"] - 73.7) < 0.05,
      str(_rows22[:1]) if _rows22 else "无可用挂载点")
check("对照: 真实文件系统上 total 仍是文件系统总大小(不得改成 used+avail 之和)",
      monitor._disk_statvfs()[0]["total"] > monitor._disk_statvfs()[0]["used"],
      str(monitor._disk_statvfs()[0]))

# 22.2 每挂载点的 I/O 用双向 startswith 匹配 diskstats: 同一块盘挂两个分区(/ 与 /data)时,
#      两行都匹配到排在前面的整盘名 ⇒ 表头(逐行相加)把同一块盘的读写数两遍。
check("分区名→整盘名的规则: nvme/mmcblk 的 pN 与 sd 系的尾数都要对",
      [monitor._parent_disk(n) for n in ("nvme0n1p2", "mmcblk0p1", "sda2", "xvda1")]
      == ["nvme0n1", "mmcblk0", "sda", "xvda"]
      and [monitor._parent_disk(n) for n in ("sda", "nvme0n1", "md0", "loop0")]
      == ["sda", "nvme0n1", "md0", "loop0"],
      str({n: monitor._parent_disk(n) for n in ("nvme0n1p2", "sda2", "md0", "loop0")}))
_real_rows22, _real_io22 = monitor._disk_rows_cached, monitor._disk_io_rates
_two = [{"dev": "nvme0n1p1", "mount": "/", "total": 1, "used": 1, "avail": 1, "pct": 50.0},
        {"dev": "nvme0n1p2", "mount": "/data", "total": 1, "used": 1, "avail": 1, "pct": 50.0}]
monitor._disk_rows_cached = lambda: [dict(d) for d in _two]
monitor._disk_io_rates = lambda: {"nvme0n1": (100.0, 50.0), "nvme0n1p1": (30.0, 10.0)}
monitor._sys_cache.update(t=0.0, v=None)
_s22 = monitor.sample_system()
_sum22 = round(sum(d["read_mb"] for d in _s22["disks"]), 1)
monitor._disk_rows_cached = lambda: [{"dev": "sdb1", "mount": "/x", "total": 1, "used": 1,
                                      "avail": 1, "pct": 50.0}]
monitor._disk_io_rates = lambda: {"sdb": (12.0, 3.0)}
monitor._sys_cache.update(t=0.0, v=None)
_s22b = monitor.sample_system()
monitor._disk_rows_cached, monitor._disk_io_rates = _real_rows22, _real_io22
monitor._sys_cache.update(t=0.0, v=None)
check("同盘两个挂载点不得各自显示整盘速率(表头逐行相加会翻倍)",
      _s22["disks"][0]["read_mb"] == 30.0 and _s22["disks"][0]["io_scope"] == "part"
      and _s22["disks"][1]["read_mb"] == 0.0 and _sum22 == 30.0,
      "表头读=%.1f 行=%s" % (_sum22, [(d["dev"], d["read_mb"], d["io_scope"]) for d in _s22["disks"]]))
check("对照: 只有一个挂载点且没有分区级计数时, 仍要退回整盘速率(不得变成一律 0)",
      _s22b["disks"][0]["read_mb"] == 12.0 and _s22b["disks"][0]["io_scope"] == "disk",
      str([(d["dev"], d["read_mb"], d["io_scope"]) for d in _s22b["disks"]]))

# 22.3 分片模型缺片: gguf_shard_paths 只列**存在**的分片, 于是"每片都读通"也可能是残缺模型
#      —— 权重按已有分片算(偏小), 而 llama-server 加载时会直接报缺片。
def _stub_gguf(path):
    with open(path, "wb") as fh:
        fh.write(b"GGUF" + struct.pack("<I", 3) + struct.pack("<Q", 0) + struct.pack("<Q", 0))


_gdir22 = os.path.join(_tmp, "shards22")
os.makedirs(_gdir22)
_g122 = os.path.join(_gdir22, "m-00001-of-00003.gguf")
_g322 = os.path.join(_gdir22, "m-00003-of-00003.gguf")
_stub_gguf(_g122)
_stub_gguf(_g322)
check("缺片必须按文件名声明的 -of-NNNNN 单独算出来(枚举只给存在的分片)",
      cmdline.gguf_shard_missing(_g122) == 1 and len(cmdline.gguf_shard_paths(_g122)) == 2,
      str((cmdline.gguf_shard_missing(_g122), len(cmdline.gguf_shard_paths(_g122)))))
_ts22 = cmdline.read_gguf_tensor_stats(_g122, 0)
check("缺片时张量表不得报\"完整\"(旧版 shards_ok==shards_total 于是 ok=True)",
      _ts22["missing_shards"] == 1 and _ts22["ok"] is False,
      str({k: _ts22.get(k) for k in ("ok", "partial", "missing_shards")}))
_stub_gguf(os.path.join(_gdir22, "m-00002-of-00003.gguf"))
_ts22c = cmdline.read_gguf_tensor_stats(_g122, 0)
check("对照: 分片齐全时照旧判 ok 且不产生缺失告警",
      _ts22c["missing_shards"] == 0 and _ts22c["ok"] is True,
      str({k: _ts22c.get(k) for k in ("ok", "partial", "missing_shards")}))
_arch22 = cmdline.read_gguf_arch
cmdline.read_gguf_arch = lambda _p: {
    "ok": True, "layers": 8, "kv_heads": 4, "head_dim": 64, "head_count": 32,
    "embed": 2048, "context_len": 4096, "architecture": "qwen35", "sliding_window": 0,
    "full_attention_interval": 1, "nextn_predict_layers": 0, "expert_count": 0,
    "ssm_inner_size": 0, "ssm_state_size": 0, "ssm_conv_kernel": 0, "vocab_size": 1000}
os.remove(os.path.join(_gdir22, "m-00002-of-00003.gguf"))
_e22 = cmdline.estimate_vram_needed(dict(_prof20, ModelPath=_g122), _defs20,
                                    lambda: {"vram_total": 64 * _GIB20, "vram_used": 0})
_stub_gguf(os.path.join(_gdir22, "m-00002-of-00003.gguf"))   # 补齐缺片再跑对照
_e22c = cmdline.estimate_vram_needed(dict(_prof20, ModelPath=_g122), _defs20,
                                     lambda: {"vram_total": 64 * _GIB20, "vram_used": 0})
cmdline.read_gguf_arch = _arch22
check("预算必须把缺片写成显眼警告(而不是按已有分片安静地少算)",
      _e22 and "分片不完整" in _e22["detail"] and "分片不完整" not in _e22c["detail"],
      str([ln for ln in (_e22 or {}).get("detail", "").splitlines()[:2]]))

# 22.4 node 在但 `node -v` 无输出: 81cdb3b 要判"已安装但版本未知", 以前候选里只收有版本的
#      节点 ⇒ version_unknown 恒为 False, 那段跳过重装的守卫是死码, 面板与"没装"同形。
_node_silent22 = os.path.join(_tmp, "node-silent22")
with open(_node_silent22, "w", encoding="utf-8") as fh:
    fh.write("#!/bin/sh\nexit 0\n")
os.chmod(_node_silent22, 0o755)
_node_ok22 = os.path.join(_tmp, "node-ok22")
with open(_node_ok22, "w", encoding="utf-8") as fh:
    fh.write("#!/bin/sh\necho v24.9.9\n")
os.chmod(_node_ok22, 0o755)
_real_cands22 = nodetool._node_candidates
nodetool._node_candidates = lambda: [_node_silent22]
_st22a = nodetool.detect(force=True)
nodetool._node_candidates = lambda: [_node_ok22]
_st22b = nodetool.detect(force=True)
nodetool._node_candidates = _real_cands22
check("exe 在但版本探测不到时必须判\"已安装、版本未知\"(并把这个 exe 报出来)",
      _st22a["version_unknown"] is True and _st22a["node24"]["path"] == _node_silent22
      and _st22a["meets"] is False,
      str({k: _st22a.get(k) for k in ("version_unknown", "meets", "node24")}))
check("对照: node 正常时不得判未知版本",
      _st22b["version_unknown"] is False and _st22b["node24"]["version"] == "v24.9.9",
      str({k: _st22b.get(k) for k in ("version_unknown", "node24")}))
#      守卫必须是活的: 版本未知时一键安装不得再去下载 node(每次重下几十 MB 也修不好它)
_real_v24_22, _real_up22 = nodetool.latest_v24, nodetool.urllib.request.urlopen
_opened22 = []


def _no_net22(*a, **k):
    _opened22.append(a[0] if a else "")
    raise IOError("不该下载")


_real_detect22 = nodetool.detect
nodetool.detect = lambda force=False: dict(_st22a, pnpm={"path": None, "version": None})
nodetool.latest_v24 = lambda: ("https://nodejs.org/dist", "v24.9.9")
nodetool.urllib.request.urlopen = _no_net22
_log22 = []
_ok22, _msg22 = nodetool.install_node_env(_log22.append)
nodetool.urllib.request.urlopen = _real_up22
nodetool.detect, nodetool.latest_v24 = _real_detect22, _real_v24_22
check("版本未知时不得发起下载(81cdb3b 的守卫必须真的生效), 且日志要说明跳过重装",
      not _opened22 and any("跳过重装" in t for t in _log22),
      str((["%s" % u for u in _opened22], [t[:40] for t in _log22[:3]])))

# 22.5 迁回旧目录的额外文件失败时, 消息让用户去 old_dir 找回, 紧接着却 rmtree 删掉它
_old22, _new22 = os.path.join(_tmp, "old22"), os.path.join(_tmp, "new22")
os.makedirs(_old22)
os.makedirs(_new22)
open(os.path.join(_old22, "my-model.gguf"), "w", encoding="utf-8").write("x")
_w22, _keep22 = toolchain._migrate_extras(_old22, _new22)
check("迁回成功时不保留旧目录(不留垃圾)",
      _w22 == "" and _keep22 is False
      and os.path.isfile(os.path.join(_new22, "my-model.gguf")), str((_w22, _keep22)))
_real_move22 = toolchain.shutil.move
shutil.rmtree(_old22, ignore_errors=True)
shutil.rmtree(_new22, ignore_errors=True)
os.makedirs(_old22)
open(os.path.join(_old22, "my-model.gguf"), "w", encoding="utf-8").write("x")
os.makedirs(_new22)
toolchain.shutil.move = lambda s, d: (_ for _ in ()).throw(OSError(28, "No space left on device"))
_w22b, _keep22b = toolchain._migrate_extras(_old22, _new22)
toolchain.shutil.move = _real_move22
check("迁回失败时必须保留旧目录并说清位置(旧版接着 rmtree, 把说要保留的删了)",
      _keep22b is True and "原目录已保留" in _w22b and _old22 in _w22b
      and os.path.isfile(os.path.join(_old22, "my-model.gguf")),
      str((_w22b, _keep22b)))

# 22.6 变体注册要按写入时的列表改: 下载安装要几分钟, 期间用户改的其他变体不能被快照抹掉
_real_pr22, _real_fetch22 = toolchain.parse_repo_input, toolchain.fetch_latest_llama_release
_real_upd22, _real_be22 = toolchain.apply_llama_update, toolchain.resolve_backend
_real_fexe22 = toolchain.find_llama_server_exe
toolchain.parse_repo_input = lambda t: "owner/repo"
toolchain.resolve_backend = lambda p: "vulkan"
toolchain.find_llama_server_exe = lambda d: None
toolchain.fetch_latest_llama_release = lambda repo, backend=None, log=None: {
    "tag": "b9", "build": 9, "asset_name": "a.tar.gz", "repo": "owner/repo"}


def _upd_with_race(dir_, release, job_log=None):
    cur = _prof22.get("LlamaVariants") or []
    _prof22["LlamaVariants"] = cur + [{"Name": "用户期间新增", "Repo": "a/b", "Dir": "/tmp/other"}]
    return True, "装好了"


toolchain.apply_llama_update = _upd_with_race
_prof22 = {"LlamaVariants": [{"Name": "旧变体", "Repo": "o/r", "Dir": "/tmp/old-var"}]}
_ok22d, _msg22d = toolchain.install_variant(_prof22, "https://github.com/owner/repo", "新变体", "/tmp/new-var")
_names22 = sorted(x["Name"] for x in _prof22["LlamaVariants"])
check("安装期间用户新增的变体必须还在(不得用下载前的快照回写)",
      _ok22d is True and _names22 == ["新变体", "旧变体", "用户期间新增"],
      str(_names22))


def _upd_same_name(dir_, release, job_log=None):
    cur = _prof22.get("LlamaVariants") or []
    _prof22["LlamaVariants"] = [x for x in cur if x["Name"] != "新变体"] + \
        [{"Name": "新变体", "Repo": "z/z", "Dir": "/tmp/elsewhere"}]
    return True, "装好了"


toolchain.apply_llama_update = _upd_same_name
_prof22 = {"LlamaVariants": [{"Name": "旧变体", "Repo": "o/r", "Dir": "/tmp/old-var"}]}
_ok22e, _msg22e = toolchain.install_variant(_prof22, "https://github.com/owner/repo",
                                            "新变体", "/tmp/new-var")
toolchain.parse_repo_input, toolchain.fetch_latest_llama_release = _real_pr22, _real_fetch22
toolchain.apply_llama_update, toolchain.resolve_backend = _real_upd22, _real_be22
toolchain.find_llama_server_exe = _real_fexe22
check("下载期间出现同名不同目录的变体时不得覆盖注册, 要说清目录已装好",
      _ok22e is False and "未做注册" in (_msg22e or "")
      and next((x for x in _prof22["LlamaVariants"] if x["Name"] == "新变体"), {})["Dir"] == "/tmp/elsewhere",
      str((_ok22e, _msg22e, _prof22["LlamaVariants"])))

import client
# 22.7 托盘助手传的 --url 是它上次记住的地址: 服务可能已退出/换了顺延端口/重启换了 token,
#      以前直接用它 ⇒ 窗口打开一片空白, 看不出服务不在。
_real_argv, _real_lock, _real_probe = sys.argv, client._acquire_window_lock, client._probe
_real_ens, _real_gtk, _real_pop22 = client.ensure_server, client.run_gtk, client.subprocess.Popen
_seen_gtk = []
sys.argv = ["client.py", "--url", "http://127.0.0.1:9"]
client._acquire_window_lock = lambda: True
client.run_gtk = lambda base: _seen_gtk.append(base)
client.subprocess.Popen = lambda *a, **k: None
client.ensure_server = lambda port: "http://127.0.0.1:8799"
client._probe = lambda base: None                      # --url 那个地址没人应
client.main()
sys.argv = ["client.py", "--url", "http://127.0.0.1:8788"]
client._probe = lambda base: {"ok": True}
client.main()
sys.argv = _real_argv
client._acquire_window_lock, client._probe = _real_lock, _real_probe
client.ensure_server, client.run_gtk = _real_ens, _real_gtk
client.subprocess.Popen = _real_pop22
check("--url 无响应时必须回退到按端口自举/拉起(不是打开空白窗口)",
      _seen_gtk[0] == "http://127.0.0.1:8799", str(_seen_gtk))
check("对照: --url 确实活着时照旧直接用它(不得每次都去拉一个新服务)",
      len(_seen_gtk) == 2 and _seen_gtk[1] == "http://127.0.0.1:8788", str(_seen_gtk))

# 22.8 托盘的"轻量化"复选只在托盘自己点过时改: 网页端开关、窗口关闭置位、服务重启归零
#      都不会回填 ⇒ 菜单勾与真实监控状态相反。
_real_url22 = _tray.urllib.request.urlopen


class _FakeTray22:
    base_url = "http://127.0.0.1:1"
    lightweight = False
    _light_busy_until = 0.0

    def set_lightweight(self, on):
        self.lightweight = bool(on)

    def _post_ui(self, fn, *a):
        fn(*a)


def _mk_st(status):
    class _R:
        def __enter__(self):
            return self

        def __exit__(self, *a):
            return False

        def read(self):
            return json.dumps({"lightweight": status}).encode()
    return lambda *a, **k: _R()


_ft22 = _FakeTray22()
_tray.urllib.request.urlopen = _mk_st(True)
_tray.TrayIcon._sync_lightweight(_ft22)
for _ in range(40):
    if _ft22.lightweight:
        break
    time.sleep(0.05)
_tray.urllib.request.urlopen = lambda *a, **k: (_ for _ in ()).throw(IOError("服务没起"))
_still22 = _ft22.lightweight
_tray.urllib.request.urlopen = _real_url22
check("托盘必须从 /api/status 回填轻量化复选态(网页端/窗口关闭改的也能看见)",
      _still22 is True, str(_ft22.lightweight))
_ft22b = _FakeTray22()
_ft22b.lightweight = True
_tray.urllib.request.urlopen = lambda *a, **k: (_ for _ in ()).throw(IOError("服务没起"))
_tray.TrayIcon._sync_lightweight(_ft22b)
for _ in range(20):
    time.sleep(0.05)
_tray.urllib.request.urlopen = _real_url22
check("对照: 读不到服务时保持现状, 不得当成\"没开\"(勾会自己跳掉)",
      _ft22b.lightweight is True, str(_ft22b.lightweight))


# --- 23) 第十四轮: 监控线程会死 / 轻量化吞掉"有人在看" / 磁盘数字口径 -------------
# 23.1 loop 末尾的 time.sleep(interval): 首拍在赋值**之前**抛异常(读 /proc、端口探测抖动),
#      interval 就未绑定 ⇒ UnboundLocalError 把整条监控线程杀掉(HEAD 实测线程存活=False)。
#      后果: 监控页永远停在最后一帧, 而显存熔断的判定就跑在这个循环里 —— 熔断从此失效且
#      毫无提示。同时同类错误 3s 刷一条会刷爆 error.log。
class _BoomLlama23:
    def __init__(self, kind=RuntimeError):
        self.kind, self.calls = kind, 0

    @property
    def running(self):
        self.calls += 1
        raise self.kind("探进程时炸了")


class _IdleDsh23:
    running = False
    status = "未启动"


class _LogHub23:
    def __init__(self):
        self.lines = []

    def log(self, text):
        self.lines.append(text)


_mon23 = core.Monitor(_LogHub23(), _BoomLlama23(), _IdleDsh23())
_orig_sleep23 = core.time.sleep
core.time.sleep = lambda s: _orig_sleep23(0.02)
_th23 = threading.Thread(target=_mon23.loop, daemon=True)
_th23.start()


def _wait23(cond, secs):
    """按条件等(休眠现在是 5s 一档, 不能按固定 0.6s 猜拍数)。"""
    end = time.time() + secs
    while time.time() < end:
        if cond():
            return True
        _orig_sleep23(0.05)
    return cond()


_ok23a = _wait23(lambda: _mon23.llama.calls >= 3, 8.0)
_alive23 = _th23.is_alive()
_calls23 = _mon23.llama.calls
_lines23 = list(_mon23.hub.lines)
# 换一种异常时, 新行必须补上"上一条同类重复了多少次"
_mon23.llama = _BoomLlama23(ValueError)
_ok23b = _wait23(lambda: len(_mon23.hub.lines) >= 2, 8.0)
core.time.sleep = _orig_sleep23
check("监控循环首拍抛异常后线程必须活着且继续复查(interval 有兜底值; HEAD 会 UnboundLocalError 退出)",
      _alive23 and _ok23a and _calls23 >= 3 and len(_lines23) == 1,
      str((_alive23, _calls23, _lines23)))
check("监控错误日志必须带异常类型名(很多异常 str 是空的)",
      any("RuntimeError:" in t for t in _lines23), str(_lines23))
check("同一错误不得逐拍刷屏; 换错时新行要说明上一条重复了多少次",
      _ok23b and len(_mon23.hub.lines) == 2 and "已重复" in _mon23.hub.lines[1]
      and "ValueError:" in _mon23.hub.lines[1], str(_mon23.hub.lines))

# 23.2 轻量化不能把 monitor_visible 也省掉: 那是监控页每 3s 打点的"有人正看着"。
#      以前 lightweight 一开就走低频分支并完全忽略 visible, 实测 sample_system 一次都不跑
#      ⇒ 浏览器直开面板, 或窗口那次"关轻量化"被 401 拒掉时, 整片图表停在旧帧。
_m23 = core.Monitor(_LogHub23(), None, None)
_cad = {}
for _lw in (True, False):
    for _vis in (True, False):
        _m23.lightweight, _m23.monitor_visible = _lw, _vis
        _m23.monitor_seen_at = time.time() if _vis else 0.0     # "有人在看"是有时效的打点
        _cad[(_lw, _vis, False)] = _m23._cadence(False, False, False)
        _cad[(_lw, _vis, True)] = _m23._cadence(True, False, False)
check("监控页可见时不得走轻量化休眠分支(间隔也要回到 3s)",
      _cad[(True, True, False)] == ("full", 3.0) and _cad[(True, True, True)] == ("full", 3.0),
      str({k: v for k, v in _cad.items() if k[1]}))
check("对照: 没人看页时才降频(无服务 60s / 有服务 30s)",
      _cad[(True, False, False)] == ("light", 60.0) and _cad[(True, False, True)] == ("light", 30.0),
      str({k: v for k, v in _cad.items() if k[1] is False}))
check("对照: 非轻量化时 30s 空转/3s 活跃照旧(不得改成一律 3s)",
      _cad[(False, False, False)] == ("full", 30.0) and _cad[(False, False, True)] == ("full", 3.0),
      str({k: v for k, v in _cad.items() if k[0] is False}))
#      而且这个标志必须有时效: 窗口关掉时那句 visible:false 常常发不出去(页面卸载时的
#      fetch 不可靠), 粘滞的 True 会让 30s 空转档与轻量化永远进不去(HEAD 实测: 标志一旦
#      True 就永久 3s 全速采样)。
_m23.monitor_visible, _m23.monitor_seen_at = True, time.time() - 9.0
_stale23 = [_m23._cadence(False, False, False)]
_m23.lightweight = True
_stale23.append(_m23._cadence(False, False, False))
check("页面打点过期(8s)后不得再算\"有人在看\"(粘滞标志会废掉轻量化与 30s 空转)",
      _stale23 == [("full", 30.0), ("light", 60.0)], str(_stale23))
_m23.monitor_seen_at = time.time()
check("对照: 打点新鲜时仍算\"有人在看\"(3s 全速)",
      _m23._cadence(False, False, False) == ("full", 3.0), str(_m23._cadence(False, False, False)))
_m23.lightweight = False
# 23.7 长休眠还要能被"有人刚打开页"打断: 空转/轻量化是 30~60s 一档, 打开面板不能等上一拍
#      睡完才刷新(活体实测: 页已可见但 sampled_at 停了 34s, snapshot_age 一路涨到 31s)。
_m23c = core.Monitor(_LogHub23(), None, None)
_t023c = time.time()
_th23c = threading.Thread(target=lambda: _m23c._sleep_until_next(60.0), daemon=True)
_th23c.start()
time.sleep(0.3)
_m23c.monitor_visible, _m23c.monitor_seen_at = True, time.time()      # 用户打开监控页
_th23c.join(6.0)
_slept23c = time.time() - _t023c
check("打点出现后必须在 ≤6s 内结束 60s 休眠(打开面板不能等上一拍睡完)",
      not _th23c.is_alive() and _slept23c < 6.0, str(round(_slept23c, 2)))
_m23c.monitor_visible = False
_t023d = time.time()
_m23c._sleep_until_next(1.2)
check("对照: 没人看页时必须睡满间隔(不得为了响应把空转也变成高频醒)",
      time.time() - _t023d >= 1.15, str(round(time.time() - _t023d, 2)))


# 23.3 visible=True 时必须真的采样(整条链路), 且快照要带上帧龄与轻量化标记
class _IdleApi23:
    online = False
    phase = ""
    _active_port = None
    slots = []
    boot = {}
    tg_tps = tg3s_tps = log_prompt_speed = log_n_decoded = last_prefill_speed = None
    prompt_progress = prompt_total_tokens = prompt_elapsed_s = None
    last_task = task_started_at = None
    mtp_acceptance = mtp_accepted = mtp_generated = mtp_mean_len = graphs_reused = None

    @staticmethod
    def events_snapshot():
        return [], 0

    @staticmethod
    def model_info():
        return {}

    @staticmethod
    def speeds():
        return 0, 0, "api"

    @staticmethod
    def ctx_view():
        return -1, 0

    @staticmethod
    def poll_fast():
        pass

    @staticmethod
    def poll_slow_if_needed():
        pass


class _IdleLlama23:
    running = False
    status = "未启动"
    api = _IdleApi23()

    @staticmethod
    def get_port():
        return None

    @staticmethod
    def oom_recredit():
        pass


_seen23 = {"sys": 0, "gpu": 0}
_real_ss23, _real_qg23 = core.monitor.sample_system, core.monitor.query_gpus
core.monitor.sample_system = lambda: (_seen23.__setitem__("sys", _seen23["sys"] + 1),
                                      {"cpu": 1.0, "disks": [], "net": {}})[1]
core.monitor.query_gpus = lambda: (_seen23.__setitem__("gpu", _seen23["gpu"] + 1), [])[1]
core.time.sleep = lambda s: _orig_sleep23(0.02)
_m23b = core.Monitor(_LogHub23(), _IdleLlama23(), _IdleDsh23())
_m23b.lightweight, _m23b.monitor_visible = True, True
_m23b.monitor_seen_at = time.time()
_th23b = threading.Thread(target=_m23b.loop, daemon=True)
_th23b.start()
_orig_sleep23(0.5)
_th23b_alive = _th23b.is_alive()
core.time.sleep = _orig_sleep23
_th23b.join(0.3)
core.monitor.sample_system, core.monitor.query_gpus = _real_ss23, _real_qg23
_snap23 = dict(_m23b.snapshot)
check("轻量化开着但页面可见时, 必须真的执行采样(HEAD 实测 0 次)",
      _th23b_alive and _seen23["sys"] >= 1 and _seen23["gpu"] >= 1,
      str((_th23b_alive, _seen23, _m23b.hub.lines[:1])))
check("快照必须带 sampled_at 与 lightweight(旧帧要能自报年龄)",
      isinstance(_snap23.get("sampled_at"), float) and _snap23.get("lightweight") is True
      and _snap23["sampled_at"] <= time.time(),
      str({k: _snap23.get(k) for k in ("sampled_at", "lightweight")}))

# 23.4 磁盘 I/O 的设备名过滤必须与磁盘列表、_parent_disk 同口径:
#      以前只认 (sd|vd|nvme|hd) ⇒ eMMC/云盘/RAID 的盘在列表里有行, 速率却永远 0。
_ds23 = os.path.join(_tmp, "diskstats23")


def _write_ds23(reads):
    with open(_ds23, "w", encoding="utf-8") as fh:
        for name, (rs, ws) in reads.items():
            fh.write("259 0 %s 1 2 %d 4 5 6 %d 10 0 0 0\n" % (name, rs, ws))


_base23 = {"nvme0n1p1": (100, 40), "nvme0n1": (100, 40), "mmcblk0p1": (300, 90),
           "mmcblk0": (300, 90), "xvda1": (400, 120), "md0": (500, 150),
           "loop0": (99999, 99999), "mmcblk0boot0": (88888, 88888)}
_real_ds23, _real_prev23 = monitor.DISKSTATS_PATH, dict(monitor._disk_prev)
monitor.DISKSTATS_PATH = _ds23
_write_ds23(_base23)
monitor._disk_prev.update(t=0.0, io={})
monitor._disk_io_rates()                                    # 第一遍只建基线
_write_ds23({k: (v[0] + 2048, v[1] + 1024) for k, v in _base23.items()})
_orig_sleep23(1.05)
_rates23 = monitor._disk_io_rates()
monitor.DISKSTATS_PATH = _real_ds23
monitor._disk_prev.update(**_real_prev23)
check("eMMC/virtio/RAID 的分区必须能取到读写速率(以前只认 sd|vd|nvme|hd, 这些盘恒显示 0)",
      all(k in _rates23 and _rates23[k][0] > 0 for k in ("nvme0n1p1", "mmcblk0p1", "xvda1", "md0")),
      str({k: round(v[0], 1) for k, v in _rates23.items()}))
check("对照: loop*(snap 镜像)与 mmcblk0boot0 这类硬件分区仍不参与采样",
      "loop0" not in _rates23 and "mmcblk0boot0" not in _rates23
      and "mmcblk0" in _rates23, str(sorted(_rates23.keys())))

# 23.5 磁盘行截断: 以前 out[:6] 按 /proc/mounts 的出现顺序砍, 既不排序也不告知;
#      表头速率还是逐显示行相加 ⇒ 被砍掉的盘连读写都不计入。
_mt23 = os.path.join(_tmp, "mounts23")
with open(_mt23, "w", encoding="utf-8") as _fh23:
    for i in range(8):
        _fh23.write("/dev/sd%c1 /m%d ext4 defaults 0 0\n" % (ord("a") + i, i))
    _fh23.write("/dev/sdz1 / ext4 defaults 0 0\n")
_real_mt23, _real_st23 = monitor.MOUNTS_PATH, monitor.os.statvfs
monitor.MOUNTS_PATH = _mt23
monitor.os.statvfs = lambda p: types.SimpleNamespace(
    f_blocks=(5000 if p == "/" else 1000 * (1 + int(p[2]))), f_bfree=500, f_bavail=400,
    f_frsize=1024)
_rows23 = monitor._disk_statvfs()
monitor.MOUNTS_PATH, monitor.os.statvfs = _real_mt23, _real_st23
check("挂载行不得在采集处截断, 且必须确定性排序(根挂载优先、其余按容量降序)",
      len(_rows23) == 9 and _rows23[0]["mount"] == "/"
      and [d["mount"] for d in _rows23[1:4]] == ["/m7", "/m6", "/m5"],
      str([d["mount"] for d in _rows23[:5]] + ["共 %d 行" % len(_rows23)]))
_real_rows23, _real_io23 = monitor._disk_rows_cached, monitor._disk_io_rates
monitor._disk_rows_cached = lambda: [{"dev": "sd%d1" % i, "mount": "/m%d" % i, "total": 1,
                                      "used": 1, "avail": 1, "pct": 10.0} for i in range(8)]
monitor._disk_io_rates = lambda: {"sd%d1" % i: (1.0 * i, 2.0 * i) for i in range(8)}
monitor._sys_cache.update(t=0.0, v=None)
_sys23 = monitor.sample_system()
monitor._sys_cache.update(t=0.0, v=None)
monitor._disk_rows_cached, monitor._disk_io_rates = _real_rows23, _real_io23
monitor._sys_cache.update(t=0.0, v=None)
check("面板只留 6 行时必须告知被截掉的条数(截断与\"只有这么多盘\"不得同形)",
      len(_sys23["disks"]) == 6 and _sys23["disks_dropped"] == 2,
      str((len(_sys23["disks"]), _sys23["disks_dropped"])))
check("表头读写速率必须按全部盘求和(截的是显示行, 不是这些盘的读写)",
      _sys23["io_total"] == [28.0, 56.0]
      and round(sum(d["read_mb"] for d in _sys23["disks"]), 1) == 15.0,
      str((_sys23["io_total"], [d["read_mb"] for d in _sys23["disks"]])))
# --- 24) 检查更新(第十五轮): 上游只能有一条链, "查不到"必须说清真因 ----------------
import updates
# 24.1 llama 的上游版本以前有**两套实现**: 工具链那条(镜像 + 严格 tag 兜底)和 updates 里
#      只走直连的副本。实测副本每次 32s 后返回 (None, ""), 面板上是"无法获取上游版本(网络?)",
#      而真因是它自己把 nightly-tag 资产超时当成了整条失败。
_flr_real24, _gh_real24 = toolchain.fetch_latest_llama_release, toolchain.gh_api
_seen24 = {"flr": 0}


def _flr_boom24(repo=None, backend=None, job_log=None):
    _seen24["flr"] += 1
    raise IOError("未在 %s 找到可用的 cuda 资产(网络或仓库无预编译包)" % repo)


toolchain.fetch_latest_llama_release = _flr_boom24
_p24 = updates.llama_update_probe()
check("上游版本只能有一条链: 探针必须调工具链的 fetch_latest_llama_release",
      _seen24["flr"] == 1 and _p24["build"] is None, str(_p24))
check("上游查不到时必须把真因带回来, 不能只回 (None, '') 让上层猜网络",
      "未在 ggml-org/llama.cpp 找到可用的 cuda 资产" in _p24["error"], _p24["error"])


def _flr_ok24(repo=None, backend=None, job_log=None):
    _seen24["flr"] += 1
    return {"build": 12110, "tag": "b12110"}


toolchain.fetch_latest_llama_release = _flr_ok24
_p24b = updates.llama_update_probe()
_lt24 = updates.llama_latest_version()
check("成功路径必须回 build/tag 且 error 为空",
      _p24b["build"] == 12110 and _p24b["tag"] == "b12110" and _p24b["error"] == "", str(_p24b))
check("旧签名 llama_latest_version() 必须与探针同源(不得再自带网络路径)",
      _lt24 == (12110, "b12110"), str(_lt24))
# 判据用**模块表面**而不是源码文本: 说明性注释里提到旧名字是正常的, 但模块里再出现
# 自己的 HTTP 副本(urllib/常量/下载函数)就意味着分叉又长回来了。
check("updates 模块里不得再留第二套 GitHub 上游实现(分叉本身就是时好时坏的根因)",
      not any(hasattr(updates, n) for n in ("_GH_API", "_MIRRORS", "_http_get", "llama_download_url", "urllib")),
      str([n for n in dir(updates) if "GH" in n or "http" in n.lower() or "urllib" in n.lower()]))
toolchain.fetch_latest_llama_release = _flr_real24

# 24.2 资产端点(nightly-tag.txt 这类小文件)与 API 是**两族**, 候选/冷却要分开。
#      2026-11-05 本机实测同一个 60 字节文件: 直连 17.5s 才通 / gh-proxy 1.2s / ghfast 恒 403
#      / ghproxy.net 恒 502。旧代码把直连排第一、每候选 15s ⇒ 每次检查更新先白等 15~17s
#      (整条实测 18.7s, 抖动时 32.3s)。现在: 镜像先试、成功的粘滞、失败的冷却、整条有预算。
import urllib.error as _ue24
_url24 = "https://github.com/ggml-org/llama.cpp/releases/download/v0.9.0/nightly-tag.txt"
_c24c = toolchain._asset_candidates(_url24)
check("资产候选默认必须镜像优先、直连垫底兜底",
      _c24c[-1] == _url24 and "gh-proxy.com" in _c24c[0] and len(_c24c) == 4,
      str([u.split('/')[2] for u in _c24c]))
_hg_real24, _st_real24, _cd_real24 = toolchain.http_get, dict(toolchain._DL_STICKY), dict(toolchain._DL_COOLDOWN)
_calls24 = []


def _hg24(u, timeout=10, binary=False):
    _calls24.append(u)
    if "gh-proxy.com" in u:
        return "b12110"
    if "ghfast" in u:
        raise _ue24.HTTPError(u, 403, "Forbidden", {}, None)
    if "ghproxy.net" in u:
        raise _ue24.HTTPError(u, 502, "Bad Gateway", {}, None)
    return "b12110"          # 直连也能通, 只是要 17s


toolchain.http_get = _hg24
toolchain._DL_STICKY["prefix"] = None
toolchain._DL_COOLDOWN.clear()
_v24 = toolchain.http_get_asset(_url24, timeout=12)
check("第一次取值必须直接命中最快候选(不再为直连白等 17s)",
      _v24 == "b12110" and len(_calls24) == 1 and "gh-proxy" in _calls24[0], str(_calls24))
check("成功的候选必须成为下次的优先候选(粘滞)",
      toolchain._DL_STICKY["prefix"] == "https://gh-proxy.com/", str(toolchain._DL_STICKY))


def _hg24b(u, timeout=10, binary=False):
    _calls24.append(u)
    if u.split("/")[2] == "github.com":
        return "b12110"
    raise _ue24.HTTPError(u, 403, "Forbidden", {}, None)


toolchain.http_get = _hg24b
toolchain._DL_STICKY["prefix"] = None
toolchain._DL_COOLDOWN.clear()
_calls24.clear()
_v24b = toolchain.http_get_asset(_url24, timeout=12)
check("三个镜像都明确失败时必须仍落到直连兜底(镜像先试不等于没有兜底)",
      _v24b == "b12110" and len([u for u in _calls24
                                 if any(u.startswith(m) for m in toolchain.MIRRORS)]) == 3,
      str(_calls24))
check("明确失败(403/502)的候选必须进冷却, 下次不再重试它",
      len(toolchain._DL_COOLDOWN) == 3, str(sorted(toolchain._DL_COOLDOWN)))
_calls24.clear()
_v24c = toolchain.http_get_asset(_url24, timeout=12)
check("冷却期内不得再对失败候选发请求(此例只剩直连可试)",
      _v24c == "b12110" and len(_calls24) == 1
      and not any(_calls24[0].startswith(m) for m in toolchain.MIRRORS), str(_calls24))
for _h in ("github.com", "gh-proxy.com", "ghfast.top", "ghproxy.net"):
    toolchain._DL_COOLDOWN[_h] = time.time() + 300.0
_calls24.clear()
_err24 = None
try:
    toolchain.http_get_asset(_url24, timeout=12)
except Exception as _ex24:                                   # noqa: BLE001
    _err24 = _ex24
_calls24.clear()
_v24d = toolchain.http_get_asset(_url24, timeout=12)
check("候选全在冷却时也必须做一次探针(不能把用户挡到冷却到期), 且只发 1 个请求",
      _v24d == "b12110" and len(_calls24) == 1, str(_calls24))
_hg24all = toolchain.http_get


def _hg24dead(u, timeout=10, binary=False):      # 连探针候选也失败
    _calls24.append(u)
    raise _ue24.HTTPError(u, 403, "Forbidden", {}, None)


for _h in ("github.com", "gh-proxy.com", "ghfast.top", "ghproxy.net"):
    toolchain._DL_COOLDOWN[_h] = time.time() + 300.0
toolchain.http_get = _hg24dead
_calls24.clear()
_err24 = None
try:
    toolchain.http_get_asset(_url24, timeout=12)
except Exception as _ex24e:                                  # noqa: BLE001
    _err24 = _ex24e
toolchain.http_get = _hg24all
check("探针也失败要说'探针仍失败 + 会在 1~4 分钟自动恢复', 不能笼统推给网络",
      _err24 is not None and "探针" in str(_err24) and "自动恢复" in str(_err24)
      and len(_calls24) == 1, str(_err24))
toolchain._DL_COOLDOWN.clear()
_calls24.clear()
_budget_real24 = toolchain._DL_BUDGET
toolchain._DL_BUDGET = 0.0
_err24b = None
try:
    toolchain.http_get_asset(_url24, timeout=12)
except Exception as _ex24:                                   # noqa: BLE001
    _err24b = _ex24
check("总预算用尽要说是预算, 不能都推给'网络不好'",
      _err24b is not None and "预算" in str(_err24b) and not _calls24, str(_err24b))
toolchain._DL_BUDGET, toolchain.http_get = _budget_real24, _hg_real24
toolchain._DL_STICKY, toolchain._DL_COOLDOWN = _st_real24, _cd_real24

# 24.3 fetch_latest_llama_release 的 nightly-tag 一步: 以前它自己内联"直连先试 ×15s", 且资产
#      失败会连带跳过同一 try 里的严格 tag 扫描。现在必须走 http_get_asset, 且资产失败仍能靠
#      releases 严格扫描返回同一个 build。
def _gh24(path, timeout=10):
    if path.endswith("/releases/latest"):
        return {"tag_name": "v0.9.0", "assets": [{"name": "nightly-tag.txt", "browser_download_url": _url24}]}
    if "/releases/tags/b12110" in path:
        return {"tag_name": "b12110", "assets": [{"name": "llama-b12110-bin-ubuntu-vulkan-x64.tar.gz",
                                                  "browser_download_url": "https://x/z", "size": 10}]}
    if "releases?per_page" in path:
        return [{"tag_name": "b12110", "assets": [{"name": "llama-b12110-bin-ubuntu-vulkan-x64.tar.gz",
                                                   "browser_download_url": "https://x/z", "size": 10}]}]
    return []


toolchain.gh_api = _gh24
_gha_real24, _gha_calls24 = toolchain.http_get_asset, []


def _gha_ok24(u, timeout=12, binary=False, budget=None):
    _gha_calls24.append(u)
    return "b12110"


toolchain.http_get_asset = _gha_ok24
_rel24 = toolchain.fetch_latest_llama_release("ggml-org/llama.cpp", "vulkan", None)
check("nightly-tag 必须经 http_get_asset(镜像先试/粘滞/冷却), 不得再内联直连先试",
      _gha_calls24 == [_url24] and _rel24["build"] == 12110, str((_gha_calls24, _rel24.get("build"))))


def _gha_boom24(u, timeout=12, binary=False, budget=None):
    raise IOError("资产候选近期全部失败过, 已跳过重试(稍后自动恢复)")


toolchain.http_get_asset = _gha_boom24
_rel24b = toolchain.fetch_latest_llama_release("ggml-org/llama.cpp", "vulkan", None)
check("资产拉不动不得抹掉严格 tag 扫描兜底(仍能比出同一个 build)",
      _rel24b["build"] == 12110 and _rel24b["tag"] == "b12110", str(_rel24b))
toolchain.http_get_asset, toolchain.gh_api = _gha_real24, _gh_real24

# 24.4 DSH 侧四态分开报。本机实测 ~/.local/share/deepseek-harness 一个 remote 都没配,
#      旧代码却报"fetch 失败(直连与镜像均不可达)" ⇒ 用户反复点按钮, 看着就是功能不稳定。
import subprocess as _sp24


def _git24(cwd, *args):
    return _sp24.run(["git", "-c", "user.email=t@example.com", "-c", "user.name=t",
                      "-C", cwd] + list(args), capture_output=True, text=True)


_d24_norem = os.path.join(_tmp, "dsh_no_remote24")
_d24_det = os.path.join(_tmp, "dsh_detached24")
_d24_plain = os.path.join(_tmp, "dsh_plain24")
for _d24x in (_d24_norem, _d24_det, _d24_plain):
    os.makedirs(_d24x, exist_ok=True)
for _d24x in (_d24_norem, _d24_det):
    _git24(_d24x, "init", "-q")
    io.open(os.path.join(_d24x, "a.txt"), "w", encoding="utf-8").write("1\n")
    _git24(_d24x, "add", "a.txt")
    _git24(_d24x, "commit", "-q", "-m", "one")
_git24(_d24_det, "remote", "add", "origin", "https://github.com/example/dsh.git")
_git24(_d24_det, "checkout", "-q", "--detach")
_c24a = updates.dsh_git_state("")[0]
_c24b = updates.dsh_git_state(os.path.join(_tmp, "nope24"))[0]
_c24c2 = updates.dsh_git_state(_d24_plain)[0]
_c24d, _h24d = updates.dsh_git_state(_d24_norem)
_c24e = updates.dsh_git_state(_d24_det)[0]
check("DSH 目录未指定或不存在必须报 not_dir(不是网络问题)",
      _c24a == "not_dir" and _c24b == "not_dir", str((_c24a, _c24b)))
check("不是 git 检出必须报 not_repo", _c24c2 == "not_repo", str(_c24c2))
check("没有 origin 必须报 no_remote(本机真实形态, 以前被报成网络不可达)",
      _c24d == "no_remote", str((_c24d, _h24d[:80])))
check("no_remote 的 hint 必须给出出路(git remote add origin), 不能只说失败",
      "remote add origin" in _h24d and _d24_norem in _h24d, _h24d[:120])
check("detached HEAD 必须报 detached", _c24e == "detached", str(_c24e))
_r24 = updates.dsh_check_update(_d24_norem)
check("dsh_check_update 对 no_remote 必须带 cause/hint 且不发任何网络请求",
      _r24.get("cause") == "no_remote" and bool(_r24.get("hint")), str(_r24)[:160])
_ok24, _msg24 = updates.dsh_pull(_d24_norem)
check("点更新同样不能把'没配 origin'说成'直连与镜像拉取均失败'",
      _ok24 is False and "没有配置 git 远端" in _msg24
      and "直连与镜像拉取均失败" not in _msg24, _msg24[:160])

# 24.5 GitHub API 的候选粘滞必须按**镜像前缀**跨 path 生效: 一次检查要发 2~3 个不同 path
#      的请求, 按完整 URL 粘滞等于每条 path 各自重新从直连开始(实测第二次检查仍要 7.6s)。
_ghs_real24, _ghc_real24 = dict(toolchain._GH_STICKY), dict(toolchain._GH_COOLDOWN)
_api_calls24 = []
_api_bad24 = {"all": False}


def _hg24c(u, timeout=10, binary=False):
    _api_calls24.append(u)
    if _api_bad24["all"]:
        raise _ue24.HTTPError(u, 403, "Forbidden", {}, None)
    if u.startswith("https://api.github.com"):
        raise _ue24.HTTPError(u, 404, "Not Found", {}, None)   # 本轮实测: 直连偶发 404/慢
    if "gh-proxy.com" in u:
        return json.dumps({"tag_name": "ok"})
    raise _ue24.HTTPError(u, 403, "Forbidden", {}, None)


toolchain.http_get = _hg24c
toolchain._GH_STICKY["prefix"] = None
toolchain._GH_COOLDOWN.clear()
toolchain.gh_api("/repos/a/b/releases/latest")
_api_calls24.clear()
toolchain.gh_api("/repos/a/b/releases/tags/b12110")
check("另一条 path 的请求必须优先用上一条成功的那个候选(粘滞按前缀不按完整 URL)",
      len(_api_calls24) == 1 and _api_calls24[0].startswith("https://gh-proxy.com/"),
      str(_api_calls24))
_c24d = toolchain._prefixed_candidates("https://api.github.com/x", {"prefix": None}, True)
check("API 族候选默认必须直连优先(镜像会缓存: 实测 gh-proxy 给 b12110 而直连已是 b12128, "
      "同一按钮两个版本号也是不稳定)",
      _c24d[0] == "https://api.github.com/x" and len(_c24d) == 4, str(_c24d[:2]))
_c24e2 = toolchain._prefixed_candidates("https://github.com/a/b", {"prefix": None}, False)
check("资产族候选默认必须镜像优先、直连垫底(直连要 17.5s, 镜像 1.2s)",
      _c24e2[0].startswith("https://gh-proxy.com/") and _c24e2[-1] == "https://github.com/a/b",
      str(_c24e2[:2]))
toolchain._GH_STICKY["prefix"] = None
toolchain._GH_COOLDOWN.clear()
_api_calls24.clear()
_api_bad24["all"] = True
_err24c = None
try:
    toolchain.gh_api("/repos/a/b/releases/latest")
except Exception as _ex24c:                                  # noqa: BLE001
    _err24c = _ex24c
check("全候选 403 时每个候选只发一次(4xx 不原地重试)且都进冷却",
      _err24c is not None and len(_api_calls24) == 4 and len(toolchain._GH_COOLDOWN) == 4,
      str((len(_api_calls24), sorted(toolchain._GH_COOLDOWN))))
_api_calls24.clear()
_err24d = None
try:
    toolchain.gh_api("/repos/a/b/releases/tags/b12110")
except Exception as _ex24d:                                  # noqa: BLE001
    _err24d = _ex24d
check("候选全在冷却时也要探针一次而不是直接拒绝; 探针仍失败要说会自行恢复",
      _err24d is not None and "探针" in str(_err24d) and len(_api_calls24) == 1,
      str((_err24d, _api_calls24)))
_now24 = time.time()
check("端点级 4xx 不得把候选长冷却(实测 gh-proxy 有的 path 通、有的 path 给 404; "
      "按 15 分钟挡会把 4 个候选全挡掉, 反而做成新的时好时坏)",
      bool(toolchain._GH_COOLDOWN)
      and all(v - _now24 <= 60.0 for v in toolchain._GH_COOLDOWN.values()),
      str([round(v - _now24) for v in sorted(toolchain._GH_COOLDOWN.values())]))
_api_bad24["all"] = False
toolchain.http_get = _hg_real24
toolchain._GH_STICKY, toolchain._GH_COOLDOWN = _ghs_real24, _ghc_real24
# 24.6 整条上游查询必须共享一个网络截止时间: 每个请求各有一档预算时**总时长没人管**
#      (实测同一个按钮 56.3s / 9.9s / 7.9s / 10.6s, 用户只会说"始终不稳定")。
import socket as _so24
_api_calls24.clear()
_slow24 = {"n": 0}


def _hg24slow(u, timeout=10, binary=False):
    _slow24["n"] += 1
    time.sleep(0.4)
    raise _so24.timeout("每个候选都要 0.4s 以上")


toolchain.http_get = _hg24slow
_budget_real24b = toolchain.CHECK_NET_BUDGET
toolchain.CHECK_NET_BUDGET = 1.2
toolchain._GH_STICKY["prefix"] = None
toolchain._GH_COOLDOWN.clear()
toolchain.begin_net_budget()
_err24e = None
try:
    toolchain.gh_api("/repos/a/b/releases/latest")
except Exception as _ex24e2:                                 # noqa: BLE001
    _err24e = _ex24e2
_n1 = len(_api_calls24)
_api_calls24.clear()
_err24f = None
try:
    toolchain.gh_api("/repos/a/b/releases/tags/b12110")
except Exception as _ex24f:                                  # noqa: BLE001
    _err24f = _ex24f2 = _ex24f
check("一次上游查询的总时长必须受共享预算约束(而不是每条 path 各排一轮候选)",
      _err24e is not None and "预算" in str(_err24e) and _n1 <= 3, str((_n1, str(_err24e)[:70])))
check("预算用尽后同一链的后续请求必须立刻失败并说明原因(不得重新排队候选)",
      _err24f is not None and "预算" in str(_err24f) and not _api_calls24, str(_api_calls24))
toolchain.CHECK_NET_BUDGET = _budget_real24b
toolchain.end_net_budget()
toolchain.http_get = _hg24c        # 对照仍走替身, 自检不得真发网络请求
_api_calls24.clear()
toolchain._GH_COOLDOWN.clear()
_ok24f = toolchain.gh_api("/repos/a/b/releases/latest")
check("对照: 预算窗口结束后同一候选链必须恢复正常(不得把一次失败变成永久)",
      _ok24f.get("tag_name") == "ok" and len(_api_calls24) == 2, str(_api_calls24))

# 24.7 前端不得比服务端先放弃: api() 默认 12s 中止, 而一次上游查询的合法预算约 22s
#      (活体实测点一次 llama 检查更新, 页面只留下 "signal timed out" 这四个字)。
_app24 = io.open(os.path.join(APP, "webui", "app.js"), encoding="utf-8").read()
check("检查更新两个入口的前端超时必须与服务端预算同量级(不是默认 12s)",
      'api("toolchains/check_update", {}, 30000)' in _app24
      and 'api("update/check", { target: "dsh", dir }, 30000)' in _app24,
      str([l.strip() for l in _app24.splitlines() if "30000" in l][:2]))
_sig24 = [l.strip() for l in _app24.splitlines() if "signal timed out" in l]
check("浏览器中止必须翻译成人话(请求名 + 秒数), 原文案只允许出现在注释里",
      "TimeoutError" in _app24 and "未完成" in _app24
      and all(l.startswith("//") for l in _sig24), str(_sig24[:2]))

# --- 17) 自检自身的边界: 不得污染用户真实的日志目录 ---------------------------
# 17.1 以前 selftest 直接往 ~/.dsh_launcher_logs 写: 每跑一次就多出几条测试噪声
#      ("资产名形态非法: ../evil.tar.gz"、"摘要与直连不一致"), 用户排障时会把它们当成
#      真事故, 同时真事故的密度被稀释。今天(2026-11-05)的四次自检运行在 error.log 里
#      留下了 8 行这样的内容, 由 grep 直接取证。
_mk17 = "SELFTEST-MARKER-%d" % os.getpid()
_applog_top.run(_mk17)
_applog_top.error(_mk17)
_real_txt17 = ""
if os.path.isdir(_REAL_LOG_DIR):
    for _f17 in sorted(os.listdir(_REAL_LOG_DIR)):
        try:
            with open(os.path.join(_REAL_LOG_DIR, _f17), encoding="utf-8", errors="replace") as _fh17:
                _real_txt17 += _fh17.read()
        except OSError:
            pass
check("自检的日志必须落在自己的临时目录, ~/.dsh_launcher_logs 里不得出现自检标记",
      _applog_top.LOG_DIR != _REAL_LOG_DIR
      and _applog_top.LOG_DIR.startswith(_SELFTEST_HOME)
      and _mk17 in open(os.path.join(_applog_top.LOG_DIR, "run.log"), encoding="utf-8").read()
      and _mk17 not in _real_txt17,
      "%s | 真目录里命中=%s" % (_applog_top.LOG_DIR, _mk17 in _real_txt17))
check("core/applog/main/toolchain 必须共用同一份 LOG_DIR(改一处仍会写真目录)",
      core.applog.LOG_DIR == main.applog.LOG_DIR == toolchain.applog.LOG_DIR == _applog_top.LOG_DIR,
      str((core.applog.LOG_DIR, main.applog.LOG_DIR, toolchain.applog.LOG_DIR)))
# 23.6 磁盘行是后台首刷的, 缓存冷的时候 sample_system 拿到空列表: 以前面板把"还没采到"
#      直接显示成"没有盘"(实测: 服务启动后第一次 /api/monitor 是 0 行, 第二次才有 4 行)。
_dc23, _real_dc23 = dict(monitor._disk_rows), monitor._disk_rows_cached
monitor._disk_rows.update(rows=[], at=0.0, busy=False)
monitor._disk_rows_cached = lambda: []
monitor._sys_cache.update(t=0.0, v=None)
_cold23 = monitor.sample_system()
check("冷启动首帧不得把\"还没采到\"当成\"没有盘\"(payload 要带 disks_pending)",
      _cold23["disks"] == [] and _cold23["disks_pending"] is True,
      str((len(_cold23["disks"]), _cold23.get("disks_pending"))))
monitor._disk_rows_cached = _real_dc23               # 换回真接缝, 否则 prewarm 踢的是假函数
monitor.prewarm_disks()                              # 服务启动时踢的那一次首刷
for _ in range(60):
    if monitor._disk_rows["rows"]:
        break
    time.sleep(0.05)
monitor._sys_cache.update(t=0.0, v=None)
_warm23 = monitor.sample_system()
monitor._disk_rows.update(**_dc23)
monitor._sys_cache.update(t=0.0, v=None)
check("对照: 首刷回来后必须真有行且 disks_pending 变 False(不得卡在\"采不到\")",
      len(_warm23["disks"]) >= 1 and _warm23["disks_pending"] is False,
      str((len(_warm23["disks"]), _warm23.get("disks_pending"))))



# --------------------------------- 25. 兼容修复不毁配置 / Node 环境(第十六轮) --
# 这一族最坏的失败不是"修不好", 而是"把有效配置改坏了、还报成功": dump-config 探针没查到
# 东西时, 一键修复会把 cordis.patch.yml 里**每条有效**补丁注释掉。断言全部真文件真跑, 且
# 每条守卫都配反向对照(证据在 → 必须改; 证据缺 → 必须一动不动), 免得守卫变成"一律不修"。
import hashlib as _hashlib25        # noqa: E402
import subprocess as _sp25          # noqa: E402
import tarfile as _tf25             # noqa: E402

_prof25 = os.path.join(_tmp, "prof25")
_dshome25 = os.path.join(_tmp, "dshome25")
_dshdir25 = os.path.join(_tmp, "dshdir25")
os.makedirs(os.path.join(_prof25, ".dsh-market"))
os.makedirs(os.path.join(_dshome25, "packages"))
os.makedirs(os.path.join(_dshdir25, "apps", "cli", "lib"))
io.open(os.path.join(_dshdir25, "apps", "cli", "lib", "bin.js"), "w", encoding="utf-8").write("// stub\n")
_PATCH25 = os.path.join(_prof25, "cordis.patch.yml")
_PATCH_SRC25 = "patch:\n  - id: keep-me\n    computeMs: 500\n  - id: second\n    enabled: true\n"
io.open(_PATCH25, "w", encoding="utf-8").write(_PATCH_SRC25)
_MANI25 = os.path.join(_prof25, "package.json")
_real_pdir25, _real_dshome25 = _compat.profile_dir, _compat.dsh_home
_compat.profile_dir = lambda p="web": _prof25
_compat.dsh_home = lambda: _dshome25


def _write_mani25(bundles, extra=None):
    doc = {"dsh": {"profile": {"bundles": list(bundles)}}, "keepMe": {"x": 1}}
    if extra:
        doc.update(extra)
    with io.open(_MANI25, "w", encoding="utf-8") as fh:
        json.dump(doc, fh)


def _mani25():
    with io.open(_MANI25, encoding="utf-8") as fh:
        return json.load(fh)


def _patch25():
    with io.open(_PATCH25, encoding="utf-8") as fh:
        return fh.read()


check("25 夹具: profile/dshHome 必须被改到临时区(否则整节会写用户真 profile)",
      _compat.profile_dir("web") == _prof25 and _compat.pinned_plugins_path().startswith(_dshome25),
      _compat.pinned_plugins_path())

# 25.1 dump-config 退出码 0 但输出不是 JSON: 必须算"探针失败", 不能交出一个空 entry 集合
_real_run_c25 = _compat.subprocess.run


def _fake_run_stdout(payload):
    _compat.subprocess.run = lambda *a, **k: types.SimpleNamespace(
        returncode=0, stdout=payload, stderr="")


_fake_run_stdout("DSH 0.9.1\nconfig written to /home/u/.dsh\n")
_ids25, _err25 = _compat._dump_config_entries(_dshdir25)
_compat.subprocess.run = _real_run_c25
check("dump-config 退出码 0 但输出不是 JSON 必须判探针失败(不得交出空 entry 集合)",
      _ids25 is None and "JSON" in (_err25 or ""), str((_ids25, (_err25 or "")[:70])))

_fake_run_stdout('banner line\n{"config": {"patch": {"entries": [{"id": "keep-me"}, '
                 '{"id": "other"}]}}}\ndone line\n')
_ids25b, _err25b = _compat._dump_config_entries(_dshdir25)
_compat.subprocess.run = _real_run_c25
check("对照: JSON 前后夹 banner 时必须仍能解析出 entry(不得一律判失败)",
      _ids25b == {"keep-me", "other"}, str((_ids25b, _err25b)))


def _audit25(stdout_payload):
    _fake_run_stdout(stdout_payload)
    try:
        return _compat.audit(_dshdir25, lambda _t: None)
    finally:
        _compat.subprocess.run = _real_run_c25


# 25.2 转储查到 0 个 entry 而补丁文件有 N 条: 按"未能核对"处理, 绝不判失效条目
_write_mani25(["core"])
_rep25b = _audit25('{"config": {"patch": {"entries": []}}}')
_kinds25b = [x["kind"] for x in _rep25b["fixables"]]
check("转储 0 个 entry 而补丁文件有 N 条时不得判失效条目(修前: 每条补丁都成 orphan, 一键修复真去注释)",
      "patch_orphan" not in _kinds25b and "patch_rename" not in _kinds25b, str(_kinds25b))
check("同一情况必须明说\"本次不判定失效条目\"并计入未能核对(不得报成一切正常)",
      any("不判定失效条目" in x["text"] for x in _rep25b["items"]),
      str([x["text"][:40] for x in _rep25b["items"]][:6]))
_rep25c = _audit25('{"config": {"patch": {"entries": [{"id": "unrelated-one"}, '
                   '{"id": "unrelated-two"}]}}}')
_orph25 = [x for x in _rep25c["fixables"] if x["kind"] == "patch_orphan"]
check("对照: 转储确实查到 entry 而补丁不在其中时仍要判失效条目, 且带上 entry 数作证据",
      len(_orph25) == 2 and all(x.get("dump_count") == 2 for x in _orph25), str(_orph25))

# 25.3 repair 改写补丁条目的门槛: 没有 entry 证据 / 备份失败 → 一个字都不改
_real_audit25 = _compat.audit


def _repair25(fixables, log):
    _compat.audit = lambda dsh_dir, logf: {"items": [], "fixables": fixables, "version": ""}
    try:
        return _compat.repair(_dshdir25, {}, log.append)
    finally:
        _compat.audit = _real_audit25


_log25 = []
_n25 = _repair25([{"kind": "patch_orphan", "old": "keep-me", "dump_count": 0}], _log25)
check("没有 entry 证据时不得注释用户补丁(修前: 依据空转储把有效补丁关掉)",
      _n25 == 0 and _patch25() == _PATCH_SRC25 and any("没有查到 entry" in t for t in _log25),
      str((_n25, _patch25()[:50])))
_log25b = []
_n25b = _repair25([{"kind": "patch_orphan", "old": "keep-me", "dump_count": 2}], _log25b)
_t25b = _patch25()
check("对照: 有 entry 证据时失效条目仍要注释掉并留备份(守卫不得变成一律不修)",
      _n25b == 1 and "# [compat] 已失效条目,自动注释:" in _t25b
      and any(n.startswith("cordis.patch.yml.bak-compat-") for n in os.listdir(_prof25)),
      str((_n25b, _t25b[:60])))
io.open(_PATCH25, "w", encoding="utf-8").write(_PATCH_SRC25)
_real_bak25 = _compat._backup
_compat._backup = lambda p: None
_log25c = []
_n25c = _repair25([{"kind": "patch_orphan", "old": "keep-me", "dump_count": 2}], _log25c)
check("备份失败时不得改写补丁文件(修前: 不查 _backup 返回值直接写)",
      _n25c == 0 and _patch25() == _PATCH_SRC25 and any("备份失败" in t for t in _log25c),
      str((_n25c, _patch25()[:50])))
_compat._backup = _real_bak25

# 25.3b 本机真实形态: cordis.patch.yml 顶层是数组(文件头注释原文如此), 条目里还嵌着别的 id
_REAL_PATCH25 = (
    "# a top-level YAML array of loader patch entries\n"
    "- id: ui-settings-general\n  name: \"@x/ui\"\n  config:\n    welcomeNoticeVersion: 2026-09-28.1\n"
    "- id: llm-pi-ai\n  config:\n    models:\n      - id: iq3_s\n        contextWindow: 128000\n"
    "  name: \"@x/llm\"\n")
io.open(_PATCH25, "w", encoding="utf-8").write(_REAL_PATCH25)
_ids25p, _why25p = _compat._parse_patch_entries(_PATCH25)
check("顶层数组形态必须被认出来(修前对真实文件报\"读不动(顶层不是映射)\", 补丁审计整段没跑)",
      _why25p == "" and _ids25p == ["ui-settings-general", "llm-pi-ai"], str((_ids25p, _why25p[:40])))
check("条目内嵌的 id(config.models 里的 iq3_s)不得当成补丁条目(否则用户模型条目被判失效并注释掉)",
      "iq3_s" not in _ids25p, str(_ids25p))
_rep25r = _audit25('{"config": {"patch": {"entries": [{"id": "ui-settings-general"}, '
                   '{"id": "other-live"}]}}}')
_orph_r25 = [x for x in _rep25r["fixables"] if x["kind"] == "patch_orphan"]
check("数组形态下的失效判定仍只看条目层: 恰好 llm-pi-ai 一条",
      len(_orph_r25) == 1 and _orph_r25[0]["old"] == "llm-pi-ai" and _orph_r25[0]["dump_count"] == 2,
      str(_orph_r25))
_log25r = []
_n25r = _repair25(_orph_r25, _log25r)
_after25r = _patch25()
import yaml as _yaml25
try:
    _doc25r = _yaml25.safe_load(_after25r)
except Exception as _ex25r:
    _doc25r = "EXC:%s" % _ex25r
_ids_left25 = [e.get("id") for e in _doc25r if isinstance(e, dict)] if isinstance(_doc25r, list) else _doc25r
_uncmt25 = [ln for ln in _after25r.splitlines() if ln.strip() and not ln.lstrip().startswith("#")]
_kept25 = _doc25r[0] if isinstance(_doc25r, list) and _doc25r else {}
check("注释失效条目必须连它的续行一起注释(修前只注释 - id 一行, 续行留在原地被上一条目吸收 = 配置搬家)",
      _n25r == 1 and _ids_left25 == ["ui-settings-general"]
      and _kept25.get("config") == {"welcomeNoticeVersion": "2026-09-28.1"}
      and not any(("llm" in ln) or ("iq3_s" in ln) for ln in _uncmt25),
      str((_ids_left25, _kept25.get("config"), [ln[:30] for ln in _uncmt25])))
io.open(_PATCH25, "w", encoding="utf-8").write(_PATCH_SRC25)

# 25.4 market_disabled: 备份失败/读不动都不能算"已清空"
_ST25 = os.path.join(_prof25, ".dsh-market", "state.json")


def _state25(doc):
    with io.open(_ST25, "w", encoding="utf-8") as fh:
        json.dump(doc, fh)


def _state25_read():
    with io.open(_ST25, encoding="utf-8") as fh:
        return fh.read()


_state25({"disabled": ["a", "b"], "keepMe": {"x": 1}})
_compat._backup = lambda p: None
_log25d = []
_n25d = _repair25([{"kind": "market_disabled", "path": _ST25, "count": 2}], _log25d)
_compat._backup = _real_bak25
check("备份失败不得清空 dsh-market disabled 还计成已修复",
      _n25d == 0 and json.loads(_state25_read())["disabled"] == ["a", "b"]
      and any("备份失败" in t for t in _log25d), str((_n25d, _state25_read()[:50])))
io.open(_ST25, "w", encoding="utf-8").write("{ disabled broken json <<")
_log25e = []
_n25e = _repair25([{"kind": "market_disabled", "path": _ST25, "count": 1}], _log25e)
check("读不动的 state.json 不得被重写成骨架(修前: json.load 抛错前已备份但照样 dump 出去)",
      _n25e == 0 and _state25_read().startswith("{ disabled broken json")
      and any("读不动" in t for t in _log25e), str((_n25e, _state25_read()[:40])))
_state25({"disabled": ["a", "b"], "keepMe": {"x": 1}})
_log25f = []
_n25f = _repair25([{"kind": "market_disabled", "path": _ST25, "count": 2}], _log25f)
_st25f = json.loads(_state25_read())
check("对照: 正常时必须清空 disabled 并保留其它键(不得只写一个 disabled)",
      _n25f == 1 and _st25f["disabled"] == [] and _st25f.get("keepMe") == {"x": 1}, str(_st25f))

# 25.5 pinned_writeback: set_bundle_active 返回 (False, …) 时不算已修复
_real_sba25 = _compat.set_bundle_active
_compat.set_bundle_active = lambda p, n, a: (False, "已有插件操作正在进行中")
_log25g = []
_n25g = _repair25([{"kind": "pinned_writeback", "name": "dsh-x"}], _log25g)
check("停用插件返回失败时不得计数为已修复(修前: 不看返回值就 fixed += 1, 复检才发现还在)",
      _n25g == 0 and any("未能再次移除" in t for t in _log25g), str((_n25g, _log25g)))
_compat.set_bundle_active = lambda p, n, a: (True, "")
_log25h = []
_n25h = _repair25([{"kind": "pinned_writeback", "name": "dsh-x"}], _log25h)
_compat.set_bundle_active = _real_sba25
check("对照: 停用成功时仍要计数(守卫不得变成一律不修)", _n25h == 1, str(_n25h))

# 25.6 enforce_pinned 必须与装/卸/激活共用 profile 锁: 它改写的是同一份 package.json
_imb25base = len(_compat.op_imbalance())     # 前面几节故意造过失衡, 只判本节新增的
_write_mani25(["dsh-locked", "dsh-keep"])
_compat.pin_inactive("web", "dsh-locked")
_log25i = []
_holder_done = [False]


def _hold25():
    if _compat.begin_profile_op("web", "安装插件 dsh-y"):
        _holder_done[0] = True
        time.sleep(0.8)
        _compat.end_profile_op("web")


_th25 = threading.Thread(target=_hold25)
_th25.start()
time.sleep(0.2)
_wb25 = _compat.enforce_pinned("web", _log25i.append)
_th25.join()
check("有其它插件操作持锁时不得改写 package.json(修前: 写回本次读到的旧文档, 抹掉并发安装的依赖)",
      _holder_done[0] and _wb25 == [] and "dsh-locked" in _mani25()["dsh"]["profile"]["bundles"]
      and any("有其它插件操作" in t for t in _log25i), str((_wb25, _mani25())))
_wb25b = _compat.enforce_pinned("web", _log25i.append)
_mani25b = _mani25()
check("对照: 无人持锁时锁定回写仍要执行, 且只动被锁定的那一个(其它声明必须保住)",
      _wb25b == ["dsh-locked"] and _mani25b["dsh"]["profile"]["bundles"] == ["dsh-keep"]
      and _mani25b.get("keepMe") == {"x": 1}, str((_wb25b, _mani25b)))
_write_mani25(["dsh-locked", "dsh-keep"])
_compat._backup = lambda p: None
_wb25c = _compat.enforce_pinned("web", _log25i.append)
_compat._backup = _real_bak25
check("备份失败时不得移除锁定包(且不能报成已移除)",
      _wb25c == [] and "dsh-locked" in _mani25()["dsh"]["profile"]["bundles"], str(_wb25c))
check("begin/end 必须成对: enforce_pinned 跑完后不得新增在途登记或锁失衡",
      _compat.inflight_ops() in ([], {}) and len(_compat.op_imbalance()) == _imb25base,
      str((_compat.inflight_ops(), _compat.op_imbalance()[_imb25base:])))

# 25.7 装插件失败的回滚: pnpm 新建的 lock 必须一起删掉(修前那段挂在 return 之后, 是死码)
_tgz25 = os.path.join(_tmp, "dsh-demo-1.2.3.tgz")
_pkgdir25 = os.path.join(_tmp, "pkg25")
os.makedirs(_pkgdir25)
with io.open(os.path.join(_pkgdir25, "package.json"), "w", encoding="utf-8") as fh:
    json.dump({"name": "dsh-demo", "version": "1.2.3"}, fh)
with _tf25.open(_tgz25, "w:gz") as _tfo25:
    _tfo25.add(os.path.join(_pkgdir25, "package.json"), arcname="package/package.json")
_real_pnpmwhich25 = _compat.procman_which_pnpm
_compat.procman_which_pnpm = lambda: "/usr/bin/pnpm"
_write_mani25(["core"], extra={"dependencies": {"old-dep": "1.0.0"}})
_lock25 = os.path.join(_prof25, "pnpm-lock.yaml")


def _add_fail25(base, spec):
    with io.open(os.path.join(base, "pnpm-lock.yaml"), "w", encoding="utf-8") as fh:
        fh.write("lockName: new-from-pnpm\n")
    return False, "模拟: pnpm add 失败(缺对等依赖)"


_real_addspec25 = _compat._pnpm_add_spec
_compat._pnpm_add_spec = _add_fail25
_log25j = []
_ok25j, _msg25j = _compat.install_local_plugin("web", _tgz25, _log25j.append, _dshdir25)
check("装失败必须回滚 package.json 并删掉 pnpm 新建的 lock(修前: 留下一份孤儿 lock 还说\"已回滚\")",
      _ok25j is False and not os.path.isfile(_lock25)
      and _mani25()["dependencies"] == {"old-dep": "1.0.0"},
      str([(_msg25j or "")[:120], os.path.isfile(_lock25)]))
with io.open(_lock25, "w", encoding="utf-8") as fh:
    fh.write("lockName: pre-existing\n")
_log25k = []
_ok25k, _msg25k = _compat.install_local_plugin("web", _tgz25, _log25k.append, _dshdir25)
_lock_txt25 = io.open(_lock25, encoding="utf-8").read().strip() if os.path.isfile(_lock25) else "(没了)"
check("原本就有 lock 时必须写回原内容而不是删掉(删掉等于抹掉用户的既有解析结果)",
      _lock_txt25 == "lockName: pre-existing", str([(_msg25k or "")[:100], _lock_txt25]))
if os.path.isfile(_lock25):
    os.remove(_lock25)              # 本次要测的是"原本没有 lock, pnpm 现场新建了一份"
_real_osremove25 = _compat.os.remove


def _guard_remove25(path, *a, **k):
    if str(path).endswith("pnpm-lock.yaml"):
        raise OSError(13, "Permission denied")
    return _real_osremove25(path, *a, **k)


_compat.os.remove = _guard_remove25
_log25l = []
_ok25l, _msg25l = _compat.install_local_plugin("web", _tgz25, _log25l.append, _dshdir25)
_compat.os.remove = _real_osremove25
check("lock 删不掉时必须说出来(不能只报\"已回滚 package.json/lock\"就完事)",
      _ok25l is False and os.path.isfile(_lock25) and "孤儿 lock" in (_msg25l or ""),
      str((_msg25l or "")[:160]))
os.remove(_lock25)
_compat._pnpm_add_spec = _real_addspec25
_compat.procman_which_pnpm = _real_pnpmwhich25

# 25.7b 一次上游查询的预算必须在自己结束时关掉(线程局部, 留着就是下一段的"突然预算用尽")
_rel25 = {"tag_name": "b12110", "assets": [
    {"name": "llama-b12110-bin-ubuntu-vulkan-x64.tar.gz", "size": 100,
     "browser_download_url": "https://github.com/ggml-org/llama.cpp/releases/download/b12110/"
                             "llama-b12110-bin-ubuntu-vulkan-x64.tar.gz", "digest": "sha256:" + "a" * 64}]}
_real_hg25 = toolchain.http_get
toolchain.http_get = lambda url, timeout=10, binary=False: json.dumps(_rel25)
try:
    _rel25r = toolchain.fetch_latest_llama_release("ggml-org/llama.cpp", "vulkan", None)
    _left25 = toolchain._deadline_for(25.0) - time.time()
finally:
    toolchain.http_get = _real_hg25
check("查询结束必须关掉线程内预算: 下一段网络动作要能用自己完整的窗口(而不是继承旧截止)",
      _rel25r["build"] == 12110 and _left25 > 24.0 and not toolchain._budget_out(),
      str((_rel25r["build"], round(_left25, 2), toolchain._budget_out())))

# 25.8 Node 环境: 版本管理器目录层级、版本比较、校验表次序、失败不许报成功
_home25 = os.path.join(_tmp, "home25")
_lbin25 = os.path.join(_home25, ".local", "bin")
_llib25 = os.path.join(_home25, ".local", "lib")
os.makedirs(_lbin25)
_real_home_env25 = os.environ.get("HOME")
_real_lbin25, _real_llib25 = nodetool.LOCAL_BIN, nodetool.LOCAL_LIB
_real_pnpm_path25, _real_detect25 = nodetool._pnpm_path, nodetool.detect
_real_v24_25, _real_up25 = nodetool.latest_v24, nodetool.urllib.request.urlopen
_real_cands25 = nodetool._node_candidates
_real_which25 = nodetool.procman.which
nodetool.procman.which = lambda name: None   # 真机 PATH 里的 node/pnpm 不能混进本节候选
nodetool.LOCAL_BIN, nodetool.LOCAL_LIB = _lbin25, _llib25
os.environ["HOME"] = _home25


def _mk_exe25(path, out):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with io.open(path, "w", encoding="utf-8") as fh:
        fh.write("#!/bin/sh\n" + (out.strip() and 'echo %s' % out.strip() or "exit 0") + "\n")
    os.chmod(path, 0o755)


_pnpm25 = os.path.join(_lbin25, "pnpm")
_mk_exe25(_pnpm25, "9.9.9")
nodetool._pnpm_path = lambda: _pnpm25
_nvm_old25 = os.path.join(_home25, ".nvm", "versions", "node", "v24.9.9", "bin", "node")
_nvm_new25 = os.path.join(_home25, ".nvm", "versions", "node", "v24.10.3", "bin", "node")
_fnm25 = os.path.join(_home25, ".local", "share", "fnm", "node-versions", "v24.11.1",
                      "installation", "bin", "node")
for _p25, _v25 in ((_nvm_old25, "v24.9.9"), (_nvm_new25, "v24.10.3"), (_fnm25, "v24.11.1")):
    _mk_exe25(_p25, _v25)
_c25 = nodetool._node_candidates()
check("nvm/fnm 的真实三层布局必须被认出来(修前层级错位: 用 nvm 的机器永远显示未装 Node)",
      _nvm_old25 in _c25 and _nvm_new25 in _c25 and _fnm25 in _c25,
      str([c for c in _c25 if "nvm" in c or "fnm" in c]))
_st25d = nodetool.detect(force=True)
check("多个 24+ 候选时必须取版本最高的(修前取候选表最前的: 面板报旧版本, 一键安装每次判\"不是最新\")",
      _st25d["node24"]["version"] == "v24.11.1" and _st25d["meets"] is True, str(_st25d["node24"]))
check("版本比较必须按段做整数比(修前字符串比会判 v24.10.0 < v24.9.9, 永远收敛不了)",
      nodetool._vkey("v24.10.0") > nodetool._vkey("v24.9.9") and "v24.10.0" < "v24.9.9",
      str((nodetool._vkey("v24.10.0"), nodetool._vkey("v24.9.9"))))


class _Resp25:
    def __init__(self, data):
        self._d, self._pos = data, 0

    def read(self, n=-1):
        if self._pos >= len(self._d):
            return b""
        step = n if (n and n > 0) else len(self._d)
        chunk = self._d[self._pos:self._pos + step]
        self._pos += len(chunk)
        return chunk

    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False


_opened25 = []


def _net_fail25(req, timeout=30, *a, **k):
    _opened25.append(getattr(req, "full_url", str(req)))
    raise IOError("不该联网")


def _st_stub25(node_v, node_path, unknown=False):
    def _f(force=False):
        return {"node24": {"path": node_path, "version": node_v},
                "any_node": {"path": node_path, "version": node_v},
                "pnpm": {"path": _pnpm25, "version": "9.9.9"},
                "meets": bool(node_v), "version_unknown": unknown}
    return _f


# 本机比上游最新 v24 更新: 不得降级(修前用 cur_v != latest_v, 装了 v25 会被降级成 v24)
nodetool.urllib.request.urlopen = _net_fail25
nodetool.latest_v24 = lambda: ("https://registry.npmmirror.com/-/binary/node", "v24.9.9")
nodetool.detect = _st_stub25("v25.1.0", _nvm_new25)
_log25n = []
_ok25n, _msg25n = nodetool.install_node_env(_log25n.append)
check("本机比上游最新 v24 更新时不得降级重装, 且要说明为什么不装",
      not _opened25 and _ok25n is True and any("不降级" in t for t in _log25n),
      str((list(_opened25), _ok25n, _msg25n)))
# 需要装但校验表取不到: 一个 tarball 都不许下(修前每个镜像各下 ~50MB 才发现取不到表)
_opened25.clear()
nodetool.latest_v24 = lambda: ("https://nodejs.org/dist", "v24.10.3")
nodetool.detect = _st_stub25("v24.9.9", _nvm_old25)
_log25s = []
_ok25s, _msg25s = nodetool.install_node_env(_log25s.append)
check("SHA-256 校验表取不到时必须直接失败且完全不下载(修前: 先下完再说, 两源各白下一遍)",
      _ok25s is False and not any(".tar.xz" in u for u in _opened25)
      and all("SHASUMS256.txt" in u for u in _opened25) and "取不到" in (_msg25s or ""),
      str((list(_opened25), (_msg25s or "")[:80])))
# 真下载真校验真解压: 校验表只取一次, 两个镜像共用同一个期望哈希
shutil.rmtree(os.path.join(_home25, ".nvm"), ignore_errors=True)
shutil.rmtree(os.path.join(_home25, ".local", "share", "fnm"), ignore_errors=True)
_nodepkg25 = os.path.join(_tmp, "nodepkg25", "node-v24.10.3-linux-x64", "bin")
os.makedirs(_nodepkg25)
_mk_exe25(os.path.join(_nodepkg25, "node"), "v24.10.3")
_targz25 = os.path.join(_tmp, "node-v24.10.3-linux-x64.tar.xz")
with _tf25.open(_targz25, "w:xz") as _tfo25:
    _tfo25.add(os.path.join(_nodepkg25, "node"),
               arcname="node-v24.10.3-linux-x64/bin/node")
_tbytes25 = open(_targz25, "rb").read()
_sha_good25 = _hashlib25.sha256(_tbytes25).hexdigest()
_table25 = ("%s  node-v24.10.3-linux-x64.tar.xz\n" % _sha_good25).encode()
_after_dl25 = [False]


def _net_ok25(req, timeout=30, *a, **k):
    u = getattr(req, "full_url", str(req))
    _opened25.append(u)
    if "SHASUMS256.txt" in u:
        return _Resp25(_table25)
    if u.endswith("node-v24.10.3-linux-x64.tar.xz"):
        _after_dl25[0] = True          # 装完之后要让 detect 走真检测
        return _Resp25(_tbytes25)
    raise IOError("不该请求 %s" % u)


def _det25(force=False):
    return _real_detect25(force=True) if _after_dl25[0] else _st_stub25("v24.9.9", None)(force)


_opened25.clear()
nodetool.urllib.request.urlopen = _net_ok25
nodetool.latest_v24 = lambda: ("https://registry.npmmirror.com/-/binary/node", "v24.10.3")
nodetool.detect = _det25
_log25t = []
_ok25t, _msg25t = nodetool.install_node_env(_log25t.append)
check("一键安装必须真跑通: 校验通过后解压、软链进 ~/.local/bin, 结尾报的版本号是真检测出来的",
      _ok25t is True and _msg25t == "Node v24.10.3 + pnpm 9.9.9 就绪"
      and os.path.islink(os.path.join(_lbin25, "node")), str((_ok25t, _msg25t)))
check("校验表只取一次且与下载源无关(修前挂在每个镜像尝试内部, 每源各取一遍)",
      sum(1 for u in _opened25 if "SHASUMS" in u) == 1
      and sum(1 for u in _opened25 if u.endswith(".tar.xz")) == 1, str(list(_opened25)))
# node 在但跑不动(node -v 无输出): pnpm 一侧早有守卫, node 一侧修前会报 "Node None … 就绪"
_silent25 = os.path.join(_lbin25, "node")
if os.path.islink(_silent25) or os.path.exists(_silent25):
    os.remove(_silent25)
_mk_exe25(_silent25, "")
_opened25.clear()
nodetool.detect = lambda force=False: {"node24": {"path": _silent25, "version": None},
                                       "any_node": {"path": _silent25, "version": None},
                                       "pnpm": {"path": _pnpm25, "version": "9.9.9"},
                                       "meets": False, "version_unknown": True}
_log25u = []
_ok25u, _msg25u = nodetool.install_node_env(_log25u.append)
check("node 版本探测不到时不得报就绪(修前: 返回 True + \"Node None + pnpm 9.9.9 就绪\", 面板一片绿)",
      _ok25u is False and "未能验证" in (_msg25u or "") and not _opened25, str((_ok25u, _msg25u)))
_mk_exe25(_silent25, "v24.10.3")
nodetool.detect = lambda force=False: {"node24": {"path": _silent25, "version": "v24.10.3"},
                                       "any_node": {"path": _silent25, "version": "v24.10.3"},
                                       "pnpm": {"path": _pnpm25, "version": "9.9.9"},
                                       "meets": True, "version_unknown": False}
_log25v = []
_ok25v, _msg25v = nodetool.install_node_env(_log25v.append)
check("对照: node 能报出版本时仍要判就绪(不得把新守卫变成一律失败)",
      _ok25v is True and _msg25v == "Node v24.10.3 + pnpm 9.9.9 就绪", str((_ok25v, _msg25v)))
nodetool.urllib.request.urlopen = _real_up25
nodetool.detect, nodetool.latest_v24 = _real_detect25, _real_v24_25
nodetool._pnpm_path, nodetool._node_candidates = _real_pnpm_path25, _real_cands25
nodetool.LOCAL_BIN, nodetool.LOCAL_LIB = _real_lbin25, _real_llib25
nodetool.procman.which = _real_which25
os.environ["HOME"] = _real_home_env25
_compat.profile_dir, _compat.dsh_home = _real_pdir25, _real_dshome25
check("25 收尾: 被测模块的接缝必须全部换回真值(否则后面的节会被静默改变行为)",
      _compat.profile_dir("web") != _prof25 and nodetool.LOCAL_BIN == _real_lbin25
      and os.environ.get("HOME") == _real_home_env25 and nodetool.procman.which is _real_which25,
      str((_compat.profile_dir("web"), nodetool.LOCAL_BIN)))

shutil.rmtree(_SELFTEST_HOME, ignore_errors=True)
shutil.rmtree(_tmp, ignore_errors=True)

print()
if FAILS:
    print("FAILED: %d 项: %s" % (len(FAILS), FAILS))
    sys.exit(1)
print("ALL REGRESSION CHECKS PASSED")
