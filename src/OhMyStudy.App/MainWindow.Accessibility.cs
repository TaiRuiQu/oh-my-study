using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.ViewManagement;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private readonly UISettings _uiSettings = new();

    private void InitializeWorkspaceAccessibility()
    {
        RootGrid.SizeChanged += (_, _) => ApplyWorkspaceLayout();
        AddAccelerator(VirtualKey.O, VirtualKeyModifiers.Control, () => OpenFiles_Click(this, new RoutedEventArgs()));
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control, () => ViewModel.FinishEditCommand.Execute(null));
        AddAccelerator(VirtualKey.E, VirtualKeyModifiers.Control, () => EditQuestion_Click(this, new RoutedEventArgs()));
        AddAccelerator(VirtualKey.Number0, VirtualKeyModifiers.Control, FitPage);
        AddAccelerator(VirtualKey.Number1, VirtualKeyModifiers.Control, () => SelectSection(0));
        AddAccelerator(VirtualKey.Number2, VirtualKeyModifiers.Control, () => SelectSection(1));
        AddAccelerator(VirtualKey.Number3, VirtualKeyModifiers.Control, () => SelectSection(2));
        AddAccelerator(VirtualKey.Number4, VirtualKeyModifiers.Control, () => SelectSection(3));
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            // Native dialogs, input composition and their accelerators keep ownership of focus.
            if (_closed || RootGrid.XamlRoot is null || VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot).Any(p => p.Child is ContentDialog)) return;
            args.Handled = true; action();
        };
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    private void SelectSection(int index)
    {
        Navigation.SelectedItem = Navigation.MenuItems[index];
        if (Navigation.MenuItems[index] is NavigationViewItem item) item.Focus(FocusState.Keyboard);
    }

    private void ApplyWorkspaceLayout()
    {
        bool compact = RootGrid.ActualWidth < 1050;
        WorkspaceColumnsRight.Width = compact ? new GridLength(0) : new GridLength(424);
        WorkspaceQuestionsRow.Height = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(QuestionPane, compact ? 0 : 1);
        Grid.SetRow(QuestionPane, compact ? 1 : 0);
        WorkspaceGrid.RowSpacing = compact ? 12 : 0;
        Navigation.PaneDisplayMode = compact ? NavigationViewPaneDisplayMode.LeftMinimal : NavigationViewPaneDisplayMode.Left;
    }
}
