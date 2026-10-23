using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.App.ViewModels;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Providers;
using OhMyStudy.Infrastructure.Storage;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private async Task RunPapersSmokeAsync(string reportPath)
    {
        string root = Path.Combine(Path.GetDirectoryName(reportPath)!, "papers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); _libraryPointerOverride = Path.Combine(root, "active-library.txt");
        var checks = new List<string>(); int exit = 1;
        try
        {
            _diagnosticRate = new(7.12, "2026-10-20", false);
            Require(await InitializeLibraryAsync(Path.Combine(root, "library")), "isolated-library");
            Navigation.SelectedItem = Navigation.MenuItems[0]; await Task.Delay(150); ShowPaperList();
            await Capture("empty"); Require(PaperEmpty.Visibility == Visibility.Visible, "empty-state");
            var image = await MakeSyntheticPngAsync();
            var document = await _library!.ImportDocumentAsync("数学 · 单元练习", ImportMode.TeacherGraded, new[] { new ImportSource("原件.png", image) });
            await _library.UpdatePaperAsync(document.Id, document.Name, "数学", 86.5, 100);
            await RefreshDocumentsAsync(); ShowPaperList(); await Capture("single");
            Require(_filteredPapers.Count == 1 && _filteredPapers[0].Score == "86.5 / 100", "metadata-persisted");
            await LoadDocumentAsync(document); await Capture("detail");
            Require(PapersPanel.Visibility == Visibility.Collapsed && WorkspaceActions.Visibility == Visibility.Visible && DocumentToolbar.Visibility == Visibility.Visible, "open-paper");
            var paperMenu = (MenuFlyout)PaperMoreButton.Flyout;
            Require(paperMenu.Placement == FlyoutPlacementMode.BottomEdgeAlignedRight, "more-menu-opens-down");
            var expectedMenu = HasSampleAction ? (string[])["管理页面", "整理参考答案", "查看示例", "删除试卷"] : ["管理页面", "整理参考答案", "删除试卷"];
            Require(paperMenu.Items.OfType<MenuFlyoutItem>().Where(item => item.Visibility == Visibility.Visible).Select(item => item.Text)
                .SequenceEqual(expectedMenu), "more-menu-actions");
            paperMenu.ShowAt(PaperMoreButton); await Task.Delay(300); Require(paperMenu.IsOpen, "more-menu-open");
            await CaptureMenu("more-menu"); paperMenu.Hide(); await Task.Delay(220);
            await CheckDeleteMenuStates(paperMenu, PaperMoreButton, "more-menu");
            RootGrid.RequestedTheme = ElementTheme.Dark; await Task.Delay(400);
            paperMenu.ShowAt(PaperMoreButton); await Task.Delay(400);
            if (!paperMenu.IsOpen) paperMenu.ShowAt(PaperMoreButton);
            await Task.Delay(300); Require(paperMenu.IsOpen, "more-menu-dark-open");
            var darkPopup = VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot).Select(popup => popup.Child).OfType<FrameworkElement>().FirstOrDefault(child => child is not ContentDialog);
            Require(darkPopup is not null, "more-menu-dark-presenter");
            await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, ".more-menu-dark-presenter.png"), darkPopup);
            await CaptureMenu("more-menu-dark"); paperMenu.Hide(); await Task.Delay(220);
            await CheckDeleteMenuStates(paperMenu, PaperMoreButton, "more-menu-dark");
            RootGrid.RequestedTheme = ElementTheme.Light; await Task.Delay(220);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 750)); await Capture("detail-medium");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 850)); await Capture("detail-compact");
            RootGrid.RequestedTheme = ElementTheme.Dark; await Capture("detail-dark-compact"); RootGrid.RequestedTheme = ElementTheme.Light; await Task.Delay(220);
            paperMenu.ShowAt(PaperMoreButton); await CaptureMenu("more-menu-compact"); paperMenu.Hide(); await Task.Delay(220);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960)); await Task.Delay(220);
            EditPaperInfo_Click(this, new RoutedEventArgs()); await Task.Delay(250);
            var editor = OpenDialog(); await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, ".edit-info.png"), editor);
            NativeDescendants(editor).OfType<TextBox>().Single(t => t.Header as string == "标题").Text = "";
            var primary = NativeDescendants(editor).OfType<Button>().Single(b => b.Name == "PrimaryButton");
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(primary);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await Task.Delay(180); await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, ".edit-info-error.png"), editor);
            Require(NativeDescendants(editor).OfType<TextBlock>().Any(t => t.Text == "请输入试卷标题。"), "invalid-paper-info-keeps-dialog");
            editor.Hide(); await Task.Delay(100);
            BackToPapers_Click(this, new RoutedEventArgs()); Require(PapersPanel.Visibility == Visibility.Visible, "back-to-list");
            var listMenu = (MenuFlyout)PaperMoreButton.Flyout;
            if (HasSampleAction)
            {
                Require(listMenu.Items.OfType<MenuFlyoutItem>().Where(item => item.Visibility == Visibility.Visible).Select(item => item.Text).SequenceEqual((string[])["查看示例"]), "list-more-menu");
                listMenu.ShowAt(PaperMoreButton); await CaptureMenu("more-menu-list"); listMenu.Hide();
            }
            else Require(PaperMoreButton.Visibility == Visibility.Collapsed, "list-more-hidden");
            var contextMenu = CreatePaperContextMenu(_filteredPapers[0]);
            Require(contextMenu.Items.OfType<MenuFlyoutItem>().Any(item => item.Text == "删除试卷"), "paper-context-menu");
            PaperList.ScrollIntoView(_filteredPapers[0], ScrollIntoViewAlignment.Leading);
            await Task.Delay(200); PaperList.UpdateLayout();
            var firstRow = PaperList.ContainerFromIndex(0) as FrameworkElement;
            Require(firstRow is not null, "paper-row-realized");
            contextMenu.ShowAt(firstRow!, new FlyoutShowOptions { Position = new Windows.Foundation.Point(40, 24) });
            await CaptureMenu("paper-context-menu"); contextMenu.Hide(); await Task.Delay(220);
            await CheckDeleteMenuStates(contextMenu, firstRow!, "paper-context-menu");
            RootGrid.RequestedTheme = ElementTheme.Dark; await Task.Delay(220);
            await CheckDeleteMenuStates(contextMenu, firstRow!, "paper-context-menu-dark");
            RootGrid.RequestedTheme = ElementTheme.Light; await Task.Delay(220);
            var paperRow = NativeDescendants(firstRow!).OfType<FrameworkElement>().FirstOrDefault(element => element.DataContext is PaperViewModel) ?? firstRow!;
            var rowSurface = NativeDescendants(firstRow!).OfType<Grid>().Single(grid => grid.Padding == new Thickness(12, 14, 12, 14));
            var blankPosition = rowSurface.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point(rowSurface.ActualWidth / 2, 2));
            Require(VisualTreeHelper.FindElementsInHostCoordinates(blankPosition, RootGrid).Contains(rowSurface), "paper-row-blank-space-hit-test");
            var requested = new Microsoft.UI.Xaml.Input.ContextRequestedEventArgs();
            PaperRow_ContextRequested(paperRow, requested); await Task.Delay(300);
            Require(requested.Handled && VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot).Any(popup => popup.Child is MenuFlyoutPresenter), "paper-context-request-opens");
            foreach (var openPopup in VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot).ToArray()) openPopup.IsOpen = false;
            await Task.Delay(220);
            // A large presentation fixture exercises virtualized rows without generating user data.
            for (int i = 0; i < 240; i++) _papers.Add(new(new PaperSummary(document with { Id = Guid.NewGuid().ToString(), Name = i == 4 ? "这是一份标题非常长的数学试卷，包含函数、导数、数列、概率以及跨页综合题，需要完整保留标题以便整理归档" : $"{new[] { "数学", "语文", "英语", "物理" }[i % 4]} · 第 {i + 1:000} 次练习", CreatedAt = DateTimeOffset.Now.AddDays(-i).ToString("O") }, i % 7 == 0 ? null : new[] { "数学", "语文", "英语", "物理" }[i % 4], i % 3 == 0 ? null : 70 + i % 30, i % 5 == 0 ? null : 100, i % 24)));
            RefreshPaperSubjects(); ApplyPaperFilters(); WorkspaceNotice.Title = "示例资料"; WorkspaceNotice.Message = "仅供界面检查。"; WorkspaceNotice.IsOpen = true; WorkspaceNotice.Visibility = Visibility.Visible;
            await Capture("many"); RootGrid.RequestedTheme = ElementTheme.Dark; await Capture("dark"); RootGrid.RequestedTheme = ElementTheme.Light;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 750)); await Capture("medium");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 850)); await Capture("compact");
            RootGrid.RequestedTheme = ElementTheme.Dark; await Capture("dark-compact"); RootGrid.RequestedTheme = ElementTheme.Light;
            PaperSearch.Text = "数学"; await Task.Delay(80); Require(_filteredPapers.Count > 1 && _filteredPapers.All(p => p.Title.Contains("数学") || p.Subject == "数学"), "search");
            PaperSubjectFilter.SelectedItem = "物理"; await Capture("no-matches"); Require(_filteredPapers.Count == 0, "filter-combined");
            PaperSearch.Text = ""; await Task.Delay(80); Require(_filteredPapers.All(p => p.Subject == "物理"), "subject-filter");
            PaperSubjectFilter.SelectedItem = "全部学科"; PaperSort.SelectedIndex = 2; await Task.Delay(80);
            Require(_filteredPapers.Select(p => p.Title).SequenceEqual(_filteredPapers.Select(p => p.Title).Order(StringComparer.CurrentCultureIgnoreCase)), "title-sort");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960));
            foreach (int page in new[] { 1, 2, 3 }) { Navigation.SelectedItem = Navigation.MenuItems[page]; await Task.Delay(220); await Capture("page-" + page); Require(WorkspaceActions.Visibility == Visibility.Collapsed, "paper-actions-hidden-" + page); }
            Navigation.SelectedItem = Navigation.SettingsItem; await Task.Delay(220); Require(WorkspaceActions.Visibility == Visibility.Collapsed, "paper-actions-hidden-settings");
            var model = await CheckModelPricingAsync(Require, Capture);
            var candidates = Enumerable.Range(1, 100).Select(i => model with { Id = $"model-{i:000}", DisplayName = i == 100 ? "名称很长的模型——包含视觉、思考和工具调用，应该保持候选列表稳定" : $"示例模型 {i:000}" }).ToList();
            var selection = CreateModelImportDialog(candidates); var shown = selection.Dialog.ShowAsync(); await Task.Delay(250);
            await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, ".model-select.png"), selection.Dialog);
            Require(!selection.Dialog.IsPrimaryButtonEnabled, "no-model-preselected");
            selection.List.SelectedItems.Add(candidates[0]); selection.Search.Text = "100"; await Task.Delay(80); selection.List.SelectedItems.Add(candidates[99]);
            Require(selection.Selected.SetEquals(new[] { candidates[0].Id, candidates[99].Id }), "selection-survives-search");
            await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, ".model-select-filtered.png"), selection.Dialog);
            selection.Search.Text = "no-match"; await Task.Delay(150); Require(selection.List.Items.Count == 0, "model-search-no-results"); await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, ".model-select-empty.png"), selection.Dialog);
            Require(selection.Selected.Count == 2 && selection.Dialog.IsPrimaryButtonEnabled, "hidden-selection-kept"); selection.Dialog.Hide(); await shown;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 850)); await Task.Delay(200);
            RootGrid.RequestedTheme = ElementTheme.Dark; await Task.Delay(200);
            var compactSelection = CreateModelImportDialog(candidates); var compactShown = compactSelection.Dialog.ShowAsync(); await Task.Delay(250);
            compactSelection.Dialog.RequestedTheme = ElementTheme.Dark; await Task.Delay(300);
            await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, ".model-select-compact.png"), compactSelection.Dialog); compactSelection.Dialog.Hide(); await compactShown;
            RootGrid.RequestedTheme = ElementTheme.Light; AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960)); await Task.Delay(180);
            var choice = CreateFixedModelChoice(Enumerable.Range(1, 80).Select(i => i == 80 ? "示例供应商 / 一个非常长的名称，用来验证滚动后也不会改变模型下拉框的宽度与位置" : $"供应商 / 模型 {i}"), 460); choice.SelectedIndex = 0;
            var choiceDialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "导入试卷", Content = choice, CloseButtonText = "取消" }; var choiceShown = choiceDialog.ShowAsync(); await Task.Delay(200);
            choice.IsDropDownOpen = true; await Task.Delay(250);
            var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot)
                .Select(p => p.Child).OfType<FrameworkElement>()
                .FirstOrDefault(child => child is not ContentDialog && FindVisualChild<ScrollViewer>(child) is not null)
                ?? throw new InvalidOperationException("dropdown-popup-not-found"); double initial = popup.ActualWidth;
            await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, ".model-dropdown-start.png"));
            var dropdownScroll = popup as ScrollViewer ?? FindVisualChild<ScrollViewer>(popup) ?? throw new InvalidOperationException("dropdown-scroll-not-found: " + popup.GetType().Name);
            dropdownScroll.ChangeView(null, dropdownScroll.ScrollableHeight, null, true); await Task.Delay(350);
            Require(dropdownScroll.VerticalOffset > 0, "dropdown-actually-scrolled");
            Require(Math.Abs(initial - popup.ActualWidth) < 1 && Math.Abs(choice.ActualWidth - 460) < 1, "dropdown-fixed-width-after-scroll");
            await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, ".model-dropdown-end.png")); choice.IsDropDownOpen = false; choiceDialog.Hide(); await choiceShown;
            await LoadDocumentAsync(document);
            var question = SmokeQuestion("visual-q", "17", [new TextContent { Text = "已知函数 f(x) = x³ − 3x，在区间 [−2, 2] 上讨论函数的最值，并说明解题思路。" }],
                [new Region { PageId = document.Pages[0].Id, X = .1, Y = .1, Width = .8, Height = .4 }], points: 14, answer: "最大值为 2，最小值为 −2。",
                attempt: new Attempt { StudentAnswer = "先求导，再比较端点和驻点的函数值，得到最大值为 2，最小值为 −2。", Grade = new Grade { Verdict = Verdict.Partial, Earned = 10, Possible = 14, Source = GradeSource.Teacher, Reason = "方法正确，部分计算步骤需要补充。" }, Teacher = new TeacherMark { Raw = "第（1）和（3）步各扣 2 分，其余步骤正确。" }, AiSolution = "求导得到 f′(x) = 3x² − 3。令导数为零，得到驻点 x = ±1，分别计算端点和驻点的函数值。" });
            question = question with { Subject = "数学", Uncertainties = ["原卷的部分批注较淡，请核对第（3）步的扣分位置。"] };
            ViewModel.LoadDocument(document, new[] { new SavedQuestion { Id = Guid.NewGuid().ToString(), DocumentId = document.Id, UpdatedAt = DateTimeOffset.Now.ToString("O"), Question = question } });
            await Task.Delay(350); await _questionPresentationTask; await Capture("question-sections");
            QuestionDetail.ChangeView(null, 350, null, true); await Capture("question-feedback");
            RootGrid.RequestedTheme = ElementTheme.Dark; await Capture("question-dark"); RootGrid.RequestedTheme = ElementTheme.Light;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 960)); QuestionDetail.ChangeView(null, 0, null, true); await Capture("question-compact");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960));
            if (HasSampleAction)
            {
                RestoreSample_Click(this, new RoutedEventArgs()); await Capture("sample");
                Require(WorkspaceGrid.Visibility == Visibility.Visible && BackToPapersButton.Visibility == Visibility.Visible && ViewModel.IsSample, "sample-opens-from-paper-list");
                BackToPapers_Click(this, new RoutedEventArgs()); Require(PapersPanel.Visibility == Visibility.Visible, "sample-returns-to-real-list");
            }
            var paperToDelete = _papers.First(paper => paper.Id == document.Id);
            var cancelDeletion = DeletePaperAsync(paperToDelete);
            await Task.Delay(500);
            var cancelDialog = OpenDialog(); await Capture("delete-confirm");
            cancelDialog.Hide(); await cancelDeletion;
            Require((await _library.ListPapersAsync()).Count == 1, "delete-cancel-keeps-paper");
            var deletion = DeletePaperAsync(paperToDelete);
            await Task.Delay(500);
            var confirmDialog = OpenDialog();
            var deleteButton = NativeDescendants(confirmDialog).OfType<Button>().Single(button => button.Name == "PrimaryButton");
            var deletePeer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(deleteButton);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)deletePeer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await deletion;
            Require((await _library.ListPapersAsync()).Count == 0 && _papers.Count == 0 && PaperEmpty.Visibility == Visibility.Visible, "delete-paper");
            await Capture("after-delete");
            exit = 0;
        }
        catch (Exception error) { AppLog.Error("PapersSmoke", error); checks.Add("FAIL " + error); }
        finally
        {
            await File.WriteAllLinesAsync(reportPath, checks.Prepend(exit == 0 ? "PASS" : "FAIL"));
            if (_jobs is not null) await _jobs.DisposeAsync(); _jobs = null;
            if (_library is not null) await _library.DisposeAsync(); _library = null;
            _shutdownFinished = true; Microsoft.UI.Xaml.Application.Current.Exit(); Environment.Exit(exit);
        }
        void Require(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); }
        async Task Capture(string name) { await Task.Delay(500); RootGrid.UpdateLayout(); await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, "." + name + ".png")); }
        async Task CaptureMenu(string name) { await Task.Delay(400); RootGrid.UpdateLayout(); await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, "." + name + ".png")); }
        async Task CheckDeleteMenuStates(MenuFlyout menu, FrameworkElement target, string name)
        {
            menu.ShowAt(target); await Task.Delay(300);
            var item = menu.Items.OfType<MenuFlyoutItem>().Single(action => action.Text == UiText.Get("Delete exam"));
            var expected = RootGrid.ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 255, 180, 171) : Windows.UI.Color.FromArgb(255, 180, 35, 24);
            foreach (string state in new[] { "Normal", "PointerOver", "Pressed" })
            {
                Require(VisualStateManager.GoToState(item, state, false), name + "-" + state + "-state");
                await Task.Delay(80);
                var label = NativeDescendants(item).OfType<TextBlock>().Single(text => text.Name == "TextBlock");
                Require(label.Foreground is SolidColorBrush textBrush && textBrush.Color == expected, name + "-" + state + "-red-text");
                Require(item.Icon.Foreground is SolidColorBrush iconBrush && iconBrush.Color == expected, name + "-" + state + "-red-icon");
                if (state == "PointerOver")
                {
                    var presenter = VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot).Select(popup => popup.Child).OfType<MenuFlyoutPresenter>().Single();
                    await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, "." + name + "-hover.png"), presenter);
                }
            }
            menu.Hide(); await Task.Delay(220);
        }
        ContentDialog OpenDialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot).Select(p => p.Child).OfType<ContentDialog>().Single();
    }
}
