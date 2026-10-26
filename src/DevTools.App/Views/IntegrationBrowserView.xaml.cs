using System.Windows.Controls;
using System.Windows.Input;
using DevTools.Contracts;

namespace DevTools.App.Views;

public partial class IntegrationBrowserView : UserControl
{
    public IntegrationBrowserView() => InitializeComponent();
    private void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ModulePage { Browser: { } browser })
        {
            browser.ClearSearchCommand.Execute(null);
            e.Handled = true;
        }
    }
}
