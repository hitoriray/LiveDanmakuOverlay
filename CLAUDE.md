# 弹幕姬（LiveDanmakuOverlay）接管说明

> 更新日期：2026-08-18（Asia/Shanghai）  
> 仓库：`C:\Users\zhanghongzhi\LiveDanmakuOverlay`  
> GitHub：`https://github.com/hitoriray/LiveDanmakuOverlay.git`  
> 当前分支：`master`

## 1. 当前目标

项目名为“弹幕姬”，是一个 .NET 8 / WPF 的 Windows 直播弹幕悬浮窗。当前客户端主体功能已经完成，正在增加“公司电脑与家里电脑之间的远程同步”。

当前最优先目标不是继续设计同步功能，而是：

1. 把已经写好的轻量同步服务部署到用户服务器。
2. 通过 `https://sync.rache1gardner.com` 验证服务可用。
3. 在弹幕姬客户端填写同步地址、账号和密码，完成两台电脑的实际同步验证。
4. 部署完成后恢复安全的 SSH 配置；是否保留本次专用部署公钥由用户决定。

## 2. 当前 Git 与发布状态

- 当前 HEAD：`d2cf25e merge origin/master and ignore generated artifacts`
- 本地 `master` 与 `origin/master` 一致（检查时 ahead/behind 均为 0）。
- 工作树在创建本文件之前是干净的。
- 远程同步相关源码已经进入 Git 并已经推送，不要再按旧上下文把它当成“尚未提交”。主要来自提交 `393122c`，之后由 `d2cf25e` 清理生成物并合并。
- Windows 客户端发布目录：`publish\`
- Linux x64 同步服务包：`danmaku-sync-linux-x64.tar.gz`，约 40 MB。
- `publish\`、`bin\`、`obj\`、`server-publish\` 等生成物已由 `.gitignore` 排除。不要重新提交生成物。

## 3. 已完成的客户端功能

此前已经完成的主要能力包括：

- B站直播间弹幕连接、二维码登录、完整用户名获取（未登录时 B站可能只返回 `***`）。
- 透明、置顶、可穿透的弹幕悬浮窗。
- 平滑滚动弹幕、窗口内裁剪、弹道调度、高峰弹幕的低延迟处理。
- 字号小/中/大；速度档位；文字透明度 10%～100%；背景透明度 0%～100%；显示区域档位。
- 弹幕显示开关、全屏/最大化/最小化/关闭按钮。
- Emoji 彩色渲染以及 B站标签/表情相关处理。
- 屏蔽词、屏蔽用户、从历史搜索中屏蔽单条内容或用户。
- SQLite 弹幕历史、搜索、保留期限和清理功能。
- 设置持久化、窗口状态处理、常用直播间保存与下拉选择。

项目说明及本地构建命令见 `README.md`。

## 4. 已完成的远程同步功能

### 同步内容

- 屏蔽词。
- 屏蔽用户。
- 常用直播间（支持重命名、置顶、删除，最多 30 个）。
- 字号、滚动速度、背景透明度、文字透明度、显示区域、弹幕开关。
- 实时策略、是否保存被屏蔽弹幕、历史保留天数。

### 明确不同步

- B站 Cookie。
- 同步密码。
- 本地弹幕历史。
- 窗口位置、大小和锁定状态。

### 同步机制

- 服务器保存单调递增的 revision，本机保存上次同步的基础版本，执行三方合并。
- 屏蔽词和屏蔽用户按集合合并；常用直播间与普通设置按基础版本判断变化。
- 只有同一字段在本机和远程都被修改时才提示冲突。
- 冲突时控制中心允许“保留本机”或“使用远程”。
- 程序启动时同步；本机设置修改约 3 秒后同步；每 5 分钟轮询一次。
- 同步密码使用 Windows DPAPI（CurrentUser）保存在本机，不写入普通设置文件。
- 服务端使用 Basic Auth + HTTPS，数据存 SQLite，仅保留最近 20 个版本，单次请求上限 256 KB。

关键文件：

- `RemoteSyncService.cs`：协议模型、HTTP 客户端、同步 payload、合并算法、DPAPI 凭据。
- `SyncCoordinator.cs`：启动同步、3 秒防抖、5 分钟轮询、冲突处理。
- `AppSettings.cs`：同步配置和常用直播间配置。
- `ControlCenterWindow.xaml(.cs)`：同步与直播间 UI。
- `MainWindow.xaml(.cs)`：直播间下拉、连接后自动保存。
- `SyncServer/Program.cs`：ASP.NET Core + SQLite 同步服务。
- `deploy/README.md`：完整服务器部署说明。
- `deploy/danmaku-sync.service`：systemd 服务。
- `deploy/nginx-sync.conf`：Nginx 2334 端口反代模板。

## 5. 已完成验证（历史结果）

开发阶段曾通过以下检查：

```text
SETTINGS_INITIALIZATION_OK
WINDOW_PLACEMENT_OK
WINDOW_DRAG_POLICY_OK
ASYNC_EMOJI_RENDERING_OK
BILIBILI_QR_LOGIN_OK
WINDOWS_EMOJI_COLORS=54
BARRAGE_RENDERER_OK
HISTORY_FILTER_OK
SYNC_MERGE_OK
SMOKE_TEST_OK
```

同步服务本机端到端测试曾得到：

```text
SYNC_SERVER_E2E_OK unauthorized=401 revision=1 loaded=1 conflict=409
```

这些是此前的测试结果。如果继续修改源码，应重新运行相关测试，不能把历史结果当成新修改后的验证。

## 6. 服务器和 Cloudflare 的已知配置

服务器：

```text
IP: 80.251.217.69
系统: Ubuntu 22.04.5 x86_64
资源: 1 核 CPU / 1 GB RAM / 20 GB 磁盘
域名: sync.rache1gardner.com
```

端口规划：

- 现有 Xray 使用公网 `443` / `8443`，部署同步服务时不能破坏它。
- Nginx 的同步站点监听 `2334`。
- ASP.NET Core 同步服务只监听 `127.0.0.1:5091`。
- Cloudflare 已添加 `sync.rache1gardner.com` 的 A 记录并开启橙云代理。
- Cloudflare Origin Rule 已把该子域名的目标端口改写为 `2334`。
- Cloudflare SSL/TLS 应使用 `Full (strict)`。

## 7. SSH 当前状态（必须先核实）

用户为了排查公钥登录，已经临时允许 root 密码 SSH，并确认密码方式可以连接服务器。很可能创建过：

```text
/etc/ssh/sshd_config.d/00-temp-open.conf
```

推测内容为：

```text
PasswordAuthentication yes
PermitRootLogin yes
```

这只是会话上下文中的推测，服务器实时状态尚未由本机再次核验。接手后先执行：

```bash
sshd -T | grep -E 'passwordauthentication|permitrootlogin'
ls -l /etc/ssh/sshd_config.d/
grep -RniE 'PasswordAuthentication|PermitRootLogin' /etc/ssh/sshd_config /etc/ssh/sshd_config.d 2>/dev/null
```

本机已经生成专用部署密钥：

```text
私钥: C:\Users\zhanghongzhi\.ssh\live_danmaku_sync_ed25519
公钥: C:\Users\zhanghongzhi\.ssh\live_danmaku_sync_ed25519.pub
指纹: SHA256:MBN3lCFcHynnQ4G70Vj/+rUl7S5Pdus4BWsX0w82L2U
注释: LiveDanmakuOverlay-deployment
```

不要把私钥内容写入 Git、聊天或服务器。此前手工录入公钥曾失败；用户最后问的是如何通过已连接的密码 SSH 会话把本机公钥正确放入服务器，目前尚未确认专用密钥登录是否成功。

在本机 PowerShell 中可准确追加公钥：

```powershell
Get-Content "$env:USERPROFILE\.ssh\live_danmaku_sync_ed25519.pub" |
ssh root@80.251.217.69 "umask 077; mkdir -p /root/.ssh; cat >> /root/.ssh/authorized_keys"
```

然后测试专用密钥：

```powershell
ssh -i "$env:USERPROFILE\.ssh\live_danmaku_sync_ed25519" -o IdentitiesOnly=yes root@80.251.217.69
```

如失败，在已保持的密码 SSH 会话里检查：

```bash
chmod 700 /root/.ssh
chmod 600 /root/.ssh/authorized_keys
chown -R root:root /root/.ssh
tail -n 3 /root/.ssh/authorized_keys
journalctl -u ssh -n 100 --no-pager
```

确认新开的终端可以使用密钥登录后，再删除临时放开配置并重启 SSH：

```bash
rm -f /etc/ssh/sshd_config.d/00-temp-open.conf
sshd -t && systemctl restart ssh
```

不要在密钥登录尚未从“另一个新终端”验证成功前关闭当前可用的 SSH 会话。

## 8. 剩余工作（按顺序执行）

### A. 建立并验证密钥登录

完成上一节的公钥追加和新终端登录验证。若用户不希望 Claude 直接连服务器，则只给用户逐条命令，让用户操作并回传结果。

### B. 上传服务包

密钥登录成功后，本机 PowerShell：

```powershell
scp -i "$env:USERPROFILE\.ssh\live_danmaku_sync_ed25519" `
  .\danmaku-sync-linux-x64.tar.gz `
  root@80.251.217.69:/root/
```

执行时工作目录应为 `C:\Users\zhanghongzhi\LiveDanmakuOverlay`。

### C. 安装同步服务

服务器执行：

```bash
useradd --system --home /var/lib/danmaku-sync --shell /usr/sbin/nologin danmaku-sync 2>/dev/null || true
install -d -o danmaku-sync -g danmaku-sync -m 750 /var/lib/danmaku-sync
install -d -o root -g root -m 755 /opt/danmaku-sync
tar -xzf /root/danmaku-sync-linux-x64.tar.gz -C /opt/danmaku-sync
chmod 755 /opt/danmaku-sync/danmaku-sync-server
```

生成随机同步密码并保存。不要把密码提交进 Git：

```bash
SYNC_PASSWORD="$(openssl rand -base64 30 | tr -d '\n')"
printf 'SYNC_USERNAME=danmaku\nSYNC_PASSWORD=%s\nSYNC_DATA_PATH=/var/lib/danmaku-sync/sync.db\n' "$SYNC_PASSWORD" > /etc/danmaku-sync.env
chmod 600 /etc/danmaku-sync.env
printf '同步账号: danmaku\n同步密码: %s\n' "$SYNC_PASSWORD"
```

让用户立刻把账号和密码保存到密码管理器。安装并验证 systemd：

```bash
cp /opt/danmaku-sync/deploy/danmaku-sync.service /etc/systemd/system/danmaku-sync.service
systemctl daemon-reload
systemctl enable --now danmaku-sync
systemctl status danmaku-sync --no-pager
curl -fsS http://127.0.0.1:5091/health
```

预期健康检查返回：

```json
{"status":"ok"}
```

### D. 安装 Nginx 站点

先找出服务器现有 Cloudflare Origin 证书的真实路径：

```bash
nginx -T 2>/dev/null | grep -E 'ssl_certificate(_key)? ' | sort -u
```

编辑 `/opt/danmaku-sync/deploy/nginx-sync.conf`，把模板证书路径改为服务器真实路径。然后：

```bash
cp /opt/danmaku-sync/deploy/nginx-sync.conf /etc/nginx/sites-available/danmaku-sync
ln -sfn /etc/nginx/sites-available/danmaku-sync /etc/nginx/sites-enabled/danmaku-sync
nginx -t
systemctl reload nginx
```

注意：

- 必须先通过 `nginx -t` 才能 reload。
- 不要改动现有 Xray 的 443/8443 配置。
- 不要把备份文件放到 `sites-enabled`，否则可能重复加载监听端口。

验证服务器本机 Nginx（可按实际证书/SNI情况调整）：

```bash
ss -lntp | grep -E ':2334|:5091'
curl -kfsS --resolve sync.rache1gardner.com:2334:127.0.0.1 https://sync.rache1gardner.com:2334/health
```

### E. 验证公网与客户端

任意电脑访问：

```text
https://sync.rache1gardner.com/health
```

应返回 `{"status":"ok"}`。然后在弹幕姬“功能 → 同步与直播间”填写：

```text
服务器地址: https://sync.rache1gardner.com
同步账号: danmaku
同步密码: 服务器刚生成的密码
```

勾选“启用自动同步”，点击“保存并立即同步”。随后至少验证：

1. 公司电脑新增一个测试屏蔽词，家里电脑能拉到。
2. 家里电脑新增一个常用直播间，公司电脑能拉到。
3. 双方修改同一普通设置时能出现冲突选择。
4. B站 Cookie、弹幕历史和窗口位置没有被上传/覆盖。

### F. 收尾

- 如果临时 root 密码 SSH 仍开启，确认密钥登录后关闭它。
- 询问用户是否保留 `LiveDanmakuOverlay-deployment` 公钥；若撤销，只删除 `authorized_keys` 中对应的那一行，不要删除其他密钥。
- 检查：`systemctl status danmaku-sync --no-pager`。
- 检查：`journalctl -u danmaku-sync -n 100 --no-pager`。
- 检查：`du -h /var/lib/danmaku-sync/sync.db*`。

## 9. 开发、测试和发布约定

用户偏好直接、少绕弯；遇到真实阻塞或需要用户操作时给出精确命令。

如果修改客户端源码：

1. 先检查 `git status`，保留用户已有改动。
2. 运行与修改范围相称的构建/测试。
3. 用户此前明确要求每次修改后覆盖发布到 `publish\`；如果仓库目录下的 `LiveDanmakuOverlay.exe` 正在运行，可以强制关闭该进程后再发布。
4. 推荐发布命令：

   ```powershell
   dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
   ```

5. 分发整个 `publish` 目录，不要只分发 EXE，因为 SQLite 和彩色 Emoji 使用原生运行库。
6. 不要使用 `git add -A`；只暂存本次意图内的源码和文档路径。
7. 在 commit/push 前再次检查 diff，确保没有密码、Cookie、私钥、数据库、`bin/obj` 或其他生成物。
8. 当前仓库已经与 GitHub 对齐。除非用户要求，部署服务器本身不需要制造源码提交；本 `CLAUDE.md` 是否提交由用户决定。

客户端构建与烟雾测试入口：

```powershell
dotnet build -c Release
dotnet run --project .\SmokeTest\SmokeTest.csproj -c Release -- 直播间房间号
```

同步服务发布示例：

```powershell
dotnet publish .\SyncServer\LiveDanmakuOverlay.SyncServer.csproj `
  -c Release -r linux-x64 --self-contained true `
  -o .\server-publish
```

发布包必须包含 `deploy\` 目录。已有 `danmaku-sync-linux-x64.tar.gz` 已确认包含服务端可执行文件和三个部署文件。

## 10. 不要误判的事项

- 目前“源码已实现并已推送”，剩余重点是服务器部署和真实双机验证。
- “密码 SSH 已经能登录”不等于“专用公钥登录已经成功”；后者仍待确认。
- 服务器当前 systemd、Nginx 和公网健康检查都尚未确认成功，不要声称已经部署完成。
- 不要占用或改坏 Xray 的 443/8443。
- 不要上传 B站 Cookie、同步密码、本地弹幕历史或私钥。
- 1 核 / 1 GB 服务器足够运行当前轻量服务；systemd 模板已经设置 `MemoryMax=160M`。
- 服务端数据文件预计位于 `/var/lib/danmaku-sync/sync.db`，不是无限保存：同步版本只保留最近 20 个。

