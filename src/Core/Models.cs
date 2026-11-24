using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WeightCheckerCS.Core
{
    public enum MachineState { StrongDown = -3, WeakDown2 = -2, WeakDown1 = -1, Stable = 0, WeakUp = 1, StrongUp = 2 }
    public enum TrendInput { Invalid, Up, Down, Neutral }
    public enum Classification { None, Light, Qualified, Heavy }

    public static class Names
    {
        public static string State(MachineState s)
        {
            switch (s)
            {
                case MachineState.StrongDown: return "STRONG_DOWN";
                case MachineState.WeakDown2: return "WEAK_DOWN_2";
                case MachineState.WeakDown1: return "WEAK_DOWN_1";
                case MachineState.Stable: return "STABLE";
                case MachineState.WeakUp: return "WEAK_UP";
                default: return "STRONG_UP";
            }
        }
        public static string StateCn(MachineState s)
        {
            switch (s)
            {
                case MachineState.StrongDown: return "强下行";
                case MachineState.WeakDown2: return "弱下行2";
                case MachineState.WeakDown1: return "弱下行1";
                case MachineState.Stable: return "稳定";
                case MachineState.WeakUp: return "弱上行";
                default: return "强上行";
            }
        }
        public static string Signal(TrendInput t)
        {
            switch (t) { case TrendInput.Up: return "U"; case TrendInput.Down: return "D"; case TrendInput.Neutral: return "N"; default: return "X"; }
        }
        /// <summary>与称重机一致的判定代码：1=过轻 2=合格 E=超重；无效=null。</summary>
        public static string Code(Classification c)
        {
            switch (c) { case Classification.Light: return "1"; case Classification.Qualified: return "2"; case Classification.Heavy: return "E"; default: return null; }
        }
        public static string CodeText(string code)
        {
            if (code == "2") return "合格";
            if (code == "1") return "过轻";
            if (code == "E") return "超重";
            return "未知";
        }
    }

    public struct PassWindow
    {
        public double Center, Lower, Upper, Shift, Degree;
        public double Width { get { return Upper - Lower; } }
    }

    public sealed class BoxResult
    {
        public double? Weight;
        public Classification Classification;
        public TrendInput Signal;
        public MachineState StateBefore, StateAfter;
        public PassWindow Window;
        public int HistoryCount;
        public double? Fast, Error;
        public bool Valid, Alarm, Reset, Outlier, Relocked;
        public string Code { get { return Names.Code(Classification); } }
        public string CenterFlag { get { return Relocked ? "RELOCK" : (Outlier ? "OUTLIER" : ""); } }
    }

    /// <summary>单个 SKU 的状态机参数（与 Python 版 configs.json 字段一一对应）。不可变。</summary>
    public sealed class WeighingConfig
    {
        /// <summary>SKU 标识 = configs.json 中的块名，规范为 "{pack}-pack-{pad_size}"（如 "9-pack-270mm"、"9-pack-240mm"）。</summary>
        public string Sku, PadSize;

        /// <summary>规范块名："{pack}-pack-{pad_size}"，尺寸去空白并转小写。</summary>
        public static string CanonicalKey(int pack, string padSize)
        {
            var sb = new StringBuilder();
            foreach (char ch in (padSize ?? "")) if (!char.IsWhiteSpace(ch)) sb.Append(char.ToLowerInvariant(ch));
            return pack + "-pack-" + sb;
        }
        /// <summary>可选的下拉框显示名；为空时显示 "{pack}-pack {pad_size}"。</summary>
        public string DisplayName;
        public int ProductPadNum;
        public string Label
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(DisplayName)) return DisplayName.Trim();
                return (ProductPadNum + "-pack " + (PadSize ?? "").Trim()).Trim();
            }
        }
        public double InitialCenter, PhysicalMin, PhysicalMax, SafetyLower;
        public double PadWeight = 7.2;
        public int CenterWindowN = 7, CenterTrimN = 1;
        public double OutlierReject = 5.4;
        public int OutlierRelockCount = 3;
        public double RelockMaxOffset = 0.0, HalfWidth = 3.7, WindowOffset = 0.0;
        public double MinWidth = 7.4, MaxWidth = 9.0, UpMaxShift = 1.0, DownMaxShift = 0.9;
        public int FastWindowK = 3;
        public double TrendUp = 1.0, TrendDown = 1.5, StopResetSeconds = 1800.0;
        public int InvalidAlarmCount = 3;
        /// <summary>剔除时同时激活的 DO 引脚号（0~31）。</summary>
        public int[] RejectPins = new[] { 2 };
        /// <summary>每次剔除引脚保持激活的时长（毫秒）。</summary>
        public int RejectActiveMs = 200;
        /// <summary>读取时是否来自旧版 do_out 格式（保存时会自动改写为 reject_output）。</summary>
        public bool LegacyDoOut;
        public double RejectPulseSeconds { get { return RejectActiveMs / 1000.0; } }

        private static readonly string[] Allowed = {
            "sku","display_name","pad_size","product_pad_num","initial_center_g","physical_min_g","physical_max_g","safety_lower_g",
            "pad_weight_g","center_window_n","center_trim_n","outlier_reject_g","outlier_relock_count","relock_max_offset_g",
            "half_width_g","window_offset_g","min_window_width_g","max_window_width_g","up_max_shift_g","down_max_shift_g",
            "fast_window_k","trend_up_threshold_g","trend_down_threshold_g","stop_reset_seconds","invalid_alarm_count",
            "reject_output","do_out" };

        private static int[] IntArray(object v, string name)
        {
            var list = v as List<object>;
            if (list == null) throw new FormatException(name + " 必须是整数数组，例如 [2] 或 [2, 3]");
            var arr = new int[list.Count];
            for (int i = 0; i < arr.Length; i++)
            {
                if (!(list[i] is double) || (double)list[i] != Math.Floor((double)list[i])) throw new FormatException(name + " 必须是整数");
                arr[i] = (int)(double)list[i];
            }
            return arr;
        }

        public static WeighingConfig FromJson(string key, Json.Obj o)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new FormatException("状态机配置块名不能为空");
            if (o == null) throw new FormatException("状态机配置 " + key + " 必须是对象");
            foreach (var k in o.Keys)
                if (Array.IndexOf(Allowed, k) < 0) throw new FormatException("状态机配置 " + key + " 包含未知字段：" + k);
            var c = new WeighingConfig();
            try
            {
                c.Sku = Str(o, "sku", false);
                if (c.Sku != null && c.Sku != key)
                    throw new FormatException("状态机配置块名 " + key + " 与内部 sku \"" + c.Sku + "\" 不一致（sku 字段可省略）");
                c.Sku = key;
                c.DisplayName = Str(o, "display_name", false);
                c.PadSize = Str(o, "pad_size", true);
                if (string.IsNullOrWhiteSpace(c.PadSize)) throw new FormatException("pad_size 不能为空，例如 \"270mm\"");
                c.ProductPadNum = Int(o, "product_pad_num", 0, true);
                c.InitialCenter = Num(o, "initial_center_g", 0, true);
                c.PhysicalMin = Num(o, "physical_min_g", 0, true);
                c.PhysicalMax = Num(o, "physical_max_g", 0, true);
                c.SafetyLower = Num(o, "safety_lower_g", 0, true);
                c.PadWeight = Num(o, "pad_weight_g", c.PadWeight, false);
                c.CenterWindowN = Int(o, "center_window_n", c.CenterWindowN, false);
                c.CenterTrimN = Int(o, "center_trim_n", c.CenterTrimN, false);
                c.OutlierReject = Num(o, "outlier_reject_g", c.OutlierReject, false);
                c.OutlierRelockCount = Int(o, "outlier_relock_count", c.OutlierRelockCount, false);
                c.RelockMaxOffset = Num(o, "relock_max_offset_g", c.RelockMaxOffset, false);
                c.HalfWidth = Num(o, "half_width_g", c.HalfWidth, false);
                c.WindowOffset = Num(o, "window_offset_g", c.WindowOffset, false);
                c.MinWidth = Num(o, "min_window_width_g", c.MinWidth, false);
                c.MaxWidth = Num(o, "max_window_width_g", c.MaxWidth, false);
                c.UpMaxShift = Num(o, "up_max_shift_g", c.UpMaxShift, false);
                c.DownMaxShift = Num(o, "down_max_shift_g", c.DownMaxShift, false);
                c.FastWindowK = Int(o, "fast_window_k", c.FastWindowK, false);
                c.TrendUp = Num(o, "trend_up_threshold_g", c.TrendUp, false);
                c.TrendDown = Num(o, "trend_down_threshold_g", c.TrendDown, false);
                c.StopResetSeconds = Num(o, "stop_reset_seconds", c.StopResetSeconds, false);
                c.InvalidAlarmCount = Int(o, "invalid_alarm_count", c.InvalidAlarmCount, false);
                if (o.Has("reject_output") && o.Has("do_out"))
                    throw new FormatException("reject_output 与旧版 do_out 不能同时存在，请只保留 reject_output");
                if (o.Has("reject_output"))
                {
                    var d = o["reject_output"] as Json.Obj;
                    if (d == null || d.Count != 2 || !d.Has("active_pins") || !d.Has("active_ms"))
                        throw new FormatException("reject_output 必须且只能包含 active_pins（激活引脚数组）和 active_ms（激活时长毫秒）");
                    c.RejectPins = IntArray(d["active_pins"], "reject_output.active_pins");
                    if (!(d["active_ms"] is double) || (double)d["active_ms"] != Math.Floor((double)d["active_ms"]))
                        throw new FormatException("reject_output.active_ms 必须是整数毫秒");
                    c.RejectActiveMs = (int)(double)d["active_ms"];
                }
                else if (o.Has("do_out"))
                {
                    // 兼容旧格式：chs=引脚，t=秒；dll 已改为 settings.json 统一指定，此处忽略
                    var d = o["do_out"] as Json.Obj;
                    if (d == null || !d.Has("chs") || !d.Has("t")) throw new FormatException("旧版 do_out 缺少 chs 或 t");
                    c.RejectPins = IntArray(d["chs"], "do_out.chs");
                    if (!(d["t"] is double)) throw new FormatException("do_out.t 必须是数字（秒）");
                    c.RejectActiveMs = (int)Math.Round((double)d["t"] * 1000.0);
                    c.LegacyDoOut = true;
                }
            }
            catch (InvalidCastException) { throw new FormatException("状态机配置 " + key + " 字段类型错误"); }
            try { c.Validate(); }
            catch (FormatException ex) { throw new FormatException("状态机配置 " + key + " 无效：" + ex.Message); }
            return c;
        }

        public Json.Obj ToJson()
        {
            var o = new Json.Obj();
            o["sku"] = Sku;
            if (!string.IsNullOrWhiteSpace(DisplayName)) o["display_name"] = DisplayName;
            o["pad_size"] = PadSize; o["product_pad_num"] = ProductPadNum;
            o["initial_center_g"] = InitialCenter; o["physical_min_g"] = PhysicalMin; o["physical_max_g"] = PhysicalMax;
            o["safety_lower_g"] = SafetyLower; o["pad_weight_g"] = PadWeight;
            o["center_window_n"] = CenterWindowN; o["center_trim_n"] = CenterTrimN;
            o["outlier_reject_g"] = OutlierReject; o["outlier_relock_count"] = OutlierRelockCount;
            o["half_width_g"] = HalfWidth; o["min_window_width_g"] = MinWidth; o["max_window_width_g"] = MaxWidth;
            o["up_max_shift_g"] = UpMaxShift; o["down_max_shift_g"] = DownMaxShift; o["fast_window_k"] = FastWindowK;
            o["trend_up_threshold_g"] = TrendUp; o["trend_down_threshold_g"] = TrendDown;
            o["stop_reset_seconds"] = StopResetSeconds; o["invalid_alarm_count"] = InvalidAlarmCount;
            o["relock_max_offset_g"] = RelockMaxOffset; o["window_offset_g"] = WindowOffset;
            var d = new Json.Obj();
            d["active_pins"] = new List<int>(RejectPins); d["active_ms"] = RejectActiveMs;
            o["reject_output"] = d;
            return o;
        }

        public WeighingConfig Clone() { var c = (WeighingConfig)MemberwiseClone(); c.RejectPins = (int[])RejectPins.Clone(); return c; }

        public void Validate()
        {
            const double eps = 1e-9;
            if (RejectPins == null || RejectPins.Length == 0) throw new FormatException("reject_output.active_pins 必须是 0~31 范围内不重复的引脚号数组");
            var seen = new HashSet<int>();
            foreach (int ch in RejectPins)
                if (ch < 0 || ch > 31 || !seen.Add(ch)) throw new FormatException("reject_output.active_pins 必须是 0~31 范围内不重复的引脚号数组");
            if (RejectActiveMs < 1 || RejectActiveMs > 60000) throw new FormatException("reject_output.active_ms 必须在 1~60000 毫秒之间");
            if (ProductPadNum <= 0) throw new FormatException("product_pad_num 必须是正整数");
            if (CenterWindowN <= 0 || FastWindowK <= 0 || InvalidAlarmCount <= 0 || OutlierRelockCount <= 0)
                throw new FormatException("center_window_n / fast_window_k / invalid_alarm_count / outlier_relock_count 必须是正整数");
            if (CenterTrimN < 0 || CenterWindowN - 2 * CenterTrimN < 1) throw new FormatException("center_trim_n 必须为非负整数，且去尾后至少保留 1 盒");
            if (OutlierReject <= HalfWidth) throw new FormatException("outlier_reject_g 必须大于 half_width_g");
            if (!(PhysicalMin < InitialCenter && InitialCenter < PhysicalMax)) throw new FormatException("initial_center_g 必须在物理量程内");
            if (!(PhysicalMin <= SafetyLower && SafetyLower < PhysicalMax)) throw new FormatException("safety_lower_g 必须在物理量程内");
            if (PadWeight <= 0 || HalfWidth <= 0) throw new FormatException("pad_weight_g 与 half_width_g 必须大于 0");
            if (!(0 < MinWidth && MinWidth <= MaxWidth)) throw new FormatException("必须满足 0 < min_window_width_g ≤ max_window_width_g");
            if (!(MinWidth - eps <= 2 * HalfWidth && 2 * HalfWidth <= MaxWidth + eps)) throw new FormatException("基础区间宽度 2*half_width_g 必须在 [min, max] 宽度内");
            if (!Finite(RelockMaxOffset) || RelockMaxOffset < 0) throw new FormatException("relock_max_offset_g 必须 ≥ 0（0 表示不限制）");
            if (!Finite(WindowOffset) || Math.Abs(WindowOffset) >= HalfWidth) throw new FormatException("window_offset_g 的绝对值必须小于 half_width_g");
            if (Math.Min(UpMaxShift, DownMaxShift) < 0) throw new FormatException("区间最大偏移不能为负数");
            if (TrendUp <= 0 || TrendDown < TrendUp) throw new FormatException("趋势阈值必须为正，且下降阈值不得小于上升阈值");
            if (StopResetSeconds <= 0) throw new FormatException("stop_reset_seconds 必须大于 0");
            foreach (double v in new[] { InitialCenter, PhysicalMin, PhysicalMax, SafetyLower, PadWeight, OutlierReject, HalfWidth, MinWidth, MaxWidth, UpMaxShift, DownMaxShift, TrendUp, TrendDown, StopResetSeconds })
                if (!Finite(v)) throw new FormatException("参数不能为 NaN/Infinity");
        }

        private static bool Finite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }

        private static string Str(Json.Obj o, string k, bool req)
        {
            if (!o.Has(k)) { if (req) throw new FormatException("缺少字段 " + k); return null; }
            var s = o[k] as string;
            if (s == null) throw new FormatException(k + " 必须是字符串");
            return s;
        }
        private static double Num(Json.Obj o, string k, double def, bool req)
        {
            if (!o.Has(k)) { if (req) throw new FormatException("缺少字段 " + k); return def; }
            if (!(o[k] is double)) throw new FormatException(k + " 必须是数字");
            return (double)o[k];
        }
        private static int Int(Json.Obj o, string k, int def, bool req)
        {
            double d = Num(o, k, def, req);
            if (d != Math.Floor(d) || d > int.MaxValue || d < int.MinValue) throw new FormatException(k + " 必须是整数");
            return (int)d;
        }
    }

    /// <summary>configs.json 读写。</summary>
    public static class SkuConfigStore
    {
        public static Json.Obj ReadRaw(string path)
        {
            if (!File.Exists(path)) throw new FormatException("状态机配置文件不存在：" + path);
            object root;
            try { root = Json.Parse(File.ReadAllText(path, Encoding.UTF8)); }
            catch (FormatException ex) { throw new FormatException("状态机配置文件 JSON 格式错误：" + path + " " + ex.Message); }
            var o = root as Json.Obj;
            if (o == null) throw new FormatException("状态机配置文件根节点必须是对象：" + path);
            return o;
        }

        /// <summary>读取全部 SKU 块。格式错误的块会被跳过并记入 Skipped（不影响其他 SKU）；一个有效 SKU 都没有时抛异常。</summary>
        public static SkuCatalog LoadAll(string path)
        {
            var raw = ReadRaw(path);
            var list = new List<WeighingConfig>();
            var skipped = new List<string>();
            foreach (var key in raw.Keys)
            {
                try { list.Add(WeighingConfig.FromJson(key, raw[key] as Json.Obj)); }
                catch (FormatException ex) { skipped.Add(ex.Message.StartsWith("状态机配置") ? ex.Message : "状态机配置 " + key + " 无效：" + ex.Message); }
            }
            if (list.Count == 0)
                throw new FormatException("状态机配置文件中没有任何有效的 SKU" + (skipped.Count > 0 ? "：" + string.Join("；", skipped.ToArray()) : ""));
            return new SkuCatalog(list, skipped, CheckKeys(raw, list));
        }

        /// <summary>块名规范检查（只警告不拒绝）：重复块名、块名与 pack/尺寸不一致、pack+尺寸重复。</summary>
        private static List<string> CheckKeys(Json.Obj raw, List<WeighingConfig> list)
        {
            var warn = new List<string>();
            foreach (var k in raw.DuplicateKeys)
                warn.Add("块名 \"" + k + "\" 在 configs.json 中重复出现，只有最后一个生效");
            var byCanon = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in list)
            {
                string canon = WeighingConfig.CanonicalKey(c.ProductPadNum, c.PadSize);
                if (!string.Equals(c.Sku, canon, StringComparison.OrdinalIgnoreCase))
                    warn.Add("块名 \"" + c.Sku + "\" 与其 product_pad_num/pad_size（" + c.ProductPadNum + "-pack " + c.PadSize + "）不一致，建议改为 \"" + canon + "\"");
                string other;
                if (byCanon.TryGetValue(canon, out other))
                    warn.Add("块 \"" + other + "\" 与 \"" + c.Sku + "\" 的 pack 数和尺寸相同（" + canon + "），请确认是否重复配置");
                else byCanon[canon] = c.Sku;
            }
            return warn;
        }

        /// <summary>只替换一个 SKU 节点并原子写回，其余内容保持原样。</summary>
        public static void SaveSku(string path, WeighingConfig cfg)
        {
            cfg.Validate();
            var raw = ReadRaw(path);
            raw[cfg.Sku] = cfg.ToJson();
            AtomicFile.WriteAllText(path, Json.Write(raw), new UTF8Encoding(false));
        }

        /// <summary>若有 SKU 仍使用旧版 do_out，则备份原文件为 .bak 后整体改写为 reject_output 格式。返回改写的 SKU 数。</summary>
        public static int MigrateLegacy(string path, SkuCatalog loaded)
        {
            var raw = ReadRaw(path);
            int n = 0;
            foreach (var c in loaded.Items)
                if (c.LegacyDoOut && raw.Has(c.Sku)) { raw[c.Sku] = c.ToJson(); c.LegacyDoOut = false; n++; }
            if (n == 0) return 0;
            File.Copy(path, path + ".bak", true);
            AtomicFile.WriteAllText(path, Json.Write(raw), new UTF8Encoding(false));
            return n;
        }
    }

    /// <summary>configs.json 中全部有效 SKU（按 pack 数、尺寸排序），以块名为键。不可变。</summary>
    public sealed class SkuCatalog
    {
        private readonly Dictionary<string, WeighingConfig> _byKey = new Dictionary<string, WeighingConfig>(StringComparer.Ordinal);
        public readonly List<WeighingConfig> Items;
        /// <summary>被跳过的格式错误块（说明文字）。</summary>
        public readonly List<string> Skipped;

        /// <summary>块名规范警告（不影响加载）。</summary>
        public readonly List<string> Warnings;
        /// <summary>configs.json 中的原始顺序；旧版 "N-pack" 有多个候选时取文件中最靠前的（通常是原有块）。</summary>
        private readonly List<WeighingConfig> _fileOrder;

        public SkuCatalog(List<WeighingConfig> items, List<string> skipped) : this(items, skipped, null) { }

        public SkuCatalog(List<WeighingConfig> items, List<string> skipped, List<string> warnings)
        {
            Warnings = warnings ?? new List<string>();
            _fileOrder = new List<WeighingConfig>(items);
            Items = new List<WeighingConfig>(items);
            Items.Sort(delegate (WeighingConfig a, WeighingConfig b)
            {
                int r = a.ProductPadNum.CompareTo(b.ProductPadNum);
                if (r == 0) r = string.Compare(a.PadSize, b.PadSize, StringComparison.OrdinalIgnoreCase);
                return r != 0 ? r : string.CompareOrdinal(a.Sku, b.Sku);
            });
            foreach (var c in Items) _byKey[c.Sku] = c;
            Skipped = skipped ?? new List<string>();
        }

        public int Count { get { return Items.Count; } }
        public bool Contains(string sku) { return sku != null && _byKey.ContainsKey(sku); }
        public WeighingConfig this[string sku] { get { return _byKey[sku]; } }

        public WeighingConfig Find(string sku, int padNum) { string note; return Find(sku, padNum, out note); }

        /// <summary>
        /// 按块名查找。兼容旧版：块名为旧式 "N-pack"（已改名为 "N-pack-尺寸"）或为空（只有 product_pad_num）时，
        /// 取 pack 数相同的 SKU；有多个候选时取 configs.json 中最靠前的，并在 note 中说明。找不到返回 null。
        /// </summary>
        public WeighingConfig Find(string sku, int padNum, out string note)
        {
            note = null;
            WeighingConfig c;
            string legacy;
            int pack;
            if (!string.IsNullOrEmpty(sku))
            {
                if (_byKey.TryGetValue(sku, out c)) return c;
                var m = LegacyKey.Match(sku.Trim());
                if (!m.Success) return null;
                pack = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                legacy = "旧版块名 \"" + sku + "\"";
            }
            else { pack = padNum; legacy = "旧版设置 product_pad_num=" + padNum; }
            var cands = new List<WeighingConfig>();
            foreach (var x in _fileOrder) if (x.ProductPadNum == pack) cands.Add(x);
            if (cands.Count == 0) return null;
            if (cands.Count == 1) note = legacy + " 已对应到 SKU \"" + cands[0].Sku + "\"（" + LabelOf(cands[0]) + "）";
            else
            {
                var names = new List<string>();
                foreach (var x in cands) names.Add(x.Sku);
                note = legacy + " 对应多个 SKU（" + string.Join("、", names.ToArray()) + "），已选 \"" + cands[0].Sku + "\"，请在 SKU 下拉框确认";
            }
            return cands[0];
        }

        private static readonly System.Text.RegularExpressions.Regex LegacyKey =
            new System.Text.RegularExpressions.Regex(@"^(\d+)-pack$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>下拉框显示名；不同块显示名重复时附加块名以便区分。</summary>
        public string LabelOf(WeighingConfig c)
        {
            foreach (var x in Items)
                if (!ReferenceEquals(x, c) && x.Label == c.Label) return c.Label + " [" + c.Sku + "]";
            return c.Label;
        }

        public string Describe()
        {
            var l = new List<string>();
            foreach (var c in Items) l.Add(c.Sku + "=" + LabelOf(c));
            return string.Join("，", l.ToArray());
        }
    }

    public static class AtomicFile
    {
        public static void WriteAllText(string path, string text, Encoding enc)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, enc);
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null, true); return; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                File.Copy(tmp, path, true);
                File.Delete(tmp);
            }
            else File.Move(tmp, path);
        }
    }

    public static class Fmt
    {
        public static string F(double? v, int digits) { return v.HasValue ? v.Value.ToString("F" + digits, CultureInfo.InvariantCulture) : "--"; }
        public static string F2(double? v) { return F(v, 2); }
        public static string S2(double? v) { return v.HasValue ? (v.Value >= 0 ? "+" : "") + v.Value.ToString("F2", CultureInfo.InvariantCulture) : "--"; }
        public static string Inv(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }
    }
}
