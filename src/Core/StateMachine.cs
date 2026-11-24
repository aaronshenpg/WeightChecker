using System;
using System.Collections.Generic;

namespace WeightCheckerCS.Core
{
    /// <summary>定长队列（等价 Python deque(maxlen=n)）。</summary>
    public sealed class Ring
    {
        private readonly double[] _buf;
        private int _start, _count;
        public Ring(int capacity) { _buf = new double[Math.Max(1, capacity)]; }
        public int Count { get { return _count; } }
        public int Capacity { get { return _buf.Length; } }
        public void Clear() { _start = 0; _count = 0; }
        public void Add(double v)
        {
            if (_count < _buf.Length) { _buf[(_start + _count) % _buf.Length] = v; _count++; }
            else { _buf[_start] = v; _start = (_start + 1) % _buf.Length; }
        }
        public double this[int i] { get { return _buf[(_start + i) % _buf.Length]; } }
        public double[] ToArray()
        {
            var a = new double[_count];
            for (int i = 0; i < _count; i++) a[i] = this[i];
            return a;
        }
    }

    /// <summary>严格因果的六状态称重判定器（逐行移植自 Python state_machine_plus）。每盒先判定、后更新。非线程安全。</summary>
    public sealed class StateMachineWeigher
    {
        private WeighingConfig _cfg;
        private Ring _history, _pending, _fast;
        private MachineState _state;
        private DateTime? _lastReceived;
        private int _consecutiveInvalid;

        public StateMachineWeigher(WeighingConfig cfg) { Initialize(cfg); }

        public WeighingConfig Config { get { return _cfg; } }
        public MachineState State { get { return _state; } }
        public int HistoryCount { get { return _history.Count; } }
        public double[] History { get { return _history.ToArray(); } }

        public double Center
        {
            get
            {
                double? c = RobustCenter(_history.ToArray(), _cfg.CenterTrimN);
                return c.HasValue ? c.Value : _cfg.InitialCenter;
            }
        }

        public PassWindow CurrentWindow() { return ComputePassWindow(Center, _state, _cfg); }

        public void Initialize(WeighingConfig cfg)
        {
            if (cfg != null) _cfg = cfg;
            if (_cfg == null) throw new ArgumentNullException("cfg");
            _history = new Ring(_cfg.CenterWindowN);
            _pending = new Ring(_cfg.OutlierRelockCount);
            _fast = new Ring(_cfg.FastWindowK);
            _state = MachineState.Stable;
            _lastReceived = null;
            _consecutiveInvalid = 0;
        }

        /// <summary>热加载参数但保留中心历史与状态（同一 SKU 参数微调时使用）。窗口长度变化时按新长度截取最近数据。</summary>
        public void ApplyConfigKeepHistory(WeighingConfig cfg)
        {
            var h = _history.ToArray(); var p = _pending.ToArray(); var f = _fast.ToArray();
            _cfg = cfg;
            _history = new Ring(cfg.CenterWindowN); foreach (var v in h) _history.Add(v);
            _pending = new Ring(cfg.OutlierRelockCount); foreach (var v in p) _pending.Add(v);
            _fast = new Ring(cfg.FastWindowK); foreach (var v in f) _fast.Add(v);
        }

        public static double TrendDegree(MachineState s)
        {
            int v = (int)s;
            if (v > 0) return v / 2.0;
            if (v < 0) return -v / 3.0;
            return 0.0;
        }

        private static readonly MachineState[,] Table = BuildTable();
        private static MachineState[,] BuildTable()
        {
            // 行：状态+3；列：X,U,D,N
            var t = new MachineState[6, 4];
            Set(t, MachineState.StrongUp, MachineState.StrongUp, MachineState.StrongUp, MachineState.WeakUp, MachineState.WeakUp);
            Set(t, MachineState.WeakUp, MachineState.WeakUp, MachineState.StrongUp, MachineState.Stable, MachineState.Stable);
            Set(t, MachineState.Stable, MachineState.Stable, MachineState.WeakUp, MachineState.WeakDown1, MachineState.Stable);
            Set(t, MachineState.WeakDown1, MachineState.WeakDown1, MachineState.Stable, MachineState.WeakDown2, MachineState.Stable);
            Set(t, MachineState.WeakDown2, MachineState.WeakDown2, MachineState.WeakDown1, MachineState.StrongDown, MachineState.WeakDown1);
            Set(t, MachineState.StrongDown, MachineState.StrongDown, MachineState.WeakDown2, MachineState.StrongDown, MachineState.WeakDown2);
            return t;
        }
        private static void Set(MachineState[,] t, MachineState s, MachineState x, MachineState u, MachineState d, MachineState n)
        {
            int r = (int)s + 3;
            t[r, 0] = x; t[r, 1] = u; t[r, 2] = d; t[r, 3] = n;
        }
        public static MachineState Transition(MachineState s, TrendInput sig)
        {
            int col = sig == TrendInput.Invalid ? 0 : sig == TrendInput.Up ? 1 : sig == TrendInput.Down ? 2 : 3;
            return Table[(int)s + 3, col];
        }

        public static double Median(double[] values)
        {
            var d = (double[])values.Clone();
            Array.Sort(d);
            int n = d.Length;
            if (n == 0) throw new InvalidOperationException("empty");
            return (n % 2 == 1) ? d[n / 2] : (d[n / 2 - 1] + d[n / 2]) / 2.0;
        }

        public static double? RobustCenter(double[] values, int trim)
        {
            int n = values.Length;
            if (n == 0) return null;
            var d = (double[])values.Clone();
            Array.Sort(d);
            if (trim > 0 && n - 2 * trim >= 3) return PySum(d, trim, n - trim) / (n - 2 * trim);
            if (trim > 0) return Median(d);
            return PySum(d, 0, n) / n;
        }

        /// <summary>与 Python 3.12+ 内置 sum() 一致的 Neumaier 补偿求和，保证与 Python 版逐位一致。</summary>
        public static double PySum(double[] d, int from, int to)
        {
            double s = 0, c = 0;
            for (int i = from; i < to; i++)
            {
                double x = d[i], t = s + x;
                if (Math.Abs(s) >= Math.Abs(x)) c += (s - t) + x; else c += (x - t) + s;
                s = t;
            }
            return s + c;
        }

        public static PassWindow ComputePassWindow(double center, MachineState state, WeighingConfig c)
        {
            double degree = TrendDegree(state);
            double extra = Math.Max(0.0, c.MaxWidth - 2 * c.HalfWidth);
            double lower = center - c.HalfWidth + c.WindowOffset;
            double upper = center + c.HalfWidth + c.WindowOffset;
            if ((int)state > 0)
            {
                lower += degree * c.UpMaxShift;
                upper += degree * (c.UpMaxShift + extra);
            }
            else if ((int)state < 0)
            {
                lower -= degree * (c.DownMaxShift + extra);
                upper -= degree * c.DownMaxShift;
            }
            lower = Math.Max(lower, c.SafetyLower);
            upper = Math.Max(upper, lower + c.MinWidth);
            if (upper - lower > c.MaxWidth) upper = lower + c.MaxWidth;
            var w = new PassWindow();
            w.Center = center; w.Lower = lower; w.Upper = upper; w.Degree = degree;
            w.Shift = (lower + upper) / 2 - center;
            return w;
        }

        private bool IsValid(double? v)
        {
            return v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value)
                && _cfg.PhysicalMin <= v.Value && v.Value <= _cfg.PhysicalMax;
        }

        public BoxResult ProcessBox(DateTime receivedAt, double? weightG)
        {
            var c = _cfg;
            bool reset = false;
            if (_lastReceived.HasValue && (receivedAt - _lastReceived.Value).TotalSeconds > c.StopResetSeconds)
            {
                Initialize(null);
                reset = true;
            }
            var stateBefore = _state;
            var window = CurrentWindow();
            int used = _history.Count;
            _lastReceived = receivedAt;

            var r = new BoxResult();
            r.Weight = weightG; r.StateBefore = stateBefore; r.Window = window; r.HistoryCount = used; r.Reset = reset;

            if (!IsValid(weightG))
            {
                _consecutiveInvalid++;
                r.Classification = Classification.None;
                r.Signal = TrendInput.Invalid;
                r.StateAfter = _state;
                r.Valid = false;
                r.Alarm = _consecutiveInvalid >= c.InvalidAlarmCount;
                return r;
            }

            _consecutiveInvalid = 0;
            double w = weightG.Value;
            r.Valid = true;
            r.Classification = w < window.Lower ? Classification.Light : (w > window.Upper ? Classification.Heavy : Classification.Qualified);

            bool outlier = _history.Count >= c.OutlierRelockCount && Math.Abs(w - window.Center) > c.OutlierReject;
            bool relocked = false;
            if (outlier)
            {
                _pending.Add(w);
                var p = _pending.ToArray();
                bool allAbove = true, allBelow = true;
                double mn = double.MaxValue, mx = double.MinValue;
                foreach (var v in p)
                {
                    if (!(v > window.Center)) allAbove = false;
                    if (!(v < window.Center)) allBelow = false;
                    if (v < mn) mn = v;
                    if (v > mx) mx = v;
                }
                if (p.Length >= c.OutlierRelockCount && (allAbove || allBelow)
                    && mx - mn <= 2 * c.HalfWidth
                    && (c.RelockMaxOffset <= 0 || Math.Abs(Median(p) - c.InitialCenter) <= c.RelockMaxOffset))
                {
                    _history.Clear();
                    foreach (var v in p) _history.Add(v);
                    _pending.Clear();
                    relocked = true;
                }
            }
            else
            {
                _history.Add(w);
                _pending.Clear();
            }
            _fast.Add(w);
            double? fast = _fast.Count >= c.FastWindowK ? (double?)Median(_fast.ToArray()) : null;
            double? error = fast.HasValue ? (double?)(fast.Value - window.Center) : null;
            TrendInput sig;
            if (error.HasValue && error.Value >= c.TrendUp) sig = TrendInput.Up;
            else if (error.HasValue && error.Value <= -c.TrendDown) sig = TrendInput.Down;
            else sig = TrendInput.Neutral;
            _state = Transition(stateBefore, sig);

            r.Signal = sig; r.StateAfter = _state; r.Fast = fast; r.Error = error;
            r.Outlier = outlier && !relocked; r.Relocked = relocked;
            return r;
        }
    }
}
