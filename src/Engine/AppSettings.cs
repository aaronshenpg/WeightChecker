using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WeightCheckerCS.Core;

namespace WeightCheckerCS.Engine
{
    /// <summary>程序设置（config\settings.json）。不可变快照：修改时 Clone → 改 → Validate → 原子替换。</summary>
    public sealed class AppSettings
    {
        public string Host = "192.168.20.122";
        public int Port = 50001;
        public double ConnectTimeoutSeconds = 10;
        public double ReceiveTimeoutSeconds = 1;
        public double ReconnectInitialSeconds = 1;
        public double ReconnectMaxSeconds = 30;
        public int BufferSize = 4096;
        public string Encoding = "ascii";
        public int WeightFieldIndex = 6;
        public int SequenceFieldIndex = 2;
        public int TimestampFieldIndex = 3;
        public int QualificationFieldIndex = 5;
        public string DataDir = "weight_checker_cs_data";
        public bool CsvEnabled = true;
        public bool StateMachineEnabled = true;
        public bool RejectOutputEnabled = true;
        /// <summary>当前 SKU（configs.json 块名）。为空时按 product_pad_num 兼容旧版设置。</summary>
        public string Sku = "";
        public int ProductPadNum = 16;
        public bool AutoStart = false;
        public bool MinimizeToTray = true;
        /// <summary>mezio_c.dll 路径（相对路径按 exe 目录解析）。程序启动时加载一次；修改后热切换。</summary>
        public string DioDllPath = @"iolib\mezio_c.dll";
        public string SkuConfigsPath = @"config\configs.json";
        public int UiRefreshMs = 100;
        public int RealtimeRows = 200;
        public int AbnormalRows = 2000;

        public AppSettings Clone() { return (AppSettings)MemberwiseClone(); }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Host)) throw new FormatException("host 不能为空");
            if (Port < 1 || Port > 65535) throw new FormatException("port 必须在 1~65535");
            if (ConnectTimeoutSeconds <= 0 || ReceiveTimeoutSeconds <= 0) throw new FormatException("超时必须大于 0");
            if (ReconnectInitialSeconds <= 0 || ReconnectMaxSeconds < ReconnectInitialSeconds) throw new FormatException("重连间隔设置无效");
            if (BufferSize < 64 || BufferSize > 1 << 20) throw new FormatException("buffer_size 必须在 64~1048576");
            try { System.Text.Encoding.GetEncoding(Encoding); }
            catch (ArgumentException) { throw new FormatException("未知编码：" + Encoding); }
            foreach (int i in new[] { WeightFieldIndex, SequenceFieldIndex, TimestampFieldIndex, QualificationFieldIndex })
                if (i < 0 || i > 1000) throw new FormatException("字段序号必须 ≥ 0");
            if (string.IsNullOrWhiteSpace(DataDir)) throw new FormatException("data_dir 不能为空");
            if (ProductPadNum <= 0) throw new FormatException("product_pad_num 必须为正整数");
            if (string.IsNullOrWhiteSpace(DioDllPath)) throw new FormatException("dio_dll_path 不能为空");
            if (string.IsNullOrWhiteSpace(SkuConfigsPath)) throw new FormatException("sku_configs_path 不能为空");
            if (UiRefreshMs < 30 || UiRefreshMs > 2000) throw new FormatException("ui_refresh_ms 必须在 30~2000");
            if (RealtimeRows < 10 || RealtimeRows > 5000) throw new FormatException("realtime_rows 必须在 10~5000");
            if (AbnormalRows < 10 || AbnormalRows > 50000) throw new FormatException("abnormal_rows 必须在 10~50000");
        }

        public static string Resolve(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            path = Environment.ExpandEnvironmentVariables(path);
            return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(AppPaths.BaseDir, path));
        }

        public string DataDirFull { get { return Resolve(DataDir); } }
        public string DioDllFull { get { return Resolve(DioDllPath); } }
        public string SkuConfigsFull { get { return Resolve(SkuConfigsPath); } }

        public string SkuDescription { get { return string.IsNullOrEmpty(Sku) ? ProductPadNum + "-pack（product_pad_num）" : Sku; } }

        public bool SameConnection(AppSettings o)
        {
            return o != null && Host == o.Host && Port == o.Port && ConnectTimeoutSeconds == o.ConnectTimeoutSeconds
                && ReceiveTimeoutSeconds == o.ReceiveTimeoutSeconds && BufferSize == o.BufferSize && Encoding == o.Encoding;
        }

        public Json.Obj ToJson()
        {
            var o = new Json.Obj();
            o["host"] = Host; o["port"] = Port;
            o["connect_timeout_seconds"] = ConnectTimeoutSeconds; o["receive_timeout_seconds"] = ReceiveTimeoutSeconds;
            o["reconnect_initial_seconds"] = ReconnectInitialSeconds; o["reconnect_max_seconds"] = ReconnectMaxSeconds;
            o["buffer_size"] = BufferSize; o["encoding"] = Encoding;
            o["weight_field_index"] = WeightFieldIndex; o["sequence_field_index"] = SequenceFieldIndex;
            o["timestamp_field_index"] = TimestampFieldIndex; o["qualification_field_index"] = QualificationFieldIndex;
            o["data_dir"] = DataDir; o["csv_enabled"] = CsvEnabled;
            o["state_machine_enabled"] = StateMachineEnabled; o["reject_output_enabled"] = RejectOutputEnabled;
            o["sku"] = Sku ?? ""; o["product_pad_num"] = ProductPadNum; o["auto_start"] = AutoStart; o["minimize_to_tray"] = MinimizeToTray;
            o["dio_dll_path"] = DioDllPath; o["sku_configs_path"] = SkuConfigsPath;
            o["ui_refresh_ms"] = UiRefreshMs; o["realtime_rows"] = RealtimeRows; o["abnormal_rows"] = AbnormalRows;
            return o;
        }

        public static AppSettings FromJson(Json.Obj o)
        {
            var s = new AppSettings();
            if (o == null) throw new FormatException("settings.json 根节点必须是对象");
            foreach (var k in o.Keys)
            {
                object v = o[k];
                try
                {
                    switch (k)
                    {
                        case "host": s.Host = (string)v; break;
                        case "port": s.Port = I(v); break;
                        case "connect_timeout_seconds": s.ConnectTimeoutSeconds = (double)v; break;
                        case "receive_timeout_seconds": s.ReceiveTimeoutSeconds = (double)v; break;
                        case "reconnect_initial_seconds": s.ReconnectInitialSeconds = (double)v; break;
                        case "reconnect_max_seconds": s.ReconnectMaxSeconds = (double)v; break;
                        case "buffer_size": s.BufferSize = I(v); break;
                        case "encoding": s.Encoding = (string)v; break;
                        case "weight_field_index": s.WeightFieldIndex = I(v); break;
                        case "sequence_field_index": s.SequenceFieldIndex = I(v); break;
                        case "timestamp_field_index": s.TimestampFieldIndex = I(v); break;
                        case "qualification_field_index": s.QualificationFieldIndex = I(v); break;
                        case "data_dir": s.DataDir = (string)v; break;
                        case "csv_enabled": s.CsvEnabled = (bool)v; break;
                        case "state_machine_enabled": s.StateMachineEnabled = (bool)v; break;
                        case "reject_output_enabled": s.RejectOutputEnabled = (bool)v; break;
                        case "sku": s.Sku = (string)v ?? ""; break;
                        case "product_pad_num": s.ProductPadNum = I(v); break;
                        case "auto_start": s.AutoStart = (bool)v; break;
                        case "minimize_to_tray": s.MinimizeToTray = (bool)v; break;
                        case "dio_dll_path": s.DioDllPath = (string)v; break;
                        case "sku_configs_path": s.SkuConfigsPath = (string)v; break;
                        case "ui_refresh_ms": s.UiRefreshMs = I(v); break;
                        case "realtime_rows": s.RealtimeRows = I(v); break;
                        case "abnormal_rows": s.AbnormalRows = I(v); break;
                        default: throw new FormatException("settings.json 包含未知字段：" + k);
                    }
                }
                catch (InvalidCastException) { throw new FormatException("settings.json 字段类型错误：" + k); }
                catch (NullReferenceException) { throw new FormatException("settings.json 字段不能为 null：" + k); }
            }
            s.Validate();
            return s;
        }

        private static int I(object v)
        {
            double d = (double)v;
            if (d != Math.Floor(d)) throw new FormatException("应为整数");
            return (int)d;
        }

        public static AppSettings Load(string path)
        {
            if (!File.Exists(path))
            {
                var d = new AppSettings();
                Save(path, d);
                return d;
            }
            return FromJson(Json.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8)) as Json.Obj);
        }

        public static void Save(string path, AppSettings s)
        {
            s.Validate();
            AtomicFile.WriteAllText(path, Json.Write(s.ToJson()), new UTF8Encoding(false));
        }
    }

    public static class AppPaths
    {
        public static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        private static string _settings = Path.Combine(BaseDir, @"config\settings.json");
        public static string SettingsFile { get { return _settings; } set { _settings = Path.GetFullPath(value); } }
    }
}
