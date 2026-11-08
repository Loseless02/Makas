using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Makas;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args.FirstOrDefault(File.Exists)));
    }
}

sealed class MainForm : Form
{
    const string AppHost = "makas.example";
    const string MediaHost = "media.makas.example";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Makas");
    static readonly string SettingsFile = Path.Combine(DataDir, "settings.json");

    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(17, 18, 22) };
    readonly string startFile;
    string mediaFolder;
    string currentFile;
    JsonObject currentInfo;
    Process waveProc;
    Process exportProc;
    string exportTarget;
    bool exportCancelled;
    List<string> hwEncoders = new();

    public MainForm(string file)
    {
        startFile = file;
        Text = "Makas";
        BackColor = Color.FromArgb(17, 18, 22);
        StartPosition = FormStartPosition.CenterScreen;
        var wa = Screen.PrimaryScreen.WorkingArea;
        Size = new Size(Math.Min(1400, wa.Width - 80), Math.Min(900, wa.Height - 60));
        MinimumSize = new Size(760, 480);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }
        Controls.Add(web);
        Load += async (_, _) => await InitAsync();
        FormClosing += (_, _) => { Kill(waveProc); Kill(exportProc); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1; // dark title bar
        DwmSetWindowAttribute(Handle, 20, ref on, 4);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static void Trace(string m) { if (Environment.GetEnvironmentVariable("MAKAS_TRACE") != null) File.AppendAllText(Path.Combine(DataDir, "trace.log"), $"{Clock.ElapsedMilliseconds,6} {m}\n"); }

    async Task InitAsync()
    {
        Trace("init");
        Directory.CreateDirectory(DataDir);
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "WebView2"),
            new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
        Trace("env");
        await web.EnsureCoreWebView2Async(env);
        Trace("core");
        var cw = web.CoreWebView2;
        var s = cw.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsStatusBarEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsReputationCheckingRequired = false; // SmartScreen check on our local page cost ~2 s per launch
#if !DEBUG
        s.AreDevToolsEnabled = false;
#endif
        cw.AddWebResourceRequestedFilter($"https://{AppHost}/*", CoreWebView2WebResourceContext.All);
        cw.AddWebResourceRequestedFilter($"https://{MediaHost}/*", CoreWebView2WebResourceContext.All);
        cw.WebResourceRequested += (_, e) =>
        {
            var uri = e.Request.Uri;
            if (uri.StartsWith($"https://{AppHost}/", StringComparison.OrdinalIgnoreCase))
            {
                if (!uri.Contains("/index.html")) { e.Response = cw.Environment.CreateWebResourceResponse(null, 404, "Not Found", ""); return; }
                using var res = typeof(MainForm).Assembly.GetManifestResourceStream("index.html");
                var page = new MemoryStream(); res.CopyTo(page); page.Position = 0;
                e.Response = cw.Environment.CreateWebResourceResponse(page, 200, "OK", $"Content-Type: text/html; charset=utf-8\r\nContent-Length: {page.Length}");
            }
            else if (uri.StartsWith($"https://{MediaHost}/", StringComparison.OrdinalIgnoreCase))
                e.Response = ServeMedia(cw.Environment, e.Request);
        };
        cw.NewWindowRequested += (_, e) => e.Handled = true;
        cw.WebMessageReceived += OnMessage;
        cw.Navigate($"https://{AppHost}/index.html");
        _ = Task.Run(DetectEncoders);
    }

    // ---------- messaging ----------
    void Post(object o)
    {
        var json = JsonSerializer.Serialize(o);
        if (IsDisposed) return;
        BeginInvoke(() => { try { web.CoreWebView2?.PostWebMessageAsJson(json); } catch { } });
    }

    void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode msg;
        try { msg = JsonNode.Parse(e.WebMessageAsJson); } catch { return; }
        var type = (string)msg?["type"];
        switch (type)
        {
            case "ready":
                Trace("ready");
                string settings = null;
                try { if (File.Exists(SettingsFile)) settings = File.ReadAllText(SettingsFile); } catch { }
                Post(new { type = "init", settings, encoders = hwEncoders, version = Application.ProductVersion });
                if (startFile != null) OpenMedia(startFile);
                break;
            case "open":
                using (var dlg = new OpenFileDialog
                {
                    Filter = "Media|*.mp4;*.mov;*.mkv;*.webm;*.m4v;*.avi;*.ts;*.mts;*.wmv;*.flv;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma|All files|*.*",
                    InitialDirectory = mediaFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
                })
                    if (dlg.ShowDialog(this) == DialogResult.OK) OpenMedia(dlg.FileName);
                break;
            case "drop":
                var f = e.AdditionalObjects?.OfType<CoreWebView2File>().FirstOrDefault();
                if (f != null && File.Exists(f.Path)) OpenMedia(f.Path);
                break;
            case "settings":
                try { File.WriteAllText(SettingsFile, msg["data"]?.ToJsonString() ?? "{}"); } catch { }
                break;
            case "export":
                StartExport(msg, (bool?)msg["quick"] == true);
                break;
            case "cancelExport":
                exportCancelled = true; Kill(exportProc);
                break;
            case "reveal":
                if (File.Exists(exportTarget)) Process.Start("explorer.exe", $"/select,\"{exportTarget}\"");
                break;
            case "openOutput":
                if (File.Exists(exportTarget)) Process.Start(new ProcessStartInfo(exportTarget) { UseShellExecute = true });
                break;
            case "openOutputInEditor":
                if (File.Exists(exportTarget)) OpenMedia(exportTarget);
                break;
            case "title":
                Text = (string)msg["text"] ?? "Makas";
                break;
        }
    }

    // Serves the open media file with HTTP range support so the player can seek instantly.
    CoreWebView2WebResourceResponse ServeMedia(CoreWebView2Environment env, CoreWebView2WebResourceRequest req)
    {
        var path = currentFile;
        if (path == null || !File.Exists(path)) return env.CreateWebResourceResponse(null, 404, "Not Found", "");
        long len = new FileInfo(path).Length, start = 0, end = len - 1;
        bool partial = false;
        var range = req.Headers.Contains("Range") ? req.Headers.GetHeader("Range") : null;
        if (range != null && range.StartsWith("bytes="))
        {
            var r = range[6..].Split(',')[0].Split('-');
            if (long.TryParse(r[0], out var s)) { start = s; if (r.Length > 1 && long.TryParse(r[1], out var en)) end = Math.Min(en, len - 1); }
            else if (r.Length > 1 && long.TryParse(r[1], out var suffix)) start = Math.Max(0, len - suffix);
            partial = true;
        }
        if (start >= len) return env.CreateWebResourceResponse(null, 416, "Range Not Satisfiable", $"Content-Range: bytes */{len}");
        const long Chunk = 4 << 20; // measured: 4 MB beats 1 MB for seek latency (fewer round trips)
        end = Math.Min(end, start + Chunk - 1);
        var count = (int)(end - start + 1);
        var data = new byte[count];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess))
        {
            fs.Position = start;
            int read = 0, n;
            while (read < count && (n = fs.Read(data, read, count - read)) > 0) read += n;
        }
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var mime = ext switch
        {
            ".webm" => "video/webm", ".mkv" => "video/x-matroska", ".mov" => "video/quicktime", ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav", ".m4a" => "audio/mp4", ".aac" => "audio/aac", ".flac" => "audio/flac", ".ogg" => "audio/ogg", ".opus" => "audio/ogg",
            _ => "video/mp4"
        };
        var headers = $"Content-Type: {mime}\r\nAccept-Ranges: bytes\r\nContent-Length: {count}\r\nCache-Control: no-store";
        if (partial || count < len) headers += $"\r\nContent-Range: bytes {start}-{end}/{len}";
        return env.CreateWebResourceResponse(new MemoryStream(data, false), partial || count < len ? 206 : 200, partial || count < len ? "Partial Content" : "OK", headers);
    }

    // ---------- tools ----------
    static string Tool(string name)
    {
        var local = Path.Combine(AppContext.BaseDirectory, name + ".exe");
        if (File.Exists(local)) return local;
        var localSub = Path.Combine(AppContext.BaseDirectory, "ffmpeg", name + ".exe");
        if (File.Exists(localSub)) return localSub;
        return name;
    }

    static Process Spawn(string exe, IEnumerable<string> args, bool stdout = true)
    {
        var psi = new ProcessStartInfo(Tool(exe))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = stdout, RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = stdout ? Encoding.UTF8 : null,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi);
    }

    static void Kill(Process p)
    {
        try { if (p != null && !p.HasExited) p.Kill(true); } catch { }
    }

    // Hardware encoder check costs a few ffmpeg spawns, so the result is cached and refreshed quietly after startup.
    const string FfmpegMissing = "ffmpeg not found. Put ffmpeg.exe and ffprobe.exe next to Makas.exe or install it (winget install Gyan.FFmpeg).";
    static readonly string EncoderCache = Path.Combine(DataDir, "encoders.txt");

    void DetectEncoders()
    {
        try
        {
            if (File.Exists(EncoderCache))
            {
                hwEncoders = File.ReadAllText(EncoderCache).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                Post(new { type = "encoders", encoders = hwEncoders });
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(EncoderCache) < TimeSpan.FromDays(7)) return;
            }
        }
        catch { }
        Thread.Sleep(4000); // keep the first file open snappy
        var found = new List<string>();
        foreach (var enc in new[] { "h264_nvenc", "hevc_nvenc", "h264_qsv", "hevc_qsv", "h264_amf", "hevc_amf" })
        {
            try
            {
                using var p = Spawn("ffmpeg", new[] { "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "color=black:s=256x256:d=0.1", "-c:v", enc, "-f", "null", "-" }, false);
                p.StandardError.ReadToEnd();
                if (p.WaitForExit(8000) && p.ExitCode == 0) found.Add(enc);
                else Kill(p);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
            {
                Post(new { type = "error", text = FfmpegMissing });
                return;
            }
            catch { }
        }
        hwEncoders = found;
        try { File.WriteAllText(EncoderCache, string.Join(",", found)); } catch { }
        Post(new { type = "encoders", encoders = found });
    }

    // ---------- open ----------
    void OpenMedia(string path)
    {
        // The player starts loading right away; ffprobe (slow to spawn) and the waveform run in parallel.
        path = Path.GetFullPath(path);
        mediaFolder = Path.GetDirectoryName(path);
        currentFile = path;
        currentInfo = null;
        var url = $"https://{MediaHost}/{Uri.EscapeDataString(Path.GetFileName(path))}?v={File.GetLastWriteTimeUtc(path).Ticks}";
        Trace("loaded");
        Post(new { type = "loaded", url, path, name = Path.GetFileName(path), size = new FileInfo(path).Length });
        Task.Run(() => { if (path == currentFile) StartWaveform(path); });
        Task.Run(() =>
        {
            try
            {
                var info = Probe(path);
                if (path != currentFile) return;
                currentInfo = info;
                Trace("probed");
                Post(new { type = "info", info = info.ToJsonString() });
            }
            catch (System.ComponentModel.Win32Exception) { Post(new { type = "error", text = FfmpegMissing }); }
            catch (Exception ex) { if (path == currentFile) Post(new { type = "error", text = "Could not read file: " + ex.Message }); }
        });
    }

    static JsonObject Probe(string path)
    {
        using var p = Spawn("ffprobe", new[] { "-v", "error", "-show_entries",
            "format=duration:stream=index,codec_type,codec_name,width,height,r_frame_rate,avg_frame_rate,sample_rate,channels:stream_disposition=attached_pic",
            "-of", "json", path });
        var outText = p.StandardOutput.ReadToEndAsync();
        var err = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception(err.Result.Trim());
        var j = JsonNode.Parse(outText.Result).AsObject();
        var r = new JsonObject();
        double dur = 0;
        double.TryParse((string)j["format"]?["duration"], NumberStyles.Float, Inv, out dur);
        bool hasVideo = false, hasAudio = false;
        double fps = 30; int w = 0, h = 0; string vcodec = "", acodec = "";
        foreach (var st in j["streams"]?.AsArray() ?? new JsonArray())
        {
            var t = (string)st["codec_type"];
            if (t == "video" && !hasVideo && (int?)st["disposition"]?["attached_pic"] != 1)
            {
                hasVideo = true;
                w = (int?)st["width"] ?? 0; h = (int?)st["height"] ?? 0;
                vcodec = (string)st["codec_name"];
                fps = ParseRate((string)st["avg_frame_rate"]) is var a && a > 0 && a < 1000 ? a : ParseRate((string)st["r_frame_rate"]);
                if (fps <= 0 || fps > 1000) fps = 30;
            }
            else if (t == "audio" && !hasAudio) { hasAudio = true; acodec = (string)st["codec_name"]; }
        }
        if (!hasVideo && !hasAudio) throw new Exception("No audio or video stream.");
        r["duration"] = dur; r["hasVideo"] = hasVideo; r["hasAudio"] = hasAudio;
        r["fps"] = Math.Round(fps, 3); r["width"] = w; r["height"] = h;
        r["vcodec"] = vcodec; r["acodec"] = acodec;
        r["size"] = new FileInfo(path).Length;
        return r;
    }

    static double ParseRate(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var parts = s.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, Inv, out var n) && double.TryParse(parts[1], NumberStyles.Float, Inv, out var d) && d != 0) return n / d;
        return double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : 0;
    }

    // ---------- waveform ----------
    void StartWaveform(string path)
    {
        Kill(waveProc);
        const int rate = 8000, perBucket = 80; // 100 peaks / second
        var p = Spawn("ffmpeg", new[] { "-hide_banner", "-v", "error", "-vn", "-sn", "-dn", "-i", path, "-map", "0:a:0", "-ac", "1", "-ar", rate.ToString(), "-f", "s16le", "-" });
        waveProc = p;
        p.StandardError.ReadToEndAsync();
        Task.Run(() =>
        {
            try
            {
                var s = p.StandardOutput.BaseStream;
                var buf = new byte[perBucket * 2 * 512];
                var peaks = new List<byte>(1 << 16);
                int inBucket = 0, max = 0; byte lo = 0; bool haveLo = false;
                int lastSent = 0;
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    int i = 0;
                    if (haveLo) { ProcessSample((short)(lo | (buf[0] << 8))); i = 1; haveLo = false; }
                    for (; i + 1 < n; i += 2) ProcessSample((short)(buf[i] | (buf[i + 1] << 8)));
                    if (i < n) { lo = buf[i]; haveLo = true; }
                    if (peaks.Count - lastSent > 6000) { SendWave(peaks, lastSent, false); lastSent = peaks.Count; }
                }
                if (inBucket > 0) peaks.Add(Scale(max));
                if (p != waveProc) return;
                SendWave(peaks, lastSent, true);

                void ProcessSample(short v)
                {
                    int a = v < 0 ? -v : v;
                    if (a > max) max = a;
                    if (++inBucket == perBucket) { peaks.Add(Scale(max)); inBucket = 0; max = 0; }
                }
            }
            catch { }
        });

        void SendWave(List<byte> peaks, int from, bool done)
        {
            if (p != waveProc) return;
            var chunk = peaks.GetRange(from, peaks.Count - from).ToArray();
            Post(new { type = "wave", rate = 100, offset = from, data = Convert.ToBase64String(chunk), done });
        }
    }

    static byte Scale(int peak) => (byte)Math.Clamp((int)(Math.Sqrt(peak / 32768.0) * 255), 0, 255);

    // ---------- export ----------
    sealed record Seg(double S, double E, double Fi, double Fo) { public double L => E - S; }

    static string F(double v) => v.ToString("0.######", Inv);

    void StartExport(JsonNode msg, bool quick)
    {
        if (currentFile == null || exportProc != null) return;
        if (currentInfo == null)
        {
            try { currentInfo = Probe(currentFile); }
            catch (Exception ex) { Post(new { type = "exportError", text = ex.Message }); return; }
        }
        var segs = new List<Seg>();
        foreach (var s in msg["segments"]!.AsArray())
            segs.Add(new Seg((double)s["s"], (double)s["e"], (double)(s["fi"] ?? 0), (double)(s["fo"] ?? 0)));
        segs = segs.Where(s => s.L > 0.01).ToList();
        if (segs.Count == 0) { Post(new { type = "error", text = "Nothing to export — every part is removed." }); return; }
        var o = msg["opts"];
        string mode = (string)o?["mode"] ?? "encode";
        string encoder = (string)o?["encoder"] ?? "auto";
        string quality = (string)o?["quality"] ?? "high";
        string format = (string)o?["format"] ?? "mp4";
        bool fadeVideo = (bool?)o?["fadeVideo"] ?? true, fadeAudio = (bool?)o?["fadeAudio"] ?? true;
        bool hasVideo = currentInfo["hasVideo"]!.GetValue<bool>(), hasAudio = currentInfo["hasAudio"]!.GetValue<bool>();

        var srcExt = Path.GetExtension(currentFile).ToLowerInvariant();
        string ext = !hasVideo || mode == "copy" || format == "same" ? srcExt : "." + format;
        if (string.IsNullOrEmpty(ext)) ext = hasVideo ? ".mp4" : ".m4a";
        // re-encoded video goes out as H.264/HEVC + AAC, so keep to containers that hold it
        if (hasVideo && mode != "copy" && ext is not (".mp4" or ".mkv" or ".mov" or ".m4v" or ".ts")) ext = ".mp4";

        var dir = Path.GetDirectoryName(currentFile);
        var baseName = Path.GetFileNameWithoutExtension(currentFile) + "_cut";
        string target = Path.Combine(dir, baseName + ext);
        for (int i = 2; File.Exists(target); i++) target = Path.Combine(dir, $"{baseName}{i}{ext}");
        if (!quick)
        {
            using var dlg = new SaveFileDialog { InitialDirectory = dir, FileName = Path.GetFileName(target), Filter = $"{ext.TrimStart('.').ToUpperInvariant()}|*{ext}|All files|*.*", OverwritePrompt = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) { Post(new { type = "exportCancelled" }); return; }
            target = dlg.FileName;
        }
        if (string.Equals(Path.GetFullPath(target), currentFile, StringComparison.OrdinalIgnoreCase))
        { Post(new { type = "error", text = "Cannot overwrite the source file. Pick another name." }); return; }

        double total = segs.Sum(s => s.L);
        exportTarget = target;
        exportCancelled = false;
        var src = currentFile;
        Task.Run(() => RunExport(src, target, segs, total, mode, encoder, quality, hasVideo, hasAudio, fadeVideo, fadeAudio, ext));
    }

    void RunExport(string src, string target, List<Seg> segs, double total, string mode, string encoder, string quality,
        bool hasVideo, bool hasAudio, bool fadeVideo, bool fadeAudio, string ext)
    {
        var sw = Stopwatch.StartNew();
        var tmp = target + ".part" + ext;
        string tempDir = null;
        try
        {
            Post(new { type = "exportStart", target, total });
            if (mode == "copy")
            {
                if (segs.Count == 1)
                    RunFfmpeg(CopyArgs(src, segs[0], tmp), total, 0, total);
                else
                {
                    tempDir = Path.Combine(Path.GetTempPath(), "makas_" + Guid.NewGuid().ToString("N")[..8]);
                    Directory.CreateDirectory(tempDir);
                    var list = new StringBuilder();
                    double done = 0;
                    for (int i = 0; i < segs.Count; i++)
                    {
                        var part = Path.Combine(tempDir, $"p{i}{ext}");
                        RunFfmpeg(CopyArgs(src, segs[i], part), total, done, segs[i].L);
                        done += segs[i].L;
                        list.Append("file '").Append(part.Replace("'", "'\\''")).Append("'\n");
                    }
                    var listFile = Path.Combine(tempDir, "list.txt");
                    File.WriteAllText(listFile, list.ToString());
                    RunFfmpeg(new List<string> { "-f", "concat", "-safe", "0", "-i", listFile, "-map", "0", "-c", "copy", "-movflags", "+faststart", "-y", tmp }, total, total, 0);
                }
            }
            else
            {
                RunFfmpeg(EncodeArgs(src, segs, tmp, encoder, quality, hasVideo, hasAudio, fadeVideo, fadeAudio, ext), total, 0, total);
            }
            if (exportCancelled) throw new OperationCanceledException();
            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
            Post(new { type = "exportDone", target, seconds = sw.Elapsed.TotalSeconds, size = new FileInfo(target).Length });
        }
        catch (OperationCanceledException) { Post(new { type = "exportCancelled" }); }
        catch (Exception ex) { Post(new { type = "exportError", text = ex.Message }); }
        finally
        {
            exportProc = null;
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            try { if (tempDir != null) Directory.Delete(tempDir, true); } catch { }
        }
    }

    static List<string> CopyArgs(string src, Seg s, string outFile) => new()
    {
        "-ss", F(s.S), "-i", src, "-t", F(s.L), "-map", "0:v?", "-map", "0:a?", "-c", "copy",
        "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", "-y", outFile
    };

    List<string> EncodeArgs(string src, List<Seg> segs, string outFile, string encoder, string quality,
        bool hasVideo, bool hasAudio, bool fadeVideo, bool fadeAudio, string ext)
    {
        var a = new List<string>();
        foreach (var s in segs) { a.Add("-ss"); a.Add(F(s.S)); a.Add("-t"); a.Add(F(s.L)); a.Add("-i"); a.Add(src); }
        var fc = new StringBuilder();
        var concatIn = new StringBuilder();
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            double fi = Math.Min(s.Fi, s.L), fo = Math.Min(s.Fo, s.L);
            if (hasVideo)
            {
                fc.Append($"[{i}:v:0]setpts=PTS-STARTPTS");
                if (fadeVideo && fi > 0) fc.Append($",fade=t=in:st=0:d={F(fi)}");
                if (fadeVideo && fo > 0) fc.Append($",fade=t=out:st={F(s.L - fo)}:d={F(fo)}");
                fc.Append($",format=yuv420p[v{i}];");
                concatIn.Append($"[v{i}]");
            }
            if (hasAudio)
            {
                fc.Append($"[{i}:a:0]asetpts=PTS-STARTPTS");
                if (fadeAudio && fi > 0) fc.Append($",afade=t=in:st=0:d={F(fi)}");
                if (fadeAudio && fo > 0) fc.Append($",afade=t=out:st={F(s.L - fo)}:d={F(fo)}");
                fc.Append($",aresample=async=1[a{i}];");
                concatIn.Append($"[a{i}]");
            }
        }
        string vOut = "[v0]", aOut = "[a0]";
        if (segs.Count > 1)
        {
            fc.Append(concatIn).Append($"concat=n={segs.Count}:v={(hasVideo ? 1 : 0)}:a={(hasAudio ? 1 : 0)}");
            fc.Append(hasVideo ? "[v]" : "").Append(hasAudio ? "[a]" : "");
            vOut = "[v]"; aOut = "[a]";
        }
        else fc.Length--; // trailing ';'
        a.Add("-filter_complex"); a.Add(fc.ToString());
        if (hasVideo) { a.Add("-map"); a.Add(vOut); }
        if (hasAudio) { a.Add("-map"); a.Add(aOut); }

        if (hasVideo)
        {
            string enc = encoder == "auto" ? (hwEncoders.Contains("h264_nvenc") ? "h264_nvenc" : hwEncoders.Contains("h264_qsv") ? "h264_qsv" : hwEncoders.Contains("h264_amf") ? "h264_amf" : "libx264") : encoder;
            int qi = quality == "small" ? 2 : quality == "balanced" ? 1 : 0;
            a.Add("-c:v"); a.Add(enc);
            if (enc.EndsWith("_nvenc")) a.AddRange(new[] { "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", new[] { "19", "24", "29" }[qi], "-b:v", "0" });
            else if (enc.EndsWith("_qsv")) a.AddRange(new[] { "-preset", "faster", "-global_quality", new[] { "20", "25", "30" }[qi] });
            else if (enc.EndsWith("_amf")) a.AddRange(new[] { "-quality", "balanced", "-rc", "cqp", "-qp_i", new[] { "19", "24", "29" }[qi], "-qp_p", new[] { "21", "26", "31" }[qi] });
            else if (enc == "libx265") a.AddRange(new[] { "-preset", "fast", "-crf", new[] { "21", "26", "30" }[qi] });
            else a.AddRange(new[] { "-preset", "veryfast", "-crf", new[] { "18", "22", "27" }[qi] });
        }
        if (hasAudio)
        {
            switch (ext)
            {
                case ".wav": a.AddRange(new[] { "-c:a", "pcm_s16le" }); break;
                case ".flac": a.AddRange(new[] { "-c:a", "flac" }); break;
                case ".mp3": a.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", "256k" }); break;
                case ".ogg": case ".opus": case ".webm": a.AddRange(new[] { "-c:a", "libopus", "-b:a", "160k" }); break;
                default: a.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" }); break;
            }
        }
        if (ext is ".mp4" or ".mov" or ".m4v" or ".m4a") { a.Add("-movflags"); a.Add("+faststart"); }
        a.Add("-y"); a.Add(outFile);
        return a;
    }

    void RunFfmpeg(List<string> args, double total, double offset, double span)
    {
        if (exportCancelled) throw new OperationCanceledException();
        var full = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-progress", "pipe:1", "-nostats" };
        full.AddRange(args);
        var p = Spawn("ffmpeg", full);
        exportProc = p;
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null && err.Length < 8000) err.AppendLine(e.Data); };
        p.BeginErrorReadLine();
        string line;
        long lastPost = 0;
        var sw = Stopwatch.StartNew();
        while ((line = p.StandardOutput.ReadLine()) != null)
        {
            if (line.StartsWith("out_time_us=") && long.TryParse(line.AsSpan(12), out var us) && us > 0)
            {
                if (sw.ElapsedMilliseconds - lastPost < 100) continue;
                lastPost = sw.ElapsedMilliseconds;
                double t = Math.Min(span, us / 1e6) + offset;
                Post(new { type = "exportProgress", done = t, total });
            }
        }
        p.WaitForExit();
        if (exportCancelled) throw new OperationCanceledException();
        if (p.ExitCode != 0)
        {
            var msg = err.ToString().Trim();
            throw new Exception(string.IsNullOrEmpty(msg) ? $"ffmpeg exited with code {p.ExitCode}" : msg);
        }
    }
}
