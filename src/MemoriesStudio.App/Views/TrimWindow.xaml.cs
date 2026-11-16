using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MemoriesStudio.Models;

namespace MemoriesStudio.Views;

/// <summary>
/// 视频剪辑对话框：LibVLC 播放本段视频（有声），用一根区间条同时控制起点 / 终点 / 当前位置。
/// 打开时不自动播放（起播只为拿到首帧，随即暂停在起点）；播放只在 [起点, 终点] 内循环。
///
/// 关键实现约束（踩坑记录）：绝不能订阅 TimeChanged/Playing 再用 Dispatcher.Invoke
/// 回 UI —— libvlc 的输入线程会在回调里被阻塞，而 UI 线程此刻又在等同一个播放器锁
/// （_player.Time 的 setter），两边互等直接死锁，表现为"打开对话框就卡死"。
/// 这里改为 UI 线程用 DispatcherTimer 轮询 _player.Time，回调链完全解耦。
/// </summary>
public partial class TrimWindow : Window
{
    private enum DragKind { None, In, Out, Pos }

    private readonly LibVLC _libvlc;
    private readonly MediaPlayer _player;
    private readonly Clip _clip;
    private readonly double _sourceDuration;
    private readonly DispatcherTimer _ticker;

    private bool _closed;
    private bool _errorShown;
    private bool _playing;                 // 用户期望的播放状态
    private bool _autoPausePending = true; // 首帧渲染后立刻暂停（不自动播放）
    private DateTime _seekGuard = DateTime.MinValue;
    private DragKind _lastDrag;
    private double _in, _out;

    // 打开时定位到剪辑起始点：起播前用 :start-time 直接从头定位，暂停后再校验/补 seek
    private double _initTarget;
    private int _initAttempts;
    private DateTime _initRetryAt = DateTime.MinValue;

    public double InPoint => _in;
    public double OutPoint => _out;

    /// <summary>当前播放位置（秒），供自检/诊断读取。</summary>
    public double CurrentTime
    {
        get { try { return Math.Max(0, _player.Time / 1000.0); } catch { return 0; } }
    }

    /// <summary>播放器状态快照，供自检日志用。</summary>
    public string DebugState
    {
        get
        {
            try { return $"state={_player.State} media={(_player.Media is not null)} playing={_player.IsPlaying}"; }
            catch { return "state=?"; }
        }
    }

    /// <summary>打开后的初始定位是否已完成（首帧已暂停到剪辑起点）。</summary>
    public bool IsInitialized => !_autoPausePending && _initTarget < 0;

    /// <summary>诊断日志（%TEMP%\memories_trim.log），卡死时看最后一条能定位到哪一步。</summary>
    internal static void Log(string msg)
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "memories_trim.log");
            var fi = new System.IO.FileInfo(path);
            if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
            System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {msg}\r\n");
        }
        catch { }
    }

    public TrimWindow(Clip clip)
    {
        // VideoView 在 InitializeComponent 里就构造出来了，LibVLC 必须先初始化
        Core.Initialize();
        Log($"ctor begin in={clip.SourceIn:0.00} dur={clip.Duration:0.00} src={clip.SourceDuration:0.00}");

        InitializeComponent();
        _clip = clip;
        _sourceDuration = Math.Max(0.5, clip.SourceDuration > 0.2 ? clip.SourceDuration : 30);
        _in = Math.Max(0, clip.SourceIn);
        _out = Math.Min(_sourceDuration, clip.SourceIn + Math.Max(0.3, clip.Duration));
        _initTarget = _in;   // 打开即定位到剪辑起始点

        _libvlc = new LibVLC(enableDebugLogs: false);
        // 软件解码：WPF 的 VideoView 与硬解（D3D11 输出）组合在部分显卡/驱动上会黑屏或卡死
        _player = new MediaPlayer(_libvlc) { EnableHardwareDecoding = false };
        Video.MediaPlayer = _player;

        _player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_errorShown || _closed) return;
            _errorShown = true;
            MessageBox.Show(this, "视频无法播放（格式不受支持或文件损坏）。", "剪辑",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }));

        _ticker = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
        _ticker.Tick += (_, _) => OnTick();

        Range.InPointDragged += v => { _lastDrag = DragKind.In; _in = v; RefreshRangeUi(keepPosition: true); };
        Range.OutPointDragged += v => { _lastDrag = DragKind.Out; _out = v; RefreshRangeUi(keepPosition: true); };
        Range.PositionDragged += OnPositionDragged;
        Range.DragCompleted += OnDragCompleted;

        RefreshRangeUi(keepPosition: false);
        SetPlayIcon(false);

        Loaded += (_, _) =>
        {
            Log("loaded -> opening media");
            // 后台线程创建 Media + 起播（只为了拿到首帧，随后自动暂停）
            Task.Run(() =>
            {
                try
                {
                    var media = new Media(_libvlc, _clip.SourcePath, FromType.FromPath);
                    // 直接从剪辑起点起播：首帧就是入点画面，不必等暂停后再 seek
                    if (_in > 0.05)
                        media.AddOption($":start-time={_in.ToString("0.###", CultureInfo.InvariantCulture)}");
                    try { _player.Mute = true; } catch { }
                    Log("play(media) ...");
                    _player.Play(media);
                    Log("play(media) returned");
                }
                catch (Exception ex)
                {
                    Log("play error: " + ex.Message);
                    // 打开失败由 EncounteredError 统一提示
                }
            });
            _ticker.Start();
        };
    }

    // ------------------------------------------------------------ 轮询刷新

    /// <summary>UI 线程定时器：只读播放器状态并刷新控件；循环跳转也在这里发起。</summary>
    private void OnTick()
    {
        if (_closed) return;

        // 打开时的"假起播"：检测到真的开始解码后立刻暂停，停在起点，不自动播放
        if (_autoPausePending)
        {
            if (_player.IsPlaying)
            {
                _autoPausePending = false;
                Log("auto-pause begin");
                PausePlayback();
                Log("auto-pause done");
                // 暂停刚生效时 seek 容易被丢弃，稍等一拍再由 ticker 校验/补正
                _initRetryAt = DateTime.UtcNow.AddMilliseconds(200);
            }
            return;
        }

        long ms;
        try { ms = _player.Time; } catch { return; }
        if (ms < 0) return;

        // 打开后定位到剪辑起始点：位置没对上就补 seek（最多 3 次），确认后解除
        if (_initTarget >= 0 && DateTime.UtcNow >= _initRetryAt)
        {
            var cur = ms / 1000.0;
            if (Math.Abs(cur - _initTarget) <= 0.8 || _initAttempts >= 3)
            {
                Log($"init seek confirmed at {cur:0.00} (target {_initTarget:0.00})");
                _initTarget = -1;
                ShowPosition(cur);
                return;
            }

            _initAttempts++;
            Log($"init seek #{_initAttempts} -> {_initTarget:0.00} (now {cur:0.00})");
            SeekTo(_initTarget);
            Log("init seek issued");
            ShowPosition(_initTarget);
            _initRetryAt = DateTime.UtcNow.AddMilliseconds(600);
            return;
        }

        // 刚发起过 seek：libvlc 还会短暂返回旧时间，等它稳定再刷新，避免播放头来回跳
        if (DateTime.UtcNow < _seekGuard) return;

        var t = ms / 1000.0;

        // 区间循环：到达终点回到起点；意外跑到起点之前也拉回来
        if (_playing && _player.IsPlaying)
        {
            if (t >= _out - 0.05) { SeekTo(_in); return; }
            if (t < _in - 0.4) { SeekTo(_in); return; }
        }

        ShowPosition(t);
    }

    private void ShowPosition(double t)
    {
        t = Math.Clamp(t, 0, _sourceDuration);
        Range.Position = t;
        TxtTime.Text = $"{Fmt(t)} / {Fmt(_sourceDuration)}";
    }

    private void SeekTo(double seconds)
    {
        _seekGuard = DateTime.UtcNow.AddMilliseconds(400);
        try { _player.Time = (long)(Math.Max(0, seconds) * 1000); } catch { }
    }
    // ------------------------------------------------------------ 区间条

    private void RefreshRangeUi(bool keepPosition)
    {
        Range.Maximum = _sourceDuration;
        Range.InPoint = _in;
        Range.OutPoint = _out;
        if (!keepPosition || Range.Position < _in) Range.Position = _in;
        else if (Range.Position > _out) Range.Position = _out;

        TxtIn.Text = $"起点 {_in:0.0}s";
        TxtOut.Text = $"终点 {_out:0.0}s　保留 {_out - _in:0.0}s";
        TxtTime.Text = $"{Fmt(Range.Position)} / {Fmt(_sourceDuration)}";
    }

    private void OnPositionDragged(double value)
    {
        _lastDrag = DragKind.Pos;
        _autoPausePending = false;   // 用户开始操作，不再自动暂停
        _initTarget = -1;            // 用户已自己定位，放弃"打开即定位"校验

        // 拖动定位时先暂停，避免连续 seek 造成卡顿
        if (_playing) PausePlayback();

        SeekTo(value);
        ShowPosition(value);
    }

    private void OnDragCompleted()
    {
        if (_playing || _lastDrag == DragKind.Pos || _lastDrag == DragKind.None) return;

        // 拖完起止点，把画面跳到对应帧，方便确认边界
        var target = _lastDrag == DragKind.In ? _in : Math.Max(_in, _out - 0.05);
        SeekTo(target);
        ShowPosition(target);
    }

    // ------------------------------------------------------------ 播放控制（与主窗口一致）

    private void SetPlayIcon(bool playing)
        => PlayIcon.Data = (System.Windows.Media.Geometry)FindResource(playing ? "IconPauseGeom" : "IconPlayGeom");

    private void OnTogglePlay(object sender, RoutedEventArgs e)
    {
        if (_playing)
        {
            PausePlayback();
            return;
        }
        BeginPlay();
    }

    /// <summary>开始播放（也供自检调用）。</summary>
    public void BeginPlay()
    {
        if (_closed) return;

        _autoPausePending = false;
        _initTarget = -1;
        try { _player.Mute = false; } catch { }

        // 位置不在区间内（例如停在片尾）时，从起点起播
        var t = Range.Position;
        if (t < _in - 0.05 || t > _out - 0.05) SeekTo(_in);

        _playing = true;
        try { _player.Play(); } catch { }
        SetPlayIcon(true);
    }

    private void PausePlayback()
    {
        _playing = false;
        try { if (_player.IsPlaying) _player.Pause(); } catch { }
        SetPlayIcon(false);
    }

    /// <summary>自检用：模拟拖动区间条定位（暂停态 seek）。</summary>
    public void SeekForTest(double seconds)
    {
        _autoPausePending = false;
        _initTarget = -1;
        var t = Math.Clamp(seconds, _in, _out);
        SeekTo(t);
        ShowPosition(t);
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _playing = false;
        _autoPausePending = false;
        _initTarget = -1;
        try { if (_player.IsPlaying) _player.Pause(); } catch { }
        SetPlayIcon(false);

        SeekTo(_in);
        ShowPosition(_in);
    }

    // ------------------------------------------------------------ 结果

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string Fmt(double seconds)
        => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss\.f");

    protected override void OnClosed(EventArgs e)
    {
        Log("closing");
        _closed = true;
        _ticker.Stop();

        // 先断开 VideoView（WPF 原生宿主句柄），再交给后台 Stop/Dispose。
        // libvlc 的 stop 会等 vout 拆完，放 UI 线程上偶发卡住整个窗口（表现"关不掉"）。
        try { Video.MediaPlayer = null; } catch { }

        Task.Run(() =>
        {
            try { _player.Stop(); } catch { }
            try { _player.Dispose(); } catch { }
            try { _libvlc.Dispose(); } catch { }
            Log("disposed");
        });

        base.OnClosed(e);
    }
}
