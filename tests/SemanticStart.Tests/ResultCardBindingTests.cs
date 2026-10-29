using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SemanticStart.App;
using Xunit;

namespace SemanticStart.Tests;

/// <summary>
/// Checks that every binding in the result card names a property that exists.
///
/// A mistyped binding path is not a build error and not a crash: WPF resolves it at run time,
/// fails, writes a line to the debug output that nobody is watching, and renders nothing. The
/// result is a control that silently shows blank - a copy button with no glyph, a tooltip with no
/// text - which is exactly the kind of fault that survives a compile, a test run and a code review
/// and is only found by opening the window and looking at it.
///
/// This reads the XAML as data instead of rendering it, so it needs no STA thread and no
/// <c>Application</c>; WPF permits one Application per process, which is why the window smoke test
/// has to keep everything inside a single test.
/// </summary>
public sealed class ResultCardBindingTests
{
    [Fact]
    public void EveryBindingInTheResultCardNamesAPropertyOnTheItem()
    {
        var template = ResultCardTemplate();

        var missing = BindingPaths(template)
            .Where(path => typeof(SearchResultItem).GetProperty(path, BindingFlags.Public | BindingFlags.Instance) is null)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"Bindings in the result card name nothing on {nameof(SearchResultItem)}: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Everything between the DataTemplate for a result and its close tag. Read as text rather than
    /// as a parsed tree because binding expressions are attribute strings in WPF's own
    /// mini-language, not XML.
    /// </summary>
    private static string ResultCardTemplate()
    {
        var xaml = File.ReadAllText(OverlayXamlPath());

        const string opening = "<DataTemplate DataType=\"{x:Type local:SearchResultItem}\">";
        var start = xaml.IndexOf(opening, StringComparison.Ordinal);
        Assert.True(start >= 0, "The result DataTemplate was not found in OverlayWindow.xaml.");

        var end = xaml.IndexOf("</ListBox.ItemTemplate>", start, StringComparison.Ordinal);
        Assert.True(end > start, "The result DataTemplate was not closed.");

        // Proves the fragment is well-formed XML, so a truncated or malformed slice cannot quietly
        // produce an empty set of paths and pass everything. The namespaces the file declares on
        // its root element are reattached to the fragment, which no longer has that root.
        var fragment = xaml[start..end];
        _ = XElement.Parse(fragment.Insert(
            "<DataTemplate".Length,
            " xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"" +
            " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"" +
            " xmlns:local=\"clr-namespace:SemanticStart.App\""));

        return fragment;
    }

    /// <summary>
    /// The property names bound inside the fragment. A binding's path is its first positional
    /// argument, so anything after the first comma - Converter, Mode - is not part of it. An empty
    /// path binds the item itself, which is how the task list renders plain strings, and names no
    /// property to check.
    /// </summary>
    private static IEnumerable<string> BindingPaths(string fragment) =>
        Regex.Matches(fragment, @"\{Binding\s*([^,}]*)")
            .Select(match => match.Groups[1].Value.Trim())
            .Select(path => path.StartsWith("Path=", StringComparison.Ordinal) ? path["Path=".Length..] : path)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal);

    private static string OverlayXamlPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SemanticStart.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);

        var path = Path.Combine(directory!.FullName, "src", "SemanticStart.App", "OverlayWindow.xaml");
        Assert.True(File.Exists(path), $"OverlayWindow.xaml was not where it was expected: {path}");
        return path;
    }
}
