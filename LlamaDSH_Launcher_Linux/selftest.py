# -*- coding: utf-8 -*-
"""Linux 版回归自检(171 项): 同步批次 + 历轮真实 bug 的断言, 失败即非零退出。

跑法: python3 selftest.py   (托盘/服务在跑时会额外做活体宿主侧协议探针,
     未运行则打印 SKIP 而不是失败)
注意: 本机没有 Xvfb/输入注入工具, traydialog 的退出码契约只能真弹一次窗
      (约 0.2s 一闪), 否则那 4 项只能空转。
每项断言都对应一个真实踩过的坑, 修 bug 前先跑它, 修完再跑它。
注意: 源码级断言(比对字符串)挡不住运行时错误 —— 收尾/退出这一族另有行为级
      测试(第 13 节): 进程内真调收尾函数 + 隔离 HOME 起真实例打 /api/shutdown。"""
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
    for _ in range(120):
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
        try:
            _tok5 = open(os.path.join(_h5, ".dsh_launcher_token"), encoding="utf-8").read().strip()
        except OSError:
            _tok5 = ""
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

shutil.rmtree(_tmp, ignore_errors=True)

print()
if FAILS:
    print("FAILED: %d 项: %s" % (len(FAILS), FAILS))
    sys.exit(1)
print("ALL REGRESSION CHECKS PASSED")
