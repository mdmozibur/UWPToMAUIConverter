using System.Collections.Immutable;
using System.Xml.Linq;

namespace UwpToMaui;

public class XamlConversionWriter(XDocument doc)
{
    public static readonly XNamespace MAUI_NAMESPACE = "http://schemas.microsoft.com/dotnet/2021/maui";
    public static readonly XNamespace X_NAMESPACE = "http://schemas.microsoft.com/dotnet/2021/maui/xaml";
    public static readonly XNamespace X_WINFX_NAMESPACE = "http://schemas.microsoft.com/winfx/2009/xaml";

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
        { "StackPanel.Resources", "StackLayout.Resources" },
        { "TextBlock", "Label" },
        { "TextBox", "Entry" },
        { "ListViewItem", "ViewCell" },
        { "ComboBox", "Picker" },
        { "FontIcon", "Label" },
        { "ScrollViewer", "ScrollView" },
    };

    internal static readonly HashSet<string> PointerEventMap = new()
    {
        { "PointerEntered" },
        { "PointerExited" },
        { "PointerPressed" },
        { "PointerMoved" },
        { "PointerReleased" }
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
        { "Visibility", "IsVisible" },
    };
    internal static readonly Dictionary<string, ImmutableArray<ValueTuple<string, string>>> XamlPropertyConditionalReplacements = new Dictionary<string, ImmutableArray<ValueTuple<string, string>>>
    {
        { "TextBlock",
            [
                ("FontWeight", "FontAttributes"),
                ("TextAlignment", "HorizontalTextAlignment"),
            ] },
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
    public record struct ClassName(string NameSpace, string Class);
    public static List<ClassName> TemplatedControlClasses = new();
    internal static readonly Dictionary<string, List<string>> RadioButtonCheckedHandlers = new();

    public XDocument Document { get; } = doc;
    private readonly Dictionary<XElement, XElement> PointerGestureRecognizerNodes = new Dictionary<XElement, XElement>();

    public void BeginProcessing()
    {
        var fullClassName = Document.Root.Attributes().Where(a => a.Name.LocalName == "Class").FirstOrDefault()?.Value;
        foreach (var element in doc.Descendants())
        {
            // 1. Rename Controls
            var prevElemName = element.Name.LocalName;

            if (prevElemName == "Button") HandleButton(element);

            if (XamlControlReplacements.TryGetValue(prevElemName, out var newControlName))
            {
                element.Name = MAUI_NAMESPACE + newControlName;
            }

            HandlePointerEvents(element);

            // 2. Rename Attributes (Properties)
            var attributesToRename = element.Attributes()
                .Where(attr => XamlPropertyReplacements.ContainsKey(attr.Name.LocalName) ||
                XamlPropertyConditionalReplacements.ContainsKey(prevElemName) && XamlPropertyConditionalReplacements[prevElemName].Any(x => x.Item1 == attr.Name.LocalName))
                .ToList();

            foreach (var attr in attributesToRename)
            {
                attr.Remove();
                string newAttributeName;
                if (XamlPropertyReplacements.TryGetValue(attr.Name.LocalName, out string? value))
                    newAttributeName = value;
                else
                    newAttributeName = XamlPropertyConditionalReplacements[prevElemName].Where(x => x.Item1 == attr.Name.LocalName).FirstOrDefault().Item2;

                // 3. Also change attribute value if needed (e.g., for alignment)
                var newValue = XamlValueReplacements.TryGetValue(attr.Value, out value) ? value : attr.Value;

                if (attr.Name.LocalName == "CornerRadius" && !newValue.Trim().StartsWith('{'))
                {
                    newValue = "RoundRectangle " + newValue;
                }

                element.SetAttributeValue(newAttributeName, newValue);
            }

            List<string> radioCheckedHandlers = [];
            foreach (var attr in element.Attributes())
            {
                // New logic for RadioButton Checked event
                if (element.Name.LocalName == "RadioButton" && attr.Name.LocalName == "Checked")
                {
                    radioCheckedHandlers.Add(attr.Value);
                    attr.Remove();
                    element.SetAttributeValue("CheckedChanged", attr.Value);
                }
            }
            if(radioCheckedHandlers.Count > 0 )
            {
                if (!RadioButtonCheckedHandlers.ContainsKey(fullClassName))
                    RadioButtonCheckedHandlers[fullClassName] = new List<string>();
                RadioButtonCheckedHandlers[fullClassName].AddRange(radioCheckedHandlers);
            }
        }

        foreach (var node in PointerGestureRecognizerNodes.Keys)
            node.Add(PointerGestureRecognizerNodes[node]);
    }

    private void HandleButton(XElement element)
    {
        var iconElement = element.Elements()
                        .FirstOrDefault(e => e.Name.LocalName == "FontIcon" || e.Name.LocalName == "SymbolIcon");

        if (iconElement != null)
        {
            // Transfer properties from the icon to the button itself.
            var fontFamily = iconElement.Attribute("FontFamily")?.Value;
            var fontSize = iconElement.Attribute("FontSize")?.Value;
            var glyph = iconElement.Attribute("Glyph")?.Value;

            if (!string.IsNullOrEmpty(fontFamily))
            {
                element.SetAttributeValue("FontFamily", fontFamily);
            }
            if (!string.IsNullOrEmpty(fontSize))
            {
                element.SetAttributeValue("FontSize", fontSize);
            }
            if (!string.IsNullOrEmpty(glyph))
            {
                // MAUI uses the 'Text' property for the icon glyph on a Button.
                element.SetAttributeValue("Text", glyph);
            }

            // Remove the original icon element as MAUI Buttons cannot have complex content.
            iconElement.Remove();
        }
    }

    private void HandlePointerEvents(XElement element)
    {
        var pointerEventAttributes = element.Attributes()
            .Where(attr => PointerEventMap.Contains(attr.Name.LocalName))
            .ToList();

        if (pointerEventAttributes.Count != 0)
        {
            // Get or create the <Element.GestureRecognizers> node.
            var gestureRecognizersNode = element.Element(element.Name + ".GestureRecognizers");
            if (gestureRecognizersNode == null)
            {
                gestureRecognizersNode = new XElement(element.Name + ".GestureRecognizers");
                PointerGestureRecognizerNodes[element] = gestureRecognizersNode;
            }

            // Get or create the <PointerGestureRecognizer> node within the collection.
            var pgrNode = gestureRecognizersNode.Element(MAUI_NAMESPACE + "PointerGestureRecognizer");
            if (pgrNode == null)
            {
                pgrNode = new XElement(MAUI_NAMESPACE + "PointerGestureRecognizer");
                gestureRecognizersNode.Add(pgrNode);
            }

            // Ensure the element has an x:Name to be accessible from code-behind.
            if (element.Attribute(X_NAMESPACE + "Name") == null && element.Attribute("Name") != null)
            {
                // Promote UWP 'Name' to MAUI 'x:Name'
                var nameAttr = element.Attribute("Name");
                if (nameAttr != null)
                {
                    element.SetAttributeValue(X_NAMESPACE + "Name", nameAttr.Value);
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
    }

    public void SaveTemplatedControlClasses()
    {
        var styles = Document.Descendants()
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
            TemplatedControlClasses.Add(new(namespace_maps[namespace_str], style_name[(colon_index + 1)..]));
        }
    }
}