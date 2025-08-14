using System.Collections.Immutable;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UwpToMaui;

public static class UwpToMauiConverter
{
    // Mappings for XAML file conversions
    internal static readonly Dictionary<string, string> XamlStringReplacements = new()
    {
        { "http://schemas.microsoft.com/winfx/2006/xaml/presentation", "http://schemas.microsoft.com/dotnet/2021/maui" },
        { "http://schemas.microsoft.com/winfx/2006/xaml", "http://schemas.microsoft.com/winfx/2009/xaml" },
        { "using:", "clr-namespace:" }
    };

    internal static readonly Dictionary<string, string> XamlControlReplacements = new()
    {
        { "Page", "ContentPage" },
        { "StackPanel", "StackLayout" },
        { "TextBlock", "Label" },
        { "TextBox", "Entry" },
        { "ListViewItem", "ViewCell" },
        { "ComboBox", "Picker" },
        { "FontIcon", "Label" },
        { "ScrollViewer", "ScrollView" },
    };

    // For renaming XAML attributes
    internal static readonly Dictionary<string, string> XamlPropertyReplacements = new()
    {
        { "Foreground", "TextColor" },
        { "MinHeight", "MinimumHeightRequest" },
        { "MinWidth", "MinimumWidthRequest" },
        { "MaxHeight", "MaximumHeightRequest" },
        { "MaxWidth", "MaximumWidthRequest" },
        { "HorizontalAlignment", "HorizontalOptions" },
        { "VerticalAlignment", "VerticalOptions" },
    };
    internal static readonly Dictionary<string, ImmutableArray<ValueTuple<string, string>>> XamlPropertyConditionalReplacements = new  Dictionary<string, ImmutableArray<ValueTuple<string, string>>>
    {
        { "TextBlock", [("FontWeight", "FontAttributes")] },
        { "FontIcon",
            [
                ("FontWeight", "FontAttributes"),
                ("Glyph", "Text")
            ]
        },
        { "Border", [("BorderThickness", "StrokeThickness"), ("BorderBrush", "Stroke"), ("CornerRadius", "StrokeShape")] },
    };

    // For changing XAML attribute values (e.g., for alignment)
    internal static readonly Dictionary<string, string> XamlValueReplacements = new()
    {
        { "Left", "Start" },
        { "Right", "End" },
        { "Top", "Start" },
        { "Bottom", "End" },
        // Center is the same
        { "Stretch", "Fill" }
    };

    // Mappings for C# file conversions
    internal static readonly Dictionary<string, string> CSharpUsingReplacements = new()
    {
        { "Windows.UI.Xaml.Controls", "Microsoft.Maui.Controls" },
        { "Windows.UI.Xaml.Shapes", "Microsoft.Maui.Controls.Shapes" },
    };

    // For converting base classes
    public static readonly Dictionary<string, string> CSharpClassReplacements = new()
    {
        { "StackPanel", "StackLayout" },
        { "TextBlock", "Label" },
        { "TextBox", "Entry" },
        { "ListViewItem", "ViewCell" },
        { "ComboBox", "Picker" },
        { "FontIcon", "Label" },
        { "ScrollViewer", "ScrollView" },
        { "Panel", "Layout" },
        { "ListViewItem", "ViewCell" },
        { "DependencyObject", "BindableObject" },
        { "RoutedEventArgs", "EventArgs" },
        { "Windows.UI.Color", "Microsoft.Maui.Graphics.Color" },
        { "Windows.UI.Text.FontWeights", "Microsoft.Maui.FontAttributes" },
        { "Orientation", "StackOrientation" },
        { "Thickness", "Microsoft.Maui.Thickness" }
    };


    public static void Main(string[] args)
    {
        Console.WriteLine("UWP to .NET MAUI Converter");
        Console.WriteLine("=========================");

        Console.WriteLine("Enter the full path to the source UWP project folder:");
        string inputPath = "/Users/nitex/dev/SmartCad2D/SmartCad2D";

        Console.WriteLine("Enter the full path for the new .NET MAUI project folder:");
        string outputPath = "/Users/nitex/dev/MAUI_GENERATED";

        if (string.IsNullOrWhiteSpace(inputPath) || !Directory.Exists(inputPath))
        {
            Console.WriteLine("Error: Source path is empty or does not exist.");
            return;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            Console.WriteLine("Error: Output path cannot be empty.");
            return;
        }

        try
        {
            Directory.CreateDirectory(outputPath);
            ConvertProject(inputPath, outputPath);
            Console.WriteLine("\nConversion completed successfully!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nAn unexpected error occurred: {ex.Message}");
        }
    }

    private static void ConvertProject(string inputDir, string outputDir)
    {
        var allFiles = Directory.GetFiles(inputDir, "*.*", SearchOption.AllDirectories);
        Console.WriteLine($"Found {allFiles.Length} files to process...");

        foreach (string filePath in allFiles)
        {
            string relativePath = Path.GetRelativePath(inputDir, filePath);
            string newFilePath = Path.Combine(outputDir, relativePath);
            string newFileDir = Path.GetDirectoryName(newFilePath);

            Directory.CreateDirectory(newFileDir);

            if (filePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Converting XAML: {relativePath}");
                ConvertXamlFile(filePath, newFilePath);
            }
            else if (filePath.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase) || filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Converting C#: {relativePath}");
                ConvertCSharpFile(filePath, newFilePath);
            }
            else
            {
                // Copy other files directly (e.g., images, converters, etc.)
                File.Copy(filePath, newFilePath, true);
            }
        }
    }

    private static void ConvertXamlFile(string sourcePath, string destPath)
    {
        try
        {
            // First, perform text-based replacements for namespaces, which is safer
            // and avoids the XML parser error.
            string content = File.ReadAllText(sourcePath);

            foreach (var replacement in XamlStringReplacements)
            {
                content = content.Replace(replacement.Key, replacement.Value);
            }

            // Now, load the string with corrected namespaces into an XDocument
            XDocument doc = XDocument.Parse(content);
            XNamespace mauiNamespace = "http://schemas.microsoft.com/dotnet/2021/maui";

            foreach (var element in doc.Descendants())
            {
                // 1. Rename Controls
                var prevElemName = element.Name.LocalName;
                if (XamlControlReplacements.TryGetValue(prevElemName, out var newControlName))
                {
                    element.Name = mauiNamespace + newControlName;
                }

                // 2. Rename Attributes (Properties)
                var attributesToRename = element.Attributes()
                    .Where(attr => XamlPropertyReplacements.ContainsKey(attr.Name.LocalName) ||
                    XamlPropertyConditionalReplacements.ContainsKey(prevElemName) && XamlPropertyConditionalReplacements[prevElemName].Any(x => x.Item1 == attr.Name.LocalName))
                    .ToList();

                foreach (var attr in attributesToRename)
                {
                    attr.Remove();
                    string newAttributeName;
                    if (XamlPropertyReplacements.ContainsKey(attr.Name.LocalName))
                        newAttributeName = XamlPropertyReplacements[attr.Name.LocalName];
                    else 
                        newAttributeName = XamlPropertyConditionalReplacements[prevElemName].Where(x => x.Item1 == attr.Name.LocalName).FirstOrDefault().Item2;

                    // 3. Also change attribute value if needed (e.g., for alignment)
                    var newValue = XamlValueReplacements.ContainsKey(attr.Value)
                                    ? XamlValueReplacements[attr.Value]
                                    : attr.Value;

                    if (attr.Name.LocalName == "CornerRadius" && !newValue.Trim().StartsWith('{'))
                    {
                        newValue = "RoundRectangle " + newValue;
                    }

                    element.SetAttributeValue(newAttributeName, newValue);
                }
            }
            if (doc.Root?.Name.LocalName == "ResourceDictionary")
            {
                ResourceDictionaryConverter.Convert(doc, destPath);
            }
            else
            {
                doc.Save(destPath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"-- Could not process XAML file {sourcePath}: {ex.Message}. File was copied without conversion.");
            File.Copy(sourcePath, destPath, true);
        }
    }

    private static void ConvertCSharpFile(string sourcePath, string destPath)
    {
        try
        {
            string sourceCode = File.ReadAllText(sourcePath);
            SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);
            var root = syntaxTree.GetRoot();

            var rewriter = new CSharpConversionRewriter(CSharpUsingReplacements);
            var newRoot = rewriter.Visit(root);

            File.WriteAllText(destPath, newRoot.ToFullString());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"-- Could not process C# file {sourcePath}: {ex.Message}. File was copied without conversion.");
            File.Copy(sourcePath, destPath, true);
        }
    }
}

