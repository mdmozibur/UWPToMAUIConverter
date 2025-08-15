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

    internal static readonly Dictionary<string, string> PointerEventMap = new()
    {
        { "PointerEntered", "PointerEntered" },
        { "PointerExited", "PointerExited" },
        { "PointerPressed", "PointerPressed" },
        { "PointerMoved", "PointerMoved" },
        { "PointerReleased", "PointerReleased" }
        // Add other pointer events if needed, e.g., PointerCanceled
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
        { "DependencyObject", "BindableObject" },
        { "DependencyProperty", "BindableProperty" },
        { "RoutedEventArgs", "EventArgs" },
        { "Windows.UI.Color", "Microsoft.Maui.Graphics.Color" },
        { "Windows.UI.Text.FontWeights", "Microsoft.Maui.FontAttributes" },
        { "Orientation", "StackOrientation" },
        { "Thickness", "Microsoft.Maui.Thickness" }
    };

    public record struct ClassName(string NameSpace, string Class);
    public static List<ClassName> TemplatedControlClasses = new();


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
            XNamespace xNamespace = "http://schemas.microsoft.com/dotnet/2021/maui/xaml";

            foreach (var element in doc.Descendants())
            {
                // 1. Rename Controls
                var prevElemName = element.Name.LocalName;
                if (XamlControlReplacements.TryGetValue(prevElemName, out var newControlName))
                {
                    element.Name = mauiNamespace + newControlName;
                }
                
                var pointerEventAttributes = element.Attributes()
                    .Where(attr => PointerEventMap.ContainsKey(attr.Name.LocalName))
                    .ToList();

                if (false && pointerEventAttributes.Count != 0)
                {
                    // Get or create the <Element.GestureRecognizers> node.
                    var gestureRecognizersNode = element.Element(element.Name + ".GestureRecognizers");
                    if (gestureRecognizersNode == null)
                    {
                        gestureRecognizersNode = new XElement(element.Name + ".GestureRecognizers");
                        element.Add(gestureRecognizersNode);
                    }

                    // Get or create the <PointerGestureRecognizer> node within the collection.
                    var pgrNode = gestureRecognizersNode.Element(mauiNamespace + "PointerGestureRecognizer");
                    if (pgrNode == null)
                    {
                        pgrNode = new XElement(mauiNamespace + "PointerGestureRecognizer");
                        gestureRecognizersNode.Add(pgrNode);
                    }
                    
                    // Ensure the element has an x:Name to be accessible from code-behind.
                    if (element.Attribute(xNamespace + "Name") == null && element.Attribute("Name") != null)
                    {
                        // Promote UWP 'Name' to MAUI 'x:Name'
                        var nameAttr = element.Attribute("Name");
                        if (nameAttr != null)
                        {
                            element.SetAttributeValue(xNamespace + "Name", nameAttr.Value);
                            nameAttr.Remove();
                        }
                    }


                    // Move the event handlers from the element to the PointerGestureRecognizer.
                    foreach (var attr in pointerEventAttributes)
                    {
                        pgrNode.SetAttributeValue(attr.Name.LocalName, attr.Value);
                        attr.Remove(); // Remove the old attribute from the parent element.
                    }
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
                if (sourcePath.EndsWith("Generic.xaml"))
                {
                    var styles = doc.Descendants()
                    .Where(e => e.Name.LocalName == "Style" && e.Attributes().Where(a => a.Name.LocalName == "TargetType").FirstOrDefault() is not null)
                    .Select(e => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "TargetType").Value)
                    .Where(x => x.Contains(':'))
                    .ToArray();

                    var namespace_maps = (doc.FirstNode as XElement)?.Attributes().ToDictionary(attr => attr.Name.LocalName, attr => attr.Value.Replace("clr-namespace:", string.Empty));

                    for (int i = 0; i < styles.Length; i++)
                    {
                        var style_name = styles[i];
                        var colon_index = style_name.IndexOf(':');
                        var namespace_str = style_name[..colon_index];
                        if (!namespace_maps.ContainsKey(namespace_str))
                            continue;
                        var class_name = style_name.Replace(namespace_str, namespace_maps[namespace_str]);
                        TemplatedControlClasses.Add(new (namespace_maps[namespace_str], style_name[(colon_index + 1)..]));
                    }
                }
                ResourceDictionaryConverter.Convert(doc, destPath, false);
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

