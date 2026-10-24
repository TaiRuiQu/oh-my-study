using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OhMyStudy.App.Controls;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.App.Services;
using Windows.ApplicationModel.DataTransfer;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private async Task RunNativeFormulaAcceptanceAsync(List<string> checks)
    {
        object? previous = QuestionContentHost.Content;
        DataPackage savedClipboard = new(); var clipboard = Clipboard.GetContent();
        foreach (string format in clipboard.AvailableFormats) savedClipboard.SetData(format, await clipboard.GetDataAsync(format));
        try
        {
            const string source = @"保留原始分隔符 $\frac{x^2+1}{2}$，以及 \(\sqrt{x}\)。";
            var reading = new NativeReadingSession(620, initialTheme: ElementTheme.Light);
            QuestionContentHost.Content = await reading.PresentAsync(source, _lifetime.Token);
            RootGrid.UpdateLayout(); await Task.Delay(100);
            var paragraphs = NativeDescendants(QuestionContentHost).OfType<NativeFormulaParagraph>().ToArray();
            if (paragraphs.Length != 1 || !paragraphs[0].NativeSurfaceReady || !paragraphs[0].BaselineChecksPassed)
                throw new InvalidOperationException("A real mixed paragraph must expose native surface and baseline evidence.");
            var paragraph = paragraphs[0];
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(paragraph);
            if (peer.GetPattern(PatternInterface.Value) is not IValueProvider value || !value.IsReadOnly || value.Value != source)
                throw new InvalidOperationException("Mixed paragraph UIA must preserve the exact original text.");
            var children = peer.GetChildren();
            if (children.Count != 2 || children.Any(child => child.GetPattern(PatternInterface.Value) is not IValueProvider item || !item.IsReadOnly))
                throw new InvalidOperationException("Each formula must expose an individual read-only UIA value.");
            if (children[0].GetName() != @"\frac{x^2+1}{2}" || children[1].GetName() != @"\sqrt{x}")
                throw new InvalidOperationException("Individual formula UIA source does not match its input.");
            checks.Add("native-paragraph-and-formula-uia-source=PASS");
            checks.Add("actual-body-and-formula-line-baselines=PASS");
            foreach (double density in new[] { 1d, 1.25, 1.5, 2d })
            {
                paragraph.RebuildForAcceptance(density);
                if (!paragraph.NativeSurfaceReady || !paragraph.BaselineChecksPassed) throw new InvalidOperationException("Formula paragraph surface-density rebuild failed.");
            }
            checks.Add("native-paragraph-density-100-125-150-200=PASS;monitor-dpi-unchanged");

            // Ctrl+C can only reach this window when it is already the active window.
            // Never activate or topmost the app just to run this functional check: the
            // smoke screenshots must stay usable while the app is in the background.
            // When another window is active, record the keyboard check as skipped
            // instead of claiming a pass; the non-keyboard checks below still run.
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (NativeGetForegroundWindow() == hwnd)
            {
                await Task.Delay(80);
                if (!paragraph.Focus(FocusState.Keyboard)) throw new InvalidOperationException("The owned reading paragraph could not receive keyboard focus.");
                DataPackage sentinel = new(); sentinel.SetText("copy acceptance sentinel"); Clipboard.SetContent(sentinel);
                NativeSendCopy();
                bool copied = false;
                for (int attempt = 0; attempt < 20; attempt++) { await Task.Delay(30); if (await Clipboard.GetContent().GetTextAsync() == source) { copied = true; break; } }
                if (!copied) throw new InvalidOperationException("Control+C did not copy the original complete paragraph.");
                checks.Add("native-paragraph-actual-control-c=PASS");
            }
            else checks.Add("native-paragraph-actual-control-c=SKIPPED-BACKGROUND");
            if (paragraph.ContextFlyout is not MenuFlyout menu || menu.Items.Count != 3) throw new InvalidOperationException("Whole paragraph and individual formula copy actions are missing.");
            menu.ShowAt(paragraph); await Task.Delay(60);
            var itemPeer = FrameworkElementAutomationPeer.CreatePeerForElement(menu.Items[1]);
            if (itemPeer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke) throw new InvalidOperationException("The individual formula copy action is not invokable.");
            invoke.Invoke(); await Task.Delay(50); menu.Hide();
            if (await Clipboard.GetContent().GetTextAsync() != @"\frac{x^2+1}{2}") throw new InvalidOperationException("Individual formula copy did not preserve the full source.");
            checks.Add("native-individual-formula-copy=PASS");

            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool observed = false;
            try { await reading.PresentAsync(source, cancelled.Token); } catch (OperationCanceledException) { observed = true; }
            if (!observed) throw new InvalidOperationException("A cancelled host request must stop before creating controls.");
            checks.Add("native-host-cancellation=PASS");

            string longSource = @"\[" + string.Join("+", Enumerable.Range(1, 100).Select(index => @"\frac{x_{" + index + @"}^2}{" + index + "}")) + @"\]";
            QuestionContentHost.Content = await reading.PresentAsync(longSource, _lifetime.Token); RootGrid.UpdateLayout(); await Task.Delay(80);
            var wrapped = NativeDescendants(QuestionContentHost).OfType<NativeFormulaParagraph>().Single();
            if (wrapped.Width > 620 || wrapped.LineBaselines.Count < 2 || !wrapped.BaselineChecksPassed
                || NativeDescendants(QuestionContentHost).OfType<ScrollViewer>().Any(view => view.ScrollableWidth > 1))
                throw new InvalidOperationException("Long formulas must wrap within the reading width without horizontal scrolling.");
            var wrappedPeer = FrameworkElementAutomationPeer.CreatePeerForElement(wrapped);
            if (wrappedPeer.GetChildren().Single().GetName() != longSource[2..^2]) throw new InvalidOperationException("Wrapped formulas must preserve their full source for accessibility and copying.");
            checks.Add("native-long-formula-wraps-with-full-source=PASS");
            QuestionContentHost.Content = await reading.PresentAsync(@"\[\frac{x^2+1}{2}\]", _lifetime.Token); RootGrid.UpdateLayout(); await Task.Delay(80);
            RootGrid.RequestedTheme = ElementTheme.Dark; await Task.Delay(80);
            var display = NativeDescendants(QuestionContentHost).OfType<VelociTex.WinUI.MathView>().Single();
            double pixelWidth = display.Width * display.XamlRoot.RasterizationScale;
            double pixelHeight = display.Height * display.XamlRoot.RasterizationScale;
            if (Math.Abs(pixelWidth - Math.Round(pixelWidth)) > .001 || Math.Abs(pixelHeight - Math.Round(pixelHeight)) > .001)
                throw new InvalidOperationException("Display formula surfaces must align to whole pixels to avoid resampling.");
            checks.Add("native-display-formula-pixel-alignment=PASS");
            if (display.FormulaColor != VelociTex.Rendering.Direct2D.MathDrawingColor.White) throw new InvalidOperationException("Display formulas must inherit dark theme colors.");
            RootGrid.RequestedTheme = ElementTheme.Light; await Task.Delay(80);
            if (display.FormulaColor != VelociTex.Rendering.Direct2D.MathDrawingColor.Black) throw new InvalidOperationException("Display formulas must inherit light theme colors.");
            checks.Add("native-display-formula-live-theme=PASS");

            // Exercise the same feedback presenter entry without constructing private grading records.
            string feedback = @"解题过程：由 \(x^2=4\)，得到 \(x=\pm2\)。";
            QuestionContentHost.Content = await reading.PresentAsync(feedback, _lifetime.Token); RootGrid.UpdateLayout(); await Task.Delay(60);
            if (!NativeDescendants(QuestionContentHost).OfType<NativeFormulaParagraph>().Any(view => view.NativeSurfaceReady && view.FormulaCount == 2)) throw new InvalidOperationException("Feedback formulas did not use the same native paragraph path.");
            checks.Add("native-feedback-paragraph-path=PASS");

            foreach (int count in new[] { 1, 10, 50, 100 })
            {
                NativeFormulaReadingSession.ClearCaches();
                string batch = string.Join('\n', Enumerable.Range(1, count).Select(index => "第 " + index + @" 个式子：\(\frac{x_{" + index + @"}^2+1}{" + (index + 1) + @"}\)。"));
                var timer = Stopwatch.StartNew();
                var body = await reading.PresentAsync(batch, _lifetime.Token);
                double prepare = timer.Elapsed.TotalMilliseconds;
                QuestionContentHost.Content = body; RootGrid.UpdateLayout(); await Task.Delay(100);
                var current = NativeDescendants(QuestionContentHost).OfType<NativeFormulaParagraph>().ToArray();
                if (current.Length != count || current.Any(view => !view.NativeSurfaceReady || !view.BaselineChecksPassed))
                    throw new InvalidOperationException("A native page workload did not render every formula with verified baselines.");
                using var process = Process.GetCurrentProcess(); process.Refresh();
                checks.Add($"native-page-{count}-prepare-ms={prepare:F3};private-bytes={process.PrivateMemorySize64};managed-bytes={GC.GetTotalMemory(false)};handles={process.HandleCount};cache-items={NativeFormulaReadingSession.CachedFormulaCount};cache-charge={NativeFormulaReadingSession.CachedFormulaBytes}");
            }
            if (NativeFormulaReadingSession.PendingFormulaCount != 0) throw new InvalidOperationException("Host formula queue did not drain.");
            checks.Add("native-page-workloads-and-bounded-cache=PASS");
        }
        finally
        {
            QuestionContentHost.Content = previous;
            try { Clipboard.SetContent(savedClipboard); Clipboard.Flush(); }
            catch (Exception error) { AppLog.Failure("NativeFormulaAcceptance", "The previous clipboard content could not be restored: " + error.Message); }
            RootGrid.UpdateLayout();
        }
    }
    private static IEnumerable<DependencyObject> NativeDescendants(DependencyObject owner)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(owner); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(owner, index); yield return child;
            foreach (var descendant in NativeDescendants(child)) yield return descendant;
        }
    }
    private static unsafe void NativeSendCopy()
    {
        NativeInput* inputs = stackalloc NativeInput[4];
        inputs[0] = new() { Type = 1, Keyboard = new() { VirtualKey = 0x11 } }; inputs[1] = new() { Type = 1, Keyboard = new() { VirtualKey = 0x43 } };
        inputs[2] = new() { Type = 1, Keyboard = new() { VirtualKey = 0x43, Flags = 2 } }; inputs[3] = new() { Type = 1, Keyboard = new() { VirtualKey = 0x11, Flags = 2 } };
        if (NativeSendInput(4, inputs, sizeof(NativeInput)) != 4) throw new InvalidOperationException("Owned native reading copy key was not delivered.");
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct NativeInput { [FieldOffset(0)] internal uint Type; [FieldOffset(8)] internal NativeKeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeKeyboardInput { internal ushort VirtualKey, Scan; internal uint Flags, Time; internal nuint ExtraInfo; }
    [LibraryImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static partial nint NativeGetForegroundWindow();
    [LibraryImport("user32.dll", EntryPoint = "SendInput")] private static unsafe partial uint NativeSendInput(uint count, NativeInput* input, int size);
}
