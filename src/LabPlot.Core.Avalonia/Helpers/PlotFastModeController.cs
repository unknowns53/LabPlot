using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ScottPlot.Avalonia;

namespace LabPlot.Core.Avalonia.Helpers;

/// <summary>
/// マウスでのパン / ホイールズーム操作「中」だけアンチエイリアスを切って、
/// 操作が終わった瞬間に高品質 (AA あり) で 1 回描き直す機構。
/// 実測で 8 系列×1000 点の重ね書きにおいて、AA あり ~16.5ms/frame (60fps 境界)
/// → AA なし ~10.2ms/frame (98fps) まで縮む。操作中は AA なしで体感 FPS を稼ぎ、
/// 操作が止まったら 1 回だけ AA ありで清書することで、見た目の劣化を「動いている間だけ」に限定する。
/// </summary>
/// <remarks>
/// <see cref="LegendDragController"/> と同じく、購読は <see cref="RoutingStrategies.Tunnel"/>
/// で行う。ただしこちらは observer に徹し、<c>e.Handled</c> は一切立てない
/// (立てると <see cref="AvaPlot"/> 自身の bubble ハンドラにイベントが届かなくなり、
/// パン / ズームそのものが止まってしまう)。あくまで「AvaPlot が今まさにパン / ズーム
/// 操作を受けている」ことを横から観測して AA の on/off を切り替えるだけの役割。
///
/// また fast mode 中は <see cref="TopLevel.RequestAnimationFrame"/> による
/// vsync 同期の invalidation ループを別途回す。ScottPlot.Avalonia 5.1.58 の
/// <c>AvaPlot.Refresh()</c> は <see cref="DispatcherPriority.Background"/> で
/// <c>InvalidateVisual</c> を予約する実装になっており、パン中は高頻度のポインタ
/// イベントが UI スレッドの入力キューを埋め続けるため、この Background 優先度の
/// 予約が飢餓状態になって画面反映が不等間隔になる (数値上は 100fps 相当でも
/// 体感がガタつく)。描画そのものは compositor の render pass 内で同期実行される
/// ので、フレームごとに直接 <c>InvalidateVisual</c> を入れてやれば表示はディスプレイ
/// の垂直同期に揃う。
/// </remarks>
public sealed class PlotFastModeController
{
    private static readonly TimeSpan DefaultWheelExitDelay = TimeSpan.FromMilliseconds(250);

    private readonly AvaPlot _avaPlot;
    private readonly Func<IEnumerable<ScottPlot.IPlottable>> _getPlottables;
    private readonly DispatcherTimer _wheelExitTimer;

    private bool _attached;
    private bool _active;
    private bool _animationLoopRunning;

    public PlotFastModeController(
        AvaPlot avaPlot,
        Func<IEnumerable<ScottPlot.IPlottable>> getPlottables,
        TimeSpan? wheelExitDelay = null)
    {
        _avaPlot = avaPlot ?? throw new ArgumentNullException(nameof(avaPlot));
        _getPlottables = getPlottables ?? throw new ArgumentNullException(nameof(getPlottables));
        _wheelExitTimer = new DispatcherTimer { Interval = wheelExitDelay ?? DefaultWheelExitDelay };
        _wheelExitTimer.Tick += OnWheelExitTimerTick;
    }

    /// <summary>
    /// Subscribe to the plot's pointer events. Idempotent — calling
    /// <c>Attach</c> twice has the same effect as calling it once.
    /// </summary>
    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        // Tunnel routing で先に観測する。e.Handled は立てないので、AvaPlot 自身の
        // bubble ハンドラ（パン開始 / ズーム処理）には引き続きイベントが届く。
        _avaPlot.AddHandler(InputElement.PointerPressedEvent, OnPress, RoutingStrategies.Tunnel);
        _avaPlot.AddHandler(InputElement.PointerReleasedEvent, OnRelease, RoutingStrategies.Tunnel);
        _avaPlot.AddHandler(InputElement.PointerWheelChangedEvent, OnWheelChanged, RoutingStrategies.Tunnel);
        _avaPlot.AddHandler(InputElement.PointerCaptureLostEvent, OnLostCapture, RoutingStrategies.Bubble);
    }

    /// <summary>
    /// Unsubscribe from the plot's pointer events. Safe to call without
    /// a prior <see cref="Attach"/>.
    /// </summary>
    public void Detach()
    {
        if (!_attached) return;
        _avaPlot.RemoveHandler(InputElement.PointerPressedEvent, OnPress);
        _avaPlot.RemoveHandler(InputElement.PointerReleasedEvent, OnRelease);
        _avaPlot.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheelChanged);
        _avaPlot.RemoveHandler(InputElement.PointerCaptureLostEvent, OnLostCapture);
        _wheelExitTimer.Stop();
        _attached = false;
        // _active を落として invalidation ループの次回コールバックで自然停止させる。
        _active = false;
    }

    private void OnPress(object? sender, PointerPressedEventArgs e)
    {
        _wheelExitTimer.Stop();
        EnterFastMode();
    }

    private void OnRelease(object? sender, PointerReleasedEventArgs e)
    {
        _wheelExitTimer.Stop();
        ExitFastMode();
    }

    private void OnLostCapture(object? sender, PointerCaptureLostEventArgs e)
    {
        _wheelExitTimer.Stop();
        ExitFastMode();
    }

    private void OnWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        EnterFastMode();
        // ホイール連打の間は毎回タイマーを再武装し、最後のスクロールから
        // DefaultWheelExitDelay 経ってから清書する。
        _wheelExitTimer.Stop();
        _wheelExitTimer.Start();
    }

    private void OnWheelExitTimerTick(object? sender, EventArgs e)
    {
        _wheelExitTimer.Stop();
        ExitFastMode();
    }

    private void EnterFastMode()
    {
        if (_active) return;
        _active = true;
        SetAntiAlias(false);
        // ここでは Refresh() を呼ばない。直後に AvaPlot 自身のパン / ズーム描画が
        // 走るので、ここで描くと二重描画になる。
        StartInvalidationLoop();
    }

    private void ExitFastMode()
    {
        if (!_active) return;
        _active = false;
        SetAntiAlias(true);
        _avaPlot.Refresh();
    }

    /// <summary>
    /// fast mode 中だけ、フレームごとに <c>InvalidateVisual</c> を直接叩く
    /// vsync 同期ループを開始する。<see cref="_active"/> が false になった時点で
    /// 次のコールバックが自ら停止するので、明示的な停止処理は不要。
    /// </summary>
    private void StartInvalidationLoop()
    {
        if (_animationLoopRunning) return;
        var topLevel = TopLevel.GetTopLevel(_avaPlot);
        if (topLevel is null) return;
        _animationLoopRunning = true;
        topLevel.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan _)
    {
        if (!_active)
        {
            _animationLoopRunning = false;
            return;
        }

        // AvaPlot.Refresh() は DispatcherPriority.Background で InvalidateVisual を
        // 予約するため、連続ポインタ入力中は実行が飢餓状態になり画面反映が不等間隔に
        // なる。操作中はこちらでフレーム周期ごとに直接 InvalidateVisual を入れて、
        // 表示更新をディスプレイの垂直同期に揃える。
        _avaPlot.InvalidateVisual();

        var topLevel = TopLevel.GetTopLevel(_avaPlot);
        if (topLevel is null)
        {
            _animationLoopRunning = false;
            return;
        }
        topLevel.RequestAnimationFrame(OnAnimationFrame);
    }

    /// <summary>
    /// 対象 plottable の線 / マーカーの AA を一括で切り替える。
    /// <see cref="ScottPlot.Plottables.BarPlot"/> は <see cref="ScottPlot.IHasLine"/> も
    /// <see cref="ScottPlot.IHasMarker"/> も実装しないため、この処理の対象外になる。
    /// 棒は点数が少なく AA の描画コストがボトルネックにならないため、これは意図的な除外。
    /// </summary>
    private void SetAntiAlias(bool antiAlias)
    {
        foreach (var plottable in _getPlottables())
        {
            if (plottable is ScottPlot.IHasLine hasLine)
            {
                hasLine.LineStyle.AntiAlias = antiAlias;
            }
            if (plottable is ScottPlot.IHasMarker hasMarker)
            {
                hasMarker.MarkerStyle.LineStyle.AntiAlias = antiAlias;
                hasMarker.MarkerStyle.FillStyle.AntiAlias = antiAlias;
            }
        }
    }
}
