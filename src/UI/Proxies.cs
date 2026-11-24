using System;
using System.ComponentModel;
using WeightCheckerCS.Core;
using WeightCheckerCS.Engine;

namespace WeightCheckerCS.UI
{
    /// <summary>PropertyGrid 编辑用的程序设置代理。</summary>
    public sealed class SettingsProxy
    {
        private readonly AppSettings _s;
        public SettingsProxy(AppSettings s) { _s = s.Clone(); }
        public AppSettings ToSettings() { return _s.Clone(); }

        [Category("1 连接"), DisplayName("称重机 IP")] public string Host { get { return _s.Host; } set { _s.Host = value; } }
        [Category("1 连接"), DisplayName("端口")] public int Port { get { return _s.Port; } set { _s.Port = value; } }
        [Category("1 连接"), DisplayName("连接超时(s)")] public double ConnectTimeout { get { return _s.ConnectTimeoutSeconds; } set { _s.ConnectTimeoutSeconds = value; } }
        [Category("1 连接"), DisplayName("接收轮询超时(s)")] public double ReceiveTimeout { get { return _s.ReceiveTimeoutSeconds; } set { _s.ReceiveTimeoutSeconds = value; } }
        [Category("1 连接"), DisplayName("重连初始间隔(s)")] public double ReconnectInitial { get { return _s.ReconnectInitialSeconds; } set { _s.ReconnectInitialSeconds = value; } }
        [Category("1 连接"), DisplayName("重连最大间隔(s)")] public double ReconnectMax { get { return _s.ReconnectMaxSeconds; } set { _s.ReconnectMaxSeconds = value; } }
        [Category("1 连接"), DisplayName("接收缓冲(字节)")] public int BufferSize { get { return _s.BufferSize; } set { _s.BufferSize = value; } }
        [Category("1 连接"), DisplayName("编码")] public string Encoding { get { return _s.Encoding; } set { _s.Encoding = value; } }
        [Category("2 字段"), DisplayName("重量字段序号")] public int WeightField { get { return _s.WeightFieldIndex; } set { _s.WeightFieldIndex = value; } }
        [Category("2 字段"), DisplayName("序号字段序号")] public int SeqField { get { return _s.SequenceFieldIndex; } set { _s.SequenceFieldIndex = value; } }
        [Category("2 字段"), DisplayName("时间字段序号")] public int TimeField { get { return _s.TimestampFieldIndex; } set { _s.TimestampFieldIndex = value; } }
        [Category("2 字段"), DisplayName("称重机判定字段序号")] public int QualField { get { return _s.QualificationFieldIndex; } set { _s.QualificationFieldIndex = value; } }
        [Category("3 功能"), DisplayName("启用状态机判定")] public bool StateMachine { get { return _s.StateMachineEnabled; } set { _s.StateMachineEnabled = value; } }
        [Category("3 功能"), DisplayName("启用剔除输出")] public bool RejectOutput { get { return _s.RejectOutputEnabled; } set { _s.RejectOutputEnabled = value; } }
        [Category("3 功能"), DisplayName("保存 CSV")] public bool Csv { get { return _s.CsvEnabled; } set { _s.CsvEnabled = value; } }
        [Category("3 功能"), DisplayName("数据目录")] public string DataDir { get { return _s.DataDir; } set { _s.DataDir = value; } }
        [Category("3 功能"), DisplayName("启动后自动采集")] public bool AutoStart { get { return _s.AutoStart; } set { _s.AutoStart = value; } }
        [Category("3 功能"), DisplayName("关闭时最小化到托盘")] public bool Tray { get { return _s.MinimizeToTray; } set { _s.MinimizeToTray = value; } }
        [Category("4 文件"), DisplayName("SKU 参数文件")] public string SkuConfigs { get { return _s.SkuConfigsPath; } set { _s.SkuConfigsPath = value; } }
        [Category("5 显示"), DisplayName("界面刷新间隔(ms)"), Description("30~2000。界面按此节拍批量刷新，与判定速度无关。")] public int UiRefresh { get { return _s.UiRefreshMs; } set { _s.UiRefreshMs = value; } }
        [Category("5 显示"), DisplayName("实时表行数")] public int RealtimeRows { get { return _s.RealtimeRows; } set { _s.RealtimeRows = value; } }
        [Category("5 显示"), DisplayName("异常/不一致表行数")] public int AbnormalRows { get { return _s.AbnormalRows; } set { _s.AbnormalRows = value; } }
    }

    /// <summary>PropertyGrid 编辑用的 SKU 状态机参数代理（reject_output 在“剔除输出”区单独编辑）。</summary>
    public sealed class SkuProxy
    {
        private readonly WeighingConfig _c;
        public SkuProxy(WeighingConfig c) { _c = c.Clone(); }
        public WeighingConfig ToConfig() { return _c.Clone(); }

        [Category("0 SKU"), DisplayName("SKU"), ReadOnly(true)] public string Sku { get { return _c.Sku; } }
        [Category("0 SKU"), DisplayName("显示名"), Description("下拉框显示名；留空则显示 \"pack数-pack 尺寸\"")] public string ShowName { get { return _c.DisplayName ?? ""; } set { _c.DisplayName = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); } }
        [Category("0 SKU"), DisplayName("尺寸")] public string PadSize { get { return _c.PadSize; } set { _c.PadSize = value; } }
        [Category("1 中心"), DisplayName("初始中心(g)")] public double InitialCenter { get { return _c.InitialCenter; } set { _c.InitialCenter = value; } }
        [Category("1 中心"), DisplayName("中心窗口盒数 N")] public int CenterN { get { return _c.CenterWindowN; } set { _c.CenterWindowN = value; } }
        [Category("1 中心"), DisplayName("去尾盒数 trim")] public int Trim { get { return _c.CenterTrimN; } set { _c.CenterTrimN = value; } }
        [Category("1 中心"), DisplayName("粗大异常阈值(g)")] public double OutlierReject { get { return _c.OutlierReject; } set { _c.OutlierReject = value; } }
        [Category("1 中心"), DisplayName("重锁盒数")] public int RelockCount { get { return _c.OutlierRelockCount; } set { _c.OutlierRelockCount = value; } }
        [Category("1 中心"), DisplayName("重锁偏离保护(g, 0=不限)")] public double RelockMaxOffset { get { return _c.RelockMaxOffset; } set { _c.RelockMaxOffset = value; } }
        [Category("2 区间"), DisplayName("基础半宽(g)")] public double HalfWidth { get { return _c.HalfWidth; } set { _c.HalfWidth = value; } }
        [Category("2 区间"), DisplayName("整体偏置(g)")] public double Offset { get { return _c.WindowOffset; } set { _c.WindowOffset = value; } }
        [Category("2 区间"), DisplayName("最小宽度(g)")] public double MinWidth { get { return _c.MinWidth; } set { _c.MinWidth = value; } }
        [Category("2 区间"), DisplayName("最大宽度(g)")] public double MaxWidth { get { return _c.MaxWidth; } set { _c.MaxWidth = value; } }
        [Category("2 区间"), DisplayName("上行最大平移(g)")] public double UpShift { get { return _c.UpMaxShift; } set { _c.UpMaxShift = value; } }
        [Category("2 区间"), DisplayName("下行最大平移(g)")] public double DownShift { get { return _c.DownMaxShift; } set { _c.DownMaxShift = value; } }
        [Category("2 区间"), DisplayName("安全下限(g)")] public double SafetyLower { get { return _c.SafetyLower; } set { _c.SafetyLower = value; } }
        [Category("3 趋势"), DisplayName("快速中值盒数 k")] public int FastK { get { return _c.FastWindowK; } set { _c.FastWindowK = value; } }
        [Category("3 趋势"), DisplayName("上行阈值(g)")] public double TrendUp { get { return _c.TrendUp; } set { _c.TrendUp = value; } }
        [Category("3 趋势"), DisplayName("下行阈值(g)")] public double TrendDown { get { return _c.TrendDown; } set { _c.TrendDown = value; } }
        [Category("4 其他"), DisplayName("单片重量(g)")] public double PadWeight { get { return _c.PadWeight; } set { _c.PadWeight = value; } }
        [Category("4 其他"), DisplayName("物理下限(g)")] public double PhysMin { get { return _c.PhysicalMin; } set { _c.PhysicalMin = value; } }
        [Category("4 其他"), DisplayName("物理上限(g)")] public double PhysMax { get { return _c.PhysicalMax; } set { _c.PhysicalMax = value; } }
        [Category("4 其他"), DisplayName("停机重置(s)")] public double StopReset { get { return _c.StopResetSeconds; } set { _c.StopResetSeconds = value; } }
        [Category("4 其他"), DisplayName("连续无效报警数")] public int InvalidAlarm { get { return _c.InvalidAlarmCount; } set { _c.InvalidAlarmCount = value; } }
    }
}
