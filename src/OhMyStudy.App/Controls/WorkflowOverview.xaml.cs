using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OhMyStudy.App.Controls;

public sealed partial class WorkflowOverview : UserControl
{
    public WorkflowOverview() => InitializeComponent();

    public bool ReferencesOnly
    {
        set
        {
            for (int i = 0; i < OverviewFlow.ColumnDefinitions.Count; i++)
            {
                bool hidden = value && (i < 2 || i >= 5);
                OverviewFlow.ColumnDefinitions[i].Width = hidden ? new GridLength(0) : i % 2 == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(16);
            }
            foreach (var child in OverviewFlow.Children.OfType<FrameworkElement>())
                child.Visibility = value && (Grid.GetColumn(child) < 2 || Grid.GetColumn(child) >= 5) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
