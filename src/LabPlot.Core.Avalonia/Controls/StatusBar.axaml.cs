using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LabPlot.Core.Avalonia.Controls;

/// <summary>
/// 各 Window 下部のステータス行を担う統一 control。WPF 版で TextBlock + bool isError
/// の 2 値だったものを Avalonia 主流化に合わせて 4 段階 severity + 表示アイコンに拡張する。
/// 既存呼び出し側との互換のため <see cref="SetStatus(string,bool)"/> も併設する。
/// </summary>
public partial class StatusBar : UserControl
{
    // severity ごとの前景色。slate-600 / emerald-700 / amber-700 / red-700。
    // CommonStyles.axaml の ForegroundMutedBrush / Success・Warning・ErrorForegroundBrush と同系統で
    // 揃えてある。control 内部で hex 直書きしておくのは、Theme リソース解決が
    // InitializeComponent 完了前に走るケースで FindResource が null を返すのを避けるため。
    private static readonly Color InfoColor = Color.FromRgb(0x47, 0x55, 0x69);
    private static readonly Color SuccessColor = Color.FromRgb(0x04, 0x78, 0x57);
    private static readonly Color WarningColor = Color.FromRgb(0xB4, 0x53, 0x09);
    private static readonly Color ErrorColor = Color.FromRgb(0xB9, 0x1C, 0x1C);

    // 14x14 用 StreamGeometry。すべて Stretch=None で control の Width/Height にそのまま乗る。
    private static readonly Geometry CheckIcon = Geometry.Parse("M 2,7 L 6,11 L 12,3");
    private static readonly Geometry WarningIcon = Geometry.Parse("M 7,1 L 13,12 L 1,12 Z M 6,5 L 8,5 L 8,9 L 6,9 Z M 6,10 L 8,10 L 8,11.5 L 6,11.5 Z");
    private static readonly Geometry ErrorIcon = Geometry.Parse("M 3,3 L 11,11 M 11,3 L 3,11");
    private static readonly Geometry InfoIcon = Geometry.Parse("M 7,2 A 5,5 0 1 0 7.001,2 Z M 7,5 L 7,5.5 M 6.25,7 L 7,7 L 7,11 L 7.75,11");

    private global::Avalonia.Controls.Shapes.Path? _iconPath;
    private TextBlock? _messageTextBlock;
    private StatusSeverity _severity = StatusSeverity.None;

    public StatusBar()
    {
        InitializeComponent();
        _iconPath = this.FindControl<global::Avalonia.Controls.Shapes.Path>("IconPath");
        _messageTextBlock = this.FindControl<TextBlock>("MessageTextBlock");
    }

    /// <summary>
    /// 現在の severity。直接読み取りたい呼び出し元向け (CalibrationCurveWindow の
    /// 「警告メッセージが既に出ているか」判定など) に公開する。
    /// </summary>
    public StatusSeverity Severity => _severity;

    /// <summary>
    /// 現在表示中の生メッセージ。空文字を返すこともある (None severity)。
    /// </summary>
    public string Message => _messageTextBlock?.Text ?? string.Empty;

    /// <summary>
    /// severity と message を一括設定する主たる API。<paramref name="severity"/> が None のとき
    /// アイコンを折り畳んで Info 同等の中立色で表示する。
    /// </summary>
    public void SetStatus(string message, StatusSeverity severity)
    {
        _severity = severity;
        if (_messageTextBlock is not null)
        {
            _messageTextBlock.Text = message ?? string.Empty;
            _messageTextBlock.Foreground = new SolidColorBrush(SeverityColor(severity));
        }
        if (_iconPath is not null)
        {
            var geometry = SeverityIcon(severity);
            if (geometry is null || severity == StatusSeverity.None || string.IsNullOrEmpty(message))
            {
                _iconPath.IsVisible = false;
            }
            else
            {
                _iconPath.Data = geometry;
                var brush = new SolidColorBrush(SeverityColor(severity));
                if (severity == StatusSeverity.Warning)
                {
                    // 警告アイコンは三角を塗りつぶし表現する
                    _iconPath.Fill = brush;
                    _iconPath.Stroke = null;
                    _iconPath.StrokeThickness = 0;
                }
                else
                {
                    _iconPath.Fill = null;
                    _iconPath.Stroke = brush;
                    _iconPath.StrokeThickness = severity == StatusSeverity.Success ? 2.0 : 1.6;
                }
                _iconPath.IsVisible = true;
            }
        }
    }

    /// <summary>
    /// 旧 API 互換。<paramref name="isError"/> が true なら Error、それ以外は Info にマップする。
    /// 3 アプリの MainWindow.axaml.cs に散在する <c>SetStatus(message, isError)</c> を一気に
    /// 書き換えずに済むようにブリッジとして残す。
    /// </summary>
    public void SetStatus(string message, bool isError) =>
        SetStatus(message, isError ? StatusSeverity.Error : StatusSeverity.Info);

    /// <summary>メッセージ部分だけ更新して severity を保つ。</summary>
    public void SetMessage(string message)
    {
        if (_messageTextBlock is not null)
        {
            _messageTextBlock.Text = message ?? string.Empty;
        }
        if (string.IsNullOrEmpty(message) && _iconPath is not null)
        {
            _iconPath.IsVisible = false;
        }
    }

    /// <summary>severity だけ更新して message を保つ。</summary>
    public void SetSeverity(StatusSeverity severity) => SetStatus(_messageTextBlock?.Text ?? string.Empty, severity);

    /// <summary>メッセージとアイコンを両方クリアして severity を None に戻す。</summary>
    public void Clear() => SetStatus(string.Empty, StatusSeverity.None);

    private static Color SeverityColor(StatusSeverity severity) => severity switch
    {
        StatusSeverity.Success => SuccessColor,
        StatusSeverity.Warning => WarningColor,
        StatusSeverity.Error => ErrorColor,
        _ => InfoColor,
    };

    private static Geometry? SeverityIcon(StatusSeverity severity) => severity switch
    {
        StatusSeverity.Success => CheckIcon,
        StatusSeverity.Warning => WarningIcon,
        StatusSeverity.Error => ErrorIcon,
        StatusSeverity.Info => InfoIcon,
        _ => null,
    };
}
