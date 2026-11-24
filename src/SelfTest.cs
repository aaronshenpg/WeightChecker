using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WeightCheckerCS.Core;
using WeightCheckerCS.Engine;

namespace WeightCheckerCS
{
    /// <summary>内置自测（--selftest）：镜像 Python 版 tests/test_state_machine.py 的核心用例 + 解析/配置用例。</summary>
    public static class SelfTest
    {
        private static int _pass, _fail;

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) _pass++; else { _fail++; Console.WriteLine("FAIL " + name + (detail != null ? " : " + detail : "")); }
        }
        private static void Near(string name, double a, double b) { Check(name, Math.Abs(a - b) < 1e-9, a + " != " + b); }

        private static WeighingConfig Cfg()
        {
            return new WeighingConfig { Sku = "16-pack-270mm", PadSize = "270mm", ProductPadNum = 16, InitialCenter = 113.8, PhysicalMin = 60, PhysicalMax = 180, SafetyLower = 60 };
        }

        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 8, 0, 0);

        public static int Run()
        {
            // 去尾均值 / 中位数
            Near("trimmed", StateMachineWeigher.RobustCenter(new[] { 100.0, 101, 102, 103, 200 }, 1).Value, 102);
            Near("median-small", StateMachineWeigher.RobustCenter(new[] { 100.0, 104 }, 1).Value, 102);
            Check("empty", StateMachineWeigher.RobustCenter(new double[0], 1) == null, null);

            var c = Cfg();
            var st = StateMachineWeigher.ComputePassWindow(113.8, MachineState.Stable, c);
            Near("stable-lower", st.Lower, 110.1); Near("stable-upper", st.Upper, 117.5);
            var up1 = StateMachineWeigher.ComputePassWindow(113.8, MachineState.WeakUp, c);
            var up2 = StateMachineWeigher.ComputePassWindow(113.8, MachineState.StrongUp, c);
            Check("up-skew", up1.Shift > 0 && up2.Shift > up1.Shift && up2.Width <= c.MaxWidth + 1e-9, null);
            var dn = StateMachineWeigher.ComputePassWindow(113.8, MachineState.StrongDown, c);
            Check("down-skew", dn.Shift < 0 && Math.Abs(dn.Width - c.MaxWidth) < 1e-9, dn.Width.ToString());

            // 宽度始终在 [min,max]
            var rnd = new Random(7);
            bool allOk = true;
            for (int i = 0; i < 3000; i++)
            {
                var x = Cfg();
                x.MinWidth = 7.4; x.MaxWidth = 7.4 + rnd.NextDouble() * 1.6; x.HalfWidth = 3.7 + rnd.NextDouble() * (x.MaxWidth / 2 - 3.7);
                x.UpMaxShift = rnd.NextDouble() * 3; x.DownMaxShift = rnd.NextDouble() * 3; x.SafetyLower = 100 + rnd.NextDouble() * 15;
                foreach (MachineState s in Enum.GetValues(typeof(MachineState)))
                {
                    var w = StateMachineWeigher.ComputePassWindow(105 + rnd.NextDouble() * 15, s, x);
                    if (w.Width < x.MinWidth - 1e-9 || w.Width > x.MaxWidth + 1e-9 || w.Lower < x.SafetyLower - 1e-9) allOk = false;
                }
            }
            Check("width-bounds-random", allOk, null);

            // 出厂配置：宽度 7.4~9.0，缺片/多片在任一状态都被拒绝，中心窗口 ≤ 7
            var skus = SkuConfigStore.LoadAll(AppSettings.Resolve(@"config\configs.json"));
            foreach (var k in skus.Items)
                foreach (MachineState s in Enum.GetValues(typeof(MachineState)))
                {
                    var w = StateMachineWeigher.ComputePassWindow(k.InitialCenter, s, k);
                    Check(k.Sku + "-" + s + "-width", w.Width >= 7.4 - 1e-9 && w.Width <= 9.0 + 1e-9, w.Width.ToString());
                    Check(k.Sku + "-" + s + "-pad", k.InitialCenter - k.PadWeight < w.Lower && k.InitialCenter + k.PadWeight > w.Upper, null);
                    Check(k.Sku + "-n", k.CenterWindowN <= 7, null);
                }

            // 先判定后更新；边界含等号
            var sm = new StateMachineWeigher(Cfg());
            var r = sm.ProcessBox(T0, 110.1);
            Check("boundary-inclusive", r.Classification == Classification.Qualified && r.HistoryCount == 0, null);
            r = sm.ProcessBox(T0.AddSeconds(1), 105.0);
            Check("light-enters-history", r.Classification == Classification.Light && sm.HistoryCount == 2, null);

            // 缺片：判过轻且不进入中心
            sm = new StateMachineWeigher(Cfg());
            for (int i = 0; i < 7; i++) sm.ProcessBox(T0.AddSeconds(i), 113.8);
            double before = sm.Center;
            r = sm.ProcessBox(T0.AddSeconds(8), 113.8 - 7.2);
            Check("missing-pad", r.Classification == Classification.Light && r.Outlier && Math.Abs(sm.Center - before) < 1e-9, null);

            // 连续一致阶跃 → 重锁
            var rc = Cfg(); rc.OutlierRelockCount = 3;
            sm = new StateMachineWeigher(rc);
            for (int i = 0; i < 7; i++) sm.ProcessBox(T0.AddSeconds(i), 113.8);
            BoxResult last = null;
            for (int i = 0; i < 3; i++) last = sm.ProcessBox(T0.AddSeconds(10 + i), 120.0);
            Check("relock", last.Relocked && Math.Abs(sm.Center - 120.0) < 1e-9, sm.Center.ToString());
            rc.RelockMaxOffset = 3.0;
            sm = new StateMachineWeigher(rc);
            for (int i = 0; i < 7; i++) sm.ProcessBox(T0.AddSeconds(i), 113.8);
            for (int i = 0; i < 3; i++) last = sm.ProcessBox(T0.AddSeconds(10 + i), 120.0);
            Check("relock-guard", !last.Relocked && Math.Abs(sm.Center - 113.8) < 1e-9, null);

            // 下行需要三次 D 才到 STRONG_DOWN
            sm = new StateMachineWeigher(Cfg());
            for (int i = 0; i < 7; i++) sm.ProcessBox(T0.AddSeconds(i), 113.8);
            var states = new List<MachineState>();
            for (int i = 0; i < 6; i++) states.Add(sm.ProcessBox(T0.AddSeconds(10 + i), 111.0).StateAfter);
            Check("down-steps", states.Contains(MachineState.WeakDown1) && states.Contains(MachineState.WeakDown2), string.Join(",", states));

            // 无效重量：不进历史，连续 3 次报警，状态不变
            sm = new StateMachineWeigher(Cfg());
            sm.ProcessBox(T0, 113.8);
            r = sm.ProcessBox(T0.AddSeconds(1), null); sm.ProcessBox(T0.AddSeconds(2), double.NaN);
            r = sm.ProcessBox(T0.AddSeconds(3), 999);
            Check("invalid", r.Alarm && !r.Valid && sm.HistoryCount == 1 && r.Code == null, null);

            // 长停机重置
            r = sm.ProcessBox(T0.AddHours(2), 113.8);
            Check("stop-reset", r.Reset && r.HistoryCount == 0, null);

            // 转移表
            Check("table", StateMachineWeigher.Transition(MachineState.Stable, TrendInput.Down) == MachineState.WeakDown1
                && StateMachineWeigher.Transition(MachineState.StrongDown, TrendInput.Neutral) == MachineState.WeakDown2
                && StateMachineWeigher.Transition(MachineState.WeakUp, TrendInput.Up) == MachineState.StrongUp, null);

            // 配置校验
            var bad = Cfg(); bad.HalfWidth = 5;
            Check("reject-wide", Throws(bad.Validate), null);
            bad = Cfg(); bad.RejectActiveMs = 0;
            Check("reject-pulse", Throws(bad.Validate), null);
            bad = Cfg(); bad.RejectPins = new[] { 2, 2 };
            Check("reject-chs", Throws(bad.Validate), null);

            // JSON 往返
            var j = Cfg().ToJson();
            var back = WeighingConfig.FromJson("16-pack-270mm", Json.Parse(Json.Write(j)) as Json.Obj);
            Check("json-roundtrip", Json.Write(back.ToJson()) == Json.Write(j), null);
            Check("json-reject-output", ((Json.Obj)j["reject_output"]).Has("active_pins") && !j.Has("do_out"), null);

            // 旧版 do_out 兼容：chs→引脚，t(秒)→毫秒，dll 忽略
            var legacy = Json.Parse(Json.Write(j)) as Json.Obj;
            legacy.Remove("reject_output");
            legacy["do_out"] = Json.Parse("{\"chs\":[3,4],\"t\":0.3,\"dll\":\"C:\\\\x\\\\mezio_c.dll\"}");
            var lc = WeighingConfig.FromJson("16-pack-270mm", legacy);
            Check("legacy-do_out", lc.LegacyDoOut && lc.RejectActiveMs == 300 && lc.RejectPins.Length == 2 && lc.RejectPins[1] == 4, null);
            legacy["reject_output"] = j["reject_output"];
            Check("both-formats-reject", Throws(delegate { WeighingConfig.FromJson("16-pack-270mm", legacy); }), null);

            // SKU 目录：块名 "pack-尺寸"；新增块无需改代码即可加载；显示 "pack数-pack 尺寸"；格式错误的块被跳过
            Check("label-default", skus.LabelOf(skus["36-pack-240mm"]) == "36-pack 240mm" && skus.LabelOf(skus["9-pack-270mm"]) == "9-pack 270mm", skus.Describe());
            Check("default-keys-canonical", skus.Warnings.Count == 0, string.Join(" | ", skus.Warnings.ToArray()));
            Check("canonical-key", WeighingConfig.CanonicalKey(9, " 240 MM") == "9-pack-240mm", null);
            string tmp = Path.Combine(Path.GetTempPath(), "wcs_selftest_configs.json");
            try
            {
                var raw = SkuConfigStore.ReadRaw(AppSettings.Resolve(@"config\configs.json"));
                var extra = Json.Parse(Json.Write(raw["36-pack-240mm"])) as Json.Obj;
                extra.Remove("sku");
                extra["pad_size"] = "270mm"; extra["initial_center_g"] = 230.0; extra["safety_lower_g"] = 222.0;
                extra["half_width_g"] = 4.1; extra["max_window_width_g"] = 9.0; extra["window_offset_g"] = 0.3;
                raw["36-pack-270mm"] = extra;
                // 同 pack 数、不同尺寸：9-pack-240mm 与 9-pack-270mm 并存，参数各自独立
                var nine = Json.Parse(Json.Write(raw["9-pack-270mm"])) as Json.Obj;
                nine["sku"] = "9-pack-240mm"; nine["pad_size"] = "240mm";
                nine["initial_center_g"] = 55.0; nine["physical_min_g"] = 30.0; nine["physical_max_g"] = 90.0; nine["safety_lower_g"] = 48.0;
                nine["half_width_g"] = 3.2; nine["min_window_width_g"] = 6.4;
                raw["9-pack-240mm"] = nine;
                var named = Json.Parse(Json.Write(raw["16-pack-270mm"])) as Json.Obj;
                named.Remove("sku"); named["display_name"] = "16片 夜用 290mm"; named["pad_size"] = "290mm";
                raw["16-pack-night"] = named;
                raw["broken"] = Json.Parse("{\"pad_size\":\"270mm\",\"product_pad_num\":12}");
                raw["typo"] = Json.Parse(Json.Write(raw["9-pack-270mm"])); ((Json.Obj)raw["typo"]).Remove("sku"); ((Json.Obj)raw["typo"])["half_widht_g"] = 3.7;
                File.WriteAllText(tmp, Json.Write(raw), new UTF8Encoding(false));
                var cat = SkuConfigStore.LoadAll(tmp);
                Check("catalog-count", cat.Count == skus.Count + 3 && cat.Skipped.Count == 2, cat.Count + "/" + cat.Skipped.Count + " " + string.Join(" | ", cat.Skipped.ToArray()));
                var x = cat.Find("36-pack-270mm", 0);
                Check("catalog-new-sku", x != null && cat.LabelOf(x) == "36-pack 270mm" && x.Sku == "36-pack-270mm", x == null ? "null" : cat.LabelOf(x));
                var xw = StateMachineWeigher.ComputePassWindow(230.0, MachineState.Stable, x);
                Near("catalog-new-lower", xw.Lower, 230.0 - 4.1 + 0.3); Near("catalog-new-upper", xw.Upper, 230.0 + 4.1 + 0.3);
                var n240 = cat.Find("9-pack-240mm", 0); var n270 = cat.Find("9-pack-270mm", 0);
                Check("same-pack-both-load", n240 != null && n270 != null && !ReferenceEquals(n240, n270)
                    && cat.LabelOf(n240) == "9-pack 240mm" && cat.LabelOf(n270) == "9-pack 270mm", cat.Describe());
                Check("same-pack-own-params", n240.InitialCenter == 55.0 && n240.HalfWidth == 3.2 && n270.InitialCenter == skus["9-pack-270mm"].InitialCenter
                    && n270.HalfWidth == skus["9-pack-270mm"].HalfWidth, null);
                var w240 = StateMachineWeigher.ComputePassWindow(55.0, MachineState.Stable, n240);
                Near("same-pack-window-240", w240.Upper - w240.Lower, 6.4);
                Check("catalog-display-name", cat.LabelOf(cat["16-pack-night"]) == "16片 夜用 290mm", null);
                Check("catalog-order", cat.Items.IndexOf(cat["36-pack-240mm"]) + 1 == cat.Items.IndexOf(x) && cat.Items.IndexOf(n240) + 1 == cat.Items.IndexOf(n270), null);
                string note;
                Check("legacy-key-single", ReferenceEquals(cat.Find("36-pack", 0, out note), cat["36-pack-240mm"]) && note != null && note.Contains("36-pack-240mm"), note);
                Check("legacy-key-ambiguous", ReferenceEquals(cat.Find("9-pack", 0, out note), n270) && note != null && note.Contains("多个"), note);
                Check("catalog-legacy-padnum", ReferenceEquals(cat.Find("", 16), cat["16-pack-270mm"]) && cat.Find("nope", 36) == null && cat.Find("12-pack", 0) == null, null);
                Check("catalog-skip-msg", cat.Skipped[0].Contains("broken") && cat.Skipped[1].Contains("half_widht_g"), string.Join(" | ", cat.Skipped.ToArray()));
                Check("key-mismatch-warning", cat.Warnings.Count == 1 && cat.Warnings[0].Contains("16-pack-night") && cat.Warnings[0].Contains("16-pack-290mm"), string.Join(" | ", cat.Warnings.ToArray()));
                File.WriteAllText(tmp, "{\"a\":{\"pad_size\":\"1\"}}", new UTF8Encoding(false));
                Check("catalog-none-valid", Throws(delegate { SkuConfigStore.LoadAll(tmp); }), null);
            }
            finally { try { File.Delete(tmp); } catch { } }
            Check("json-duplicate-key", (Json.Parse("{\"a\":1,\"b\":2,\"a\":3}") as Json.Obj).DuplicateKeys.Count == 1, null);
            var mism = Cfg().ToJson(); mism["sku"] = "other";
            Check("sku-key-mismatch", Throws(delegate { WeighingConfig.FromJson("16-pack-270mm", mism); }), null);
            var setj = new AppSettings { Sku = "36-pack-270mm", ProductPadNum = 36 }.ToJson();
            Check("settings-sku-roundtrip", AppSettings.FromJson(Json.Parse(Json.Write(setj)) as Json.Obj).Sku == "36-pack-270mm", null);

            // 协议解析 + 分行
            var rec = Protocol.Parse("A,B,7,12:00:07,X,2,105.80", new AppSettings());
            Check("parse", rec.Weight == 105.80 && rec.SequenceNo == "7" && rec.MachineStatus == "合格", null);
            var dec = new LineDecoder(Encoding.ASCII);
            var lines = new List<string>();
            var b1 = Encoding.ASCII.GetBytes("a,1\r\n\r\nb,"); var b2 = Encoding.ASCII.GetBytes("2\n");
            dec.Feed(b1, b1.Length, lines); dec.Feed(b2, b2.Length, lines);
            Check("decoder", lines.Count == 2 && lines[0] == "a,1" && lines[1] == "b,2", string.Join("|", lines));

            // 更新参数时保留历史
            sm = new StateMachineWeigher(Cfg());
            for (int i = 0; i < 5; i++) sm.ProcessBox(T0.AddSeconds(i), 114);
            var nc = Cfg(); nc.HalfWidth = 4.0; nc.MaxWidth = 9.0;
            sm.ApplyConfigKeepHistory(nc);
            Check("hot-keep-history", sm.HistoryCount == 5 && Math.Abs(sm.CurrentWindow().Width - 8.0) < 1e-9, null);

            Console.WriteLine("自测完成：通过 " + _pass + "，失败 " + _fail);
            return _fail == 0 ? 0 : 1;
        }

        private static bool Throws(Action a)
        {
            try { a(); return false; } catch (FormatException) { return true; }
        }
    }
}
