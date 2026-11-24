using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using WeightCheckerCS.Core;

namespace WeightCheckerCS.Engine
{
    /// <summary>
    /// 采集与判定核心（与 UI 完全解耦）：
    /// 采集线程：TCP 读取 → 分行 → 解析 → 状态机判定 → 剔除脉冲入队 → CSV 入队 → UI 入队。
    /// 全部跨线程交接均为非阻塞有界队列，UI 渲染再慢也不会拖慢判定和喷气。
    /// 配置热加载：settings.json / configs.json 变化后自动校验并原子替换，无效配置被拒绝并保留旧配置。
    /// </summary>
    public sealed class WeighEngine : IDisposable
    {
        private readonly object _procLock = new object();
        private volatile AppSettings _settings;
        private volatile SkuCatalog _skus;
        private StateMachineWeigher _weigher;
        private readonly DioOutput _dio;
        private readonly CsvStorage _csv;
        private readonly ConcurrentQueue<WeightRecord> _uiQueue = new ConcurrentQueue<WeightRecord>();
        private Thread _collector;
        private volatile bool _running;
        private volatile bool _reconnect;
        private readonly ManualResetEvent _stopEvent = new ManualResetEvent(false);
        private volatile string _status = "STOPPED", _statusDetail = "未开始采集";
        private long _records, _errors, _seq, _light, _heavy, _unknown, _rejects;
        private ConfigWatcher _watcher;
        public volatile bool HeadlessPrint;
        /// <summary>回放测试用：称重机时间字段是完整日期时间（ISO 8601）时，用它代替本机接收时间（停机重置等按数据时间计算）。</summary>
        public volatile bool UseMachineTime;

        public WeighEngine(AppSettings settings) : this(settings, DioSimulation.Off) { }

        public WeighEngine(AppSettings settings, DioSimulation dioSim)
        {
            _settings = settings;
            Log.Start(settings.DataDirFull);
            _skus = SkuConfigStore.LoadAll(settings.SkuConfigsFull);
            LogSkipped(_skus);
            TryMigrate(settings.SkuConfigsFull, _skus);
            var cfg0 = Pick(_skus, settings, "启动失败");
            if (settings.Sku != cfg0.Sku)
            {
                // 旧版设置（"N-pack" 或只有 product_pad_num）：写回新块名，避免以后新增同 pack 数 SKU 时对应关系变化
                settings.Sku = cfg0.Sku; settings.ProductPadNum = cfg0.ProductPadNum;
                try { if (File.Exists(AppPaths.SettingsFile)) { AppSettings.Save(AppPaths.SettingsFile, settings); Log.Info("settings.json 中的 SKU 已更新为新块名 \"" + cfg0.Sku + "\""); } }
                catch (Exception ex) { Log.Info("settings.json 写回新块名失败（不影响运行）：" + ex.Message); }
            }
            settings.Sku = cfg0.Sku; settings.ProductPadNum = cfg0.ProductPadNum;
            Log.Info("已加载 " + _skus.Count + " 个 SKU：" + _skus.Describe() + "；当前 " + _skus.LabelOf(cfg0));
            _weigher = new StateMachineWeigher(cfg0);
            _csv = new CsvStorage(settings.DataDirFull, Log.Info);
            _dio = new DioOutput(Log.Info, dioSim);
            _dio.Load(settings.DioDllFull);   // 启动时加载一次
        }

        private static WeighingConfig Pick(SkuCatalog cat, AppSettings s, string ctx)
        {
            string note;
            var c = cat.Find(s.Sku, s.ProductPadNum, out note);
            if (c == null) throw new FormatException(ctx + "：状态机配置中找不到 SKU " + s.SkuDescription + "；可用：" + cat.Describe());
            if (note != null) Log.Info(note);
            return c;
        }

        private static void LogSkipped(SkuCatalog cat)
        {
            foreach (var m in cat.Skipped) Log.Info("已跳过格式错误的 SKU 配置块：" + m);
            foreach (var m in cat.Warnings) Log.Info("SKU 块名警告：" + m);
        }

        // ---------- 只读状态（任意线程可读） ----------
        public AppSettings Settings { get { return _settings; } }
        public SkuCatalog Skus { get { return _skus; } }
        public WeighingConfig CurrentSku { get { var s = _settings; return _skus.Find(s.Sku, s.ProductPadNum) ?? _weigher.Config; } }
        public DioOutput Dio { get { return _dio; } }
        public bool Running { get { return _running; } }
        public string Status { get { return _status; } }
        public string StatusDetail { get { return _statusDetail; } }
        public long Records { get { return Interlocked.Read(ref _records); } }
        public long Errors { get { return Interlocked.Read(ref _errors); } }
        public long Light { get { return Interlocked.Read(ref _light); } }
        public long Heavy { get { return Interlocked.Read(ref _heavy); } }
        public long Unknown { get { return Interlocked.Read(ref _unknown); } }
        public long Rejects { get { return Interlocked.Read(ref _rejects); } }
        public long CsvDropped { get { return _csv.Dropped; } }

        /// <summary>当前状态机快照（中心/区间/状态），用于 UI 显示；加锁时间极短。</summary>
        public bool TryGetSnapshot(out PassWindow window, out MachineState state, out int historyCount)
        {
            lock (_procLock)
            {
                window = _weigher.CurrentWindow(); state = _weigher.State; historyCount = _weigher.HistoryCount;
                return true;
            }
        }

        public bool TryDequeueRecord(out WeightRecord r) { return _uiQueue.TryDequeue(out r); }

        public void ResetCounters()
        {
            Interlocked.Exchange(ref _light, 0); Interlocked.Exchange(ref _heavy, 0);
            Interlocked.Exchange(ref _unknown, 0); Interlocked.Exchange(ref _rejects, 0);
        }

        private void SetStatus(string s, string detail)
        {
            _status = s; _statusDetail = detail;
            Log.Info("[" + s + "] " + detail);
        }

        // ---------- 采集 ----------
        public void Start()
        {
            if (_running) return;
            _running = true;
            _stopEvent.Reset();
            _collector = new Thread(CollectLoop) { IsBackground = true, Name = "collector", Priority = ThreadPriority.AboveNormal };
            _collector.Start();
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            _stopEvent.Set();
            if (_collector != null) _collector.Join(5000);
            SetStatus("STOPPED", "采集已停止");
        }

        private void CollectLoop()
        {
            double backoff = _settings.ReconnectInitialSeconds;
            var buf = new byte[65536];
            var lines = new List<string>();
            while (_running)
            {
                var s = _settings;
                _reconnect = false;
                Socket sock = null;
                try
                {
                    SetStatus("CONNECTING", "正在连接 " + s.Host + ":" + s.Port);
                    sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    sock.NoDelay = true;
                    sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    var ar = sock.BeginConnect(s.Host, s.Port, null, null);
                    int which = WaitHandle.WaitAny(new[] { ar.AsyncWaitHandle, _stopEvent }, TimeSpan.FromSeconds(s.ConnectTimeoutSeconds));
                    if (which == 1) break;
                    if (which == WaitHandle.WaitTimeout) throw new TimeoutException("连接超时");
                    sock.EndConnect(ar);
                    SetStatus("CONNECTED", "已连接 " + s.Host + ":" + s.Port);
                    backoff = s.ReconnectInitialSeconds;
                    var decoder = new LineDecoder(Encoding.GetEncoding(s.Encoding));
                    int pollUs = (int)Math.Min(int.MaxValue, s.ReceiveTimeoutSeconds * 1e6);
                    while (_running && !_reconnect)
                    {
                        if (!sock.Poll(pollUs, SelectMode.SelectRead)) continue;
                        int n = sock.Receive(buf, 0, Math.Min(buf.Length, s.BufferSize), SocketFlags.None);
                        if (n == 0) throw new IOException("称重机关闭了连接");
                        lines.Clear();
                        try { decoder.Feed(buf, n, lines); }
                        catch (InvalidDataException ex) { Interlocked.Increment(ref _errors); SetStatus("WARNING", ex.Message); }
                        foreach (var line in lines) HandleLine(line);
                    }
                    if (_reconnect && _running) SetStatus("RECONNECTING", "连接参数已变更，重新连接");
                }
                catch (Exception ex)
                {
                    if (!_running) break;
                    Interlocked.Increment(ref _errors);
                    SetStatus("RECONNECTING", ex.Message + "，" + backoff.ToString("0.#") + " 秒后重连");
                    if (_stopEvent.WaitOne(TimeSpan.FromSeconds(backoff))) break;
                    backoff = Math.Min(backoff * 2, _settings.ReconnectMaxSeconds);
                }
                finally
                {
                    if (sock != null) { try { sock.Close(); } catch { } }
                }
            }
        }

        /// <summary>处理一行数据。单行出错绝不影响后续采集。也用于回放/自测。</summary>
        public WeightRecord HandleLine(string line)
        {
            try
            {
                var s = _settings;
                var r = Protocol.Parse(line, s);
                DateTimeOffset mt;
                if (UseMachineTime && r.MachineTime != null && r.MachineTime.IndexOf('T') > 0
                    && DateTimeOffset.TryParse(r.MachineTime, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out mt))
                    r.ReceivedAt = mt.LocalDateTime;
                r.Id = Interlocked.Increment(ref _seq);
                WeighingConfig cfg = null;
                if (s.StateMachineEnabled)
                {
                    lock (_procLock)
                    {
                        cfg = _weigher.Config;
                        try { r.Algo = _weigher.ProcessBox(r.ReceivedAt, r.Weight); }
                        catch (Exception ex)
                        {
                            Log.Info("状态机异常，已重建：" + ex.Message);
                            _weigher = new StateMachineWeigher(cfg);
                            r.Algo = null;
                        }
                    }
                }
                if (r.Algo != null && s.RejectOutputEnabled && r.Algo.Code != "2")
                {
                    r.RejectFired = _dio.Enqueue(cfg.RejectPins, cfg.RejectPulseSeconds, "#" + (r.SequenceNo ?? r.Id.ToString()) + " " + r.AlgoStatus);
                    if (r.RejectFired) Interlocked.Increment(ref _rejects);
                }
                string v = r.Verdict;
                if (v == "过轻") Interlocked.Increment(ref _light);
                else if (v == "超重") Interlocked.Increment(ref _heavy);
                else if (v != "合格") Interlocked.Increment(ref _unknown);
                Interlocked.Increment(ref _records);
                if (s.CsvEnabled) _csv.Enqueue(r);
                _uiQueue.Enqueue(r);
                WeightRecord drop;
                while (_uiQueue.Count > 5000 && _uiQueue.TryDequeue(out drop)) { }
                if (HeadlessPrint) PrintRecord(r);
                return r;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errors);
                Log.Info("数据行处理失败：" + ex.Message + " | " + line);
                return null;
            }
        }

        private static void PrintRecord(WeightRecord r)
        {
            var a = r.Algo;
            string algo = a == null ? "状态机未启用" :
                "状态机 " + r.AlgoStatus + " 中心 " + Fmt.F2(a.Window.Center) + " 区间 " + Fmt.F2(a.Window.Lower) + "~" + Fmt.F2(a.Window.Upper)
                + " 宽 " + Fmt.F2(a.Window.Width) + " 倾斜 " + Fmt.S2(a.Window.Shift) + " " + Names.State(a.StateBefore)
                + (a.CenterFlag.Length > 0 ? " " + a.CenterFlag : "") + (r.RejectFired ? " [剔除]" : "");
            try { Console.WriteLine(r.ReceivedAt.ToString("HH:mm:ss.fff") + " #" + (r.SequenceNo ?? "-") + " " + Fmt.F2(r.Weight) + " g 称重机 " + r.MachineStatus + " | " + algo); }
            catch { }
        }

        public void ManualPulse()
        {
            var c = CurrentSku;
            _dio.Enqueue(c.RejectPins, c.RejectPulseSeconds, "手动触发");
        }

        // ---------- 热加载 ----------
        public void EnableHotReload()
        {
            if (_watcher != null) return;
            _watcher = new ConfigWatcher(this);
        }

        /// <summary>应用新的程序设置（UI 保存或文件热加载）。校验失败抛 FormatException，旧配置不变。</summary>
        public void ApplySettings(AppSettings ns, bool save)
        {
            ns.Validate();
            var old = _settings;
            var oldCfg = CurrentSku;
            SkuCatalog skus = _skus;
            if (!string.Equals(ns.SkuConfigsFull, old.SkuConfigsFull, StringComparison.OrdinalIgnoreCase))
            {
                skus = SkuConfigStore.LoadAll(ns.SkuConfigsFull);
                LogSkipped(skus);
            }
            var cfg = Pick(skus, ns, "设置未生效");
            ns.Sku = cfg.Sku; ns.ProductPadNum = cfg.ProductPadNum;
            bool skuChanged = oldCfg == null || oldCfg.Sku != cfg.Sku;
            if (save) AppSettings.Save(AppPaths.SettingsFile, ns);
            lock (_procLock)
            {
                _skus = skus;
                _settings = ns;
                if (skuChanged || ns.StateMachineEnabled != old.StateMachineEnabled)
                    _weigher = new StateMachineWeigher(cfg);
                else if (!SameJson(cfg, _weigher.Config))
                    _weigher.ApplyConfigKeepHistory(cfg);
            }
            if (skuChanged) Log.Info("已切换 SKU：" + skus.LabelOf(cfg) + "（" + cfg.Sku + "，状态机重新初始化）");
            if (ns.DataDirFull != old.DataDirFull) { _csv.DataDir = ns.DataDirFull; Log.DataDir = ns.DataDirFull; }
            if (!string.Equals(ns.DioDllFull, old.DioDllFull, StringComparison.OrdinalIgnoreCase) || !_dio.Ready)
            {
                string path = ns.DioDllFull;
                ThreadPool.QueueUserWorkItem(delegate { _dio.Load(path); });
            }
            if (!ns.SameConnection(old) && _running) _reconnect = true;
            if (_watcher != null) _watcher.Rebind(ns);
            Log.Info("程序设置已生效");
        }

        /// <summary>重新读取 configs.json（文件热加载）。同一 SKU 参数变化时保留中心历史与状态。</summary>
        /// <summary>旧版 do_out 自动改写为 reject_output（失败只记日志，不影响运行）。</summary>
        private static void TryMigrate(string path, SkuCatalog skus)
        {
            try
            {
                int n = SkuConfigStore.MigrateLegacy(path, skus);
                if (n > 0) Log.Info("configs.json 中 " + n + " 个 SKU 的旧版 do_out 已改写为 reject_output（原文件备份为 configs.json.bak；DLL 路径改由程序设置统一指定）");
            }
            catch (Exception ex) { Log.Info("configs.json 旧格式自动改写失败（仍按旧格式运行）：" + ex.Message); }
        }
        public void ReloadSkuConfigs()
        {
            var skus = SkuConfigStore.LoadAll(_settings.SkuConfigsFull);
            LogSkipped(skus);
            TryMigrate(_settings.SkuConfigsFull, skus);
            var s = _settings;
            string note;
            var cfg = skus.Find(s.Sku, s.ProductPadNum, out note);
            if (cfg == null) throw new FormatException("状态机配置中当前 SKU " + s.SkuDescription + " 缺失或格式错误，拒绝热加载（保留原参数）");
            if (note != null) { Log.Info(note); s.Sku = cfg.Sku; }
            bool changed;
            lock (_procLock)
            {
                _skus = skus;
                changed = !SameJson(cfg, _weigher.Config);
                if (changed) _weigher.ApplyConfigKeepHistory(cfg);
            }
            if (changed) Log.Info("状态机参数已热加载（" + skus.LabelOf(cfg) + "，保留中心历史）");
            else Log.Info("configs.json 已重新读取：" + skus.Count + " 个 SKU");
        }

        /// <summary>运行期间修改喷气激活时长/引脚：写回 configs.json 当前 SKU 的 reject_output 并立即生效。</summary>
        public void UpdatePulse(int activeMs, int[] pins)
        {
            var cfg = CurrentSku.Clone();
            cfg.RejectActiveMs = activeMs;
            cfg.RejectPins = (int[])pins.Clone();
            cfg.Validate();
            SkuConfigStore.SaveSku(_settings.SkuConfigsFull, cfg);
            ReloadSkuConfigs();
            Log.Info("喷气参数已保存并生效：激活引脚 " + string.Join(",", Array.ConvertAll(pins, x => x.ToString())) + "，激活时长 " + activeMs + " ms");
        }

        /// <summary>保存某个 SKU 的状态机参数（UI 编辑）并立即生效。</summary>
        public void SaveSku(WeighingConfig cfg)
        {
            cfg.Validate();
            SkuConfigStore.SaveSku(_settings.SkuConfigsFull, cfg);
            ReloadSkuConfigs();
        }

        private static bool SameJson(WeighingConfig a, WeighingConfig b)
        {
            return Json.Write(a.ToJson()) == Json.Write(b.ToJson());
        }

        public void Dispose()
        {
            Stop();
            if (_watcher != null) _watcher.Dispose();
            _dio.Dispose();
            _csv.Dispose();
            Log.Flush();
        }
    }

    /// <summary>监视 settings.json 与 configs.json，去抖 400 ms 后热加载；内容未变（含本程序自身写入）时忽略。</summary>
    public sealed class ConfigWatcher : IDisposable
    {
        private readonly WeighEngine _engine;
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private readonly Timer _timer;
        private readonly object _lock = new object();
        private string _settingsText, _skuText;

        public ConfigWatcher(WeighEngine engine)
        {
            _engine = engine;
            _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
            _settingsText = ReadSafe(AppPaths.SettingsFile);
            _skuText = ReadSafe(engine.Settings.SkuConfigsFull);
            Rebind(engine.Settings);
        }

        public void Rebind(AppSettings s)
        {
            lock (_lock)
            {
                foreach (var w in _watchers) w.Dispose();
                _watchers.Clear();
                var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                dirs.Add(Path.GetDirectoryName(AppPaths.SettingsFile));
                dirs.Add(Path.GetDirectoryName(s.SkuConfigsFull));
                foreach (var d in dirs)
                {
                    if (!Directory.Exists(d)) continue;
                    var w = new FileSystemWatcher(d, "*.json");
                    w.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
                    w.Changed += OnChanged; w.Created += OnChanged; w.Renamed += OnChanged;
                    w.EnableRaisingEvents = true;
                    _watchers.Add(w);
                }
                _skuText = ReadSafe(s.SkuConfigsFull);
                _settingsText = ReadSafe(AppPaths.SettingsFile);
            }
        }

        private void OnChanged(object sender, FileSystemEventArgs e) { _timer.Change(400, Timeout.Infinite); }

        private static string ReadSafe(string path)
        {
            for (int i = 0; i < 5; i++)
            {
                try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null; }
                catch (IOException) { Thread.Sleep(100); }
                catch (UnauthorizedAccessException) { Thread.Sleep(100); }
            }
            return null;
        }

        private void OnTimer(object state)
        {
            lock (_lock)
            {
                try
                {
                    string st = ReadSafe(AppPaths.SettingsFile);
                    if (st != null && st != _settingsText)
                    {
                        _settingsText = st;
                        try
                        {
                            var ns = AppSettings.FromJson(Json.Parse(st) as Json.Obj);
                            _engine.ApplySettings(ns, false);
                            Log.Info("检测到 settings.json 变更，已热加载");
                        }
                        catch (Exception ex) { Log.Info("settings.json 热加载被拒绝（保留旧配置）：" + ex.Message); }
                    }
                    string kt = ReadSafe(_engine.Settings.SkuConfigsFull);
                    if (kt != null && kt != _skuText)
                    {
                        _skuText = kt;
                        try { _engine.ReloadSkuConfigs(); Log.Info("检测到 configs.json 变更，已校验并热加载"); }
                        catch (Exception ex) { Log.Info("configs.json 热加载被拒绝（保留旧配置）：" + ex.Message); }
                    }
                }
                catch (Exception ex) { Log.Info("配置监视异常：" + ex.Message); }
            }
        }

        public void Dispose()
        {
            lock (_lock) { foreach (var w in _watchers) w.Dispose(); _watchers.Clear(); }
            _timer.Dispose();
        }
    }
}
