using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Parakeet.App;
using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.App.Views;
using Parakeet.Core.Models;

// The rule: every wrapped description the window draws fits in this many lines — except on the
// Models tab, where the catalogue notes are long on purpose (the maintainer's call, 2026-09-06),
// so that page is not measured at all.
const int MaxLines = 2;

// Skia measures with the embedded typefaces. The default headless host's text shaper is a stub
// whose glyph advances are fiction, so a line count from it would be too; the sanity check below
// refuses to report if that is what it got.
AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var dumpPath = args.Length >= 2 && args[0] == "--dump" ? args[1] : null;
using var dump = dumpPath is null ? null : new StreamWriter(dumpPath, false, new UTF8Encoding(false));
var rows = new List<Row>();

// The window's default width and its minimum: a block that fills its row wraps more at the second,
// and a block with a MaxWidth wraps the same at both.
foreach (var width in new[] { 1080.0, 920.0 })
{
    var directory = Directory.CreateTempSubdirectory("uindosill-measure-").FullName;
    var viewModel = new MainWindowViewModel(
        new FakeEngineProvider(), new LocalModelStore(directory), ModelCatalog.Default, player: new FakeMediaPlayer());
    var window = new MainWindow { DataContext = viewModel, Width = width };
    window.Show();
    Settle(window);

    var tabs = window.FindControl<TabControl>("Tabs")!;
    for (var i = 0; i < tabs.ItemCount; i++)
    {
        viewModel.SelectedTab = i;
        Settle(window);
        var header = (tabs.Items[i] as TabItem)?.Header?.ToString() ?? i.ToString();

        if (header == "Settings")
        {
            var sub = window.FindControl<TabControl>("SettingsSubTabs")!;
            for (var s = 0; s < sub.ItemCount; s++)
            {
                sub.SelectedIndex = s;
                Settle(window);
                Collect(window, $"{header}/{(sub.Items[s] as TabItem)?.Header}", width);
            }
        }
        else if (header == "Models")
        {
            // Exempt: the catalogue notes are the long descriptions the Models tab is for.
            continue;
        }
        else
        {
            Collect(window, header, width);
        }
    }

    window.Close();

    var about = new AboutWindow
    {
        DataContext = new AboutViewModel("1.2.3", @"C:\models", @"C:\settings\settings.json"),
    };
    about.Show();
    Settle(about);
    Collect(about, "About", width);
    about.Close();
}

// Sanity: a proportional face at these sizes averages well under 0.6 em per character; the stub
// shaper would put every glyph at a full em or more. Refuse rather than report fiction.
var measured = rows.Where(r => r.Text.Length >= 40).ToList();
var emPerChar = measured.Average(r => r.TextWidth / r.Text.Length / r.FontSize);
if (emPerChar > 0.8)
{
    Console.Error.WriteLine($"Text was measured at {emPerChar:0.00} em per character, which is the stub shaper, not Skia. Nothing counted.");
    return 2;
}

// One report per page and text; the worst of the two widths is the line count that matters.
var blocks = rows
    .GroupBy(r => (r.Page, r.Text))
    .Select(g => new
    {
        g.Key.Page,
        g.Key.Text,
        First = g.First(),
        Lines = g.Max(r => r.Lines),
        AtDefault = g.Where(r => r.WindowWidth == 1080.0).Select(r => r.Lines).DefaultIfEmpty(0).Max(),
        AtMinimum = g.Where(r => r.WindowWidth == 920.0).Select(r => r.Lines).DefaultIfEmpty(0).Max(),
    })
    .ToList();

var over = blocks.Where(b => b.Lines > MaxLines).ToList();
Console.WriteLine($"{blocks.Count} wrapped blocks across {blocks.Select(b => b.Page).Distinct().Count()} pages (the Models tab exempt), measured at {emPerChar:0.00} em per character; {over.Count} over {MaxLines} lines.");
foreach (var b in over)
{
    Console.WriteLine();
    Console.WriteLine($"[{b.Page}] {b.AtDefault} lines at 1080, {b.AtMinimum} at 920; width {b.First.Width}, size {b.First.FontSize}, classes '{b.First.Classes}'{(b.First.Name is null ? "" : $", name {b.First.Name}")}");
    Console.WriteLine($"    {b.Text.Length} chars: {b.Text}");
}

return Math.Min(over.Count, 100);

void Settle(Window window)
{
    window.UpdateLayout();
    Dispatcher.UIThread.RunJobs();
    window.UpdateLayout();
}

void Collect(Window window, string page, double width)
{
    foreach (var block in window.GetVisualDescendants().OfType<TextBlock>())
    {
        if (block.TextWrapping != TextWrapping.Wrap || string.IsNullOrWhiteSpace(block.Text) || !block.IsEffectivelyVisible)
        {
            continue;
        }

        var layout = block.TextLayout;
        var row = new Row(
            page, width, block.Name, string.Join(" ", block.Classes), block.FontSize,
            Math.Round(block.Bounds.Width, 1), layout.TextLines.Count, Math.Round(layout.Width, 1), block.Text);
        rows.Add(row);
        dump?.WriteLine(JsonSerializer.Serialize(row));
    }
}

internal sealed record Row(
    string Page, double WindowWidth, string? Name, string Classes, double FontSize,
    double Width, int Lines, double TextWidth, string Text);
