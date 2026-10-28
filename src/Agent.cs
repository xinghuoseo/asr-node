// ============================================================
// 语音识别 Agent (C# WinForms 版)
// 功能:
//   1. 启动/停止本地模型服务 (asr_server.py, 127.0.0.1:8088)
//   2. 按需下载: 基础包(ffmpeg) 与 识别模型, 多源测速 + 断点续传 + sha256 校验
//      下载完成后自动重新加载模型
//   3. 连接服务器: wss:// 端点 + token, 长连接自动重连
//      服务器下发 {"type":"call","id":..,"action":"transcribe","payload":{..}}
//      本地转写后回传 {"type":"result","id":..,"ok":true,"data":{...}}
//   4. 建档管理: 转写记录存 records.json, 左侧列表选中后右侧显示
//      分句 + 说话人 + 时间轴
//   5. 最小化/关闭 -> 缩到系统托盘(右下角), 托盘双击弹出主窗口
// 编译(无需安装任何东西, 用 Win10 自带编译器):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
//     /nologo /target:winexe /codepage:65001 /win32icon:app.ico
//     /r:System.Windows.Forms.dll /r:System.Drawing.dll
//     /r:System.Web.Extensions.dll
//     /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll
//     /out:AsrAgent.exe Agent.cs
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AsrAgent
{
    public static class Globals
    {
        public static string BaseDir;
        public static string PyExe;
        public static string ServerPy;
        public static string ConfigPath;
        public static string RecordsPath;
        public static string ToolsDir;       // 基础包(ffmpeg)解压目录
        public static string ModelsDir;      // 模型解压目录
        public static string ManifestPath;   // models.json 下载清单
        public const string Api = "http://127.0.0.1:8088";
        public static readonly JavaScriptSerializer Js = CreateSerializer();

        private static JavaScriptSerializer CreateSerializer()
        {
            var js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;   // 音频 base64 很大, 取消默认 2MB 限制
            return js;
        }
        public static string FindFfmpeg()
        {
            try
            {
                // 优先使用按需下载的基础包 tools\ffmpeg.exe
                if (!string.IsNullOrEmpty(ToolsDir))
                {
                    string local = Path.Combine(ToolsDir, "ffmpeg.exe");
                    if (File.Exists(local)) return local;
                }
                // 开发环境回退: 直接使用 imageio_ffmpeg 自带的二进制
                string dir = Path.Combine(BaseDir, ".venv", "Lib", "site-packages",
                    "imageio_ffmpeg", "binaries");
                if (Directory.Exists(dir))
                {
                    foreach (string f in Directory.GetFiles(dir, "ffmpeg*.exe")) return f;
                }
            }
            catch { }
            return null;
        }
    }

    // ---------------- 配置 ----------------
    public class Config
    {
        public string server_url = "";
        public string token = "";
        public bool auto_start_model = true;
        public bool auto_connect = true;
        public bool show_token = false;
        public string asr_model = "qwen3";   // qwen3(精准,默认) | sensevoice(快) | paraformer

        public static Config Load()
        {
            try
            {
                if (File.Exists(Globals.ConfigPath))
                    return Globals.Js.Deserialize<Config>(File.ReadAllText(Globals.ConfigPath, Encoding.UTF8));
            }
            catch { }
            return new Config();
        }
        public void Save()
        {
            File.WriteAllText(Globals.ConfigPath, Globals.Js.Serialize(this), Encoding.UTF8);
        }
    }

    // ---------------- 转写记录 ----------------
    public class Sent
    {
        public string text;
        public double? start;   // 毫秒
        public double? end;
        public int? spk;
    }

    public class Record
    {
        public string id;
        public string created_at;
        public string source;
        public string filename;
        public double? duration_sec;
        public int speakers;
        public string text;
        public double? elapsed_sec;
        public List<Sent> sentences;
    }

    public class RecordStore
    {
        public List<Record> Items = new List<Record>();
        public RecordStore()
        {
            try
            {
                if (File.Exists(Globals.RecordsPath))
                {
                    Items = Globals.Js.Deserialize<List<Record>>(
                        File.ReadAllText(Globals.RecordsPath, Encoding.UTF8));
                    if (Items == null) Items = new List<Record>();
                }
            }
            catch { Items = new List<Record>(); }
        }
        public void Save()
        {
            File.WriteAllText(Globals.RecordsPath, Globals.Js.Serialize(Items), Encoding.UTF8);
        }
        public Record AddFromResult(Dictionary<string, object> r, string source,
            string filename, string fmt)
        {
            var rec = new Record();
            rec.id = Util.GetStr(r, "id");
            if (string.IsNullOrEmpty(rec.id)) rec.id = Guid.NewGuid().ToString("N");
            rec.created_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            rec.source = source;
            rec.filename = filename;
            rec.duration_sec = Util.GetDbl(r, "duration_sec");
            rec.elapsed_sec = Util.GetDbl(r, "elapsed_sec");
            rec.text = Util.GetStr(r, "text");
            var spk = r.ContainsKey("speakers") ? Util.AsList(r["speakers"]) : null;
            rec.speakers = spk != null ? spk.Count : 0;
            rec.sentences = ParseSentences(r);
            Items.Insert(0, rec);
            if (Items.Count > 2000) Items.RemoveRange(2000, Items.Count - 2000);
            Save();
            return rec;
        }

        private static List<Sent> ParseSentences(Dictionary<string, object> r)
        {
            var list = new List<Sent>();
            object arr;
            if (r == null || !r.TryGetValue("sentences", out arr)) return list;
            var items = Util.AsList(arr);
            foreach (var o in items)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                var s = new Sent();
                s.text = Util.GetStr(d, "text");
                s.start = Util.GetDbl(d, "start");
                s.end = Util.GetDbl(d, "end");
                double? spk = Util.GetDbl(d, "spk");
                s.spk = spk.HasValue ? (int)spk.Value : (int?)null;
                list.Add(s);
            }
            return list;
        }
    }

    public static class Util
    {
        public static string GetStr(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) ? v as string : null;
        }
        public static double? GetDbl(Dictionary<string, object> d, string k)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return null;
            try { return Convert.ToDouble(v); } catch { return null; }
        }
        // JavaScriptSerializer 把 JSON 数组反序列化成 ArrayList(非泛型 IEnumerable),
        // 不能用 as IEnumerable<object>, 必须逐项枚举
        public static List<object> AsList(object o)
        {
            var list = new List<object>();
            if (o == null) return list;
            var gen = o as IEnumerable<object>;
            if (gen != null) { list.AddRange(gen); return list; }
            var raw = o as System.Collections.IEnumerable;
            if (raw != null) { foreach (var x in raw) list.Add(x); }
            return list;
        }
    }

    // ---------------- 按需下载: 清单 / 测速 / 断点续传 ----------------
    public class PackInfo
    {
        public string title = "";
        public string zip = "";
        public long size;
        public string sha256 = "";
        public string dest = "";
        public List<string> need = new List<string>();
    }

    // models.json: 下载源 + 各资源包(ffmpeg / qwen3 / sensevoice / paraformer)
    public class Manifest
    {
        public int version;
        public List<string> sources = new List<string>();
        public Dictionary<string, PackInfo> packs = new Dictionary<string, PackInfo>();

        public static Manifest Load()
        {
            try
            {
                if (!File.Exists(Globals.ManifestPath)) return null;
                var m = Globals.Js.Deserialize<Manifest>(
                    File.ReadAllText(Globals.ManifestPath, Encoding.UTF8));
                if (m == null || m.packs == null || m.packs.Count == 0) return null;
                return m;
            }
            catch { return null; }
        }
    }

    public static class Net
    {
        // 探测单个 URL 可用性, 返回毫秒延迟; -1 表示不可用
        public static int Probe(string url, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (Head(url, timeoutMs)) return (int)sw.ElapsedMilliseconds;
            }
            catch { }
            return -1;
        }

        private static bool Head(string url, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "HEAD";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "AsrAgent/1.0";
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    int code = (int)resp.StatusCode;
                    return code >= 200 && code < 400;
                }
            }
            catch (WebException we)
            {
                var hr = we.Response as HttpWebResponse;
                // 部分镜像不支持 HEAD, 退化为 Range: bytes=0-0
                if (hr != null && hr.StatusCode == HttpStatusCode.MethodNotAllowed)
                    return RangeProbe(url, timeoutMs);
                return false;
            }
            catch { return false; }
        }

        private static bool RangeProbe(string url, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.AddRange(0, 0);
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "AsrAgent/1.0";
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    int code = (int)resp.StatusCode;
                    return code >= 200 && code < 400;
                }
            }
            catch { return false; }
        }

        // 并行测速, 按延迟升序返回可用源; 全部失败时回退为清单原始顺序
        public static List<string> OrderSources(List<string> sources, string file, int timeoutMs)
        {
            var result = new List<string>();
            if (sources == null || sources.Count == 0) return result;

            var urls = new List<string>();
            var tasks = new List<Task<int>>();
            foreach (string s0 in sources)
            {
                if (string.IsNullOrEmpty(s0)) continue;
                string s = s0.TrimEnd('/');
                string u = s + "/" + file;
                urls.Add(s);
                tasks.Add(Task.Run(delegate { return Probe(u, timeoutMs); }));
            }

            try { Task.WaitAll(tasks.ToArray(), timeoutMs + 2000); }
            catch { }

            var ok = new List<KeyValuePair<int, string>>();
            for (int i = 0; i < tasks.Count; i++)
            {
                int ms = -1;
                try { if (tasks[i].IsCompleted) ms = tasks[i].Result; }
                catch { }
                if (ms >= 0) ok.Add(new KeyValuePair<int, string>(ms, urls[i]));
            }
            ok.Sort(delegate(KeyValuePair<int, string> a, KeyValuePair<int, string> b)
            {
                return a.Key.CompareTo(b.Key);
            });
            foreach (var kv in ok) result.Add(kv.Value);
            if (result.Count == 0) result.AddRange(urls);
            return result;
        }

        // Range 断点续传下载; 返回 null 成功, 否则错误信息
        public static string Download(string url, string partFile, long expectSize,
            Action<long, long> onProgress)
        {
            try
            {
                string dir = Path.GetDirectoryName(partFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                long have = File.Exists(partFile) ? new FileInfo(partFile).Length : 0;
                if (expectSize > 0 && have > expectSize)
                {
                    File.Delete(partFile);
                    have = 0;
                }
                // .part 已是完整文件(上次下载完成但后续步骤中断), 无需再请求: AddRange(have) 会得到 416
                if (expectSize > 0 && have == expectSize)
                {
                    if (onProgress != null) onProgress(have, expectSize);
                    return null;
                }

                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 30000;
                req.ReadWriteTimeout = 60000;
                req.UserAgent = "AsrAgent/1.0";
                req.AllowAutoRedirect = true;
                if (have > 0) req.AddRange(have);

                HttpWebResponse resp;
                try
                {
                    resp = (HttpWebResponse)req.GetResponse();
                }
                catch (WebException we)
                {
                    var hr = we.Response as HttpWebResponse;
                    if (have > 0 && hr != null &&
                        hr.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        if (onProgress != null) onProgress(have, have);
                        return null;
                    }
                    throw;
                }

                using (resp)
                {
                    bool append = have > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
                    if (!append) have = 0;   // 服务器不支持 Range, 从头重下
                    long total = resp.ContentLength > 0 ? resp.ContentLength + have : expectSize;
                    using (var fs = new FileStream(partFile,
                        append ? FileMode.Append : FileMode.Create,
                        FileAccess.Write, FileShare.None))
                    using (var st = resp.GetResponseStream())
                    {
                        var buf = new byte[128 * 1024];
                        int n;
                        while ((n = st.Read(buf, 0, buf.Length)) > 0)
                        {
                            fs.Write(buf, 0, n);
                            have += n;
                            if (onProgress != null) onProgress(have, total);
                        }
                    }
                }

                if (expectSize > 0)
                {
                    long got = new FileInfo(partFile).Length;
                    if (got != expectSize)
                        return "大小不符: " + got + " / " + expectSize;
                }
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        public static string Sha256(string path)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(path))
                {
                    byte[] h = sha.ComputeHash(fs);
                    var sb = new StringBuilder(h.Length * 2);
                    foreach (byte b in h) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return null; }
        }

        public static string Mb(long bytes)
        {
            return (bytes / 1048576.0).ToString("0.#");
        }
    }

    // ---------------- HTTP ----------------
    public static class Http
    {
        public static string GetJson(string url, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            using (var resp = req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        // 返回 python 服务的原始 JSON; 失败返回 null 并设置 err
        public static string PostB64(string audioB64, string fmt, string id, out string err)
        {
            err = null;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(Globals.Api + "/transcribe_b64");
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = 600000;
                req.ReadWriteTimeout = 600000;
                var payload = new Dictionary<string, object>();
                payload.Add("audio_b64", audioB64);
                payload.Add("format", fmt);
                payload.Add("id", id);
                var data = Encoding.UTF8.GetBytes(Globals.Js.Serialize(payload));
                req.ContentLength = data.Length;
                using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException we)
            {
                try
                {
                    using (var sr = new StreamReader(we.Response.GetResponseStream(), Encoding.UTF8))
                        err = sr.ReadToEnd();
                }
                catch { }
                if (string.IsNullOrEmpty(err)) err = we.Message;
                return null;
            }
            catch (Exception e)
            {
                err = e.Message;
                return null;
            }
        }
    }

    // ---------------- WebSocket 隧道线程 ----------------
    public class WsClient
    {
        private readonly MainForm _ui;
        private readonly string _url;
        private volatile bool _stop;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private ClientWebSocket _ws;

        public WsClient(MainForm ui, string url)
        {
            _ui = ui;
            _url = url;
        }

        public void Stop()
        {
            _stop = true;
            try { _cts.Cancel(); } catch { }
        }

        public void Run()
        {
            int backoff = 1;
            while (!_stop)
            {
                _ui.UiStatus("connecting", _url);
                try
                {
                    using (_ws = new ClientWebSocket())
                    {
                        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                        var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                        connectCts.CancelAfter(TimeSpan.FromSeconds(15));
                        _ws.ConnectAsync(new Uri(_url), connectCts.Token).Wait();
                        _ui.UiStatus("connected", _url);
                        _ui.UiLog("[隧道] 已连接 " + _url);
                        backoff = 1;
                        ReceiveLoop();
                    }
                }
                catch (Exception e)
                {
                    if (!_stop)
                    {
                        string msg = e.Message;
                        var agg = e as AggregateException;
                        if (agg != null && agg.InnerException != null) msg = agg.InnerException.Message;
                        _ui.UiStatus("disconnected", msg);
                        _ui.UiLog("[隧道] 断开: " + msg + ", " + backoff + "s 后重连");
                        WaitSeconds(backoff);
                        backoff = Math.Min(backoff * 2, 30);
                    }
                }
            }
        }

        private void WaitSeconds(int n)
        {
            for (int i = 0; i < n * 10 && !_stop; i++) Thread.Sleep(100);
        }

        private void Send(string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            _ws.SendAsync(new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text, true, _cts.Token).Wait();
        }

        private void ReceiveLoop()
        {
            var buf = new byte[96 * 1024];
            while (!_stop)
            {
                using (var ms = new MemoryStream())
                {
                    WebSocketReceiveResult rr;
                    do
                    {
                        rr = _ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token).Result;
                        if (rr.MessageType == WebSocketMessageType.Close) return;
                        ms.Write(buf, 0, rr.Count);
                    } while (!rr.EndOfMessage);
                    string text = Encoding.UTF8.GetString(ms.ToArray());
                    HandleMessage(text);
                }
            }
        }

        private void HandleMessage(string json)
        {
            Dictionary<string, object> msg = null;
            try { msg = Globals.Js.Deserialize<Dictionary<string, object>>(json); }
            catch { }
            if (msg == null)
            {
                _ui.UiLog("[隧道] 非JSON消息: " + (json.Length > 100 ? json.Substring(0, 100) : json));
                return;
            }
            string type = Util.GetStr(msg, "type");
            if (type == "call")
            {
                // id 兼容字符串与数字: 数字原样保留, 回传时类型不变, 便于网关按 id 匹配
                object rawId;
                msg.TryGetValue("id", out rawId);
                string id = rawId as string;
                if (string.IsNullOrEmpty(id))
                    id = rawId != null ? Convert.ToString(rawId) : Guid.NewGuid().ToString("N");
                string action = Util.GetStr(msg, "action") ?? "";
                var payload = msg.ContainsKey("payload")
                    ? msg["payload"] as Dictionary<string, object> : null;
                _ui.UiLog("[隧道] 收到调用 action=" + action + " id=" + id);
                HandleCall(id, rawId, action, payload ?? new Dictionary<string, object>());
            }
            else if (type == "ping")
            {
                var pong = new Dictionary<string, object>();
                pong.Add("type", "pong");
                Send(Globals.Js.Serialize(pong));
            }
            else
            {
                _ui.UiLog("[隧道] 忽略消息类型: " + type);
            }
        }

        // 服务器->节点: {"type":"call","id":..,"action":..,"payload":{..}}
        // 节点->服务器: {"type":"result","id":..,"ok":true,"data":{..}} / {"ok":false,"data":{"error":..}}
        private void HandleCall(string id, object rawId, string action, Dictionary<string, object> payload)
        {
            Dictionary<string, object> data = null;
            string err = null;
            if (action == "transcribe")
                data = DoTranscribe(payload, id, out err);
            else if (action == "status")
                data = DoStatus(out err);
            else
                err = "unknown action: " + action;

            var reply = new Dictionary<string, object>();
            reply.Add("type", "result");
            // id 原样回传: 网关下发数字则回数字, 字符串则回字符串
            reply.Add("id", rawId ?? (object)id);
            if (err == null && data != null)
            {
                reply.Add("ok", true);
                reply.Add("data", data);
                _ui.UiLog("[隧道] 回传完成 id=" + id);
            }
            else
            {
                if (err == null) err = "empty result";
                var e = new Dictionary<string, object>();
                e.Add("error", err);
                reply.Add("ok", false);
                reply.Add("data", e);
                _ui.UiLog("[隧道] 调用失败 id=" + id + " : " + err);
            }
            Send(Globals.Js.Serialize(reply));
        }

        // action=transcribe: payload 支持 audio_b64 或 audio_url (二选一), 仅 wav
        private Dictionary<string, object> DoTranscribe(
            Dictionary<string, object> payload, string id, out string err)
        {
            err = null;
            string b64 = Util.GetStr(payload, "audio_b64");
            string url = Util.GetStr(payload, "audio_url");
            string fmt = Util.GetStr(payload, "format") ?? "wav";
            string filename = Util.GetStr(payload, "filename");
            string tmp = null;
            try
            {
                if (!string.IsNullOrEmpty(b64))
                {
                    tmp = Path.Combine(Path.GetTempPath(), id + "." + fmt.TrimStart('.'));
                    File.WriteAllBytes(tmp, Convert.FromBase64String(b64));
                }
                else if (!string.IsNullOrEmpty(url))
                {
                    string ext = "wav";
                    int k = url.LastIndexOf('.');
                    if (k > 0 && url.Length - k <= 6) ext = url.Substring(k + 1);
                    tmp = Path.Combine(Path.GetTempPath(), id + "." + ext.TrimEnd('/'));
                    using (var wc = new WebClient())
                        wc.DownloadFile(url, tmp);
                }
                else
                {
                    err = "payload 需要 audio_b64 或 audio_url";
                    return null;
                }
                if (!fmt.Equals("wav", StringComparison.OrdinalIgnoreCase))
                {
                    err = "仅支持 wav 格式(16kHz 单声道), 请先转码";
                    return null;
                }

                string json = Http.PostB64(
                    Convert.ToBase64String(File.ReadAllBytes(tmp)), "wav", id, out err);
                if (json == null) return null;

                var data = Globals.Js.Deserialize<Dictionary<string, object>>(json);
                string fname = filename;
                if (string.IsNullOrEmpty(fname))
                {
                    fname = "remote_" + id.Substring(0, Math.Min(8, id.Length)) + ".wav";
                    if (!string.IsNullOrEmpty(url))
                    {
                        try { fname = Path.GetFileName(new Uri(url).LocalPath); } catch { }
                    }
                }
                _ui.UiAddRemoteRecord(fname, data);
                return data;
            }
            catch (Exception e)
            {
                err = "音频处理失败: " + e.Message;
                return null;
            }
            finally
            {
                try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        // action=status: 返回本地模型健康状态
        private Dictionary<string, object> DoStatus(out string err)
        {
            err = null;
            try
            {
                return Globals.Js.Deserialize<Dictionary<string, object>>(
                    Http.GetJson(Globals.Api + "/health", 3000));
            }
            catch (Exception e)
            {
                err = e.Message;
                return null;
            }
        }
    }

    // ---------------- 主窗口 ----------------
    public class MainForm : Form
    {
        private Config _cfg;
        private readonly RecordStore _records = new RecordStore();
        private Process _proc;
        private WsClient _ws;
        private bool _transcribing;
        private bool _modelExternal;   // 模型服务由外部启动(孤儿/手动), GUI 仅接管状态
        private Thread _wsThread;
        private bool _autoConnectFired;
        private bool _balloonShown;

        private Button _btnStart, _btnStop, _btnConnect, _btnDisconnect,
            _btnTranscribe, _btnDelete, _btnFfmpeg, _btnModelDl;
        private Label _lblModel, _lblGpu, _lblConn, _lblDl, _lblDetail;
        private TextBox _txtUrl;
        private CheckBox _chkAutoModel, _chkAutoConn;
        private ComboBox _cmbModel;   // 识别模型切换: 快速/精准/经典
        private ListView _lv;
        private RichTextBox _txtDetail;   // 右侧详情: 分句 + 说话人 + 时间轴
        private TextBox _txtLog;
        private NotifyIcon _tray;
        private System.Windows.Forms.Timer _timer;
        private System.Windows.Forms.Timer _saveTimer;   // 输入防抖: 停止输入800ms后自动保存配置

        private Manifest _man;            // models.json 缓存
        private string _currentModel;     // 当前进程实际加载的 ASR_MODEL
        private bool _downloading;        // 同一时刻只允许一个下载任务
        private string _dlKey;            // 正在下载的包名
        private int _lastProgTick;
        private int _lastLogPct;
        private long _dlGot, _dlTotal;

        public MainForm(EventWaitHandle showEvent)
        {
            _cfg = Config.Load();
            Text = "语音识别 Agent";
            Size = new Size(1200, 780);
            MinimumSize = new Size(1020, 720);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // 监听第二个实例的唤起信号: 双击新 exe 时弹出本窗口
            if (showEvent != null)
            {
                var t = new Thread(delegate()
                {
                    while (showEvent.WaitOne())
                    {
                        try { BeginInvoke((MethodInvoker)delegate { ShowFromTray(); }); }
                        catch { break; }
                    }
                });
                t.IsBackground = true;
                t.Start();
            }

            BuildUi();
            BuildTray();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 5000;
            _timer.Tick += OnHealthTick;
            _timer.Start();

            // 端点/Token 输入即自动保存(停止输入800ms后落盘), 无需保存按钮
            _saveTimer = new System.Windows.Forms.Timer();
            _saveTimer.Interval = 800;
            _saveTimer.Tick += delegate
            {
                _saveTimer.Stop();
                SaveConfig();
            };
            _txtUrl.TextChanged += delegate { _saveTimer.Stop(); _saveTimer.Start(); };

            Load += delegate
            {
                RefreshList();
                UpdateDownloadHints();
                if (_cfg.auto_start_model && File.Exists(Globals.PyExe)) StartModel();
            };
        }

        // ---------- UI 构建 ----------
        private void BuildUi()
        {
            int pad = 12;
            int cw = ClientSize.Width;
            int ch = ClientSize.Height;
            int splitter = 6;
            int sw = cw - pad * 2;      // 左右分栏总宽
            int sh = ch - pad * 2;      // 左右分栏总高(贯穿整个窗口)

            // 右栏(详情)宽度保持原样; 左栏(操作区)随窗口变宽
            int rightW = 690;
            int minLeft = 464;
            int leftW = sw - splitter - rightW;
            if (leftW < minLeft) leftW = minLeft;
            if (sw - splitter - leftW < 400) leftW = sw - splitter - 400;

            int lh = sh;                                 // 左栏可用高度
            int gModelH = 132, gConnH = 118, gLogH = 130, gap = 8;
            int recTop = gModelH + gap + gConnH + gap;
            int recH = lh - recTop - gap - gLogH;

            var split = new SplitContainer();
            split.Orientation = Orientation.Vertical;
            split.SetBounds(pad, pad, sw, sh);
            split.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            split.SplitterWidth = splitter;
            split.FixedPanel = FixedPanel.Panel2;   // 右栏固定宽度, 左栏随窗口变宽
            split.Panel1MinSize = 440;
            split.Panel2MinSize = 400;
            try { split.SplitterDistance = leftW; } catch { }
            Controls.Add(split);

            // ===== 左栏 1/4: 模型 =====
            var gModel = new GroupBox();
            gModel.Text = "模型 (127.0.0.1:8088)";
            gModel.SetBounds(0, 0, leftW, gModelH);
            gModel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            int mcw = gModel.ClientSize.Width;

            // 第一行 = [基础包] 识别模型 [下载该模型]
            // 第二行 = [启动][停止] + 下载提示
            // 第三行 = 随程序自动启动 + 运行状态 + 内存
            _btnFfmpeg = new Button();
            _btnFfmpeg.Text = "基础包";
            _btnFfmpeg.SetBounds(12, 22, 92, 28);
            _btnFfmpeg.Click += delegate { DownloadPack("ffmpeg", null); };

            var lblModelSel = new Label();
            lblModelSel.Text = "模型:";
            lblModelSel.TextAlign = ContentAlignment.MiddleLeft;
            lblModelSel.SetBounds(110, 22, 44, 28);

            _cmbModel = new ComboBox();
            _cmbModel.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbModel.Items.AddRange(new object[]
            {
                "快速 SenseVoice",
                "精准 Qwen3 (默认)",
                "经典 Paraformer"
            });
            int idx = 1;   // 默认 Qwen3
            if (_cfg.asr_model == "sensevoice") idx = 0;
            else if (_cfg.asr_model == "paraformer") idx = 2;
            _cmbModel.SelectedIndex = idx;
            _cmbModel.SetBounds(154, 25, 142, 22);
            _cmbModel.SelectedIndexChanged += delegate
            {
                SaveConfig();
                UpdateDownloadHints();
            };

            _btnModelDl = new Button();
            _btnModelDl.Text = "下载该模型";
            _btnModelDl.SetBounds(302, 22, 110, 28);
            _btnModelDl.Click += delegate
            {
                string key = CurrentAsrModel();
                DownloadPack(key, delegate { StartModel(); });
            };

            _btnStart = new Button();
            _btnStart.Text = "启动";
            _btnStart.SetBounds(12, 54, 84, 28);
            _btnStart.Click += delegate { StartModel(); };

            _btnStop = new Button();
            _btnStop.Text = "停止";
            _btnStop.SetBounds(102, 54, 84, 28);
            _btnStop.Click += delegate { StopModel(); };

            _lblDl = new Label();
            _lblDl.ForeColor = Color.DimGray;
            _lblDl.AutoEllipsis = true;
            _lblDl.TextAlign = ContentAlignment.MiddleLeft;
            _lblDl.SetBounds(194, 54, mcw - 206, 28);
            _lblDl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _chkAutoModel = new CheckBox();
            _chkAutoModel.Text = "随程序自动启动";
            _chkAutoModel.SetBounds(12, 88, 132, 26);
            _chkAutoModel.Checked = _cfg.auto_start_model;
            _chkAutoModel.CheckedChanged += delegate { SaveConfig(); };

            _lblModel = new Label();
            _lblModel.Text = "未运行";
            _lblModel.AutoEllipsis = true;
            _lblModel.TextAlign = ContentAlignment.MiddleLeft;
            _lblModel.SetBounds(144, 88, mcw - 144 - 122 - 20, 26);
            _lblModel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _lblGpu = new Label();
            _lblGpu.Text = "内存: -";
            _lblGpu.AutoEllipsis = true;
            _lblGpu.TextAlign = ContentAlignment.MiddleRight;
            _lblGpu.SetBounds(mcw - 122 - 12, 88, 122, 26);
            _lblGpu.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            gModel.Controls.Add(_btnFfmpeg);
            gModel.Controls.Add(lblModelSel);
            gModel.Controls.Add(_cmbModel);
            gModel.Controls.Add(_btnModelDl);
            gModel.Controls.Add(_btnStart);
            gModel.Controls.Add(_btnStop);
            gModel.Controls.Add(_lblModel);
            gModel.Controls.Add(_lblGpu);
            gModel.Controls.Add(_chkAutoModel);
            gModel.Controls.Add(_lblDl);
            split.Panel1.Controls.Add(gModel);

            // ===== 左栏 2/4: 服务器连接 =====
            var gConn = new GroupBox();
            gConn.Text = "服务器连接 (长连接隧道)";
            gConn.SetBounds(0, gModelH + gap, leftW, gConnH);
            gConn.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            int gccw = gConn.ClientSize.Width;

            var lblUrl = new Label();
            lblUrl.Text = "端点";
            lblUrl.SetBounds(12, 26, 40, 24);
            _txtUrl = new TextBox();
            _txtUrl.Text = _cfg.server_url ?? "";
            _txtUrl.SetBounds(56, 24, gccw - 56 - 12 - 158 - 8, 26);
            _txtUrl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            var lblUrlTip = new Label();
            lblUrlTip.Text = "完整地址, 如 wss://host/path?token=xxx";
            lblUrlTip.ForeColor = Color.Gray;
            lblUrlTip.AutoEllipsis = true;
            lblUrlTip.SetBounds(12, 56, gccw - 24, 18);
            lblUrlTip.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _btnConnect = new Button();
            _btnConnect.Text = "连接";
            _btnConnect.SetBounds(gccw - 158 - 12, 24, 76, 26);
            _btnConnect.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnConnect.Click += delegate { ConnectWs(); };

            _btnDisconnect = new Button();
            _btnDisconnect.Text = "断开";
            _btnDisconnect.SetBounds(gccw - 76 - 12, 24, 76, 26);
            _btnDisconnect.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnDisconnect.Enabled = false;
            _btnDisconnect.Click += delegate { DisconnectWs(); };

            _lblConn = new Label();
            _lblConn.Text = "未连接";
            _lblConn.ForeColor = Color.Gray;
            _lblConn.AutoEllipsis = true;
            _lblConn.SetBounds(12, 84, gccw - 12 - 12 - 210, 22);
            _lblConn.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _chkAutoConn = new CheckBox();
            _chkAutoConn.Text = "模型就绪后自动连接";
            _chkAutoConn.SetBounds(gccw - 198 - 12, 82, 198, 24);
            _chkAutoConn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _chkAutoConn.Checked = _cfg.auto_connect;
            _chkAutoConn.CheckedChanged += delegate { SaveConfig(); };

            gConn.Controls.Add(lblUrl);
            gConn.Controls.Add(_txtUrl);
            gConn.Controls.Add(lblUrlTip);
            gConn.Controls.Add(_btnConnect);
            gConn.Controls.Add(_btnDisconnect);
            gConn.Controls.Add(_lblConn);
            gConn.Controls.Add(_chkAutoConn);
            split.Panel1.Controls.Add(gConn);

            // ===== 左栏 3/4: 转写建档 =====
            var gRec = new GroupBox();
            gRec.Text = "转写建档 (选中记录看右侧详情)";
            gRec.SetBounds(0, recTop, leftW, recH);
            gRec.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

            _btnTranscribe = new Button();
            _btnTranscribe.Text = "选择音频转写";
            _btnTranscribe.SetBounds(12, 24, 124, 30);
            _btnTranscribe.Click += delegate { PickAndTranscribe(); };

            _btnDelete = new Button();
            _btnDelete.Text = "删除所选";
            _btnDelete.SetBounds(144, 24, 96, 30);
            _btnDelete.Click += delegate { DeleteSelected(); };

            _lv = new ListView();
            _lv.View = View.Details;
            _lv.FullRowSelect = true;
            _lv.HideSelection = false;
            _lv.SetBounds(12, 62, leftW - 24, recH - 74);
            _lv.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _lv.Columns.Add("时间", 104);
            _lv.Columns.Add("来源", 44);
            _lv.Columns.Add("文件", 140);
            _lv.Columns.Add("时长(s)", 48);
            _lv.Columns.Add("说话人", 54);
            _lv.SelectedIndexChanged += delegate { ShowRecordInPanel(); };
            _lv.DoubleClick += delegate { ShowRecordInPanel(); };

            gRec.Controls.Add(_btnTranscribe);
            gRec.Controls.Add(_btnDelete);
            gRec.Controls.Add(_lv);
            split.Panel1.Controls.Add(gRec);

            // ===== 左栏 4/4: 运行日志 =====
            var gLog = new GroupBox();
            gLog.Text = "运行日志";
            gLog.SetBounds(0, lh - gLogH, leftW, gLogH);
            gLog.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            _txtLog = new TextBox();
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.BackColor = Color.White;
            _txtLog.SetBounds(12, 20, leftW - 24, gLogH - 32);
            _txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _txtLog.Font = new Font("Consolas", 9f);
            gLog.Controls.Add(_txtLog);
            split.Panel1.Controls.Add(gLog);

            // ===== 右栏: 分句详情(贯穿整个窗口高度) =====
            var gDetail = new GroupBox();
            gDetail.Text = "详情 (分句 / 说话人 / 时间轴)";
            gDetail.Dock = DockStyle.Fill;
            gDetail.Padding = new Padding(12, 6, 12, 12);

            _lblDetail = new Label();
            _lblDetail.Text = "详情: 未选择记录";
            _lblDetail.Dock = DockStyle.Top;
            _lblDetail.Height = 32;
            _lblDetail.TextAlign = ContentAlignment.MiddleLeft;
            _lblDetail.Font = new Font("Microsoft YaHei", 9f, FontStyle.Bold);
            _lblDetail.AutoEllipsis = true;

            _txtDetail = new RichTextBox();
            _txtDetail.Dock = DockStyle.Fill;
            _txtDetail.ReadOnly = true;
            _txtDetail.BackColor = Color.White;
            _txtDetail.BorderStyle = BorderStyle.FixedSingle;
            _txtDetail.Font = new Font("Microsoft YaHei", 9.5f);
            _txtDetail.HideSelection = false;

            // 先加 Fill, 再加 Top: 后加的先参与停靠, 保证 Top 不被 Fill 覆盖
            gDetail.Controls.Add(_txtDetail);
            gDetail.Controls.Add(_lblDetail);
            split.Panel2.Controls.Add(gDetail);
        }

        private void BuildTray()
        {
            _tray = new NotifyIcon();
            _tray.Text = "语音识别 Agent";
            try { _tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { _tray.Icon = SystemIcons.Application; }
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ShowFromTray(); };

            var menu = new ContextMenuStrip();
            menu.Items.Add("显示主界面", null, delegate { ShowFromTray(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("启动模型", null, delegate { StartModel(); });
            menu.Items.Add("停止模型", null, delegate { StopModel(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { RealExit(); });
            _tray.ContextMenuStrip = menu;
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void RealExit()
        {
            _tray.Visible = false;
            StopModel();
            DisconnectWs();
            SaveConfig();
            Application.Exit();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized) Hide();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                // 关闭按钮 = 缩到托盘
                e.Cancel = true;
                Hide();
                if (!_balloonShown)
                {
                    _balloonShown = true;
                    _tray.BalloonTipTitle = "语音识别 Agent";
                    _tray.BalloonTipText = "程序已缩小到托盘, 双击托盘图标可再次打开";
                    _tray.ShowBalloonTip(2000);
                }
                return;
            }
            base.OnFormClosing(e);
        }

        // ---------- 模型管理 ----------
        private void StartModel()
        {
            if (!File.Exists(Globals.PyExe))
            {
                MessageBox.Show("未找到 Python 环境: " + Globals.PyExe, "错误");
                return;
            }

            string wantModel = CurrentAsrModel();

            // 本地缺模型: 提示按需下载, 不启动必然失败的服务
            if (!ModelInstalled(wantModel))
            {
                var pi = Pack(wantModel);
                string name = pi != null ? pi.title : wantModel;
                string size = pi != null && pi.size > 0 ? " (" + Net.Mb(pi.size) + "MB)" : "";
                string head = Globals.FindFfmpeg() == null
                    ? "首次使用请先点\"下载基础包\"安装 ffmpeg。\n\n" : "";
                UiLog("[模型] 本地缺少模型 " + wantModel + ", 需要先下载");
                MessageBox.Show(head + "本地缺少模型: " + name + size +
                    "\n\n点\"下载该模型\"按钮下载, 完成后会自动加载。", "缺少模型");
                UpdateDownloadHints();
                return;
            }

            // 本 GUI 启动的进程仍存活: 同模型无事可做; 切换模型则杀旧起新
            if (_proc != null && !_proc.HasExited)
            {
                if (_currentModel == wantModel) return;
                UiLog("[模型] 切换 " + _currentModel + " -> " + wantModel + ", 正在重启进程...");
                try { _proc.Kill(); } catch { }
                _proc = null;
                _modelExternal = false;
                var ui2 = TaskScheduler.FromCurrentSynchronizationContext();
                Task.Run(delegate
                {
                    for (int i = 0; i < 20; i++)
                    {
                        Thread.Sleep(400);
                        try { Http.GetJson(Globals.Api + "/health", 400); }
                        catch { return; }   // 端口已释放
                    }
                }).ContinueWith(delegate { StartModelProcess(wantModel); }, ui2);
                return;
            }

            // 探测 8088 已有实例: 同模型直接复用; 不同模型自动停旧起新
            string runningModel = null;
            bool alive = false;
            try
            {
                string h = Http.GetJson(Globals.Api + "/health", 1500);
                if (h != null && h.Contains("\"status\":\"ok\""))
                {
                    alive = true;
                    runningModel = ExtractModel(h);
                }
            }
            catch { }

            if (alive)
            {
                if (runningModel == wantModel)
                {
                    _modelExternal = true;
                    _lblModel.Text = "运行中 (复用已运行实例)";
                    UiLog("[模型] 检测到 8088 已有健康模型服务(" + runningModel + "), 直接复用");
                    return;
                }
                // 模型不同: 停旧 + 等端口释放 + 用所选模型重启
                UiLog("[模型] 已运行实例为 " + runningModel + ", 与所选 " + wantModel + " 不同, 正在切换...");
                _modelExternal = true;
                StopModel();   // 走 /shutdown 停掉旧实例
                var ui = TaskScheduler.FromCurrentSynchronizationContext();
                Task.Run(delegate
                {
                    for (int i = 0; i < 20; i++)
                    {
                        Thread.Sleep(500);
                        try { Http.GetJson(Globals.Api + "/health", 500); }
                        catch { return; }   // 端口已释放
                    }
                }).ContinueWith(delegate { StartModelProcess(wantModel); }, ui);
                return;
            }

            StartModelProcess(wantModel);
        }

        private string CurrentAsrModel()
        {
            switch (_cmbModel.SelectedIndex)
            {
                case 1: return "qwen3";
                case 2: return "paraformer";
                default: return "sensevoice";
            }
        }

        private static string ExtractModel(string healthJson)
        {
            try
            {
                var d = Globals.Js.Deserialize<Dictionary<string, object>>(healthJson);
                return Util.GetStr(d, "model");
            }
            catch { return null; }
        }

        private void StartModelProcess(string asrModel)
        {
            SaveConfig();
            var psi = new ProcessStartInfo();
            psi.FileName = Globals.PyExe;
            psi.Arguments = "\"" + Globals.ServerPy + "\"";
            psi.WorkingDirectory = Globals.BaseDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";
            psi.EnvironmentVariables["ASR_MODEL"] = asrModel;
            _currentModel = asrModel;
            _modelExternal = false;
            _proc = Process.Start(psi);
            _proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (!string.IsNullOrEmpty(e.Data)) UiLog(e.Data);
            };
            _proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (!string.IsNullOrEmpty(e.Data)) UiLog(e.Data);
            };
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
            _proc.EnableRaisingEvents = true;
            _proc.Exited += delegate
            {
                UiLog("[模型] 进程退出");
                UiStatusModel("未运行");
            };
            UiStatusModel("加载中... (" + asrModel + ")");
            UiLog("[模型] 启动 asr_server.py (模型=" + asrModel + ")");
        }

        private void StopModel()
        {
            if (_proc != null && !_proc.HasExited)
            {
                try { _proc.Kill(); } catch { }
                UiLog("[模型] 已请求停止");
                return;
            }
            if (_modelExternal)
            {
                // 外部/孤儿实例: 通过 /shutdown 接口停止
                Task.Run(delegate
                {
                    try
                    {
                        var req = (HttpWebRequest)WebRequest.Create(Globals.Api + "/shutdown");
                        req.Method = "POST";
                        req.Timeout = 3000;
                        using (req.GetResponse()) { }
                    }
                    catch { }
                });
                _modelExternal = false;
                _lblModel.Text = "未运行";
                UiLog("[模型] 已请求外部实例停止");
            }
        }

        // ---------- 按需下载 (基础包 / 模型) ----------
        private Manifest Man()
        {
            if (_man == null) _man = Manifest.Load();
            return _man;
        }

        private PackInfo Pack(string key)
        {
            var m = Man();
            if (m == null || m.packs == null) return null;
            PackInfo pi;
            return m.packs.TryGetValue(key, out pi) ? pi : null;
        }

        // 清单 need 里的相对路径全部存在, 视为已安装
        private bool PackInstalled(PackInfo pi)
        {
            if (pi == null || pi.need == null || pi.need.Count == 0) return false;
            foreach (string rel in pi.need)
            {
                if (string.IsNullOrEmpty(rel)) continue;
                string p = Path.Combine(Globals.BaseDir,
                    rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(p) && !Directory.Exists(p)) return false;
            }
            return true;
        }

        // 模型是否就绪: 有清单按 need 判断, 无清单退化为目录探测(开发环境)
        private bool ModelInstalled(string key)
        {
            var pi = Pack(key);
            if (pi != null) return PackInstalled(pi);
            try
            {
                string root = string.IsNullOrEmpty(Globals.ModelsDir)
                    ? Path.Combine(Globals.BaseDir, "models") : Globals.ModelsDir;
                string d = Path.Combine(root, key);
                return Directory.Exists(d) &&
                    Directory.GetFiles(d, "*.onnx", SearchOption.AllDirectories).Length > 0;
            }
            catch { return false; }
        }

        private void UpdateDownloadHints()
        {
            if (_btnFfmpeg == null || _btnModelDl == null) return;
            bool hasMan = Man() != null;

            if (_downloading)
            {
                if (_dlKey == "ffmpeg")
                {
                    _btnFfmpeg.Enabled = false;
                    _btnModelDl.Enabled = false;
                }
                else
                {
                    _btnFfmpeg.Enabled = false;
                    _btnModelDl.Enabled = false;
                }
                _lblDl.Text = "下载中 " + Net.Mb(_dlGot) + " / " + Net.Mb(_dlTotal) + " MB, 进度见日志";
                return;
            }

            // 基础包: 已存在则灰掉并显示就绪
            if (Globals.FindFfmpeg() != null)
            {
                _btnFfmpeg.Text = "ffmpeg 已就绪";
                _btnFfmpeg.Enabled = false;
            }
            else
            {
                var pf = Pack("ffmpeg");
                _btnFfmpeg.Text = "下载基础包" +
                    (pf != null && pf.size > 0 ? " " + Net.Mb(pf.size) + "MB" : "");
                _btnFfmpeg.Enabled = hasMan;
            }

            // 模型: 已安装则灰掉
            string key = CurrentAsrModel();
            var pm = Pack(key);
            bool ok = ModelInstalled(key);
            _btnModelDl.Enabled = hasMan && !ok;
            if (ok) _btnModelDl.Text = "模型已就绪";
            else if (!hasMan) _btnModelDl.Text = "缺少 models.json";
            else _btnModelDl.Text = "下载该模型" +
                (pm != null && pm.size > 0 ? " " + Net.Mb(pm.size) + "MB" : "");

            string tip;
            if (!hasMan) tip = "缺少 models.json 下载清单, 无法在线下载";
            else if (Globals.FindFfmpeg() == null) tip = "首次使用请先下载基础包 (ffmpeg), 用于音频转码";
            else if (!ok) tip = "本地缺少该模型" + (pm != null ? ": " + pm.title +
                (pm.size > 0 ? " (" + Net.Mb(pm.size) + "MB)" : "") : "");
            else tip = "";
            _lblDl.Text = tip;
        }

        private void DownloadPack(string key, Action after)
        {
            if (_downloading)
            {
                UiLog("[下载] 已有下载任务在进行, 请稍候");
                return;
            }
            var man = Man();
            if (man == null)
            {
                MessageBox.Show("缺少 models.json 下载清单, 无法在线下载。\n请确认程序目录完整。", "提示");
                return;
            }
            var pi = Pack(key);
            if (pi == null)
            {
                MessageBox.Show("清单中没有该资源包: " + key, "提示");
                return;
            }
            if (string.IsNullOrEmpty(pi.zip))
            {
                MessageBox.Show("清单中该资源包缺少 zip 字段: " + key, "提示");
                return;
            }

            _downloading = true;
            _dlKey = key;
            _dlGot = 0;
            _dlTotal = pi.size;
            _lastProgTick = 0;
            _lastLogPct = 0;
            UpdateDownloadHints();
            UiLog("[下载] 开始: " + pi.title + " (" + Net.Mb(pi.size) + "MB), 正在测速选择最快源...");

            var ui = TaskScheduler.FromCurrentSynchronizationContext();
            Task.Run(delegate { return DoDownload(man, pi); })
            .ContinueWith(delegate(Task<string> t)
            {
                string err = null;
                try { err = t.Result; } catch (Exception e) { err = e.Message; }

                _downloading = false;
                _dlKey = null;
                UpdateDownloadHints();
                if (err == null)
                {
                    UiLog("[下载] 完成: " + pi.title + " 已解压到程序目录");
                    if (after != null) after();
                }
                else
                {
                    UiLog("[下载] 失败: " + err);
                    MessageBox.Show("下载失败: " + err +
                        "\n\n已下载的部分会保留, 再次点击下载可断点续传。", "下载失败");
                }
            }, ui);
        }

        // 返回 null 表示成功
        private string DoDownload(Manifest man, PackInfo pi)
        {
            string part = Path.Combine(Globals.BaseDir, "downloads", pi.zip + ".part");

            var sources = Net.OrderSources(man.sources, pi.zip, 3000);
            if (sources.Count == 0) return "没有可用的下载源";
            UiLog("[下载] 选源顺序: " + string.Join(" > ", sources.ToArray()));

            string err = "没有可用的下载源";
            foreach (string src in sources)
            {
                string url = src.TrimEnd('/') + "/" + pi.zip;
                UiLog("[下载] 尝试: " + url);
                err = Net.Download(url, part, pi.size, ReportDownload);
                if (err == null) break;
                UiLog("[下载] 该源失败: " + err);
            }
            if (err != null) return err;

            if (!string.IsNullOrEmpty(pi.sha256))
            {
                UiLog("[下载] 校验 sha256 ...");
                string h = Net.Sha256(part);
                if (h == null || !h.Equals(pi.sha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(part); } catch { }
                    return "sha256 校验失败, 已删除损坏文件, 请重新下载";
                }
                UiLog("[下载] sha256 校验通过");
            }

            try
            {
                UiLog("[下载] 解压到 " + Globals.BaseDir + " ...");
                ExtractZip(part, Globals.BaseDir);
                try { File.Delete(part); } catch { }
            }
            catch (Exception e) { return "解压失败: " + e.Message; }
            return null;
        }

        // .NET 4.0 无覆盖式解压重载, 手动逐项写入(已存在则覆盖)
        private static void ExtractZip(string zip, string dest)
        {
            using (var za = ZipFile.OpenRead(zip))
            {
                foreach (var e in za.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;   // 目录项跳过
                    string target = Path.Combine(dest,
                        e.FullName.Replace('/', Path.DirectorySeparatorChar));
                    string dir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    using (var src = e.Open())
                    using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                        src.CopyTo(fs);
                }
            }
        }

        // 下载线程回调: 限频刷新界面 + 按 5% 步长写日志
        private void ReportDownload(long got, long total)
        {
            _dlGot = got;
            _dlTotal = total;
            int pct = total > 0 ? (int)(got * 100 / total) : -1;
            if (Environment.TickCount - _lastProgTick < 250 && got < total) return;
            _lastProgTick = Environment.TickCount;
            bool doLog = pct >= 0 && pct - _lastLogPct >= 5;
            if (doLog) _lastLogPct = pct;

            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    string ps = pct >= 0 ? pct + "%" : "";
                    _lblDl.Text = "下载中 " + Net.Mb(got) + " / " + Net.Mb(total) + " MB " + ps;
                    Button b = _dlKey == "ffmpeg" ? _btnFfmpeg : _btnModelDl;
                    if (b != null) b.Text = "下载中 " + ps;
                });
            }
            catch { }
            if (doLog)
                UiLog("[下载] " + Net.Mb(got) + " / " + Net.Mb(total) + " MB (" + pct + "%)");
        }

        private void OnHealthTick(object s, EventArgs e)
        {
            // 无论模型由本 GUI 启动还是外部实例(孤儿/手动), 都持续探测健康状态
            var task = Task.Run(delegate
            {
                try { return Http.GetJson(Globals.Api + "/health", 2000); }
                catch { return null; }
            });
            task.ContinueWith(delegate(Task<string> t)
            {
                string json = t.Result;
                if (json == null)
                {
                    // 8088 无健康服务
                    if (_modelExternal)
                    {
                        _modelExternal = false;
                        _lblModel.Text = "未运行";
                        UiLog("[模型] 外部实例已停止");
                    }
                    else if (_proc != null && !_proc.HasExited)
                    {
                        _lblModel.Text = "加载中...";
                    }
                    return;
                }
                var h = Globals.Js.Deserialize<Dictionary<string, object>>(json);
                if (h == null) return;

                // 同步服务端实际加载的模型名, 供切换判断与下载按钮状态使用
                string hm = Util.GetStr(h, "model");
                bool modelChanged = !string.IsNullOrEmpty(hm) && hm != _currentModel;
                if (!string.IsNullOrEmpty(hm)) _currentModel = hm;
                object memObj;
                if (h.TryGetValue("mem_mb", out memObj))
                    _lblGpu.Text = "内存: " + memObj + " MB";
                string status = Util.GetStr(h, "status");
                if (status == "ok")
                {
                    // 健康服务存在; 若非本 GUI 启动, 标记为外部实例并接管状态
                    if (_proc == null || _proc.HasExited)
                    {
                        if (!_modelExternal)
                        {
                            _modelExternal = true;
                            _lblModel.Text = "运行中 (复用已运行实例)";
                            UiLog("[模型] 检测到已有健康模型服务, 已接管状态");
                        }
                    }
                    else
                    {
                        _lblModel.Text = "运行中 (就绪)";
                    }
                    if (modelChanged) UpdateDownloadHints();
                    if (_chkAutoConn.Checked && _ws == null && !_autoConnectFired)
                    {
                        _autoConnectFired = true;
                        ConnectWs();
                    }
                }
                else
                {
                    _lblModel.Text = "加载中...";
                }
            });
        }

        // ---------- WebSocket ----------
        private void ConnectWs()
        {
            string url = _txtUrl.Text.Trim();
            if (!url.StartsWith("ws://") && !url.StartsWith("wss://"))
            {
                MessageBox.Show("端点必须以 ws:// 或 wss:// 开头", "提示");
                return;
            }
            if (_ws != null) return;

            SaveConfig();
            _lblConn.Text = "连接中...";
            _lblConn.ForeColor = Color.Orange;
            _btnConnect.Enabled = false;
            _btnDisconnect.Enabled = true;
            _ws = new WsClient(this, url);
            _wsThread = new Thread(_ws.Run);
            _wsThread.IsBackground = true;
            _wsThread.Start();
        }

        private void DisconnectWs()
        {
            if (_ws != null)
            {
                _ws.Stop();
                _ws = null;
                UiStatus("disconnected", "已手动断开");
            }
        }

        // ---------- 本地转写 (后台线程, 不卡界面) ----------

        private class TranscribeOutcome
        {
            public string Error;
            public Dictionary<string, object> Data;
            public string Filename;
            public string WavPath;
            public string OriginalPath;   // 用户选择的原始音频(转码前), txt 写到它的同目录
            public string TxtPath;
            public string Elapsed;
        }

        // 记录文本(带说话人标签), 用于 txt 文件与详情展示
        private static string BuildRecordText(Record r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("文件: " + r.filename);
            sb.AppendLine("转写时间: " + r.created_at);
            sb.AppendLine("时长: " + (r.duration_sec.HasValue ? r.duration_sec.Value.ToString("0.#") + "s" : "-")
                + " | 说话人: " + r.speakers + " 人"
                + " | 耗时: " + (r.elapsed_sec.HasValue ? r.elapsed_sec.Value.ToString("0.#") + "s" : "-"));
            sb.AppendLine();
            if (r.sentences != null && r.sentences.Count > 0)
            {
                foreach (var s in r.sentences)
                    sb.AppendLine("[" + (s.spk.HasValue ? "spk" + s.spk.Value : "spk?") + "] " + s.text);
            }
            else
            {
                sb.AppendLine(r.text ?? "");
            }
            return sb.ToString();
        }

        private TranscribeOutcome TranscribeJob(string path)
        {
            var o = new TranscribeOutcome();
            o.OriginalPath = path;
            try
            {
                // 模型未启动时给出明确提示
                if (_proc == null || _proc.HasExited)
                {
                    o.Error = "模型未启动, 请先点击\"启动模型\"并等待其就绪";
                    return o;
                }

                string ffmpeg = Globals.FindFfmpeg();
                string ext = Path.GetExtension(path).ToLowerInvariant();
                // 视频文件: ffmpeg 剥离音轨 -> 同目录同名 .wav (16kHz 单声道)
                bool isVideo = ext == ".mp4" || ext == ".mkv" || ext == ".avi" ||
                    ext == ".mov" || ext == ".flv" || ext == ".webm" || ext == ".ts" ||
                    ext == ".wmv" || ext == ".m4v";
                if (isVideo)
                {
                    if (ffmpeg == null)
                    {
                        o.Error = "未找到内置 ffmpeg, 无法从视频剥离音频";
                        return o;
                    }
                    string dst = Path.ChangeExtension(o.OriginalPath, ".wav");
                    UiLog("[本地] 视频剥离音轨(16kHz wav) -> " + Path.GetFileName(dst));
                    var psi = new ProcessStartInfo();
                    psi.FileName = ffmpeg;
                    psi.Arguments = "-y -i \"" + o.OriginalPath + "\" -vn -ar 16000 -ac 1 \"" + dst + "\"";
                    psi.CreateNoWindow = true;
                    psi.UseShellExecute = false;
                    var p = Process.Start(psi);
                    p.WaitForExit();
                    if (p.ExitCode != 0 || !File.Exists(dst))
                    {
                        o.Error = "视频提取音频失败, 请检查文件是否包含音轨";
                        return o;
                    }
                    path = dst;
                }

                bool needConvert = !path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
                if (!needConvert)
                {
                    // wav 也要校验采样率, 非 16kHz 同样需要重采样
                    try
                    {
                        using (var fs = File.OpenRead(path))
                        {
                            var header = new byte[44];
                            if (fs.Read(header, 0, 44) >= 44 &&
                                BitConverter.ToInt32(header, 24) != 16000)
                                needConvert = true;
                        }
                    }
                    catch { needConvert = true; }
                }

                if (needConvert)
                {
                    if (ffmpeg == null)
                    {
                        o.Error = "未找到内置 ffmpeg, 请先用 transcode.py 转成 16k wav";
                        return o;
                    }
                    // 非 wav -> 同目录同名 .wav (可复用);
                    // wav 但采样率不符 -> 临时目录, 不覆盖源文件
                    string dst = path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".wav")
                        : Path.ChangeExtension(path, ".wav");
                    UiLog("[本地] 自动转码(16kHz) -> " + Path.GetFileName(dst));
                    var psi = new ProcessStartInfo();
                    psi.FileName = ffmpeg;
                    psi.Arguments = "-y -i \"" + path + "\" -ar 16000 -ac 1 -vn \"" + dst + "\"";
                    psi.CreateNoWindow = true;
                    psi.UseShellExecute = false;
                    var p = Process.Start(psi);
                    p.WaitForExit();
                    if (p.ExitCode != 0 || !File.Exists(dst))
                    {
                        o.Error = "转码失败, 请检查音频文件";
                        return o;
                    }
                    path = dst;
                }
                o.WavPath = path;
                o.Filename = Path.GetFileName(o.OriginalPath);

                UiLog("[本地] 转写中, 请稍候...");
                string id = Guid.NewGuid().ToString("N");
                string b64 = Convert.ToBase64String(File.ReadAllBytes(path));
                string err;
                string json = Http.PostB64(b64, "wav", id, out err);
                if (json == null)
                {
                    o.Error = err;
                    return o;
                }
                o.Data = Globals.Js.Deserialize<Dictionary<string, object>>(json);
                o.Elapsed = (Util.GetDbl(o.Data, "elapsed_sec") ?? 0).ToString("0.#");
            }
            catch (Exception ex)
            {
                o.Error = ex.Message;
            }
            return o;
        }

        private void PickAndTranscribe()
        {
            if (_transcribing) return;
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "选择音频/视频 (非 wav 会自动转码)";
                dlg.Filter = "音频/视频|*.wav;*.mp3;*.m4a;*.flac;*.ogg;*.aac;*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm;*.flv;*.ts;*.m4v|所有文件|*.*";
                dlg.InitialDirectory = Path.Combine(Globals.BaseDir, "..", "data");
                if (dlg.ShowDialog() != DialogResult.OK) return;

                string path = dlg.FileName;
                _transcribing = true;
                _btnTranscribe.Enabled = false;
                UiLog("[本地] 处理: " + Path.GetFileName(path));

                var ui = TaskScheduler.FromCurrentSynchronizationContext();
                Task.Run(delegate { return TranscribeJob(path); })
                .ContinueWith(delegate(Task<TranscribeOutcome> t)
                {
                    _transcribing = false;
                    _btnTranscribe.Enabled = true;
                    var o = t.Result;
                    if (o.Error != null)
                    {
                        UiLog("[本地] 转写失败: " + o.Error);
                        MessageBox.Show(o.Error, "转写失败");
                        return;
                    }
                    var rec = _records.AddFromResult(o.Data, "local", o.Filename, "wav");
                    RefreshList();

                    // 结果写入音频同目录同名 txt
                    // txt 始终写到用户原始音频的同目录同名 (而非临时转码产物旁)
                    string txtBase = string.IsNullOrEmpty(o.OriginalPath) ? o.WavPath : o.OriginalPath;
                    o.TxtPath = Path.ChangeExtension(txtBase, ".txt");
                    try
                    {
                        File.WriteAllText(o.TxtPath, BuildRecordText(rec), Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                        UiLog("[本地] txt 写入失败: " + ex.Message);
                    }

                    UiLog("[本地] 转写完成 (" + o.Elapsed + "s), 已保存 " + o.TxtPath);
                    string preview = BuildRecordText(rec);
                    MessageBox.Show(
                        "时长: " + rec.duration_sec + "s | 说话人: " + rec.speakers + " 人 | 耗时: " + o.Elapsed + "s\n" +
                        "已保存: " + o.TxtPath + "\n\n" +
                        (preview.Length > 800 ? preview.Substring(0, 800) + "..." : preview),
                        "转写完成");
                }, ui);
            }
        }

        // ---------- 建档管理 ----------
        private void RefreshList()
        {
            _lv.BeginUpdate();
            _lv.Items.Clear();
            foreach (var r in _records.Items)
            {
                var item = new ListViewItem(r.created_at);
                item.SubItems.Add(r.source);
                item.SubItems.Add(r.filename);
                item.SubItems.Add(r.duration_sec.HasValue ? r.duration_sec.Value.ToString("0.#") : "-");
                item.SubItems.Add(r.speakers.ToString());
                item.Tag = r;
                _lv.Items.Add(item);
            }
            _lv.EndUpdate();
            ClearDetail();
        }

        private static string FmtTime(double? ms)
        {
            if (!ms.HasValue) return "--:--";
            long total = (long)(ms.Value / 1000.0);
            if (total < 0) total = 0;
            long h = total / 3600, m = (total % 3600) / 60, s = total % 60;
            return h > 0
                ? string.Format("{0}:{1:00}:{2:00}", h, m, s)
                : string.Format("{0:00}:{1:00}", m, s);
        }

        private void ClearDetail()
        {
            if (_txtDetail == null) return;
            _txtDetail.Text = "";
            _lblDetail.Text = "详情: 未选择记录 (点左侧任一记录查看分句 / 说话人 / 时间轴)";
        }

        // 右侧面板: 分句 + 说话人 + 时间轴
        private void ShowRecordInPanel()
        {
            if (_lv == null || _txtDetail == null) return;
            if (_lv.SelectedItems.Count == 0) { ClearDetail(); return; }
            var r = (Record)_lv.SelectedItems[0].Tag;
            if (r == null) { ClearDetail(); return; }

            var sb = new StringBuilder();
            sb.AppendLine("文件: " + r.filename);
            sb.AppendLine("转写时间: " + r.created_at
                + "    时长: " + (r.duration_sec.HasValue ? r.duration_sec.Value.ToString("0.#") + "s" : "-")
                + "    说话人: " + r.speakers + " 人"
                + "    耗时: " + (r.elapsed_sec.HasValue ? r.elapsed_sec.Value.ToString("0.#") + "s" : "-"));
            sb.AppendLine(new string('-', 60));

            if (r.sentences != null && r.sentences.Count > 0)
            {
                foreach (var s in r.sentences)
                {
                    string spk = s.spk.HasValue ? "说话人" + (s.spk.Value + 1) : "说话人?";
                    sb.AppendLine("[" + FmtTime(s.start) + " - " + FmtTime(s.end) + "]  " + spk);
                    sb.AppendLine("    " + ((s.text ?? "").Trim()));
                }
            }
            else
            {
                sb.AppendLine(r.text ?? "");
            }

            _txtDetail.Text = sb.ToString();
            _txtDetail.SelectionStart = 0;
            _txtDetail.ScrollToCaret();
            _lblDetail.Text = "详情: " + r.filename + "  (" + r.created_at + ")";
        }

        private void DeleteSelected()
        {
            if (_lv.SelectedItems.Count == 0) return;
            if (MessageBox.Show("删除所选 " + _lv.SelectedItems.Count + " 条记录?",
                "确认", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            foreach (ListViewItem item in _lv.SelectedItems)
                _records.Items.Remove((Record)item.Tag);
            _records.Save();
            RefreshList();
        }

        // ---------- 供隧道线程回调 ----------
        public void UiStatus(string state, string detail)
        {
            try { BeginInvoke((MethodInvoker)delegate
            {
                if (state == "connected")
                {
                    _lblConn.Text = "已连接";
                    _lblConn.ForeColor = Color.Green;
                }
                else if (state == "connecting")
                {
                    _lblConn.Text = "连接中...";
                    _lblConn.ForeColor = Color.Orange;
                }
                else
                {
                    _lblConn.Text = (detail == "已手动断开" || string.IsNullOrEmpty(detail))
                        ? "未连接" : "未连接 (" + detail + ")";
                    _lblConn.ForeColor = Color.Gray;
                    _btnConnect.Enabled = true;
                    _btnDisconnect.Enabled = false;
                    if (_ws != null && state == "disconnected" && detail != "已手动断开")
                        _ws = null;   // 线程自然结束后允许手动重连
                }
            }); } catch { }
        }

        public void UiLog(string line)
        {
            try { BeginInvoke((MethodInvoker)delegate
            {
                _txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + Environment.NewLine);
            }); } catch { }
        }

        public void UiStatusModel(string text)
        {
            try { BeginInvoke((MethodInvoker)delegate { _lblModel.Text = text; }); } catch { }
        }

        public void UiAddRemoteRecord(string filename, Dictionary<string, object> data)
        {
            try { BeginInvoke((MethodInvoker)delegate
            {
                _records.AddFromResult(data, "remote", filename, "wav");
                RefreshList();
            }); } catch { }
        }

        // ---------- 配置 ----------
        private void SaveConfig()
        {
            _cfg.server_url = _txtUrl.Text.Trim();
            _cfg.asr_model = CurrentAsrModel();
            _cfg.auto_start_model = _chkAutoModel.Checked;
            _cfg.auto_connect = _chkAutoConn.Checked;
            try { _cfg.Save(); } catch { }
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            // 过渡方案: 服务器(wss)证书链不完整(nginx 未配置 fullchain, 只有叶子证书),
            // 标准校验必然失败。本进程内仅有这一条 wss 出站连接, 故全局放行;
            // 服务器补全证书链后应删除此回调。
            ServicePointManager.ServerCertificateValidationCallback =
                delegate { return true; };
            ServicePointManager.SecurityProtocol =
                System.Net.SecurityProtocolType.Tls11 | System.Net.SecurityProtocolType.Tls12;

            Globals.BaseDir = AppDomain.CurrentDomain.BaseDirectory;
            Globals.PyExe = Path.Combine(Globals.BaseDir, ".venv", "Scripts", "python.exe");
            Globals.ServerPy = Path.Combine(Globals.BaseDir, "asr_server.py");
            Globals.ConfigPath = Path.Combine(Globals.BaseDir, "agent_config.json");
            Globals.RecordsPath = Path.Combine(Globals.BaseDir, "records.json");
            Globals.ToolsDir = Path.Combine(Globals.BaseDir, "tools");
            Globals.ModelsDir = Path.Combine(Globals.BaseDir, "models");
            Globals.ManifestPath = Path.Combine(Globals.BaseDir, "models.json");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 单实例保护: 已有实例运行时提示并激活其窗口, 防止重复启动导致端口冲突
            bool createdNew;
            using (var mutex = new Mutex(true, "AsrAgent_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    try
                    {
                        using (var ev = EventWaitHandle.OpenExisting("AsrAgent_ShowEvent"))
                            ev.Set();   // 让已运行实例弹出主窗口
                    }
                    catch { }
                    MessageBox.Show("语音识别 Agent 已经在运行中, 请勿重复打开。\n" +
                        "(程序可能缩小到了托盘, 已为你弹出主窗口)", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var showEv = new EventWaitHandle(false, EventResetMode.AutoReset,
                    "AsrAgent_ShowEvent");
                GC.KeepAlive(mutex);
                Application.Run(new MainForm(showEv));
            }
        }
    }
}
