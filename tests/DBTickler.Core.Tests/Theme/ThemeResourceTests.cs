using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DBTickler.Core.Tests.Theme;

/// <summary>
/// Guards the desktop application's resource dictionaries.
///
/// These invariants cannot be checked by compiling: XAML resource lookup happens at runtime,
/// and a <c>DynamicResource</c> naming a key that does not exist resolves to nothing at all
/// rather than failing. In a themed application that shows up as an unreadable control —
/// white text on a white background — and only on the theme that is missing the key. This
/// suite catches that on Linux, without WPF, before anyone launches the app.
/// </summary>
public class ThemeResourceTests
{
    private static readonly string AppRoot = LocateAppRoot();

    private static string ThemeFile(string name) => Path.Combine(AppRoot, "Themes", name);

    [Fact]
    public void The_two_palettes_define_exactly_the_same_keys()
    {
        var dark = KeysDefinedIn(ThemeFile("Dark.xaml"));
        var light = KeysDefinedIn(ThemeFile("Light.xaml"));

        var onlyDark = dark.Except(light).Order().ToList();
        var onlyLight = light.Except(dark).Order().ToList();

        Assert.True(
            onlyDark.Count == 0 && onlyLight.Count == 0,
            $"Palette keys have drifted apart. Only in Dark: [{string.Join(", ", onlyDark)}]. " +
            $"Only in Light: [{string.Join(", ", onlyLight)}]. Every key must exist in both, or " +
            "controls bound to the missing one render with no brush in that theme.");

        Assert.NotEmpty(dark);
    }

    [Fact]
    public void Every_palette_entry_is_a_brush_with_a_parseable_colour()
    {
        foreach (var file in new[] { ThemeFile("Dark.xaml"), ThemeFile("Light.xaml") })
        {
            var document = XDocument.Load(file);
            var brushes = document.Root!.Elements()
                .Where(element => element.Name.LocalName == "SolidColorBrush")
                .ToList();

            Assert.NotEmpty(brushes);

            foreach (var brush in brushes)
            {
                var key = brush.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value;
                var colour = brush.Attribute("Color")?.Value;

                Assert.False(string.IsNullOrWhiteSpace(key), $"A brush in {Path.GetFileName(file)} has no key.");
                Assert.True(
                    colour is not null && Regex.IsMatch(colour, "^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$"),
                    $"{key} in {Path.GetFileName(file)} has colour '{colour}', which is not a hex value.");
            }
        }
    }

    [Fact]
    public void Every_resource_reference_in_every_view_resolves()
    {
        var themeKeys = new[] { "Dark.xaml", "Light.xaml", "Controls.xaml" }
            .SelectMany(name => KeysDefinedIn(ThemeFile(name)))
            .ToHashSet(StringComparer.Ordinal);

        var unresolved = new List<string>();

        foreach (var file in Directory.EnumerateFiles(AppRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            // A file may also declare its own resources — converters, for instance.
            var available = new HashSet<string>(themeKeys, StringComparer.Ordinal);
            available.UnionWith(KeysDefinedInText(text));

            foreach (Match match in Regex.Matches(text, @"\{(?:Dynamic|Static)Resource\s+([A-Za-z0-9_]+)\}"))
            {
                var key = match.Groups[1].Value;
                if (!available.Contains(key))
                    unresolved.Add($"{Path.GetFileName(file)} → {key}");
            }
        }

        Assert.True(
            unresolved.Count == 0,
            "These resource keys are referenced but never defined, so they resolve to nothing " +
            "at runtime:\n  " + string.Join("\n  ", unresolved.Distinct().Order()));
    }

    [Fact]
    public void The_ListView_template_keeps_the_GridView_column_headers()
    {
        // A ListView whose template hosts a plain ScrollViewer renders its rows but silently
        // drops every column header, because the header presenter lives in the keyed GridView
        // scroll-viewer style rather than in the ListView template.
        var controls = File.ReadAllText(ThemeFile("Controls.xaml"));

        Assert.Contains("GridView.GridViewScrollViewerStyleKey", controls, StringComparison.Ordinal);
        Assert.Contains("GridViewRowPresenter", controls, StringComparison.Ordinal);
    }

    [Theory]
    // The controls whose stock templates hard-code light-theme chrome. If any of these loses
    // its template, that control goes back to being unreadable in dark mode.
    [InlineData("ComboBox")]
    [InlineData("ComboBoxItem")]
    [InlineData("ListBoxItem")]
    [InlineData("ListViewItem")]
    [InlineData("GridViewColumnHeader")]
    [InlineData("ScrollBar")]
    [InlineData("CheckBox")]
    [InlineData("Slider")]
    [InlineData("ToolTip")]
    [InlineData("TextBox")]
    [InlineData("PasswordBox")]
    public void Control_is_fully_retemplated_rather_than_only_recoloured(string controlName)
    {
        var document = XDocument.Load(ThemeFile("Controls.xaml"));

        var style = document.Root!.Elements()
            .Where(element => element.Name.LocalName == "Style")
            .FirstOrDefault(element =>
                element.Attribute("TargetType")?.Value == controlName &&
                element.Attributes().All(a => a.Name.LocalName != "Key"));

        Assert.True(style is not null, $"No implicit style found for {controlName}.");

        var setsTemplate = style!.Elements()
            .Any(setter => setter.Name.LocalName == "Setter" &&
                           setter.Attribute("Property")?.Value == "Template");

        Assert.True(setsTemplate, $"The {controlName} style does not set a Template, so WPF's stock " +
                                  "template — which paints itself for a light theme — is still in use.");
    }

    private static IEnumerable<string> KeysDefinedIn(string file) => KeysDefinedInText(File.ReadAllText(file));

    private static IEnumerable<string> KeysDefinedInText(string text) =>
        Regex.Matches(text, @"x:Key=""([^""]+)""").Select(match => match.Groups[1].Value);

    /// <summary>Walks up from the test binaries until the solution file identifies the repo root.</summary>
    private static string LocateAppRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DBTickler.slnx")))
            directory = directory.Parent;

        Assert.True(directory is not null, "Could not locate the repository root from the test output directory.");

        var appRoot = Path.Combine(directory!.FullName, "src", "DBTickler.App");
        Assert.True(Directory.Exists(appRoot), $"Expected the desktop project at {appRoot}.");
        return appRoot;
    }
}
