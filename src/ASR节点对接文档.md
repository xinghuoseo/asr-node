# ASR 节点对接文档（服务器端 ⇄ 本地识别节点）

> 版本: 2026-10-06（v2）
> 适用: 本地识别节点程序 AsrAgent.exe（**Qwen3-ASR-0.6B** 阿里通义 2026 开源模型 + pyannote/CAM++ 说话人分离，INT8 量化，纯 CPU 推理）

---

## 1. 系统架构

```
最终用户 / AI客户端
        │ MCP / HTTP
        ▼
公网服务器 (网关, 本文档的"服务器端")
        │ WSS 长连接 (本地节点主动出站, 无需公网IP)
        ▼
本地识别节点 (Windows, AsrAgent.exe)
        │ HTTP 127.0.0.1:8088
        ▼
sherpa-onnx 推理服务 (Qwen3-ASR 转写 + Silero-VAD + 说话人分离)
```

- 本地节点**主动出站**连接服务器，服务器只做等待连接和消息转发
- 节点与服务器之间全双工长连接，断线自动重连（指数退避 1s→30s）
- 识别主模型: **Qwen3-ASR-0.6B**（阿里通义 2026-01 开源，Apache 2.0），支持 30 种语言 +
  22 种中文方言 + 歌词/说唱识别，自带标点与智能文本规整（数字/英文大小写）
- 备选模型（节点端切换，接口不变）: SenseVoice-Small（更快更省内存）、Paraformer-zh

## 2. 连接方式

```
wss://<服务器域名>/<路径>/?token=<token>
```

- **token 放在 URL query 参数中**（节点不再使用 HTTP Header 传递）
- 建议网关在握手阶段（HTTP Upgrade 请求）校验 `token`，校验失败返回 HTTP 401/403 拒绝握手
- 节点连接成功后**不发送任何注册消息**，静默等待服务器下发指令
- 心跳：网关如需应用层心跳，发 `{"type":"ping"}`，节点回 `{"type":"pong"}`；
  同时节点自身每 20s 发 WebSocket 协议层 PING，网关按协议自动回应即可

## 3. 消息协议

### 3.1 服务器 → 节点（下发调用）

```json
{"type":"call","id":"<唯一调用ID>","action":"<动作名>","payload":{...}}
```

| 字段 | 说明 |
|---|---|
| type | 固定 `call` |
| id | 调用唯一 ID（服务器生成，节点原样返回） |
| action | `transcribe`（转写）或 `status`（健康状态） |
| payload | 动作参数，见下文 |

### 3.2 节点 → 服务器（回传结果）

成功：

```json
{"type":"result","id":"<同调用ID>","ok":true,"data":{...}}
```

失败：

```json
{"type":"result","id":"<同调用ID>","ok":false,"data":{"error":"失败原因"}}
```

> 注意：失败时也是 `type:"result"`，用 `ok` 字段区分，不要等待 `type:"error"`。
> 节点对同一条 call 只回一条 result；转写耗时与音频长度相关（1 小时音频约 3~4 分钟），
> 网关侧建议 result 超时设置为 ≥ 10 分钟。

## 4. action=transcribe（语音转文字）

### 4.1 请求

两种方式二选一（都支持，**推荐 audio_url**，避免大 base64 消息）：

```json
{"type":"call","id":"c-1001","action":"transcribe",
 "payload":{"audio_b64":"<wav 文件的 base64>","format":"wav"}}
```

```json
{"type":"call","id":"c-1002","action":"transcribe",
 "payload":{"audio_url":"https://your-server/tmp/x.wav","filename":"可选,显示名.wav"}}
```

| payload 字段 | 必填 | 说明 |
|---|---|---|
| audio_b64 | 与 audio_url 二选一 | wav 文件内容的 base64 |
| audio_url | 与 audio_b64 二选一 | 节点可公网访问的 wav 下载地址，节点自行下载 |
| format | 否 | 默认 `wav`，**仅支持 wav** |
| filename | 否 | 记录显示用文件名，不影响识别 |

**音频硬性要求：**

- 格式必须为 **WAV**（其他 mp3/m4a 等请服务器端先用 ffmpeg 转码：
  `ffmpeg -i in.mp3 -ar 16000 -ac 1 out.wav`）
- 采样率必须 **16000 Hz**，单声道
- 建议单文件 ≤ 500MB（约 4 小时音频）

### 4.2 成功返回

```json
{
  "type": "result", "id": "c-1002", "ok": true,
  "data": {
    "id": "bd0d5c1e-....",
    "text": "hello，大家好，欢迎收听本周的ai新闻播报，我是小雅。我是阿凯。",
    "sentences": [
      {"text": "hello，大家好，欢迎收听本周的ai新闻播报，我是小雅。", "start": 7264, "end": 13063, "spk": 1},
      {"text": "我是阿凯。", "start": 13100, "end": 14400, "spk": 0}
    ],
    "speakers": [0, 1],
    "duration_sec": 201.49,
    "elapsed_sec": 23.5
  }
}
```

| data 字段 | 说明 |
|---|---|
| text | 全文（无说话人标签，可直接展示） |
| sentences | 分句数组，按时间排序 |
| sentences[].text | 该句文本（已自动加标点） |
| sentences[].start / end | 该句起止时间，**单位毫秒** |
| sentences[].spk | 说话人编号（整数，0 起；自动聚类，同一编号=同一人） |
| speakers | 出现过的说话人编号列表，如 `[0,1]` |
| duration_sec | 音频时长（秒） |
| elapsed_sec | 节点实际处理耗时（秒） |
| id | 本次转写的内部 ID |

### 4.3 说话人分离说明

- 无需调用方指定人数，节点对全音频自动做声纹提取 + 聚类
- `spk` 编号（0/1/2...）仅代表"不同的说话人"，**不对应真实身份**；
  调用方如需映射"小雅/阿凯"等名字，按业务自行绑定（如首个说话人）
- 单人音频返回 `speakers:[0]`；极短音频（<3s）也可能得到说话人信息，但建议 ≥5s
- 无法分离时 `sentences[].spk` 可能为 null

### 4.4 失败样例

```json
{"type":"result","id":"c-1001","ok":false,
 "data":{"error":"仅支持 wav 格式(16kHz 单声道), 请先转码"}}
```

| 常见 error | 原因 |
|---|---|
| 仅支持 wav 格式... | 传了非 wav 或未转码 |
| payload 需要 audio_b64 或 audio_url | 两者都没传 |
| 音频处理失败: ... | base64 非法 / 采样率非 16k / 文件损坏 |
| unknown action: xxx | action 名写错 |

## 5. action=status（健康检查）

```json
{"type":"call","id":"c-2001","action":"status","payload":{}}
```

```json
{"type":"result","id":"c-2001","ok":true,
 "data":{"status":"ok","device":"cpu","model":"qwen3","mem_mb":897,
         "features":["file_transcribe","speaker_diarization"]}}
```

网关可定时轮询 `status` 判断节点是否可用，再决定是否把用户请求路由到该节点。

## 6. 服务器端参考实现（Python 示例，可直接抄）

```python
import asyncio, json, uuid
import websockets   # pip install websockets

CLIENTS = set()   # 所有已连接节点

async def handler(ws):
    CLIENTS.add(ws)
    try:
        # 1) 握手校验 (token 在 URL query)
        #    websockets 库可在 process_request/connect 回调中校验, 此处省略
        async for raw in ws:
            msg = json.loads(raw)
            if msg.get("type") == "ping":
                await ws.send(json.dumps({"type": "pong"}))
            # 网关自身逻辑: 收 result 匹配 id, 唤醒等待的调用方
    finally:
        CLIENTS.discard(ws)

async def call_node(action: str, payload: dict, timeout: float = 600) -> dict:
    """向任一在线节点下发调用并等待 result"""
    ws = next(iter(CLIENTS))
    call_id = uuid.uuid4().hex
    fut = asyncio.get_event_loop().create_future()
    pending[call_id] = fut
    await ws.send(json.dumps({"type": "call", "id": call_id,
                              "action": action, "payload": payload}))
    return await asyncio.wait_for(fut, timeout)

async def transcribe(wav_url: str) -> dict:
    return await call_node("transcribe", {"audio_url": wav_url})

# 收到节点 result 时: pending[rid].set_result(msg["data"])
```

`pending` 字典在网关主循环中维护：节点回 `{"type":"result","id":...}` 时按 id 取出
future 并 set_result 即可。

## 7. ⚠️ 服务器端需要修复：WSS 证书链不完整

**当前 `home.xinghuoseo.com` 的 HTTPS/WSS 握手只返回了叶子证书（1471 字节，签发者
TrustAsia），缺少中间证书**，导致所有标准客户端（Python websockets、C#、浏览器外
大多数环境）TLS 校验失败：

```
SSLCertVerificationError: unable to get local issuer certificate
```

**修复方法（nginx）：**

```nginx
# 错误: 只配了证书包里的单个 .crt / .pem
ssl_certificate     /path/domain.crt;

# 正确: 使用证书包中的完整链 (叶子证书 + 中间证书 拼在一起的 fullchain)
ssl_certificate     /path/fullchain.cer;    # 腾讯云/TrustAsia 包里的完整链文件
ssl_certificate_key /path/domain.key;
```

- 腾讯云 TrustAsia 证书下载包内，`fullchain.cer`（或 Apache 目录下的
  `2_root_bundle.crt` + `1_domain.crt` 合并）才是完整链
- 改完 `nginx -t && nginx -s reload`
- 验证：`openssl s_client -connect home.xinghuoseo.com:443 -servername home.xinghuoseo.com`
  输出中 `Verify return code: 0 (ok)` 即为修复成功

修复前节点端已临时放行证书校验（可先用），修复后建议节点恢复严格校验。

## 8. 本地 HTTP API（同机调试用，127.0.0.1:8088）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | /health | `{"status":"ok","device":"cpu","model":"qwen3","mem_mb":897,...}` |
| POST | /transcribe | multipart 上传 wav 文件，返回同上 data 结构 |
| POST | /transcribe_b64 | JSON `{"audio_b64":"...","format":"wav","id":...}` |

## 9. 性能参考（Qwen3-ASR-0.6B 实测）

| 音频时长 | 处理耗时 | 说明 |
|---|---|---|
| 6 秒 | 约 6.5 秒 | 短音频有固定开销（说话人分离等） |
| 201 秒（3.3 分钟） | 约 59 秒 | RTF≈0.29，仍为实时 3 倍速以上 |

- 节点配置: i5-12490F，纯 CPU 推理（INT8），常驻内存约 900MB
- 如需更快/更省内存，可将节点切换为 SenseVoice-Small（201 秒音频约 24 秒，
  内存约 430MB），精度略低于 Qwen3，切换方式为节点端环境变量，对接方无感知
