# CtYun 云电脑保活

CtYun 用于登录天翼云电脑并维持 WebSocket 保活连接。当前版本支持：

- 多账号同时登录和保活。
- Windows/本机可执行文件：支持本地配置文件模式和交互输入模式。
- Docker：支持挂载本地配置文件模式和 `-it` 交互输入模式。
- 首次绑定设备需要短信验证码时，通过终端交互输入。

## 配置文件模式

程序默认在数据目录读取 `accounts.json`：

- 本机可执行文件：默认数据目录为程序所在目录。
- Docker：默认数据目录为 `/app/data`，建议挂载到宿主机。
- 也可以通过环境变量 `CTYUN_CONFIG` 指定配置文件路径。
- 也可以通过环境变量 `CTYUN_DATA_DIR` 指定数据目录。
- 也可以通过环境变量 `OCR_URL` 指定验证码识别（OCR）接口地址，未设置时使用内置默认地址。

`accounts.json` 示例：

```json
{
  "keepAliveSeconds": 60,
  "keepAliveJitterPercent": 20,
  "sleepWindow": "02:00-07:00",
  "accounts": [
    {
      "name": "account-a",
      "user": "你的账号1",
      "password": "你的密码1",
      "deviceCode": "web_自行生成的32位随机字符"
    },
    {
      "name": "account-b",
      "user": "你的账号2",
      "password": "你的密码2"
    }
  ]
}
```

`deviceCode` 可不填。程序会为每个账号自动生成设备码，并保存到 `devices/{账号名}.txt`。为了避免每次 Docker 重建镜像后重新绑定设备，务必持久化数据目录。

Linux 生成设备码示例：

```bash
echo "web_$(cat /dev/urandom | tr -dc 'a-zA-Z0-9' | fold -w 32 | head -n 1)"
```

## 本机运行

把 `accounts.json` 放到程序目录后直接运行：

```bash
CtYun.exe
```

如果没有配置文件，也没有环境变量，程序会进入交互输入模式：

```text
账号:
密码:
继续添加账号? (y/N):
```

首次设备绑定时，程序会提示输入短信验证码。

## Docker 首次运行

准备宿主机配置目录：

```bash
mkdir -p ./ctyun-data
```

把 `accounts.json` 放到 `./ctyun-data/accounts.json`。首次运行建议使用 `-it`，方便输入短信验证码：

```bash
docker run -it --rm \
  --name ctyun-init \
  -v "$(pwd)/ctyun-data:/app/data" \
  su3817807/ctyun:latest
```

看到保活任务启动后，说明设备码已经绑定成功。之后可以按 `Ctrl+C` 停止初始化容器，再改为后台运行。

## Docker 后台运行

设备绑定完成后使用：

```bash
docker run -d \
  --name ctyun \
  -v "$(pwd)/ctyun-data:/app/data" \
  su3817807/ctyun:latest
```

查看日志：

```bash
docker logs -f ctyun
```

## 兼容旧环境变量模式

单账号仍支持旧环境变量。首次绑定设备时同样使用 `-it` 输入短信验证码：

```bash
docker run -it --rm \
  --name ctyun-init \
  -v "$(pwd)/ctyun-data:/app/data" \
  -e APP_USER="你的账号" \
  -e APP_PASSWORD="你的密码" \
  -e DEVICECODE="web_你的设备码" \
  su3817807/ctyun:latest
```

绑定完成后改为后台运行：

```bash
docker run -d \
  --name ctyun \
  -v "$(pwd)/ctyun-data:/app/data" \
  -e APP_USER="你的账号" \
  -e APP_PASSWORD="你的密码" \
  -e DEVICECODE="web_你的设备码" \
  su3817807/ctyun:latest
```

建议新部署优先使用 `accounts.json`，多账号管理更清晰，也更适合 Docker 持久化。

## 自定义 OCR 接口

登录时需要识别图形验证码，默认调用内置的 OCR 服务。如果想使用自建的 OCR 服务（例如基于 [ddddocr](https://github.com/sml2h3/ddddocr) 部署），可以通过环境变量 `OCR_URL` 指定完整地址，支持在地址中携带 token 等查询参数：

```bash
docker run -d \
  --name ctyun \
  -v "$(pwd)/ctyun-data:/app/data" \
  -e OCR_URL="http://localhost:8000/ocr?token=YOUR_SECRET_TOKEN" \
  su3817807/ctyun:latest
```

程序会以 `multipart/form-data` 方式 POST 到该地址，表单字段 `image` 为验证码图片的 Base64 字符串，期望返回 JSON，并从 `data` 字段读取识别结果，例如：

```json
{ "data": "a3f9" }
```

`OCR_URL` 未设置或不是合法的 http/https 地址时，会回退为内置默认地址。

## 保活周期、随机抖动与睡眠时段

以下参数既可写在 `accounts.json`，也可用环境变量指定，环境变量优先级更高：

| 环境变量 | accounts.json 字段 | 说明 | 默认 |
| --- | --- | --- | --- |
| `KEEP_ALIVE_SECONDS` | `keepAliveSeconds` | 强制重连周期（秒），最小 10 | 60 |
| `KEEP_ALIVE_JITTER` | `keepAliveJitterPercent` | 周期随机抖动百分比（0~90）。设为 20 表示每个周期在基准值 ±20% 内随机；0 或不设置为固定周期 | 0 |
| `SLEEP_WINDOW` | `sleepWindow` | 睡眠时段，格式 `HH:mm-HH:mm`，支持跨午夜（如 `23:30-06:45`），多个时段用逗号分隔。处于该时段时暂停保活，结束后自动刷新连接并恢复；不设置则全天保活 | 无 |
| `SLEEP_JITTER_MINUTES` | `sleepJitterMinutes` | 睡眠结束后随机推迟 0~N 分钟再恢复，避免每天同一分钟准时上线；0 为准时恢复 | 10 |
| `USER_AGENT` | 无 | 请求 User-Agent。设备信息 `deviceModel`/`sysVersion` 会自动取 UA 括号内的平台片段，保持一致 | macOS Chrome 155 |
| `TZ` | 无 | 容器时区，睡眠时段按此时区计算。Docker 默认是 UTC，务必设置 | UTC |

登录时识别完图形验证码后会随机等待 1~3 秒再提交，最多重试 3 次，每次重试前随机等待 3~8 秒。

Docker 示例：

```bash
docker run -d \
  --name ctyun \
  -v "$(pwd)/ctyun-data:/app/data" \
  -e TZ=Asia/Shanghai \
  -e KEEP_ALIVE_SECONDS=90 \
  -e KEEP_ALIVE_JITTER=20 \
  -e SLEEP_WINDOW="02:00-07:00" \
  su3817807/ctyun:latest
```

## 日志与保活

程序会为每个账号、每台云电脑启动独立保活任务。日志格式会带上账号名和云电脑编号，便于区分：

```text
[account-a][desktop-code] -> 收到保活校验
[account-a][desktop-code] -> 发送保活响应成功
```

## 说明

登录图形验证码识别接口方案来自 [sml2h3/ddddocr](https://github.com/sml2h3/ddddocr)。
