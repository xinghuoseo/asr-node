# AsrNode 局域网 API 调用文档

本地语音识别节点（`AsrAgent.exe`）启动模型后会监听固定端口 **38088**，同机与局域网内其他机器均可直接调用，无需 token。

- 服务地址：`http://<节点IP>:38088`
- 协议：HTTP/1.1，请求与响应均为 UTF-8
- 鉴权：无（仅限可信局域网）
- 音频要求：**16 kHz 采样率的 wav**（其他格式先转码，见第 5 节）

---

## 1. 接口一览

| 方法 | 路径 | 用途 | 本机 | 局域网 |
|---|---|---|---|---|
| GET | `/health` | 健康检查、当前模型、内存占用 | 可用 | 可用 |
| POST | `/transcribe` | 上传 wav 文件转写（multipart） | 可用 | 可用 |
| POST | `/transcribe_b64` | base64 音频转写（JSON，便于程序调用） | 可用 | 可用 |
| POST | `/shutdown` | 停止识别服务 | 可用 | 403 拒绝 |

---

## 2. 准备工作

### 2.1 拿到节点 IP

在运行 `AsrAgent.exe` 的机器上执行：

```powershell
ipconfig | Select-String -Pattern "IPv4"
```

假设得到 `192.168.1.20`，则接口根地址为 `http://192.168.1.20:38088`。

### 2.2 放行端口（只需一次）

Windows 首次启动模型时会弹「Windows 安全中心警报」，勾选 **专用网络** 并允许即可。

若没有弹窗（或被误点拒绝），用管理员权限的 PowerShell 执行：

```powershell
netsh advfirewall firewall add rule name="AsrNode API 38088" dir=in action=allow protocol=TCP localport=38088
```

删除该规则：`netsh advfirewall firewall delete rule name="AsrNode API 38088"`

### 2.3 确认服务已就绪

```powershell
curl.exe http://192.168.1.20:38088/health
```

- `status` 为 `ok` 表示可以转写；为 `loading` 表示模型还在加载（首次启动通常 10~30 秒），稍后重试。
- 连不上先排查：`AsrAgent.exe` 里模型是否显示「运行中」、端口 38088 是否被防火墙拦截、两台机器是否同一网段。

---

## 3. GET /health

### 请求

```
GET http://<节点IP>:38088/health
```

### 响应示例

```json
{
  "status": "ok",
  "device": "cpu",
  "model": "qwen3",
  "mem_mb": 897,
  "features": ["file_transcribe", "speaker_diarization"]
}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| status | string | `ok` 可转写；`loading` 模型加载中 |
| device | string | 固定 `cpu`（纯 CPU INT8 推理） |
| model | string | 当前主模型：`qwen3` / `sensevoice` / `paraformer` |
| mem_mb | int | 识别进程内存占用（MB） |
| features | array | 固定为文件转写 + 说话人分离 |

---

## 4. POST /transcribe（上传文件）

### 请求

`multipart/form-data`，文件字段名固定为 **`file`**，文件名后缀必须是 `.wav`。

```bash
curl -X POST "http://192.168.1.20:38088/transcribe" ^
     -F "file=@D:\audio\meeting.wav"
```

```powershell
curl.exe -X POST "http://192.168.1.20:38088/transcribe" -F "file=@D:\audio\meeting.wav"
```

### 响应示例

```json
{
  "text": "大家好，今天讨论三件事。第一是预算。",
  "sentences": [
    { "text": "大家好，今天讨论三件事。", "start": 0, "end": 2400, "spk": 0 },
    { "text": "第一是预算。", "start": 2400, "end": 4180, "spk": 1 }
  ],
  "speakers": [0, 1],
  "duration_sec": 12.34,
  "elapsed_sec": 5.21,
  "id": "0f1c2f9e-6a3c-4c1e-9d2b-8a7f5c3e1b44"
}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| text | string | 全文（各句拼接，已加标点） |
| sentences | array | 逐句结果，见下表 |
| speakers | array\<int\> | 出现的说话人编号，从 0 开始 |
| duration_sec | float | 音频时长（秒） |
| elapsed_sec | float | 服务端处理耗时（秒） |
| id | string | 本次任务 id（服务端生成 UUID） |

`sentences[]` 字段：

| 字段 | 类型 | 说明 |
|---|---|---|
| text | string | 该句文本（含标点） |
| start | int | 起始时间，**毫秒** |
| end | int | 结束时间，**毫秒** |
| spk | int \| null | 说话人编号；无法判定说话人时为 `null` |

> 说话人编号只在单次请求内有效，跨请求不保证是同一人。

---

## 5. POST /transcribe_b64（JSON，推荐程序调用）

省去上传文件与临时目录，适合 Agent / 脚本直接调用。

### 请求

```
POST http://<节点IP>:38088/transcribe_b64
Content-Type: application/json
```

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| audio_b64 | string | 是 | wav 文件的 base64（不含 `data:audio/wav;base64,` 前缀） |
| format | string | 否 | 固定填 `wav`，默认 `wav`；其他格式会被拒绝 |
| id | string | 否 | 自定义任务 id，原样回传到响应；不传则服务端生成 UUID |

```json
{
  "audio_b64": "UklGRiQAAABXQVZFZm10IBAAAAABAAEA...",
  "format": "wav",
  "id": "task-20261029-001"
}
```

### 响应

与 `/transcribe` 完全一致，唯一区别是 `id` 回传请求里的值。

### Python 示例

```python
import base64, json, urllib.request

with open(r"D:\audio\meeting.wav", "rb") as f:
    audio = base64.b64encode(f.read()).decode("ascii")

req = urllib.request.Request(
    "http://192.168.1.20:38088/transcribe_b64",
    data=json.dumps({
        "audio_b64": audio,
        "format": "wav",
        "id": "task-001",
    }).encode("utf-8"),
    headers={"Content-Type": "application/json"},
    method="POST",
)
with urllib.request.urlopen(req, timeout=600) as r:   # 长音频超时给足
    res = json.loads(r.read().decode("utf-8"))

print(res["id"], res["duration_sec"], res["elapsed_sec"], res["speakers"])
print(res["text"])
```

### Node.js / 其他 Agent 示例

```javascript
const fs = require("fs");
const body = JSON.stringify({
  audio_b64: fs.readFileSync("D:/audio/meeting.wav").toString("base64"),
  format: "wav",
  id: "task-001",
});
const res = await fetch("http://192.168.1.20:38088/transcribe_b64", {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body,
});
const data = await res.json();   // data.text / data.sentences / data.speakers
```

---

## 6. POST /shutdown

停止识别服务（GUI 里点「停止」走的也是这个接口）。

- 只允许 **本机** 调用（源地址为 `127.0.0.1` / `::1`），局域网调用返回 `403 {"detail":"仅允许本机调用"}`。
- 正常调用无响应体，进程立即退出，端口释放。

```powershell
curl.exe -X POST http://127.0.0.1:38088/shutdown
```

---

## 7. 错误码与排查

| HTTP | detail 示例 | 原因与处理 |
|---|---|---|
| 400 | `仅支持 wav 格式, 请先用 transcode.py 或 ffmpeg 转码...` | 传了 mp3/m4a 等，先转码 |
| 400 | `采样率必须为 16000Hz, 当前 44100Hz (请用 transcode.py 转码)` | wav 采样率不是 16 kHz |
| 400 | `空文件` / `空音频` | 请求体为空 |
| 400 | `audio_b64 不是合法的 base64` | base64 字符串有误（注意别带 data URI 前缀） |
| 403 | `仅允许本机调用` | 在局域网机器上调用了 `/shutdown` |
| 503 | `模型尚未加载完成` | 服务刚启动，等 `/health` 返回 `ok` 再重试 |
| 500 | `转写失败: ...` | 音频损坏或推理异常，详见节点目录下 `asr_server.log` |
| 连不上 | — | 防火墙未放行 38088 / 模型未启动 / IP 或网段不对 |

---

## 8. 转码（客户端自己处理）

服务端**只接受 16 kHz wav**。其他格式先转：

```powershell
ffmpeg -i input.mp3 -ar 16000 -ac 1 -c:a pcm_s16le output.wav
```

- 节点自带 ffmpeg（`tools\ffmpeg.exe`，界面上下载「基础包」后获得），也可用客户端自己的 ffmpeg。
- 采样率必须是 16000，声道数不限（多声道会取平均转单声道）。

---

## 9. 调用约定（重要）

1. **串行调用**：单节点只有一个识别进程、占用 CPU 做推理，建议一次只跑一个转写请求，并发请求会排队并显著变慢。
2. **超时给足**：CPU 推理速度约为实时的 3 倍速（RTF≈0.3），10 分钟音频约需 3 分钟。客户端读超时建议 ≥ 600 秒。
3. **先查健康**：调用前 `GET /health` 确认 `status=ok`，避免 503。
4. **不要频繁切换模型**：模型切换由节点界面操作，切换期间（数十秒）服务不可用。
5. **端口固定**：`38088`。虽然可用环境变量 `ASR_PORT` 覆盖，但 GUI 的探活固定走 38088，改了会导致界面显示「未运行」，不建议修改。
6. **安全边界**：接口无鉴权，任何能访问该端口的机器都能转写并看到识别结果，请只在可信局域网使用；不要把 38088 直接映射到公网。
