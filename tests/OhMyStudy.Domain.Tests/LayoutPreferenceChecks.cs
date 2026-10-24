using OhMyStudy.App.Services;

internal static class LayoutPreferenceChecks
{
    internal static void Run(Action<string, Action> check)
    {
        check("long_equation_breaks_preserve_source", () =>
        {
            const string source = @"f(x_1)+f(x_2)=e^{x_1}+e^{x_2}-\frac12(x_1^2+x_2^2)-a(x_1+x_2)=x_1+a+x_2+a";
            var fragments = FormulaLineBreaks.Split(source);
            Require(fragments.Count > 5 && string.Concat(fragments) == source);
            Require(fragments.Contains(@"-\frac12(x_1^2+x_2^2)"));
        });
        check("formula_breaks_preserve_groups_and_unary_signs", () =>
        {
            foreach (string source in new[] { @"\frac{x+y}{x-y}", @"\left(x+y\right)", @"|x+y|", @"x^{-1}", @"\begin{matrix}x+y&z\end{matrix}" })
                Require(FormulaLineBreaks.Split(source).SequenceEqual([source]));
            Require(FormulaLineBreaks.Split("x=-y+z").SequenceEqual(["x", "=-y", "+z"]));
            Require(FormulaLineBreaks.Split(@"x\leq y\approx z").SequenceEqual(["x", @"\leq y", @"\approx z"]));
        });
        check("window_bounds_roundtrip_and_legacy", () =>
        {
            var restored = new WindowBounds(1180, 840, false);
            var maximized = restored with { Maximized = true };
            Require(WindowBounds.Parse(restored.ToString()) == restored && WindowBounds.Parse(maximized.ToString()) == maximized);
            Require(WindowBounds.Parse("1180 840") == restored && WindowBounds.Parse("max") == WindowBounds.Default with { Maximized = true });
            Require(WindowBounds.Parse("broken") == WindowBounds.Default && WindowBounds.Parse("0 -1") == WindowBounds.Default);
        });
        check("debug_host_keeps_normal_window_preferences", () =>
        {
            Require(!WindowBounds.IsDiagnosticLaunch(["OhMyStudy.App.exe", "--ui-process"]));
            Require(WindowBounds.IsDiagnosticLaunch(["OhMyStudy.App.exe", "--ui-process", "--math-smoke", "report.txt"]));
            Require(WindowBounds.IsDiagnosticLaunch(["OhMyStudy.App.exe", "--workflow-smoke", "report.json"]));
        });
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Layout or window preference regression.");
    }
}
