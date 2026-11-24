using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using WeightCheckerCS.Core;

namespace WeightCheckerCS.Engine
{
    /// <summary>一条称重记录（采集线程创建后只读，可安全跨线程传递）。</summary>
    public sealed class WeightRecord
    {
        public long Id;
        public DateTime ReceivedAt;
        public string RawLine;
        public string[] Fields;
        public string MachineTime, SequenceNo;
        public double? Weight;
        public string MachineCode, MachineStatus;
        public BoxResult Algo;           // 状态机未启用时为 null
        public bool RejectFired;         // 是否已提交剔除脉冲

        public string AlgoStatus { get { return Algo == null ? "未启用" : Names.CodeText(Algo.Code); } }
        /// <summary>最终判定（启用状态机时以状态机为准，否则用称重机自身判定）。</summary>
        public string Verdict { get { return Algo != null ? AlgoStatus : MachineStatus; } }
        public bool Mismatch { get { return Algo != null && AlgoStatus != MachineStatus; } }
    }

    public static class Protocol
    {
        /// <summary>CSV 单行拆分（支持双引号），字段去除首尾空白。</summary>
        public static string[] SplitCsv(string line)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else quoted = false;
                    }
                    else sb.Append(c);
                }
                else if (c == '"' && sb.Length == 0) quoted = true;
                else if (c == ',') { list.Add(sb.ToString().Trim()); sb.Length = 0; }
                else sb.Append(c);
            }
            list.Add(sb.ToString().Trim());
            return list.ToArray();
        }

        private static string Get(string[] f, int i) { return i >= 0 && i < f.Length && f[i].Length > 0 ? f[i] : null; }

        public static WeightRecord Parse(string line, AppSettings s)
        {
            var f = SplitCsv(line);
            var r = new WeightRecord();
            r.ReceivedAt = DateTime.Now;
            r.RawLine = line;
            r.Fields = f;
            r.MachineTime = Get(f, s.TimestampFieldIndex);
            r.SequenceNo = Get(f, s.SequenceFieldIndex);
            string wt = Get(f, s.WeightFieldIndex);
            double w;
            if (wt != null && double.TryParse(wt, NumberStyles.Float, CultureInfo.InvariantCulture, out w)) r.Weight = w;
            int qi = s.QualificationFieldIndex;
            r.MachineCode = qi >= 0 && qi < f.Length ? f[qi] : "";
            r.MachineStatus = Names.CodeText(r.MachineCode);
            return r;
        }
    }

    /// <summary>TCP 字节流 → 按 \n 分行（去掉 \r），超过 1 MiB 无换行则清空。</summary>
    public sealed class LineDecoder
    {
        private byte[] _buf = new byte[8192];
        private int _len;
        private readonly Encoding _enc;
        public const int MaxPending = 1024 * 1024;
        public LineDecoder(Encoding enc) { _enc = enc; }
        public void Reset() { _len = 0; }

        public void Feed(byte[] data, int count, List<string> output)
        {
            if (_len + count > MaxPending)
            {
                _len = 0;
                throw new InvalidDataException("连续数据超过 1 MiB 且没有换行符，已清空异常缓冲区");
            }
            if (_len + count > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + count));
            Buffer.BlockCopy(data, 0, _buf, _len, count);
            _len += count;
            int start = 0;
            for (int i = 0; i < _len; i++)
            {
                if (_buf[i] != (byte)'\n') continue;
                int end = i;
                while (end > start && _buf[end - 1] == (byte)'\r') end--;
                string line = _enc.GetString(_buf, start, end - start);
                if (line.Trim().Length > 0) output.Add(line);
                start = i + 1;
            }
            if (start > 0)
            {
                Buffer.BlockCopy(_buf, start, _buf, 0, _len - start);
                _len -= start;
            }
        }
    }

    /// <summary>后台 CSV 写入线程：按日滚动 csv\weight_YYYY-MM-DD.csv（UTF-8 BOM），批量写入、每 0.5 s 刷盘。</summary>
    public sealed class CsvStorage : IDisposable
    {
        public static readonly string[] Header = {
            "received_at","machine_time","sequence_no","weight","qualification_field_value","weight_qualification_status",
            "algo_quality_code","algo_center_g","algo_lower_g","algo_upper_g","algo_width_g","algo_shift_g",
            "algo_state_before","algo_state_after","algo_trend","algo_history_count","algo_center_flag","reject_output","raw_line" };

        private readonly BlockingCollection<WeightRecord> _queue = new BlockingCollection<WeightRecord>(20000);
        private readonly Thread _thread;
        private readonly Action<string> _log;
        private volatile string _dir;
        private StreamWriter _writer;
        private string _currentFile;
        private long _dropped;

        public CsvStorage(string dataDir, Action<string> log)
        {
            _dir = dataDir;
            _log = log ?? delegate { };
            _thread = new Thread(Run) { IsBackground = true, Name = "csv-writer" };
            _thread.Start();
        }

        public string DataDir { get { return _dir; } set { _dir = value; } }
        public long Dropped { get { return Interlocked.Read(ref _dropped); } }

        public void Enqueue(WeightRecord r)
        {
            if (!_queue.IsAddingCompleted && !_queue.TryAdd(r)) Interlocked.Increment(ref _dropped);
        }

        private void Run()
        {
            var lastFlush = DateTime.UtcNow;
            while (true)
            {
                WeightRecord r;
                bool got;
                try { got = _queue.TryTake(out r, 500); }
                catch (InvalidOperationException) { break; }
                if (!got && _queue.IsCompleted) break;
                try
                {
                    if (got) Write(r);
                    if (_writer != null && (DateTime.UtcNow - lastFlush).TotalMilliseconds >= 500)
                    {
                        _writer.Flush();
                        lastFlush = DateTime.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    _log("CSV 写入失败：" + ex.Message);
                    CloseWriter();
                    Thread.Sleep(200);
                }
            }
            CloseWriter();
        }

        private void CloseWriter()
        {
            if (_writer == null) return;
            try { _writer.Flush(); _writer.Dispose(); } catch { }
            _writer = null; _currentFile = null;
        }

        private void Write(WeightRecord r)
        {
            string dir = Path.Combine(_dir, "csv");
            string file = Path.Combine(dir, "weight_" + r.ReceivedAt.ToString("yyyy-MM-dd") + ".csv");
            if (file != _currentFile)
            {
                CloseWriter();
                Directory.CreateDirectory(dir);
                bool exists = File.Exists(file) && new FileInfo(file).Length > 0;
                var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(fs, new UTF8Encoding(!exists), 65536);
                _currentFile = file;
                if (!exists) _writer.WriteLine(string.Join(",", Header));
            }
            _writer.WriteLine(ToCsvLine(Row(r)));
        }

        public static string[] Row(WeightRecord r)
        {
            var a = r.Algo;
            return new[] {
                r.ReceivedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"), r.MachineTime ?? "", r.SequenceNo ?? "",
                r.Weight.HasValue ? Fmt.Inv(r.Weight.Value) : "", r.MachineCode ?? "", r.MachineStatus,
                a == null ? "" : (a.Code ?? ""),
                a == null ? "" : Fmt.Inv(Math.Round(a.Window.Center, 4)),
                a == null ? "" : Fmt.Inv(Math.Round(a.Window.Lower, 4)),
                a == null ? "" : Fmt.Inv(Math.Round(a.Window.Upper, 4)),
                a == null ? "" : Fmt.Inv(Math.Round(a.Window.Width, 4)),
                a == null ? "" : Fmt.Inv(Math.Round(a.Window.Shift, 4)),
                a == null ? "" : ((int)a.StateBefore).ToString(), a == null ? "" : ((int)a.StateAfter).ToString(),
                a == null ? "" : Names.Signal(a.Signal), a == null ? "" : a.HistoryCount.ToString(),
                a == null ? "" : a.CenterFlag, r.RejectFired ? "1" : "0", r.RawLine };
        }

        public static string ToCsvLine(string[] values)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                string v = values[i] ?? "";
                if (v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0) sb.Append('"').Append(v.Replace("\"", "\"\"")).Append('"');
                else sb.Append(v);
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(5000);
        }
    }
}
