namespace Vantage.Lockscreen.Widgets;

static class Program
{
    /// <summary>
    /// The widget host launches this process with -RegisterProcessAsComServer when a Vantage
    /// widget is pinned. With no such argument there is nothing to do: this is a headless COM
    /// server, not an app you open.
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("-RegisterProcessAsComServer")) ComServer.Run(Guid.Parse(WidgetProvider.ClassId));
        else if (args.Contains("--selftest")) return SelfTest();
        return 0;
    }

    /// <summary>
    /// Every template is valid JSON, and every ${field} it shows is in that widget's data.
    /// (A WinExe has no console, so results go to an exit code and a file beside the exe.)
    /// </summary>
    static int SelfTest()
    {
        var problems = new List<string>();
        foreach (var kind in new[] { "Node_Status", "Day_Year_Progress", "Countdown" })
        {
            var data = System.Text.Json.Nodes.JsonNode.Parse(Cards.Data(kind))!.AsObject();
            foreach (var size in new[] { Microsoft.Windows.Widgets.WidgetSize.Small, Microsoft.Windows.Widgets.WidgetSize.Medium })
            {
                var template = Cards.Template(kind, size);
                try { System.Text.Json.Nodes.JsonNode.Parse(template); }
                catch (System.Text.Json.JsonException e) { problems.Add($"{kind}/{size}: template isn't valid JSON ({e.Message})"); continue; }
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(template, @"\$\{(\w+)\}"))
                    if (!data.ContainsKey(m.Groups[1].Value)) problems.Add($"{kind}/{size}: ${{{m.Groups[1].Value}}} has no data");
            }
            problems.AddRange(data.Where(kv => kv.Value is null || kv.Value.ToString().Length == 0).Select(kv => $"{kind}: {kv.Key} is empty"));
        }
        var report = problems.Count == 0 ? "selftest ok" : string.Join(Environment.NewLine, problems);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest.txt"), report + Environment.NewLine +
            string.Join(Environment.NewLine, new[] { "Node_Status", "Day_Year_Progress", "Countdown" }.Select(k => k + ": " + Cards.Data(k))));
        return problems.Count == 0 ? 0 : 1;
    }
}
