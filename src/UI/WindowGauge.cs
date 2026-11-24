using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WeightCheckerCS.UI
{
    /// <summary>
    /// 合格区间仪表：双缓冲 GDI+ 自绘，只在数据变化时由 UI 定时器调用 SetSnapshot → Invalidate。
    /// 画笔/画刷全部预创建复用，OnPaint 内不分配大对象，不抛异常。
    /// </summary>
    public sealed class WindowGauge : Control
    {
        private double _center = double.NaN, _lower, _upper, _baseLower, _baseUpper, _weight = double.NaN;
        private string _verdict = "";
        private readonly double[] _recent = new double[12];
        private int _recentCount;

        private static readonly Pen AxisPen = new Pen(Color.FromArgb(150, 150, 150), 1);
        private static readonly Pen BasePen = new Pen(Color.FromArgb(120, 120, 120), 1.5f) { DashStyle = DashStyle.Dash };
        private static readonly Pen CenterPen = new Pen(Color.FromArgb(21, 101, 192), 2);
        private static readonly Brush WindowBrush = new SolidBrush(Color.FromArgb(70, 46, 160, 67));
        private static readonly Pen WindowPen = new Pen(Color.FromArgb(46, 125, 50), 2);
        private static readonly Brush RecentBrush = new SolidBrush(Color.FromArgb(150, 120, 120, 120));
        private static readonly Brush TextBrush = new SolidBrush(Color.FromArgb(50, 50, 50));
        private static readonly Brush OkBrush = new SolidBrush(Color.FromArgb(22, 128, 60));
        private static readonly Brush LightBrush = new SolidBrush(Color.FromArgb(198, 40, 40));
        private static readonly Brush HeavyBrush = new SolidBrush(Color.FromArgb(178, 58, 0));
        private static readonly Brush UnknownBrush = new SolidBrush(Color.FromArgb(21, 101, 192));
        private readonly Font _small;

        public WindowGauge()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
            _small = new Font("Microsoft YaHei UI", 8.5f);
            MinimumSize = new Size(200, 90);
        }

        public void Clear()
        {
            _center = double.NaN; _weight = double.NaN; _recentCount = 0;
            Invalidate();
        }

        public void SetSnapshot(double center, double lower, double upper, double baseLower, double baseUpper, double weight, string verdict, double[] recent, int recentCount)
        {
            _center = center; _lower = lower; _upper = upper; _baseLower = baseLower; _baseUpper = baseUpper;
            _weight = weight; _verdict = verdict ?? "";
            _recentCount = Math.Min(recentCount, _recent.Length);
            Array.Copy(recent, Math.Max(0, recentCount - _recentCount), _recent, 0, _recentCount);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try { Draw(e.Graphics); }
            catch { /* 绘制失败不能影响程序 */ }
        }

        private void Draw(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w < 40 || h < 40) return;
            if (double.IsNaN(_center))
            {
                g.DrawString("等待数据…", Font, TextBrush, 8, 8);
                return;
            }
            double span = Math.Max(12.0, Math.Max(_upper - _center, _center - _lower) * 2 + 6);
            double lo = _center - span / 2, hi = _center + span / 2;
            if (!double.IsNaN(_weight) && Math.Abs(_weight - _center) < 40) { lo = Math.Min(lo, _weight - 1); hi = Math.Max(hi, _weight + 1); }
            else if (!double.IsNaN(_weight)) _weight = Math.Max(lo, Math.Min(hi, _weight));
            float left = 12, right = w - 12, axisY = h * 0.62f;
            Func<double, float> X = v => (float)(left + (v - lo) / (hi - lo) * (right - left));

            g.DrawLine(AxisPen, left, axisY, right, axisY);
            for (double t = Math.Ceiling(lo); t <= hi; t += 1)
            {
                float x = X(t);
                g.DrawLine(AxisPen, x, axisY - 3, x, axisY + 3);
                if (((int)Math.Round(t)) % 2 == 0) g.DrawString(t.ToString("0"), _small, TextBrush, x - 9, axisY + 5);
            }
            float bandTop = axisY - 30, bandH = 26;
            g.DrawRectangle(BasePen, X(_baseLower), bandTop - 4, X(_baseUpper) - X(_baseLower), bandH + 8);
            var rect = new RectangleF(X(_lower), bandTop, X(_upper) - X(_lower), bandH);
            g.FillRectangle(WindowBrush, rect);
            g.DrawRectangle(WindowPen, rect.X, rect.Y, rect.Width, rect.Height);
            g.DrawString(_lower.ToString("F2"), _small, TextBrush, rect.X - 18, 2);
            g.DrawString(_upper.ToString("F2"), _small, TextBrush, rect.Right - 18, 2);
            g.DrawLine(CenterPen, X(_center), bandTop - 8, X(_center), axisY);

            for (int i = 0; i < _recentCount; i++)
            {
                if (_recent[i] < lo || _recent[i] > hi) continue;
                float x = X(_recent[i]);
                g.FillEllipse(RecentBrush, x - 3, axisY - 3, 6, 6);
            }
            if (!double.IsNaN(_weight))
            {
                float x = X(_weight);
                Brush b = _verdict == "合格" ? OkBrush : _verdict == "过轻" ? LightBrush : _verdict == "超重" ? HeavyBrush : UnknownBrush;
                var tri = new[] { new PointF(x, axisY - 2), new PointF(x - 8, axisY + 14), new PointF(x + 8, axisY + 14) };
                g.FillPolygon(b, tri);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _small.Dispose();
            base.Dispose(disposing);
        }
    }
}
