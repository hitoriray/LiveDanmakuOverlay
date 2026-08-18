# 弹幕姬同步服务部署

同步服务为低资源 Linux 服务器设计：只监听 `127.0.0.1:5091`，使用 SQLite，保留最近 20 个同步版本，并限制单次请求为 256 KB。B站 Cookie、同步密码和弹幕历史不会上传。

## 1. Cloudflare

1. 添加 `sync.rache1gardner.com` 的 `A` 记录，指向当前服务器，代理状态设为“已代理”。
2. 添加 Origin Rule：当主机名等于 `sync.rache1gardner.com` 时，将目标端口重写为 `2334`。
3. SSL/TLS 模式保持 `Full (strict)`。

## 2. 上传发布包

把仓库生成的 `danmaku-sync-linux-x64.tar.gz` 上传到服务器 `/root/`。可以使用服务器厂商网页控制台的文件上传功能。

## 3. 安装服务

在服务器网页终端中逐条执行：

```bash
useradd --system --home /var/lib/danmaku-sync --shell /usr/sbin/nologin danmaku-sync 2>/dev/null || true
install -d -o danmaku-sync -g danmaku-sync -m 750 /var/lib/danmaku-sync
install -d -o root -g root -m 755 /opt/danmaku-sync
tar -xzf /root/danmaku-sync-linux-x64.tar.gz -C /opt/danmaku-sync
chmod 755 /opt/danmaku-sync/danmaku-sync-server
```

生成随机同步密码并创建仅 root 可读的配置。命令会在最后显示一次账号和密码，请保存到密码管理器：

```bash
SYNC_PASSWORD="$(openssl rand -base64 30 | tr -d '\n')"
printf 'SYNC_USERNAME=danmaku\nSYNC_PASSWORD=%s\nSYNC_DATA_PATH=/var/lib/danmaku-sync/sync.db\n' "$SYNC_PASSWORD" > /etc/danmaku-sync.env
chmod 600 /etc/danmaku-sync.env
printf '同步账号: danmaku\n同步密码: %s\n' "$SYNC_PASSWORD"
```

安装 systemd 服务：

```bash
cp /opt/danmaku-sync/deploy/danmaku-sync.service /etc/systemd/system/danmaku-sync.service
systemctl daemon-reload
systemctl enable --now danmaku-sync
curl -fsS http://127.0.0.1:5091/health
```

看到 `{"status":"ok"}` 表示同步服务正常。

## 4. 配置 Nginx

先查看现有文档站实际使用的 Cloudflare Origin 证书路径：

```bash
nginx -T 2>/dev/null | grep -E 'ssl_certificate(_key)? ' | sort -u
```

编辑发布包中的 `/opt/danmaku-sync/deploy/nginx-sync.conf`，把证书和私钥路径改成上一步显示的实际路径，然后安装：

```bash
cp /opt/danmaku-sync/deploy/nginx-sync.conf /etc/nginx/sites-available/danmaku-sync
ln -sfn /etc/nginx/sites-available/danmaku-sync /etc/nginx/sites-enabled/danmaku-sync
nginx -t
systemctl reload nginx
```

不要把备份文件放进 `sites-enabled`，否则 Nginx 会重复加载端口配置。

## 5. 验证与客户端配置

等待 Cloudflare DNS 生效后，在任意电脑访问：

```text
https://sync.rache1gardner.com/health
```

应显示 `{"status":"ok"}`。然后在弹幕姬“功能 → 同步与直播间”填写：

- 服务器地址：`https://sync.rache1gardner.com`
- 同步账号：`danmaku`
- 同步密码：第 3 步生成的密码

勾选“启用自动同步”，点击“保存并立即同步”。

## 维护

```bash
systemctl status danmaku-sync --no-pager
journalctl -u danmaku-sync -n 100 --no-pager
du -h /var/lib/danmaku-sync/sync.db*
```

更换密码时修改 `/etc/danmaku-sync.env` 后执行：

```bash
systemctl restart danmaku-sync
```
