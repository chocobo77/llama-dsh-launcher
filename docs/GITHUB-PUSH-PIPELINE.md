# GitHub 推送链路（api.github.com Git Data API）

> 适用：本机 **无法直连 `github.com:443`**（`git push` 报 `Failed to connect` / `Recv failure: Connection was reset`），
> 但 **`api.github.com` 可达**，且本机已存 GitHub 凭据（Windows Credential Manager / `git credential`）。
> 本文把「本地提交 → GitHub」的完整链路整理成可复用流程，供其他会话/项目直接套用。

## 1. 先判断走哪条路

| 现象 | 可用链路 |
| --- | --- |
| `git push` 正常 | 普通 `git push`（首选） |
| `git push` 超时，但 `ssh -T -p 443 git@ssh.github.com` 返回 `Hi <user>!` | SSH over 443：`git remote set-url origin ssh://git@ssh.github.com:443/<owner>/<repo>.git` |
| `github.com:443` 不通，`api.github.com` 通 | **本文的 Git Data API 链路** |
| 全部不通 | 只能等网络/代理；不要盲目重试 |

快速判断：

```powershell
git ls-remote --heads origin          # 失败 = github.com 的 git 通道不通
curl -sS -o NUL -w "%{http_code}" https://api.github.com/   # 200/401 = API 可达
ssh -o ConnectTimeout=8 -p 443 -T git@ssh.github.com        # Hi ... = SSH 可用
```

## 2. 原理

`git push` 走的是 `github.com/<owner>/<repo>.git/info/refs`（smart HTTP），被阻断时无法推送。
GitHub REST API 走 `api.github.com`，其中的 **Git Data API** 允许在服务端创建
blob → tree → commit → ref，等价于“把本地提交放上 GitHub”。

**关键差异**：API 重放会生成**新的 commit SHA**（作者/提交者/父提交都保留，但对象是新建的）。
只要最终 `tree` SHA 与本地一致，**代码内容逐字节相同**；本地与远端的历史 SHA 会分叉，
需要时在有网络的环境 `git fetch` 对齐即可。

## 3. 前置检查

```powershell
# 1) 取 token（不要打印到日志）
git credential fill
# 输入：
# protocol=https
# host=github.com
# （空行）
# 输出里的 password= 就是 token

# 2) 找 git 可执行文件的绝对路径（Windows 上 execFileSync('git') 可能启动失败）
where git
# 典型：C:\Program Files\Git\cmd\git.exe

# 3) 记录本地基线
git rev-parse HEAD
git rev-parse HEAD^{tree}
```

API 侧验证（脚本会做）：

- `GET /user` → 200，且 `x-oauth-scopes` 含 `repo`；
- `GET /repos/{owner}/{repo}` → `permissions.push === true`；
- `GET /git/ref/heads/{branch}` → 远端当前 SHA。

## 4. 推送算法（逐步）

1. `git rev-list --reverse <remoteMain>..HEAD` 得到要重放的提交（旧→新）。
2. `GET /git/commits/<remoteMain>` → `tree.sha` 作为 `baseTree`。
3. 对每个提交：
   - 元数据：`git show -s --format=%an%x00%ae%x00%at%x00%cn%x00%ce%x00%ct%x00%B <sha>`
     （作者/邮箱/时间、提交者/邮箱/时间、完整消息）
   - 变更：`git diff-tree -r --no-renames --no-commit-id --raw <sha>`
     - `A`/`M`：`git show <sha>:<path>` → base64 → `POST /git/blobs`
     - `D`：tree entry `{ path, mode, type: "blob", sha: null }`
   - `POST /git/trees`：`{ base_tree: baseTree, tree: entries }` → 新 tree SHA
   - `POST /git/commits`：`{ message, tree, parents: [prev], author, committer }` → 新 commit SHA
   - 更新 `baseTree` / `prev`
4. `PATCH /git/refs/heads/{branch}`：`{ sha: prev, force: false }`（应快进成功）。
5. 校验：`GET /git/commits/<newHead>` 的 `tree.sha` **必须等于**本地 `HEAD^{tree}`。

## 5. Tag 与 Release

```text
POST /git/tags        { tag, message, object: <commit>, type: "commit", tagger }
POST /git/refs        { ref: "refs/tags/<tag>", sha: <tag object sha> }
POST /releases        { tag_name, name, body, draft: false, prerelease: false }
```

## 6. 可直接复用的脚本

保存为 `push-github-api.mjs`，在**有完整 git 历史的本地仓库根目录**运行：

```js
// push-github-api.mjs — 通过 api.github.com 的 Git Data API 推送本地提交
// 用法：
//   GH_OWNER=owner GH_REPO=repo GH_BRANCH=main GH_TAG=v1.2.3 GH_RELEASE_NAME=v1.2.3 //   GIT_BIN="C:\\Program Files\\Git\\cmd\\git.exe" node push-github-api.mjs
// 可选：GH_BASE_SHA=<远端当前 main>（默认自动读取）、GH_RELEASE_BODY_FILE=notes.md
import { execFileSync, execSync } from 'node:child_process'
import { readFileSync } from 'node:fs'

const OWNER = process.env.GH_OWNER
const REPO = process.env.GH_REPO
const BRANCH = process.env.GH_BRANCH ?? 'main'
const GIT = process.env.GIT_BIN ?? 'git'
const TAG = process.env.GH_TAG
const RELEASE_NAME = process.env.GH_RELEASE_NAME ?? TAG
const RELEASE_BODY = process.env.GH_RELEASE_BODY_FILE
  ? readFileSync(process.env.GH_RELEASE_BODY_FILE, 'utf8')
  : (process.env.GH_RELEASE_BODY ?? '')
if (!OWNER || !REPO) throw new Error('GH_OWNER / GH_REPO required')

// --- token：从 git 凭据库读取，绝不打印 ---
const cred = execSync(`"${GIT}" credential fill`, {
  input: 'protocol=https\nhost=github.com\n\n',
  encoding: 'utf8',
  env: { ...process.env, GIT_TERMINAL_PROMPT: '0' },
})
const token = cred.match(/^password=(.*)$/m)?.[1]
if (!token) throw new Error('no GitHub token in the credential store')

const H = {
  Authorization: `Bearer ${token}`,
  Accept: 'application/vnd.github+json',
  'user-agent': 'push-github-api',
  'content-type': 'application/json',
}
async function api(method, pathname, body) {
  // /user 与 /repos/* 是账号级路径：拼上 repos/{owner}/{repo} 前缀会 404
  // （2026-11-17 实测：GET /user -> 404，token 本身是好的）。
  const base = pathname.startsWith('/user') || pathname.startsWith('/repos/')
    ? 'https://api.github.com'
    : `https://api.github.com/repos/${OWNER}/${REPO}`
  const r = await fetch(`${base}${pathname}`, {
    method, headers: H, body: body === undefined ? undefined : JSON.stringify(body),
  })
  const text = await r.text()
  let json
  try { json = JSON.parse(text) } catch { json = text.slice(0, 400) }
  if (r.status >= 300) throw new Error(`${method} ${pathname} -> ${r.status}: ${JSON.stringify(json).slice(0, 400)}`)
  return json
}
const git = (args) => execFileSync(GIT, args, { maxBuffer: 1 << 28 })
const gitText = (args) => git(args).toString('utf8')

// --- 0) 权限与远端基线 ---
const me = await api('GET', '/user')
const repo = await api('GET', '/repos/' + OWNER + '/' + REPO)
if (!repo.permissions?.push) throw new Error('token has no push permission')
const remoteRef = await api('GET', `/git/ref/heads/${BRANCH}`)
const baseSha = process.env.GH_BASE_SHA ?? remoteRef.object.sha
const baseCommit = await api('GET', `/git/commits/${baseSha}`)
console.log(`pushing to ${OWNER}/${REPO}#${BRANCH} from ${baseSha.slice(0, 7)} as ${me.login}`)

// --- 1) 要重放的提交（旧→新） ---
const shas = gitText(['rev-list', '--reverse', `${baseSha}..HEAD`]).trim().split(/\s+/).filter(Boolean)
if (shas.length === 0) {
  console.log('nothing to push (remote is already at or ahead of HEAD)')
  process.exit(0)
}

// --- 2) 逐个提交：blob → tree → commit ---
let baseTree = baseCommit.tree.sha
let parent = baseSha
const map = new Map()
for (const sha of shas) {
  const meta = gitText(['show', '-s', '--format=%an%x00%ae%x00%at%x00%cn%x00%ce%x00%ct%x00%B', sha])
  const [an, ae, at, cn, ce, ct, ...rest] = meta.split('\x00')
  const message = rest.join('\x00').replace(/\n+$/, '')
  const raw = gitText(['diff-tree', '-r', '--no-renames', '--no-commit-id', '--raw', sha])
    .trim().split('\n').filter(Boolean)
  const entries = []
  for (const line of raw) {
    const m = line.match(/^:(\d{6}) (\d{6}) ([0-9a-f]{40}) ([0-9a-f]{40}) ([A-Z])\t(.+)$/)
    if (!m) throw new Error('unparsed diff-tree line: ' + line)
    const [, , newMode, , newSha, status, p] = m
    if (status === 'D' || /^0+$/.test(newSha)) {
      entries.push({ path: p, mode: newMode, type: 'blob', sha: null })
    } else {
      const blob = await api('POST', '/git/blobs', {
        content: git(['show', `${sha}:${p}`]).toString('base64'),
        encoding: 'base64',
      })
      entries.push({ path: p, mode: newMode, type: 'blob', sha: blob.sha })
    }
  }
  const tree = await api('POST', '/git/trees', { base_tree: baseTree, tree: entries })
  const commit = await api('POST', '/git/commits', {
    message, tree: tree.sha, parents: [parent],
    author: { name: an, email: ae, date: new Date(Number(at) * 1000).toISOString() },
    committer: { name: cn, email: ce, date: new Date(Number(ct) * 1000).toISOString() },
  })
  map.set(sha, commit.sha)
  console.log(`  ${sha.slice(0, 7)} -> ${commit.sha.slice(0, 7)}  ${message.split('\n')[0].slice(0, 72)}`)
  baseTree = tree.sha
  parent = commit.sha
}

// --- 3) 快进分支 ---
await api('PATCH', `/git/refs/heads/${BRANCH}`, { sha: parent, force: false })
console.log('branch updated:', parent)
const remoteHead = await api('GET', `/git/commits/${parent}`)
const localTree = gitText(['rev-parse', 'HEAD^{tree}']).trim()
if (remoteHead.tree.sha !== localTree) throw new Error(`tree mismatch: ${remoteHead.tree.sha} != ${localTree}`)
console.log('tree verified:', localTree)

// --- 4) 可选：annotated tag + release ---
if (TAG) {
  const tagCommit = map.get(gitText(['rev-parse', `${TAG}^{}`]).trim()) ?? parent
  const tagObj = await api('POST', '/git/tags', {
    tag: TAG, message: RELEASE_NAME, object: tagCommit, type: 'commit',
    tagger: { name: me.login, email: `${me.login}@users.noreply.github.com`, date: new Date().toISOString() },
  })
  await api('POST', '/git/refs', { ref: `refs/tags/${TAG}`, sha: tagObj.sha })
  console.log('tag created:', TAG, '->', tagCommit)
  if (RELEASE_BODY) {
    const rel = await api('POST', '/releases', {
      tag_name: TAG, name: RELEASE_NAME, body: RELEASE_BODY, draft: false, prerelease: false,
    })
    console.log('release:', rel.html_url)
  }
}
```

> Windows 提示：`GIT_BIN` 建议用 `where git` 的绝对路径（如 `C:\Program Files\Git\cmd\git.exe`）。
> Node 24 自带 `fetch`，无需安装依赖。

## 7. 坑与注意事项

- **SHA 会分叉**：API 重放生成新 commit；本地 `origin/main` 仍指向旧 SHA，且新对象不在本地库，
  `git update-ref refs/remotes/origin/main <new>` 会报 `nonexistent object`。有网络时 `git fetch` 对齐即可。
- **重命名**：用 `--no-renames` 拆成 `A`+`D`，避免解析 `R100`。
- **二进制**：`git show` 取到 Buffer，直接 base64；不要按 UTF-8 转。
- **符号链接 / 子模块**：`mode 120000` 是 symlink（内容为链接目标，按 blob 处理）；`mode 160000` 是 submodule，
  需要 `type: "commit"` 和子模块 SHA，不能当 blob。
- **大文件**：单 blob 上限 100 MB；大文件用 Git LFS 或分块，API 链路不适合。
- **并发**：先读远端 `main`，重放期间被他人推进时 `PATCH`（force:false）会失败；重新读取后重放。
- **限流**：认证 5000 次/小时；每个提交约 `变更文件数 + 2` 次调用。
- **账号级路径**：`/user`、`/repos/{owner}/{repo}` 不能带仓库前缀（拼了就 404，见 §6 的 `api()`）。
- **发布附件**：`POST https://uploads.github.com/repos/{owner}/{repo}/releases/{id}/assets?name=<文件名>`
  （`Content-Type` 按文件给，`body` 传原始字节）；本机 `uploads.github.com` 与 `api.github.com` 同样可达，
  而 `github.com` 的 git 通道不通 —— 附件上传不受影响。2026-11-17 v1.1.4 实测 exe/zip 各 2~4 秒传完。
- **Token 安全**：只从 `git credential fill` 读，绝不写进仓库、日志或命令行参数。
- **回退**：`PATCH /git/refs/heads/{branch}` 到旧 SHA（`force:true`），或正常 `git revert`。
- **不要**在脚本里 `console.log(token)`；错误信息里也不要拼接 token。

## 8. 验收清单

- [ ] `GET /git/ref/heads/{branch}` 的 SHA == 脚本输出的最终 commit；
- [ ] `GET /git/commits/<sha>` 的 `tree.sha` == 本地 `git rev-parse HEAD^{tree}`；
- [ ] `GET /contents/<关键文件>?ref=<sha>` 内容正确；
- [ ] `GET /git/ref/tags/<tag>` → tag object → 目标 commit；
- [ ] Release 页面可打开。
