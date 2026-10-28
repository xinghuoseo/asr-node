using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading;
using System.Windows.Forms;

// 极简版启动器: 单 exe (~40KB), 首次运行下载并解压运行环境(AsrNode-core.zip), 之后直接启动 AsrAgent.exe
static class Lite
{
    private const string ZipName = "AsrNode-core.zip";
    // releases/latest 避免每次发版改版本号; 前两个为国内镜像
    private static readonly string[] Sources = new string[]
    {
        "https://github.com/xinghuoseo/asr-node/releases/latest/download/",
        "https://ghfast.top/https://github.com/xinghuoseo/asr-node/releases/latest/download/",
        "https://gh-proxy.com/https://github.com/xinghuoseo/asr-node/releases/latest/download/"
    };

    private static string BaseDir
    {
        get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }
    }

    private static string AgentExe { get { return Path.Combine(BaseDir, "AsrAgent.exe"); } }
    private static string PyExe { get { return Path.Combine(BaseDir, @".venv\Scripts\python.exe"); } }
    private static string ServerPy { get { return Path.Combine(BaseDir, "asr_server.py"); } }
    private static string PartFile { get { return Path.Combine(BaseDir, "AsrNode-core.zip.part"); } }

    [STAThread]
    private static void Main()
    {
        // .NET 4.0 默认只启用 TLS 1.0, GitHub 要求 TLS 1.2
        ServicePointManager.SecurityProtocol =
            SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (IsInstalled())
        {
            Launch();
            return;
        }

        var form = new SetupForm();
        // --install: 跳过手动点击, 直接开始下载安装
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--install") >= 0)
            form.Shown += delegate { form.BeginInstall(); };
        Application.Run(form);
    }

    private static bool IsInstalled()
    {
        return File.Exists(AgentExe) && File.Exists(ServerPy) && File.Exists(PyExe);
    }

    private static void Launch()
    {
        try
        {
            var psi = new ProcessStartInfo(AgentExe);
            psi.WorkingDirectory = BaseDir;
            psi.UseShellExecute = true;
            Process.Start(psi);
        }
        catch (Exception e)
        {
            MessageBox.Show("启动失败: " + e.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private class SetupForm : Form
    {
        private readonly Label _lblTitle;
        private readonly Label _lblDesc;
        private readonly ProgressBar _bar;
        private readonly Label _lblStatus;
        private readonly Button _btn;
        private bool _busy;

        public SetupForm()
        {
            Text = "本地中文语音识别 极简版";
            Font = new Font("Microsoft YaHei", 9F);
            ClientSize = new Size(470, 262);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            _lblTitle = new Label();
            _lblTitle.Text = "本地中文语音识别 极简版";
            _lblTitle.Font = new Font("Microsoft YaHei", 12F, FontStyle.Bold);
            _lblTitle.SetBounds(16, 14, 438, 28);

            _lblDesc = new Label();
            _lblDesc.Text = "首次运行需要下载运行环境（约 64 MB），下载并解压完成后即可离线使用。\r\n"
                + "之后再次双击本程序会直接启动，不再联网。";
            _lblDesc.SetBounds(16, 50, 438, 48);

            _bar = new ProgressBar();
            _bar.SetBounds(16, 108, 438, 18);
            _bar.Minimum = 0;
            _bar.Maximum = 1000;

            _lblStatus = new Label();
            _lblStatus.Text = "等待开始...";
            _lblStatus.ForeColor = Color.DimGray;
            _lblStatus.AutoEllipsis = true;
            _lblStatus.SetBounds(16, 132, 438, 22);

            _btn = new Button();
            _btn.Text = "开始下载并安装";
            _btn.SetBounds(16, 166, 168, 34);
            _btn.Click += delegate { BeginInstall(); };

            var lblHint = new Label();
            lblHint.Text = "安装目录: " + BaseDir;
            lblHint.ForeColor = Color.Gray;
            lblHint.AutoEllipsis = true;
            lblHint.SetBounds(16, 212, 438, 20);

            Controls.Add(_lblTitle);
            Controls.Add(_lblDesc);
            Controls.Add(_bar);
            Controls.Add(_lblStatus);
            Controls.Add(_btn);
            Controls.Add(lblHint);
        }

        private void Ui(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch { }
        }

        private void SetStatus(string text, Color color)
        {
            Ui(delegate
            {
                _lblStatus.Text = text;
                _lblStatus.ForeColor = color;
            });
        }

        private void SetProgress(int value)
        {
            Ui(delegate
            {
                if (value < _bar.Minimum) value = _bar.Minimum;
                if (value > _bar.Maximum) value = _bar.Maximum;
                _bar.Value = value;
            });
        }

        public void BeginInstall()
        {
            if (_busy) return;
            _busy = true;
            _btn.Enabled = false;
            _bar.Value = 0;

            var t = new Thread(Install);
            t.IsBackground = true;
            t.Start();
        }

        private void Install()
        {
            try
            {
                string err = DownloadWithFallback();

                if (err != null)
                {
                    SetStatus("下载失败: " + err, Color.Firebrick);
                    Finish();
                    return;
                }

                SetStatus("正在解压运行环境...", Color.DimGray);
                SetProgress(0);
                Extract();
                try { File.Delete(PartFile); } catch { }

                if (!IsInstalled())
                    throw new Exception("解压后未找到 AsrAgent.exe / .venv，请重试或改用 AsrNode-core.zip");

                SetStatus("安装完成，正在启动...", Color.SeaGreen);
                Thread.Sleep(400);
                Ui(delegate
                {
                    Launch();
                    Close();
                });
            }
            catch (Exception e)
            {
                SetStatus("安装失败: " + e.Message, Color.Firebrick);
                Finish();
            }
        }

        private void Finish()
        {
            _busy = false;
            Ui(delegate
            {
                _btn.Enabled = true;
                _btn.Text = "重试";
            });
        }

        // 按镜像顺序尝试; 支持断点续传 (.part)
        private string DownloadWithFallback()
        {
            string lastErr = null;
            foreach (string src in Sources)
            {
                string host = src;
                try
                {
                    var uri = new Uri(src);
                    host = uri.Host;
                }
                catch { }

                SetStatus("连接 " + host + " ...", Color.DimGray);
                string err = Download(src + ZipName);
                if (err == null) return null;
                lastErr = host + ": " + err;
            }
            return lastErr ?? "没有可用下载源";
        }

        private string Download(string url)
        {
            try
            {
                long have = File.Exists(PartFile) ? new FileInfo(PartFile).Length : 0;

                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 20000;
                req.ReadWriteTimeout = 60000;
                req.UserAgent = "AsrNodeLite/1.0";
                req.AllowAutoRedirect = true;
                if (have > 0) req.AddRange(have);

                HttpWebResponse resp;
                try
                {
                    resp = (HttpWebResponse)req.GetResponse();
                }
                catch (WebException we)
                {
                    // 416: .part 已经是完整文件(上次下载完成但解压被中断), 直接跳过下载
                    var hr = we.Response as HttpWebResponse;
                    if (have > 0 && hr != null && hr.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                        return null;
                    throw;
                }

                using (resp)
                {
                    bool append = have > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
                    if (!append) have = 0;
                    long total = resp.ContentLength > 0 ? resp.ContentLength + have : 0;
                    long lastReport = 0;

                    using (var fs = new FileStream(PartFile,
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
                            if (have - lastReport < 512 * 1024) continue;
                            lastReport = have;
                            double mb = have / 1048576.0;
                            if (total > 0)
                            {
                                int pct = (int)(have * 1000 / total);
                                SetProgress(pct);
                                SetStatus("已下载 " + mb.ToString("0.0") + " / "
                                    + (total / 1048576.0).ToString("0.0") + " MB", Color.DimGray);
                            }
                            else
                            {
                                SetStatus("已下载 " + mb.ToString("0.0") + " MB", Color.DimGray);
                            }
                        }
                    }

                    if (total > 0 && have != total)
                        return "传输中断 (" + have + "/" + total + ")";
                }
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        private void Extract()
        {
            ZipArchive zip;
            try
            {
                zip = ZipFile.OpenRead(PartFile);
            }
            catch
            {
                try { File.Delete(PartFile); } catch { }
                throw new Exception("下载文件不完整，已清理，请重试");
            }

            using (zip)
            {
                int total = zip.Entries.Count;
                int done = 0;
                foreach (var entry in zip.Entries)
                {
                    string rel = entry.FullName.Replace('/', '\\');
                    string dest = Path.Combine(BaseDir, rel);
                    if (entry.FullName.EndsWith("/"))
                    {
                        Directory.CreateDirectory(dest);
                    }
                    else
                    {
                        string dir = Path.GetDirectoryName(dest);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        entry.ExtractToFile(dest, true);
                    }

                    done++;
                    if (done % 50 == 0 || done == total)
                        SetProgress((int)(done * 1000L / total));
                }
            }
        }
    }
}
