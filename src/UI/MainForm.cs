using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using WeightCheckerCS.Core;
using WeightCheckerCS.Engine;

namespace WeightCheckerCS.UI
{
    /// <summary>定长记录环形缓冲（仅 UI 线程访问）。索引 0 = 最新。</summary>
    public sealed class RecordBuffer
    {
        private WeightRecord[] _buf;
        private int _head, _count;
        public RecordBuffer(int cap) { _buf = new WeightRecord[Math.Max(1, cap)]; }
        public int Count { get { return _count; } }
        public void Add(WeightRecord r) { _buf[_head] = r; _head = (_head + 1) % _buf.Length; if (_count < _buf.Length) _count++; }
        public WeightRecord NewestAt(int i) { return i < 0 || i >= _count ? null : _buf[((_head - 1 - i) % _buf.Length + _buf.Length) % _buf.Length]; }
        public void Clear() { Array.Clear(_buf, 0, _buf.Length); _head = 0; _count = 0; }
        public void Resize(int cap)
        {
            if (cap == _buf.Length) return;
            var old = new List<WeightRecord>();
            for (int i = Math.Min(_count, cap) - 1; i >= 0; i--) old.Add(NewestAt(i));
            _buf = new WeightRecord[Math.Max(1, cap)]; _head = 0; _count = 0;
            foreach (var r in old) Add(r);
        }
    }

    /// <summary>双缓冲虚拟 ListView：只为可见行创建条目，十万级数据也不卡。</summary>
    public sealed class FastListView : ListView
    {
        public FastListView()
        {
            DoubleBuffered = true;
            View = View.Details; FullRowSelect = true; GridLines = true; HideSelection = false;
            Dock = DockStyle.Fill;
        }
    }

    public sealed class MainForm : Form
    {
        private static readonly string[] Columns = { "接收时间", "序号", "重量(g)", "称重机", "状态机", "中心(g)", "合格区间(g)", "宽度(g)", "倾斜(g)", "STATE(判定时)", "中心备注", "剔除", "原始数据" };
        private static readonly int[] ColWidths = { 95, 60, 70, 60, 60, 70, 120, 60, 60, 120, 110, 45, 260 };
        private static readonly Color OkColor = Color.FromArgb(22, 128, 60), LightColor = Color.FromArgb(198, 40, 40),
            HeavyColor = Color.FromArgb(178, 58, 0), UnknownColor = Color.FromArgb(21, 101, 192);

        private readonly WeighEngine _engine;
        private readonly Timer _uiTimer = new Timer();
        private RecordBuffer _realtime, _abnormal, _mismatch;
        private readonly double[] _recent = new double[12];
        private int _recentCount;
        private FastListView _rtList, _abList, _mmList;
        private ListView _rulesList;
        private TextBox _logBox;
        private Label _weightLabel, _verdictLabel, _infoLabel, _countLabel;
        private WindowGauge _gauge;
        private ToolStripButton _startBtn, _stopBtn;
        private ToolStripComboBox _skuCombo;
        private ToolStripStatusLabel _stStatus, _stDetail, _stCount, _stDio, _stTime;
        private PropertyGrid _settingsGrid, _skuGrid;
        private TextBox _chsBox, _dllBox;
        private NumericUpDown _pulseNum;
        private Label _dioLabel;
        private NotifyIcon _tray;
        private WeighingConfig _shownSku;
        private SkuCatalog _shownCatalog;

        private sealed class SkuItem
        {
            public readonly string Key, Text;
            public SkuItem(string key, string text) { Key = key; Text = text; }
            public override string ToString() { return Text; }
        }
        private bool _forceClose, _suppressSkuEvent;
        private readonly Stopwatch _runWatch = new Stopwatch();
        private int _logLines;

        public MainForm(WeighEngine engine)
        {
            _engine = engine;
            var s = engine.Settings;
            _realtime = new RecordBuffer(s.RealtimeRows);
            _abnormal = new RecordBuffer(s.AbnormalRows);
            _mismatch = new RecordBuffer(s.AbnormalRows);
            Text = "WeightCheckerCS 状态机称重判定系统（工控版）";
            Font = new Font("Microsoft YaHei UI", 9f);
            Size = new Size(1360, 820);
            MinimumSize = new Size(1000, 600);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BuildUi();
            _uiTimer.Interval = s.UiRefreshMs;
            _uiTimer.Tick += OnUiTick;
            _uiTimer.Start();
            RefreshSkuView(true);
            UpdateRunButtons();
            if (s.AutoStart) StartCollect();
        }

        // ===================== 界面搭建 =====================
        private void BuildUi()
        {
            var tool = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, ImageScalingSize = new Size(16, 16), Padding = new Padding(6, 2, 6, 2) };
            _startBtn = new ToolStripButton("▶ 开始采集") { Font = new Font(Font, FontStyle.Bold), ForeColor = OkColor };
            _stopBtn = new ToolStripButton("■ 停止采集") { Font = new Font(Font, FontStyle.Bold), ForeColor = LightColor };
            _startBtn.Click += delegate { StartCollect(); };
            _stopBtn.Click += delegate { StopCollect(); };
            _skuCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 190 };
            _skuCombo.SelectedIndexChanged += OnSkuChanged;
            var pulseBtn = new ToolStripButton("手动剔除一次");
            pulseBtn.Click += delegate { _engine.ManualPulse(); };
            var dirBtn = new ToolStripButton("打开数据目录");
            dirBtn.Click += delegate { OpenDir(_engine.Settings.DataDirFull); };
            var resetBtn = new ToolStripButton("清空计数/列表");
            resetBtn.Click += delegate { ClearViews(); };
            tool.Items.AddRange(new ToolStripItem[] { _startBtn, _stopBtn, new ToolStripSeparator(), new ToolStripLabel("产品 SKU:"), _skuCombo,
                new ToolStripSeparator(), pulseBtn, dirBtn, resetBtn });

            var status = new StatusStrip();
            _stStatus = new ToolStripStatusLabel("STOPPED") { Font = new Font(Font, FontStyle.Bold) };
            _stDetail = new ToolStripStatusLabel("") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _stCount = new ToolStripStatusLabel("");
            _stDio = new ToolStripStatusLabel("");
            _stTime = new ToolStripStatusLabel("");
            status.Items.AddRange(new ToolStripItem[] { _stStatus, _stDetail, _stCount, _stDio, _stTime });

            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 6 };
            split.Panel1MinSize = 340;

            // ---- 左侧：当前盒 ----
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(8) };
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.Controls.Add(new Label { Text = "当前重量 (g)", Dock = DockStyle.Fill, ForeColor = Color.DimGray }, 0, 0);
            _weightLabel = new Label { Text = "--", Dock = DockStyle.Fill, Font = new Font("Consolas", 36f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
            _verdictLabel = new Label { Text = "--", Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 18f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            _gauge = new WindowGauge { Dock = DockStyle.Fill };
            _infoLabel = new Label { Dock = DockStyle.Fill, Font = new Font("Consolas", 10.5f), Text = "" };
            _countLabel = new Label { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 10.5f), Text = "" };
            left.Controls.Add(_weightLabel, 0, 1);
            left.Controls.Add(_verdictLabel, 0, 2);
            left.Controls.Add(_gauge, 0, 3);
            left.Controls.Add(_infoLabel, 0, 4);
            left.Controls.Add(_countLabel, 0, 5);
            split.Panel1.Controls.Add(left);

            // ---- 右侧：标签页 ----
            var tabs = new TabControl { Dock = DockStyle.Fill };
            _rtList = MakeList(() => _realtime);
            _abList = MakeList(() => _abnormal);
            _mmList = MakeList(() => _mismatch);
            tabs.TabPages.Add(Page("实时数据", _rtList));
            var abPage = Page("异常数据", _abList);
            var saveAb = new Button { Text = "导出异常数据 CSV", Dock = DockStyle.Top, Height = 30 };
            saveAb.Click += delegate { ExportBuffer(_abnormal, "abnormal"); };
            abPage.Controls.Add(saveAb);
            tabs.TabPages.Add(abPage);
            var mmPage = Page("称重机/状态机不一致", _mmList);
            var saveMm = new Button { Text = "导出不一致数据 CSV", Dock = DockStyle.Top, Height = 30 };
            saveMm.Click += delegate { ExportBuffer(_mismatch, "mismatch"); };
            mmPage.Controls.Add(saveMm);
            tabs.TabPages.Add(mmPage);
            _rulesList = new ListView { View = View.Details, FullRowSelect = true, GridLines = true, Dock = DockStyle.Fill };
            foreach (var h in new[] { "STATE", "趋势程度", "下限(相对中心 g)", "上限(相对中心 g)", "宽度(g)", "中点倾斜(g)" }) _rulesList.Columns.Add(h, 130);
            tabs.TabPages.Add(Page("各状态区间规则", _rulesList));
            _logBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), BackColor = Color.White, WordWrap = false };
            tabs.TabPages.Add(Page("运行日志", _logBox));
            tabs.TabPages.Add(BuildSettingsPage());
            split.Panel2.Controls.Add(tabs);

            Controls.Add(split);
            Controls.Add(tool);
            Controls.Add(status);
            Load += delegate { try { split.SplitterDistance = 380; } catch { } };

            _tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "WeightCheckerCS", Visible = true };
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, delegate { RestoreWindow(); });
            menu.Items.Add("退出程序", null, delegate { _forceClose = true; Close(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { RestoreWindow(); };
        }

        private static TabPage Page(string title, Control c)
        {
            var p = new TabPage(title);
            p.Controls.Add(c);
            return p;
        }

        private FastListView MakeList(Func<RecordBuffer> source)
        {
            var lv = new FastListView { VirtualMode = true, VirtualListSize = 0 };
            for (int i = 0; i < Columns.Length; i++) lv.Columns.Add(Columns[i], ColWidths[i]);
            lv.RetrieveVirtualItem += (sender, e) =>
            {
                WeightRecord r = null;
                try { r = source().NewestAt(e.ItemIndex); } catch { }
                e.Item = MakeItem(r, Columns.Length);
            };
            return lv;
        }

        private static ListViewItem MakeItem(WeightRecord r, int cols)
        {
            if (r == null) { var blank = new ListViewItem(""); for (int i = 1; i < cols; i++) blank.SubItems.Add(""); return blank; }
            var a = r.Algo;
            var it = new ListViewItem(r.ReceivedAt.ToString("HH:mm:ss.fff"));
            it.SubItems.Add(r.SequenceNo ?? "");
            it.SubItems.Add(Fmt.F2(r.Weight));
            it.SubItems.Add(r.MachineStatus);
            it.SubItems.Add(r.AlgoStatus);
            it.SubItems.Add(a == null ? "--" : Fmt.F2(a.Window.Center));
            it.SubItems.Add(a == null ? "--" : Fmt.F2(a.Window.Lower) + " ~ " + Fmt.F2(a.Window.Upper));
            it.SubItems.Add(a == null ? "--" : Fmt.F2(a.Window.Width));
            it.SubItems.Add(a == null ? "--" : Fmt.S2(a.Window.Shift));
            it.SubItems.Add(a == null ? "--" : Names.State(a.StateBefore) + " (" + (int)a.StateBefore + ")");
            it.SubItems.Add(a == null ? "" : (a.Relocked ? "阶跃确认·中心重锁" : a.Outlier ? "粗大异常·不计入中心" : ""));
            it.SubItems.Add(r.RejectFired ? "是" : "");
            it.SubItems.Add(r.RawLine);
            string v = r.Verdict;
            if (v != "合格") it.ForeColor = VerdictColor(v);
            return it;
        }

        private static Color VerdictColor(string v)
        {
            return v == "合格" ? OkColor : v == "过轻" ? LightColor : v == "超重" ? HeavyColor : UnknownColor;
        }

        private TabPage BuildSettingsPage()
        {
            var page = new TabPage("设置（热加载）");
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(6) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // 喷气 / DLL
            var dio = new GroupBox { Text = "剔除输出（喷气）— 每个 SKU 单独设置，修改后立即生效并保存；DLL 为全部 SKU 共用", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6) };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            for (int i = 0; i < 3; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _pulseNum = new NumericUpDown { DecimalPlaces = 0, Minimum = 1m, Maximum = 60000m, Increment = 10m, Width = 100 };
            _chsBox = new TextBox { Width = 120 };
            var savePulse = new Button { Text = "保存喷气参数", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
            savePulse.Click += delegate { SavePulse(); };
            var testPulse = new Button { Text = "测试喷气一次", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
            testPulse.Click += delegate { _engine.ManualPulse(); };
            t.Controls.Add(new Label { Text = "激活时长(毫秒):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            var row0 = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            row0.Controls.Add(_pulseNum);
            row0.Controls.Add(new Label { Text = "  激活引脚(逗号分隔):", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
            row0.Controls.Add(_chsBox);
            t.Controls.Add(row0, 1, 0);
            t.SetColumnSpan(row0, 2);
            t.Controls.Add(savePulse, 3, 0);
            t.Controls.Add(testPulse, 4, 0);
            _dllBox = new TextBox { Dock = DockStyle.Fill };
            var browse = new Button { Text = "浏览…", Dock = DockStyle.Fill, AutoSize = true };
            browse.Click += delegate
            {
                using (var dlg = new OpenFileDialog { Filter = "DLL (*.dll)|*.dll", FileName = _dllBox.Text })
                    if (dlg.ShowDialog(this) == DialogResult.OK) _dllBox.Text = dlg.FileName;
            };
            var reload = new Button { Text = "保存并加载 DLL", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
            reload.Click += delegate { SaveDllPath(); };
            t.Controls.Add(new Label { Text = "mezio_c.dll 路径:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            t.Controls.Add(_dllBox, 1, 1);
            t.Controls.Add(browse, 2, 1);
            t.Controls.Add(reload, 3, 1);
            t.SetColumnSpan(reload, 2);
            _dioLabel = new Label { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(0, 4, 0, 4) };
            t.Controls.Add(_dioLabel, 0, 2);
            t.SetColumnSpan(_dioLabel, 5);
            dio.Controls.Add(t);
            root.Controls.Add(dio, 0, 0);
            root.SetColumnSpan(dio, 2);

            root.Controls.Add(GridBox("程序设置（config\\settings.json）", out _settingsGrid, SaveSettings, delegate { _settingsGrid.SelectedObject = new SettingsProxy(_engine.Settings); }), 0, 1);
            root.Controls.Add(GridBox("当前 SKU 状态机参数（configs.json）", out _skuGrid, SaveSkuParams, delegate { _skuGrid.SelectedObject = new SkuProxy(_engine.CurrentSku); }), 1, 1);
            page.Controls.Add(root);
            _settingsGrid.SelectedObject = new SettingsProxy(_engine.Settings);
            _dllBox.Text = _engine.Settings.DioDllPath;
            return page;
        }

        private static GroupBox GridBox(string title, out PropertyGrid grid, Action save, Action revert)
        {
            var g = new GroupBox { Text = title, Dock = DockStyle.Fill };
            grid = new PropertyGrid { Dock = DockStyle.Fill, ToolbarVisible = false, PropertySort = PropertySort.Categorized };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.RightToLeft };
            var b1 = new Button { Text = "保存并应用", Width = 110, Height = 28 };
            var b2 = new Button { Text = "还原", Width = 80, Height = 28 };
            b1.Click += delegate { save(); };
            b2.Click += delegate { revert(); };
            bar.Controls.Add(b1); bar.Controls.Add(b2);
            g.Controls.Add(grid);
            g.Controls.Add(bar);
            return g;
        }

        // ===================== 操作 =====================
        private void StartCollect()
        {
            _engine.Start();
            _runWatch.Restart();
            UpdateRunButtons();
        }

        private void StopCollect()
        {
            Cursor = Cursors.WaitCursor;
            try { _engine.Stop(); } finally { Cursor = Cursors.Default; }
            _runWatch.Stop();
            UpdateRunButtons();
        }

        private void UpdateRunButtons()
        {
            _startBtn.Enabled = !_engine.Running;
            _stopBtn.Enabled = _engine.Running;
        }

        private void ClearViews()
        {
            _realtime.Clear(); _abnormal.Clear(); _mismatch.Clear(); _recentCount = 0;
            _rtList.VirtualListSize = 0; _abList.VirtualListSize = 0; _mmList.VirtualListSize = 0;
            _engine.ResetCounters();
            _gauge.Clear();
        }

        private void OnSkuChanged(object sender, EventArgs e)
        {
            if (_suppressSkuEvent || _skuCombo.SelectedItem == null) return;
            var item = _skuCombo.SelectedItem as SkuItem;
            if (item == null || item.Key == _engine.CurrentSku.Sku) return;
            if (!_engine.Skus.Contains(item.Key)) { RefreshSkuView(true); return; }
            var target = _engine.Skus[item.Key];
            if (MessageBox.Show(this, "切换到 " + item.Text + "？状态机将重新初始化。", "切换 SKU", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            {
                RefreshSkuView(true);
                return;
            }
            var ns = _engine.Settings.Clone();
            ns.Sku = target.Sku;
            ns.ProductPadNum = target.ProductPadNum;
            Try(delegate { _engine.ApplySettings(ns, true); });
            _recentCount = 0;
            _gauge.Clear();
            RefreshSkuView(true);
        }

        private void SavePulse()
        {
            Try(delegate
            {
                var parts = _chsBox.Text.Split(new[] { ',', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var chs = new int[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                    if (!int.TryParse(parts[i], out chs[i])) throw new FormatException("引脚号必须是整数：" + parts[i]);
                _engine.UpdatePulse((int)_pulseNum.Value, chs);
                MessageBox.Show(this, "喷气参数已保存并立即生效。", "保存成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }

        private void SaveDllPath()
        {
            Try(delegate
            {
                var ns = _engine.Settings.Clone();
                ns.DioDllPath = _dllBox.Text.Trim();
                if (!File.Exists(ns.DioDllFull)) throw new FormatException("文件不存在：" + ns.DioDllFull);
                _engine.ApplySettings(ns, true);
                _settingsGrid.SelectedObject = new SettingsProxy(_engine.Settings);
            });
        }

        private void SaveSettings()
        {
            var proxy = _settingsGrid.SelectedObject as SettingsProxy;
            if (proxy == null) return;
            if (Try(delegate { _engine.ApplySettings(proxy.ToSettings(), true); }))
            {
                ApplyUiSettings();
                RefreshSkuView(true);
                MessageBox.Show(this, "程序设置已保存并生效（无需重启）。", "保存成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void SaveSkuParams()
        {
            var proxy = _skuGrid.SelectedObject as SkuProxy;
            if (proxy == null) return;
            if (Try(delegate { _engine.SaveSku(proxy.ToConfig()); }))
            {
                RefreshSkuView(true);
                MessageBox.Show(this, "SKU 参数已保存并热加载（保留中心历史）。", "保存成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void ApplyUiSettings()
        {
            var s = _engine.Settings;
            if (_uiTimer.Interval != s.UiRefreshMs) _uiTimer.Interval = s.UiRefreshMs;
            _realtime.Resize(s.RealtimeRows);
            _abnormal.Resize(s.AbnormalRows);
            _mismatch.Resize(s.AbnormalRows);
            _rtList.VirtualListSize = _realtime.Count; _abList.VirtualListSize = _abnormal.Count; _mmList.VirtualListSize = _mismatch.Count;
        }

        private bool Try(Action a)
        {
            try { a(); return true; }
            catch (Exception ex)
            {
                Log.Info("操作失败：" + ex.Message);
                MessageBox.Show(this, ex.Message, "操作失败（原配置保持不变）", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        private void ExportBuffer(RecordBuffer buf, string name)
        {
            if (buf.Count == 0) { MessageBox.Show(this, "当前没有可导出的数据。", "导出"); return; }
            Try(delegate
            {
                string dir = Path.Combine(_engine.Settings.DataDirFull, "csv");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, name + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
                using (var w = new StreamWriter(file, false, new UTF8Encoding(true)))
                {
                    w.WriteLine(string.Join(",", CsvStorage.Header));
                    for (int i = buf.Count - 1; i >= 0; i--) w.WriteLine(CsvStorage.ToCsvLine(CsvStorage.Row(buf.NewestAt(i))));
                }
                MessageBox.Show(this, "已导出 " + buf.Count + " 条：\n" + file, "导出完成");
            });
        }

        private static void OpenDir(string dir)
        {
            try { Directory.CreateDirectory(dir); Process.Start("explorer.exe", "\"" + dir + "\""); } catch { }
        }

        // ===================== 定时刷新（唯一的 UI 更新入口） =====================
        private void OnUiTick(object sender, EventArgs e)
        {
            try { UiTick(); }
            catch (Exception ex) { Log.Info("界面刷新异常（已忽略）：" + ex.Message); }
        }

        private void UiTick()
        {
            WeightRecord r, last = null;
            int n = 0, ab = 0, mm = 0;
            while (n < 5000 && _engine.TryDequeueRecord(out r))
            {
                _realtime.Add(r);
                if (r.Verdict != "合格") { _abnormal.Add(r); ab++; }
                if (r.Mismatch) { _mismatch.Add(r); mm++; }
                last = r; n++;
                if (r.Algo != null && r.Algo.Valid)
                {
                    if (_recentCount == _recent.Length) { Array.Copy(_recent, 1, _recent, 0, _recent.Length - 1); _recentCount--; }
                    _recent[_recentCount++] = r.Weight.Value;
                }
            }
            if (n > 0)
            {
                UpdateList(_rtList, _realtime);
                if (ab > 0) UpdateList(_abList, _abnormal);
                if (mm > 0) UpdateList(_mmList, _mismatch);
                ShowCurrent(last);
            }
            SetText(_countLabel, "过轻 " + _engine.Light + "   超重 " + _engine.Heavy + "   未知 " + _engine.Unknown
                + "\n剔除脉冲 " + _engine.Rejects + "   总记录 " + _engine.Records + "   错误 " + _engine.Errors);

            string st = _engine.Status;
            if (_stStatus.Text != st)
            {
                _stStatus.Text = st;
                _stStatus.ForeColor = st == "CONNECTED" ? OkColor : st == "STOPPED" ? Color.DimGray : HeavyColor;
                _tray.Text = "WeightCheckerCS - " + st;
                UpdateRunButtons();
            }
            SetText(_stDetail, _engine.StatusDetail);
            SetText(_stCount, "记录 " + _engine.Records + " | 错误 " + _engine.Errors + (_engine.CsvDropped > 0 ? " | CSV丢弃 " + _engine.CsvDropped : ""));
            var dio = _engine.Dio;
            SetText(_stDio, "DIO: " + dio.Status + " | 脉冲 " + dio.PulseCount + (dio.FailCount > 0 ? " 失败 " + dio.FailCount : "") + (dio.Pending > 0 ? " 排队 " + dio.Pending : ""));
            if (_runWatch.IsRunning) { var ts = _runWatch.Elapsed; SetText(_stTime, string.Format("运行 {0:00}:{1:00}:{2:00}", (int)ts.TotalHours, ts.Minutes, ts.Seconds)); }
            SetText(_dioLabel, "DLL 状态：" + dio.Status + "    已加载：" + (dio.LoadedPath ?? "-") + "    实际剔除脉冲 " + dio.PulseCount + "，失败 " + dio.FailCount + "，队列丢弃 " + dio.DropCount);

            if (!ReferenceEquals(_shownSku, _engine.CurrentSku) || !ReferenceEquals(_shownCatalog, _engine.Skus)) RefreshSkuView(false);
            DrainLogs();
        }

        private static void UpdateList(FastListView lv, RecordBuffer buf)
        {
            if (lv.VirtualListSize != buf.Count) lv.VirtualListSize = buf.Count;
            if (lv.Visible) lv.Invalidate();
        }

        private static void SetText(Control c, string text) { if (c.Text != text) c.Text = text; }
        private static void SetText(ToolStripItem c, string text) { if (c.Text != text) c.Text = text; }

        private void ShowCurrent(WeightRecord r)
        {
            SetText(_weightLabel, Fmt.F2(r.Weight));
            var a = r.Algo;
            string v = a != null ? r.AlgoStatus : "未启用";
            SetText(_verdictLabel, a != null ? "状态机: " + v + "\n称重机: " + r.MachineStatus : "状态机未启用\n称重机: " + r.MachineStatus);
            _verdictLabel.ForeColor = a != null ? VerdictColor(v) : Color.DimGray;
            if (a == null) { _infoLabel.Text = ""; return; }
            var w = a.Window;
            SetText(_infoLabel, "中心  " + Fmt.F2(w.Center) + " g  (历史 " + a.HistoryCount + " 盒)\n区间  " + Fmt.F2(w.Lower) + " ~ " + Fmt.F2(w.Upper) + " g\n宽度  "
                + Fmt.F2(w.Width) + " g   倾斜 " + Fmt.S2(w.Shift) + " g\nSTATE " + Names.State(a.StateBefore) + " → " + Names.State(a.StateAfter)
                + "\n" + (a.Relocked ? "阶跃确认·中心重锁" : a.Outlier ? "粗大异常·不计入中心" : "") + (r.RejectFired ? "  [已剔除]" : ""));
            var baseW = StateMachineWeigher.ComputePassWindow(w.Center, MachineState.Stable, _engine.CurrentSku);
            _gauge.SetSnapshot(w.Center, w.Lower, w.Upper, baseW.Lower, baseW.Upper, a.Valid ? r.Weight.Value : double.NaN, v, _recent, _recentCount);
        }

        private void RefreshSkuView(bool force)
        {
            var cfg = _engine.CurrentSku;
            var cat = _engine.Skus;
            _shownSku = cfg;
            _shownCatalog = cat;
            _suppressSkuEvent = true;
            try
            {
                _skuCombo.Items.Clear();
                foreach (var k in cat.Items)
                {
                    var item = new SkuItem(k.Sku, cat.LabelOf(k));
                    _skuCombo.Items.Add(item);
                    if (k.Sku == cfg.Sku) _skuCombo.SelectedItem = item;
                }
                _skuCombo.ToolTipText = "当前 SKU 配置块：" + cfg.Sku + (cat.Skipped.Count > 0 ? "\n已跳过 " + cat.Skipped.Count + " 个格式错误的配置块（见运行日志）" : "")
                    + (cat.Warnings.Count > 0 ? "\n" + cat.Warnings.Count + " 条块名警告（见运行日志）" : "");
            }
            finally { _suppressSkuEvent = false; }
            _rulesList.BeginUpdate();
            _rulesList.Items.Clear();
            foreach (MachineState st in new[] { MachineState.StrongUp, MachineState.WeakUp, MachineState.Stable, MachineState.WeakDown1, MachineState.WeakDown2, MachineState.StrongDown })
            {
                var w = StateMachineWeigher.ComputePassWindow(cfg.InitialCenter, st, cfg);
                var it = new ListViewItem(Names.State(st) + " (" + (int)st + ")");
                it.SubItems.Add(w.Degree.ToString("0.###"));
                it.SubItems.Add(Fmt.S2(w.Lower - cfg.InitialCenter));
                it.SubItems.Add(Fmt.S2(w.Upper - cfg.InitialCenter));
                it.SubItems.Add(Fmt.F2(w.Width));
                it.SubItems.Add(Fmt.S2(w.Shift));
                _rulesList.Items.Add(it);
            }
            _rulesList.EndUpdate();
            _pulseNum.Value = Math.Max(_pulseNum.Minimum, Math.Min(_pulseNum.Maximum, (decimal)cfg.RejectActiveMs));
            _chsBox.Text = string.Join(",", Array.ConvertAll(cfg.RejectPins, x => x.ToString(CultureInfo.InvariantCulture)));
            if (force || !_skuGrid.ContainsFocus) _skuGrid.SelectedObject = new SkuProxy(cfg);
            if (force || !_settingsGrid.ContainsFocus) _settingsGrid.SelectedObject = new SettingsProxy(_engine.Settings);
            if (!_dllBox.Focused) _dllBox.Text = _engine.Settings.DioDllPath;
            ApplyUiSettings();
        }

        private void DrainLogs()
        {
            StringBuilder sb = null;
            string line;
            int n = 0;
            while (n < 500 && Log.TryDequeueUi(out line))
            {
                if (sb == null) sb = new StringBuilder();
                sb.Append(line).Append("\r\n");
                n++;
            }
            if (sb == null) return;
            _logLines += n;
            if (_logLines > 3000 || _logBox.TextLength > 400000)
            {
                string keep = _logBox.Text;
                int cut = keep.Length > 200000 ? keep.IndexOf('\n', keep.Length - 200000) : -1;
                _logBox.Text = cut > 0 ? keep.Substring(cut + 1) : "";
                _logLines = 1500;
            }
            _logBox.AppendText(sb.ToString());
        }

        // ===================== 窗口/托盘 =====================
        private void RestoreWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_forceClose && e.CloseReason == CloseReason.UserClosing)
            {
                if (_engine.Settings.MinimizeToTray)
                {
                    e.Cancel = true;
                    Hide();
                    _tray.ShowBalloonTip(2000, "WeightCheckerCS", "程序仍在后台运行，双击托盘图标恢复；右键可退出。", ToolTipIcon.Info);
                    return;
                }
                if (_engine.Running && MessageBox.Show(this, "正在采集，确定退出？", "退出", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                {
                    e.Cancel = true;
                    return;
                }
            }
            _uiTimer.Stop();
            _tray.Visible = false;
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _uiTimer.Dispose(); _tray.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
