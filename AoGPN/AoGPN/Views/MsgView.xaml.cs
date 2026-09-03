using System.Windows.Documents;
using System.Windows.Media;

namespace AoGPN.Views;

public partial class MsgView
{
    public MsgView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            this.Bind(ViewModel, vm => vm.MsgFilter, v => v.cmbMsgFilter.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.AutoRefresh, v => v.togAutoRefresh.IsChecked).DisposeWith(disposables);

            ViewModel.DispatcherShowMsgInteraction.RegisterHandler(interaction =>
            {
                var msg = interaction.Input;
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    ShowMsg(msg);
                }, DispatcherPriority.ApplicationIdle);
                interaction.SetOutput(Unit.Default);
            }).DisposeWith(disposables);
        });

        btnCopy.Click += menuMsgViewCopyAll_Click;
        btnClear.Click += menuMsgViewClear_Click;
        menuMsgViewSelectAll.Click += menuMsgViewSelectAll_Click;
        menuMsgViewCopy.Click += menuMsgViewCopy_Click;
        menuMsgViewCopyAll.Click += menuMsgViewCopyAll_Click;
        menuMsgViewClear.Click += menuMsgViewClear_Click;

        cmbMsgFilter.ItemsSource = Global.PresetMsgFilters;
    }

    private int _lines;

    private static readonly Brush BrushDefault = CreateBrush("#E6EDF3");
    private static readonly Brush BrushInfo = CreateBrush("#4FC3F7");
    private static readonly Brush BrushWarn = CreateBrush("#FFB74D");
    private static readonly Brush BrushError = CreateBrush("#EF5350");
    private static readonly Brush BrushDebug = CreateBrush("#9AA4B2");
    private static readonly Brush BrushSystem = CreateBrush("#4CAF50");

    private static Brush CreateBrush(string hex)
    {
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    private void ShowMsg(object msg)
    {
        if (msg is null)
        {
            return;
        }
        var text = msg.ToString();
        if (ViewModel is not null && _lines > ViewModel.NumMaxMsg)
        {
            ClearMsg();
        }

        AppendColored(text);
        _lines += text.Count(ch => ch == '\n');
        if (togScrollToEnd.IsChecked ?? true)
        {
            txtMsg.ScrollToEnd();
        }
    }

    public void ClearMsg()
    {
        txtMsg.Document.Blocks.Clear();
        _lines = 1;
        AppendColored("----- Message cleared -----\n", BrushSystem);
    }

    private void AppendColored(string text)
    {
        AppendColored(text, null);
    }

    private void AppendColored(string text, Brush? overrideBrush)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        foreach (var line in text.Split('\n'))
        {
            var brush = overrideBrush ?? LevelColor(line);
            paragraph.Inlines.Add(new Run(line + "\n") { Foreground = brush });
        }
        txtMsg.Document.Blocks.Add(paragraph);
    }

    private static Brush LevelColor(string line)
    {
        if (line.Contains("çekirdek çalıştırılamadı", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return BrushError;
        }
        if (line.Contains("warning", StringComparison.OrdinalIgnoreCase))
        {
            return BrushWarn;
        }
        if (line.Contains("debug", StringComparison.OrdinalIgnoreCase))
        {
            return BrushDebug;
        }
        if (line.Contains("info", StringComparison.OrdinalIgnoreCase))
        {
            return BrushInfo;
        }
        if (line.StartsWith("-----") || line.Contains("servis", StringComparison.OrdinalIgnoreCase))
        {
            return BrushSystem;
        }
        return BrushDefault;
    }

    private void menuMsgViewSelectAll_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        txtMsg.Focus();
        txtMsg.SelectAll();
    }

    private void menuMsgViewCopy_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var data = new TextRange(txtMsg.Selection.Start, txtMsg.Selection.End).Text.Trim();
        WindowsUtils.SetClipboardData(data);
    }

    private void menuMsgViewCopyAll_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var data = new TextRange(txtMsg.Document.ContentStart, txtMsg.Document.ContentEnd).Text;
        WindowsUtils.SetClipboardData(data);
    }

    private void menuMsgViewClear_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ClearMsg();
    }
}
