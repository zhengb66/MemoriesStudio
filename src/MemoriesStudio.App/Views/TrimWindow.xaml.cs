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

    // 播放自愈：libvlc 的 Pause/Play 是异步的，快速"停止→播放"或在媒体末尾 Play() 会被吞掉。
    // _playRetry 限次重发，避免媒体真出错时无限重试。
    private int _playRetry;
    private DateTime _playRetryAt = DateTime.MinValue;

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

        // 媒体自然播到片尾后 libvlc 进入 Ended，此时 Play() 不会重启。
        // 用户仍在期望播放（_playing=true）时，主动从起点重建媒体续播。
        if (_playing)
        {
            var st = _player.State;
            if (st == LibVLCSharp.Shared.VLCState.Ended)
            {
                Log("state=Ended while playing -> restart at in");
                RestartPlaybackAt(_in);
                return;
            }
            // Pause/Play 竞态导致"图标是暂停但没在播"：限次补一次 Play。
            if (!_player.IsPlaying && st != LibVLCSharp.Shared.VLCState.Buffering &&
                st != LibVLCSharp.Shared.VLCState.Opening && DateTime.UtcNow >= _playRetryAt &&
                _playRetry < 3)
            {
                _playRetry++;
                _playRetryAt = DateTime.UtcNow.AddMilliseconds(300);
                Log($"play noop retry #{_playRetry} state={st}");
                try { _player.Play(); } catch { }
            }
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
        _playRetry = 0;
        _playRetryAt = DateTime.UtcNow.AddMilliseconds(500);
        try { _player.Mute = false; } catch { }

        // 位置不在区间内（例如停在片尾）时，从起点起播
        var t = Range.Position;
        if (t < _in - 0.05 || t > _out - 0.05)
        {
            SeekTo(_in);
            ShowPosition(_in);
        }

        _playing = true;
        // libvlc 在 Ended/Stopped 状态下 Play() 是空操作：必须重建 Media 才能真正起播。
        // 这正是"设完首/尾帧、停住后再点播放没反应"的根因。
        if (_player.State is LibVLCSharp.Shared.VLCState.Ended or LibVLCSharp.Shared.VLCState.Stopped
            or LibVLCSharp.Shared.VLCState.NothingSpecial)
        {
            RestartPlaybackAt(Range.Position);
        }
        else
        {
            try { _player.Play(); } catch { }
        }
        SetPlayIcon(true);
    }

    /// <summary>从指定位置重建媒体并播放（UI 线程调用，Media 构造/Play 走后台避免卡 UI）。</summary>
    private void RestartPlaybackAt(double seconds)
    {
        var target = Math.Clamp(seconds, _in, Math.Max(_in, _out - 0.05));
        Log($"restart media -> {target:0.00}");
        Task.Run(() =>
        {
            if (_closed) return;
            try
            {
                var media = new Media(_libvlc, _clip.SourcePath, FromType.FromPath);
                if (target > 0.05)
                    media.AddOption($":start-time={target.ToString("0.###", CultureInfo.InvariantCulture)}");
                try { _player.Mute = false; } catch { }
                _player.Play(media);
                Log("restart media play issued");
            }
            catch (Exception ex) { Log("restart media error: " + ex.Message); }
        });
    }

    private void PausePlayback()
    {
        _playing = false;
        _playRetry = 0;
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

    /// <summary>自检用：模拟把终点拖到指定秒（不触发播放）。</summary>
    public void SetOutPointForTest(double seconds)
    {
        _autoPausePending = false;
        _initTarget = -1;
        _out = Math.Clamp(seconds, _in + 0.3, _sourceDuration);
        RefreshRangeUi(keepPosition: true);
    }

    /// <summary>自检用：模拟点击"停止"（暂停并回到起点）。</summary>
    public void StopForTest() => OnStop(this, new RoutedEventArgs());

    /// <summary>自检播放状态：是否处于用户期望的播放中。</summary>
    public bool IsPlayingForTest => _playing && _player.IsPlaying;

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _playing = false;
        _autoPausePending = false;
        _initTarget = -1;
        _playRetry = 0;
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
