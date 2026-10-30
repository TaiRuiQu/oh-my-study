using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace OhMyStudy.App.Controls;

public sealed partial class WorkflowStep : UserControl
{
    public WorkflowStep() => InitializeComponent();

    public string Title { get => StepTitle.Text; set => StepTitle.Text = value; }
    public string Description { get => StepDescription.Text; set => StepDescription.Text = value; }
    public string Glyph { get => StepIcon.Glyph; set => StepIcon.Glyph = value; }
    public UIElement? Fields { get => FieldsHost.Content as UIElement; set => FieldsHost.Content = value; }
    public bool IsLast { get => Connector.Visibility == Visibility.Collapsed; set => Connector.Visibility = value ? Visibility.Collapsed : Visibility.Visible; }

    public void SetStatus(string status, Brush color)
    {
        StepIcon.Foreground = color;
        AutomationProperties.SetName(this, Title + ", " + status + ", " + Description);
    }

    private void StepRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < 680;
        CaptionColumn.Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(Math.Clamp(e.NewSize.Width * .28, 220, 320));
        FieldGap.Width = new GridLength(compact ? 0 : 32);
        Grid.SetColumnSpan(Caption, compact ? 3 : 1);
        Grid.SetRow(FieldsHost, compact ? 1 : 0);
        Grid.SetColumn(FieldsHost, compact ? 2 : 4);
        Grid.SetColumnSpan(FieldsHost, compact ? 3 : 1);
        Caption.Margin = new Thickness(0, 6, 0, compact ? 12 : 24);
        FieldsHost.Margin = new Thickness(0, compact ? 0 : 3, 0, 26);
    }
}
