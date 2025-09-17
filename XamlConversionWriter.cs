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
        { "ScrollSensingListView", "CollectionView" },
        { "ScrollSensingListView.ItemTemplateSelector", "CollectionView.ItemTemplate" },
        { "ScrollSensingListView.ItemContainerTransitions", "CollectionView.ItemContainerTransitions" },
        { "StackPanel", "StackLayout" },
        { "StackPanel.Resources", "StackLayout.Resources" },
        { "TextBlock", "Label" },
        { "TextBox", "Entry" },
        { "ToggleSwitch", "Switch" },
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
        { "FontWeight", "FontAttributes"},
    };
    internal static readonly Dictionary<string, ImmutableArray<ValueTuple<string, string>>> XamlPropertyConditionalReplacements = new Dictionary<string, ImmutableArray<ValueTuple<string, string>>>
    {
        { "TextBlock",
            [
                ("TextAlignment", "HorizontalTextAlignment"),
                ("TextWrapping", "LineBreakMode" ),
            ] },
        { "FontIcon",
            [
                ("Glyph", "Text")
            ]
        },
        { "Border", [("BorderThickness", "StrokeThickness"), ("BorderBrush", "Stroke"), ("CornerRadius", "StrokeShape")] },
        { "Button", [("Click", "Clicked")] },
        { "CheckBox", [("Checked", "CheckedChanged")] },
    };

    // For changing XAML attribute values (e.g., for alignment)
    internal static readonly Dictionary<string, string> XamlValueReplacements = new()
    {
        { "Left", "Start" },
        { "Right", "End" },
        { "Top", "Start" },
        { "Bottom", "End" },
        { "Stretch", "Fill" },
        { "Collapsed", "False" },
        { "Visible", "True" },
        { "ExtraBold", "Bold" },
        { "SemiBold", "Bold" },
    };
    public record struct ClassName(string NameSpace, string Class);
    public static List<ClassName> TemplatedControlClasses = new();
    internal static readonly Dictionary<string, List<string>> RadioButtonCheckedHandlers = new();

    public XDocument Document { get; } = doc;
    private readonly Dictionary<XElement, XElement> PointerGestureRecognizerNodes = new Dictionary<XElement, XElement>();

    public void BeginProcessing()
    {
        var descendents = Document.Descendants().ToList();
        foreach (var element in descendents)
        {
            ProcessRecursive(element);
        }

        foreach (var node in PointerGestureRecognizerNodes.Keys)
            node.Add(PointerGestureRecognizerNodes[node]);
    }

    private void ProcessRecursive(XElement element)
    {
        // 1. Rename Controls
        var prevElemName = element.Name.LocalName;
        var attributes = element.Attributes().ToList();

        if (prevElemName == "Button")
            HandleButton(element);

        else if (element.Name.LocalName == "TextBlock" && element.Nodes().OfType<XElement>().Any())
        {
            HandleLabel(element);
            element.ReplaceAttributes(attributes);
        }
        else if (element.Name.LocalName == "CheckBox")
        {
            HandleCheckBox(element);
            foreach(var descendents in element.Descendants())
                ProcessRecursive(descendents);
        }

        if (XamlControlReplacements.TryGetValue(prevElemName, out var newControlName))
        {
            element.Name = MAUI_NAMESPACE + newControlName;
        }

        if (prevElemName.EndsWith(".ItemContainerTransitions"))
        {
            element.Remove();
            return; // Element fully removed.
        }

        if (prevElemName == "Style" && element.Attributes().Where(a => a.Name.LocalName == "TargetType" && a.Value == "ListViewItem").FirstOrDefault() is not null)
        {
            element.Remove();
            return;
        }

        HandlePointerEvents(element);

        // 2. Rename Attributes (Properties)
        foreach (var attr in attributes)
        {
            string currentName = attr.Name.LocalName;
            string currentValue = attr.Value;

            // Case 1: Visibility="Collapsed" -> IsVisible="False"
            if (currentName == "Visibility")
            {
                string isVisibleValue = (currentValue == "Collapsed") ? "False" : "True";
                element.SetAttributeValue("IsVisible", isVisibleValue);
                attr.Remove();
                continue; // Attribute fully converted.
            }

            // Case 2: SelectionMode="Extended" -> SelectionMode="Multiple"
            else if (currentName == "SelectionMode" && currentValue == "Extended")
            {
                attr.Value = "Multiple";
                currentValue = "Multiple"; // Update for subsequent logic
            }

            string newAttributeName = null;
            string finalValue = currentValue;

            // Case 3: General Property Renaming (e.g., Foreground -> TextColor)
            if (XamlPropertyReplacements.TryGetValue(currentName, out var renamedProp))
            {
                newAttributeName = renamedProp;
            }
            else if (XamlPropertyConditionalReplacements.TryGetValue(prevElemName, out var conditionalProps) &&
                        conditionalProps.Any(x => x.Item1 == currentName))
            {
                newAttributeName = conditionalProps.First(x => x.Item1 == currentName).Item2;
                if(newAttributeName == "LineBreakMode" && currentValue == "Wrap")
                    finalValue = "WordWrap";
            }

            // Case 4: General Value Replacements (e.g., Stretch -> Fill)
            if (XamlValueReplacements.TryGetValue(currentValue, out var replacedValue))
            {
                finalValue = replacedValue;
            }
            else if (currentValue.Contains("{ThemeResource"))
            {
                finalValue = currentValue.Replace("ThemeResource", "StaticResource");
            }

            // Special case for CornerRadius from existing code
            if (currentName == "CornerRadius" && !finalValue.Trim().StartsWith('{'))
            {
                finalValue = "RoundRectangle " + finalValue;
            }

            // Apply changes to the XML
            if (newAttributeName != null)
            {
                attr.Remove();
                element.SetAttributeValue(newAttributeName, finalValue);
            }
            else if (finalValue != currentValue)
            {
                attr.Value = finalValue;
            }
        }

        List<string> radioCheckedHandlers = [];
        if (element.Name.LocalName == "RadioButton")
            foreach (var attr in element.Attributes())
            {
                // New logic for RadioButton Checked event
                if (attr.Name.LocalName == "Checked")
                {
                    radioCheckedHandlers.Add(attr.Value);
                    attr.Remove();
                    element.SetAttributeValue("CheckedChanged", attr.Value);
                }
            }
        if (radioCheckedHandlers.Count > 0)
        {
            var fullClassName = Document.Root.Attributes().Where(a => a.Name.LocalName == "Class").FirstOrDefault()?.Value;
            if (!RadioButtonCheckedHandlers.ContainsKey(fullClassName))
            RadioButtonCheckedHandlers[fullClassName] = new List<string>();
            RadioButtonCheckedHandlers[fullClassName].AddRange(radioCheckedHandlers);
        }
    }
    private void HandleLabel(XElement element)
    {
        var formattedText = new XElement("Label.FormattedText");
        var formattedString = new XElement(MAUI_NAMESPACE + "FormattedString");
        formattedText.Add(formattedString);

        foreach (var node in element.Nodes())
        {
            if (node is XText textNode)
            {
                // Convert plain text into a Span
                formattedString.Add(new XElement(MAUI_NAMESPACE + "Span", new XAttribute("Text", textNode.Value)));
            }
            else if (node is XElement childElement)
            {
                XElement span = null;
                switch (childElement.Name.LocalName)
                {
                    case "Run":
                        span = new XElement(MAUI_NAMESPACE + "Span", new XAttribute("Text", childElement.Value));
                        // Map properties like Foreground, FontWeight, etc.
                        foreach (var attr in childElement.Attributes())
                        {
                            if (XamlPropertyReplacements.TryGetValue(attr.Name.LocalName, out var newProp))
                            {
                                if (newProp == "TextColor" && attr.Value.Contains("{ThemeResource"))
                                    attr.Value = attr.Value.Replace("ThemeResource", "StaticResource");
                                span.SetAttributeValue(newProp, attr.Value);
                            }
                            else if (attr.Name.LocalName == "FontWeight") // Special case
                                span.SetAttributeValue("FontAttributes", attr.Value);
                            else
                                span.SetAttributeValue(attr.Name, attr.Value);
                        }
                        break;

                    case "LineBreak":
                        span = new XElement(MAUI_NAMESPACE + "Span", new XAttribute("Text", "\n"));
                        break;

                    case "Hyperlink":
                        span = new XElement(MAUI_NAMESPACE + "Span",
                            new XAttribute("Text", childElement.Value),
                            new XAttribute("TextColor", "Blue"),
                            new XAttribute("TextDecorations", "Underline"));

                        var gestureRecognizers = new XElement(span.Name + ".GestureRecognizers");
                        var tapGesture = new XElement(MAUI_NAMESPACE + "TapGestureRecognizer",
                            new XAttribute("Command", "{Binding OpenUrlCommand}")); // A conventional command name

                        var uri = childElement.Attribute("NavigateUri")?.Value;
                        if (uri != null)
                        {
                            tapGesture.SetAttributeValue("CommandParameter", uri);
                        }
                        gestureRecognizers.Add(tapGesture);
                        span.Add(gestureRecognizers);
                        break;
                }
                if (span != null)
                {
                    formattedString.Add(span);
                }
            }
        }

        // Now, replace the old TextBlock with the new Label structure
        element.Name = MAUI_NAMESPACE + "Label";
        element.RemoveNodes();
        element.RemoveAttributes(); // Remove old attributes like TextWrapping
        element.Add(formattedText);
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

    private void HandleCheckBox(XElement element)
    {
        var hStack = new XElement(MAUI_NAMESPACE + "HorizontalStackLayout");
        var newCheckBox = new XElement(MAUI_NAMESPACE + "CheckBox");
        var newLabel = new XElement(MAUI_NAMESPACE + "Label");

        string checkedHandler = null;
        var textProperties = new HashSet<string> { "Content", "FontSize", "FontFamily", "FontWeight", "FontStyle", "Foreground" };
        var layoutProperties = new HashSet<string> { "Grid.Column", "Grid.Row", "Grid.ColumnSpan", "Grid.RowSpan", "HorizontalAlignment", "VerticalAlignment", "Margin" };

        foreach (var attr in element.Attributes())
        {
            string attrName = attr.Name.ToString();
            string attrValue = attr.Value;

            if (layoutProperties.Contains(attrName))
            {
                // Move layout properties to the parent HorizontalStackLayout
                hStack.SetAttributeValue(attr.Name, attr.Value);
            }
            else if (textProperties.Contains(attrName))
            {
                // Move text properties to the Label
                if (attrName == "Content")
                    newLabel.SetAttributeValue("Text", attrValue);
                else
                    newLabel.SetAttributeValue(attr.Name, attrValue);
            }
            else if (attrName == "Checked" || attrName == "Unchecked")
                {
                    // Consolidate Checked/Unchecked events into CheckedChanged
                    if (!string.IsNullOrEmpty(attrValue))
                        checkedHandler = attrValue;
                }
            else
            {
                newCheckBox.SetAttributeValue(attr.Name, attrValue);
            }
        }

        if (checkedHandler != null)
        {
            newCheckBox.SetAttributeValue("CheckedChanged", checkedHandler);
        }

        hStack.Add(newCheckBox);
        hStack.Add(newLabel);

        // Replace the original CheckBox with our new layout
        element.ReplaceWith(hStack);
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