using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;

namespace WeightCheckerCS.Engine
{
    public enum DioSimulation { Off, Instant, RealTiming }

    /// <summary>
    /// 剔除输出（喷气）。mezio_c.dll 在程序启动时 LoadLibrary + Init_DIO 一次，之后每个脉冲只调用
    /// DO_WritePortChecked(mask) → 等待 t → DO_WritePortChecked(0)。脉冲在独立线程串行执行，队列上限 100，
    /// 采集线程只入队、不阻塞。DLL 路径变化时热切换；调用异常时自动重新初始化。
    /// </summary>
    public sealed class DioOutput : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WritePortFn(int mask);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StopFn();

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr h, string name);
        [DllImport("kernel32", SetLastError = true)] private static extern bool FreeLibrary(IntPtr h);
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetDllDirectoryW(string path);
        [DllImport("winmm")] private static extern uint timeBeginPeriod(uint p);
        [DllImport("winmm")] private static extern uint timeEndPeriod(uint p);

        private struct Pulse { public int Mask; public double Seconds; public string Source; public long EnqueuedTicks; }

        private readonly object _dllLock = new object();
        private readonly BlockingCollection<Pulse> _queue = new BlockingCollection<Pulse>(new ConcurrentQueue<Pulse>(), 100);
        private readonly Thread _thread;
        private readonly Action<string> _log;
        private IntPtr _handle = IntPtr.Zero;
        private InitFn _init; private WritePortFn _write; private StopFn _stop;
        private bool _initialized;
        private string _loadedPath;
        private volatile string _status = "未加载";
        private long _pulseCount, _failCount, _dropCount;
        private volatile bool _disposed;
        private readonly DioSimulation _sim;
        private long _simMaskOr;

        public DioOutput(Action<string> log) : this(log, DioSimulation.Off) { }

        /// <summary>sim != Off 时不加载 DLL，脉冲只计数（Instant 不等待；RealTiming 按激活时长等待），用于无硬件的端到端测试。</summary>
        public DioOutput(Action<string> log, DioSimulation sim)
        {
            _log = log ?? delegate { };
            _sim = sim;
            timeBeginPeriod(1);
            _thread = new Thread(Run) { IsBackground = true, Name = "dio-output", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        public string Status { get { return _status; } }
        public bool Ready { get { return _initialized; } }
        public string LoadedPath { get { return _loadedPath; } }
        public long PulseCount { get { return Interlocked.Read(ref _pulseCount); } }
        public long FailCount { get { return Interlocked.Read(ref _failCount); } }
        public long DropCount { get { return Interlocked.Read(ref _dropCount); } }
        public int Pending { get { return _queue.Count; } }
        public bool Simulated { get { return _sim != DioSimulation.Off; } }
        /// <summary>模拟模式下所有已执行脉冲掩码的按位或（用于核对引脚）。</summary>
        public long SimulatedMaskOr { get { return Interlocked.Read(ref _simMaskOr); } }

        /// <summary>等待队列中的脉冲全部执行完（测试用）。</summary>
        public bool WaitIdle(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (_queue.Count == 0 && Interlocked.Read(ref _executing) == 0)
                {
                    Thread.Sleep(20);
                    if (_queue.Count == 0 && Interlocked.Read(ref _executing) == 0) return true;
                }
                Thread.Sleep(10);
            }
            return false;
        }
        private long _executing;

        /// <summary>加载（或切换）DLL 并 Init_DIO。返回是否成功；失败不抛异常，程序继续采集。</summary>
        public bool Load(string path)
        {
            lock (_dllLock)
            {
                if (_initialized && string.Equals(path, _loadedPath, StringComparison.OrdinalIgnoreCase)) return true;
                UnloadLocked();
                _loadedPath = path;
                if (_sim != DioSimulation.Off)
                {
                    _initialized = true;
                    _status = "模拟（无硬件，" + (_sim == DioSimulation.Instant ? "不计时" : "按激活时长计时") + "）";
                    _log("DIO 模拟模式：不加载 " + path);
                    return true;
                }
                try
                {
                    if (!File.Exists(path)) throw new FileNotFoundException("找不到 DLL：" + path);
                    if (IntPtr.Size != 8) throw new InvalidOperationException("程序必须以 64 位运行才能加载 x64 mezio_c.dll");
                    SetDllDirectoryW(Path.GetDirectoryName(path));
                    _handle = LoadLibraryW(path);
                    SetDllDirectoryW(null);
                    if (_handle == IntPtr.Zero)
                    {
                        int err = Marshal.GetLastWin32Error();
                        throw new InvalidOperationException("LoadLibrary 失败，Win32 错误码 " + err +
                            (err == 126 ? "（依赖 DLL 缺失：请安装板卡驱动 WDT_DIO64.dll 及 VC++ 2015-2022 x64 运行库，或将其放在 mezio_c.dll 同目录）" : ""));
                    }
                    _init = Fn<InitFn>("Init_DIO");
                    _write = Fn<WritePortFn>("DO_WritePortChecked");
                    _stop = Fn<StopFn>("Stop_DIO");
                    InitLocked();
                    _status = "已就绪";
                    _log("DIO 已加载并初始化：" + path);
                    return true;
                }
                catch (Exception ex)
                {
                    UnloadLocked();
                    _status = "加载失败：" + ex.Message;
                    _log("DIO " + _status);
                    return false;
                }
            }
        }

        private T Fn<T>(string name) where T : class
        {
            IntPtr p = GetProcAddress(_handle, name);
            if (p == IntPtr.Zero) throw new InvalidOperationException("DLL 缺少导出函数 " + name);
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        private void InitLocked()
        {
            int r;
            try { r = _init(); }
            catch (Exception ex) { throw new InvalidOperationException("Init_DIO 异常：" + ex.Message); }
            if (r != 1) throw new InvalidOperationException("Init_DIO 失败，返回 " + r);
            _initialized = true;
        }

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        private void UnloadLocked()
        {
            if (_initialized && _sim == DioSimulation.Off)
            {
                try { _write(0); } catch { }
                try { _stop(); } catch { }
            }
            _initialized = false;
            if (_handle != IntPtr.Zero)
            {
                try { FreeLibrary(_handle); } catch { }
                _handle = IntPtr.Zero;
            }
            _init = null; _write = null; _stop = null;
            _status = "未加载";
        }

        /// <summary>非阻塞入队一个脉冲。队列满时丢弃并计数。</summary>
        public bool Enqueue(int[] channels, double seconds, string source)
        {
            if (_disposed) return false;
            int mask = 0;
            foreach (int ch in channels) mask |= 1 << ch;
            var p = new Pulse { Mask = mask, Seconds = seconds, Source = source, EnqueuedTicks = Stopwatch.GetTimestamp() };
            if (!_queue.TryAdd(p))
            {
                Interlocked.Increment(ref _dropCount);
                _log("剔除输出队列已满，丢弃脉冲：" + source);
                return false;
            }
            return true;
        }

        private long _lastFailLogTicks; private int _suppressedFails;

        /// <summary>DIO 持续不可用时每 10 秒最多记一条失败日志，避免日志刷屏。</summary>
        private void LogFailThrottled(string msg)
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastFailLogTicks != 0 && now - _lastFailLogTicks < Stopwatch.Frequency * 10) { _suppressedFails++; return; }
            if (_suppressedFails > 0) msg += "（此前 10 秒内另有 " + _suppressedFails + " 次失败未记录）";
            _suppressedFails = 0; _lastFailLogTicks = now;
            _log(msg);
        }

        private void Run()
        {
            foreach (var p in _queue.GetConsumingEnumerable())
            {
                if (_disposed) break;
                Interlocked.Increment(ref _executing);
                try
                {
                    if (_sim != DioSimulation.Off) Simulate(p);
                    else Execute(p);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failCount);
                    LogFailThrottled("剔除输出失败（" + p.Source + "）：" + ex.Message);
                }
                finally { Interlocked.Decrement(ref _executing); }
            }
        }

        private void Simulate(Pulse p)
        {
            if (_sim == DioSimulation.RealTiming && p.Seconds > 0) Thread.Sleep((int)Math.Round(p.Seconds * 1000));
            long cur;
            do { cur = Interlocked.Read(ref _simMaskOr); }
            while (Interlocked.CompareExchange(ref _simMaskOr, cur | (uint)p.Mask, cur) != cur);
            Interlocked.Increment(ref _pulseCount);
        }

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        private void Execute(Pulse p)
        {
            lock (_dllLock)
            {
                if (!_initialized)
                {
                    if (_handle == IntPtr.Zero) throw new InvalidOperationException("DIO 未就绪（" + _status + "）");
                    InitLocked(); // 上次异常后重试初始化
                    _status = "已就绪";
                }
                try
                {
                    _write(p.Mask);
                    var sw = Stopwatch.StartNew();
                    int ms = (int)Math.Round(p.Seconds * 1000);
                    if (ms > 2) Thread.Sleep(ms - 1);
                    while (sw.Elapsed.TotalMilliseconds < p.Seconds * 1000) Thread.SpinWait(50);
                }
                catch (Exception ex)
                {
                    _initialized = false;
                    _status = "调用异常，将在下次脉冲时重新初始化";
                    try { _write(0); } catch { }
                    try { _stop(); } catch { }
                    throw new InvalidOperationException("DO_WritePortChecked 异常：" + ex.Message);
                }
                finally
                {
                    if (_initialized) { try { _write(0); } catch { _initialized = false; } }
                }
                Interlocked.Increment(ref _pulseCount);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queue.CompleteAdding();
            _thread.Join(3000);
            lock (_dllLock) { UnloadLocked(); }
            timeEndPeriod(1);
        }
    }
}
