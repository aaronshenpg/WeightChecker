using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using WeightCheckerCS.Core;
using WeightCheckerCS.Engine;
using WeightCheckerCS.UI;

namespace WeightCheckerCS
{
    public static class Program
    {
        private static Mutex _single;

        [STAThread]
        public static int Main(string[] args)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { Log.Info("未处理异常：" + e.ExceptionObject); Log.Flush(); } catch { }
            };
            var opts = ParseArgs(args);
            try
            {
                if (opts.ContainsKey("settings")) AppPaths.SettingsFile = opts["settings"];
                if (opts.ContainsKey("selftest")) return SelfTest.Run();
                if (opts.ContainsKey("replay")) return Replay(opts);
                if (opts.ContainsKey("headless")) return Headless(opts);
                return Gui();
            }
            catch (Exception ex)
            {
                try { Log.Info("启动失败：" + ex); Log.Flush(); } catch { }
                if (opts.ContainsKey("headless") || opts.ContainsKey("replay") || opts.ContainsKey("selftest")) Console.Error.WriteLine("错误：" + ex.Message);
                else MessageBox.Show(ex.Message, "WeightCheckerCS 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--")) continue;
                string k = args[i].Substring(2);
                string v = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "1";
                d[k] = v;
            }
            return d;
        }

        private static int Gui()
        {
            bool created;
            _single = new Mutex(true, "Global\\WeightCheckerCS_" + AppPaths.SettingsFile.ToLowerInvariant().GetHashCode().ToString("x"), out created);
            if (!created)
            {
                MessageBox.Show("程序已在运行（同一配置只能运行一个实例）。", "WeightCheckerCS", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 2;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Log.Info("界面异常（已拦截，程序继续运行）：" + e.Exception);
            var settings = AppSettings.Load(AppPaths.SettingsFile);
            using (var engine = new WeighEngine(settings))
            {
                engine.EnableHotReload();
                Log.Info("程序已启动。配置：" + AppPaths.SettingsFile);
                Application.Run(new MainForm(engine));
            }
            GC.KeepAlive(_single);
            return 0;
        }

        private static int Headless(Dictionary<string, string> opts)
        {
            Log.Console = opts.ContainsKey("verbose");
            var settings = AppSettings.Load(AppPaths.SettingsFile);
            int seconds = opts.ContainsKey("seconds") ? int.Parse(opts["seconds"]) : 0;
            long expect = opts.ContainsKey("expect") ? long.Parse(opts["expect"]) : 0;
            var sim = DioSimulation.Off;
            if (opts.ContainsKey("dio-sim")) sim = opts["dio-sim"].Equals("real", StringComparison.OrdinalIgnoreCase) ? DioSimulation.RealTiming : DioSimulation.Instant;
            int exit = 0;
            var summary = new Json.Obj();
            using (var engine = new WeighEngine(settings, sim))
            {
                engine.HeadlessPrint = !opts.ContainsKey("quiet");
                engine.UseMachineTime = opts.ContainsKey("use-machine-time");
                engine.EnableHotReload();
                var done = new ManualResetEvent(false);
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; done.Set(); };
                Console.WriteLine("无界面模式：" + settings.Host + ":" + settings.Port + "  SKU " + engine.Skus.LabelOf(engine.CurrentSku) + "（" + engine.CurrentSku.Sku + "）  DIO " + engine.Dio.Status + "（Ctrl+C 退出）");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                engine.Start();
                if (expect > 0)
                {
                    // 收到 expect 行（含解析失败行）后自动退出；--seconds 为超时
                    while (engine.Records + engine.Errors < expect && !done.WaitOne(50))
                        if (seconds > 0 && sw.Elapsed.TotalSeconds >= seconds) { exit = 3; Console.Error.WriteLine("超时：只收到 " + engine.Records + " / " + expect + " 行"); break; }
                }
                else if (seconds > 0) done.WaitOne(seconds * 1000); else done.WaitOne();
                engine.Stop();
                bool dioIdle = engine.Dio.WaitIdle(60000);
                Console.WriteLine("记录 " + engine.Records + "，错误 " + engine.Errors + "，剔除脉冲入队 " + engine.Rejects + "，DIO 实际脉冲 " + engine.Dio.PulseCount);
                summary["sku"] = engine.CurrentSku.Sku;
                summary["records"] = engine.Records; summary["errors"] = engine.Errors;
                summary["light"] = engine.Light; summary["heavy"] = engine.Heavy; summary["unknown"] = engine.Unknown;
                summary["rejects_enqueued"] = engine.Rejects;
                summary["dio_status"] = engine.Dio.Status; summary["dio_simulated"] = engine.Dio.Simulated;
                summary["dio_pulses"] = engine.Dio.PulseCount; summary["dio_failed"] = engine.Dio.FailCount;
                summary["dio_dropped"] = engine.Dio.DropCount; summary["dio_idle"] = dioIdle;
                summary["dio_mask_or"] = engine.Dio.SimulatedMaskOr;
                var pins = new List<object>();
                foreach (int p in engine.CurrentSku.RejectPins) pins.Add((double)p);
                summary["reject_pins"] = pins;
                summary["csv_dropped"] = engine.CsvDropped;
                summary["elapsed_seconds"] = sw.Elapsed.TotalSeconds;
                summary["timed_out"] = exit == 3;
            }
            // engine 已 Dispose：CSV 写入线程已刷盘退出
            if (opts.ContainsKey("summary")) File.WriteAllText(opts["summary"], Json.Write(summary), new UTF8Encoding(false));
            return exit;
        }

        /// <summary>离线回放：--replay in.csv(received_at,weight,...) (--sku 块名 | --pad 36) [--configs path] --out out.csv。用于与 Python 版逐盒对比。</summary>
        private static int Replay(Dictionary<string, string> opts)
        {
            string cfgPath = opts.ContainsKey("configs") ? opts["configs"] : AppSettings.Resolve(@"config\configs.json");
            var cat = SkuConfigStore.LoadAll(cfgPath);
            foreach (var m in cat.Skipped) Console.Error.WriteLine("已跳过：" + m);
            foreach (var m in cat.Warnings) Console.Error.WriteLine("警告：" + m);
            string sku = opts.ContainsKey("sku") ? opts["sku"] : null;
            int pad = opts.ContainsKey("pad") ? int.Parse(opts["pad"]) : 0;
            if (sku == null && pad <= 0) throw new FormatException("请用 --sku 块名 或 --pad pack数 指定 SKU；可用：" + cat.Describe());
            string note;
            var cfg = cat.Find(sku, pad, out note);
            if (cfg == null) throw new FormatException("找不到 SKU " + (sku ?? pad + "-pack") + "；可用：" + cat.Describe());
            if (note != null) Console.Error.WriteLine(note);
            Console.Error.WriteLine("SKU：" + cat.LabelOf(cfg) + "（" + cfg.Sku + "）");
            var sm = new StateMachineWeigher(cfg);
            using (var reader = new StreamReader(opts["replay"], Encoding.UTF8))
            using (var w = new StreamWriter(opts["out"], false, new UTF8Encoding(false)))
            {
                var head = Protocol.SplitCsv(reader.ReadLine());
                int ti = Array.IndexOf(head, "received_at"), wi = Array.IndexOf(head, "weight");
                w.WriteLine("idx,code,center,lower,upper,state_before,state_after,flag");
                string line; int idx = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    var f = Protocol.SplitCsv(line);
                    var t = DateTimeOffset.Parse(f[ti], CultureInfo.InvariantCulture).UtcDateTime;
                    double v; double? wt = null;
                    if (wi < f.Length && double.TryParse(f[wi], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) wt = v;
                    var r = sm.ProcessBox(t, wt);
                    w.WriteLine(idx + "," + (r.Code ?? "") + "," + r.Window.Center.ToString("F6") + "," + r.Window.Lower.ToString("F6") + "," + r.Window.Upper.ToString("F6")
                        + "," + (int)r.StateBefore + "," + (int)r.StateAfter + "," + r.CenterFlag);
                    idx++;
                }
                Console.WriteLine("回放完成：" + idx + " 行 → " + opts["out"]);
            }
            return 0;
        }
    }
}
