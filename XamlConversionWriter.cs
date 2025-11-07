using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.RegularExpressions;
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
        { "StackPanel", "StackLayout" },
        { "StackPanel.Resources", "StackLayout.Resources" },
        { "TextBlock", "Label" },
        { "TextBox", "Entry" },
        // { "ToggleSwitch", "Switch" },
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
        { "TextWrapping", "LineBreakMode"},
        { "TextAlignment", "HorizontalTextAlignment"},
        { "PlaceholderText", "Placeholder"},
        { "Glyph", "Text"},
        { "IsOn", "IsToggled"},
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
        { "Wrap", "WordWrap" },
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

    /// <summary>
    /// Processes a given attribute value, applying MAUI conversions.
    /// </summary>
    private string ProcessAttributeValue(string currentValue)
    {
        if (currentValue.Contains("{x:Bind"))
        {
            currentValue = currentValue.Replace("{x:Bind", "{Binding");
        }

        if (currentValue.StartsWith("#") && currentValue.Length == 5 && Regex.IsMatch(currentValue, @"^#[0-9a-fA-F]{4}$"))
        {
            // Converts #ARGB to #AARRGGBB
            char a = currentValue[1];
            char r = currentValue[2];
            char g = currentValue[3];
            char b = currentValue[4];
            return $"#{a}{a}{r}{r}{g}{g}{b}{b}";
        }
        
        // General value replacements (e.g., Collapsed -> False)
        if (XamlValueReplacements.TryGetValue(currentValue, out var replacedValue))
        {
            return replacedValue;
        }

        if (currentValue.Contains("{ThemeResource"))
        {
            return currentValue.Replace("ThemeResource", "StaticResource");
        }

        return currentValue;
    }
    
    /// <summary>
    /// Processes all attributes of a given XElement, renaming and converting them for MAUI.
    /// </summary>
    private void ProcessAttributes(XElement element, string originalElementName)
    {
        var attributes = element.Attributes().ToList();
        // var originalElementName = element.Name.LocalName;

        foreach (var attr in attributes)
        {
            string currentName = attr.Name.LocalName;
            string currentValue = attr.Value;

            if (originalElementName == "Grid" || originalElementName == "StackPanel")
            {
                // UWP's Grid and StackPanel had border properties which are not supported
                // in their MAUI equivalents. These are removed.
                element.Attribute("BorderBrush")?.Remove();
                element.Attribute("BorderThickness")?.Remove();
                element.Attribute("CornerRadius")?.Remove();
            }

            if (currentValue.Trim().StartsWith("{Binding") && currentValue.Contains("ElementName="))
            {
                var elementNameMatch = Regex.Match(currentValue, @"ElementName\s*=\s*([^,}\s]+)");
                if (elementNameMatch.Success)
                {
                    var elementName = elementNameMatch.Groups[1].Value;

                    // Add BindingContext="{x:Reference ...}" if it doesn't already exist
                    if (element.Attribute("BindingContext") == null)
                    {
                        element.SetAttributeValue("BindingContext", $"{{x:Reference Name={elementName}}}");
                    }

                    // Remove the ElementName part from the original binding string
                    string newBindingValue = Regex.Replace(currentValue, @"ElementName\s*=\s*[^,}\s]+\s*,?\s*", "");

                    // Clean up the binding string if it now starts with a comma, e.g., "{Binding , Path=...}"
                    newBindingValue = Regex.Replace(newBindingValue, @"({\s*Binding)\s*,", "$1 ");

                    // If the binding is now empty (was only ElementName), default to a simple context binding
                    if (Regex.IsMatch(newBindingValue, @"^{\s*Binding\s*}$"))
                    {
                        newBindingValue = "{Binding}";
                    }

                    // Update the attribute value for further processing
                    attr.Value = newBindingValue;
                    currentValue = newBindingValue;
                }
            }
            
            if (currentName == "SelectionMode" && currentValue == "Extended")
            {
                attr.Value = "Multiple";
                currentValue = "Multiple";
            }

            string newAttributeName = null;
            string finalValue = ProcessAttributeValue(currentValue);

            // General property renaming (e.g., Foreground -> TextColor)
            if (XamlPropertyReplacements.TryGetValue(currentName, out var renamedProp))
            {
                newAttributeName = renamedProp;
            }
            // Conditional property renaming (e.g., TextBlock.TextWrapping -> Label.LineBreakMode)
            else if (XamlPropertyConditionalReplacements.TryGetValue(originalElementName, out var conditionalProps) &&
                     conditionalProps.Any(x => x.Item1 == currentName))
            {
                newAttributeName = conditionalProps.First(x => x.Item1 == currentName).Item2;
                if (newAttributeName == "LineBreakMode" && currentValue == "Wrap")
                    finalValue = "WordWrap";
            }

            // Special case for CornerRadius to StrokeShape on Border
            if (originalElementName == "Border" && currentName == "CornerRadius" && !finalValue.Trim().StartsWith('{'))
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
    }

    private void ProcessRecursive(XElement element)
    {
        var prevElemName = element.Name.LocalName;
        
        if (prevElemName == "Button")
            HandleButton(element);
        else if (element.Name.LocalName == "TextBlock" && element.Nodes().OfType<XElement>().Any())
        {
            HandleLabel(element);
        }
        else if (element.Name.LocalName == "CheckBox")
        {
            HandleCheckBox(element);
            foreach (var descendent in element.Descendants())
                ProcessRecursive(descendent);
        }
        else if (prevElemName == "ListView" || prevElemName == "GridView")
        {
            // This now handles its own attribute processing internally
            HandleListView(element);
            // After HandleListView, the original element is replaced, so we stop processing it.
            return;
        }
        else if (prevElemName == "ButtonWithIcon")
        {
            HandleButtonWithIcon(element);
            return;
        }
        else if (prevElemName == "ToggleSwitch")
        {
            HandleToggleSwitch(element);
            return;
        }

        if (XamlControlReplacements.TryGetValue(prevElemName, out var newControlName))
        {
            element.Name = MAUI_NAMESPACE + newControlName;
        }

        if (prevElemName.EndsWith(".ItemContainerTransitions"))
        {
            element.Remove();
            return; 
        }

        if (prevElemName == "Style" && element.Attributes().FirstOrDefault(a => a.Name.LocalName == "TargetType" && a.Value == "ListViewItem") is not null)
        {
            element.Remove();
            return;
        }

        HandlePointerEvents(element);

        // Process attributes for the current element
        ProcessAttributes(element, prevElemName);
        
        List<string> radioCheckedHandlers = [];
        if (element.Name.LocalName == "RadioButton")
        {
            var checkedAttr = element.Attribute("Checked");
            if (checkedAttr != null)
            {
                radioCheckedHandlers.Add(checkedAttr.Value);
                checkedAttr.Remove();
                element.SetAttributeValue("CheckedChanged", checkedAttr.Value);
            }
        }

        if (radioCheckedHandlers.Count > 0)
        {
            var fullClassName = Document.Root.Attribute(X_WINFX_NAMESPACE + "Class")?.Value;
            if (!string.IsNullOrEmpty(fullClassName))
            {
                if (!RadioButtonCheckedHandlers.ContainsKey(fullClassName))
                    RadioButtonCheckedHandlers[fullClassName] = new List<string>();
                RadioButtonCheckedHandlers[fullClassName].AddRange(radioCheckedHandlers);
            }
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
                formattedString.Add(new XElement(MAUI_NAMESPACE + "Span", new XAttribute("Text", textNode.Value)));
            }
            else if (node is XElement childElement)
            {
                XElement span = null;
                switch (childElement.Name.LocalName)
                {
                    case "Run":
                        span = new XElement(MAUI_NAMESPACE + "Span", new XAttribute("Text", childElement.Value));
                        foreach (var attr in childElement.Attributes())
                        {
                            if (XamlPropertyReplacements.TryGetValue(attr.Name.LocalName, out var newProp))
                            {
                                var finalVal = ProcessAttributeValue(attr.Value);
                                span.SetAttributeValue(newProp, finalVal);
                            }
                            else if (attr.Name.LocalName == "FontWeight") 
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
                            new XAttribute("Command", "{Binding OpenUrlCommand}")); 

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
        
        element.Name = MAUI_NAMESPACE + "Label";
        element.RemoveNodes();
        element.RemoveAttributes();
        element.Add(formattedText);
    }

    private void HandleButton(XElement element)
    {
        var iconElement = element.Elements()
                        .FirstOrDefault(e => e.Name.LocalName == "FontIcon" || e.Name.LocalName == "SymbolIcon");

        if (iconElement != null)
        {
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
                element.SetAttributeValue("Text", glyph);
            }
            
            iconElement.Remove();
        }
    }

    private void HandleListView(XElement element)
    {
        var needsBorderWrapper = element.Attribute("Background") != null || element.Attribute("CornerRadius") != null;

        var collectionView = new XElement(MAUI_NAMESPACE + "CollectionView");
        var wrapperElement = needsBorderWrapper ? new XElement(MAUI_NAMESPACE + "Border") : null;
        var targetElement = wrapperElement ?? collectionView; 
        var layoutProperties = new HashSet<string> { "Grid.Column", "Grid.Row", "Grid.ColumnSpan", "Grid.RowSpan", "HorizontalAlignment", "VerticalAlignment", "Margin", "MinHeight", "MinWidth", "MaxHeight", "MaxWidth" };

        // 1. Copy attributes from the old ListView to the new elements without processing them yet.
        foreach (var attr in element.Attributes())
        {
            string attrName = attr.Name.ToString();

            if (layoutProperties.Contains(attrName))
            {
                targetElement.SetAttributeValue(attr.Name, attr.Value);
            }
            else if (attrName == "Background" && wrapperElement != null)
            {
                wrapperElement.SetAttributeValue("BackgroundColor", attr.Value);
            }
            else if (attrName == "CornerRadius" && wrapperElement != null)
            {
                wrapperElement.SetAttributeValue("StrokeShape", attr.Value);
            }
            else if (attrName == "Padding" && wrapperElement != null)
            {
                wrapperElement.SetAttributeValue("Padding", attr.Value);
            }
            else if (attrName == "ItemContainerStyle")
            {
                // This style is often incompatible, so we drop it.
            }
            else
            {
                collectionView.SetAttributeValue(attr.Name, attr.Value);
            }
        }

        // 2. Process Child Nodes: Rename and move Header/ItemTemplate
        
        foreach (var node in element.Nodes())
        {
            if (node is XElement child)
            {
                bool processed = false;
                string localName = child.Name.LocalName;

                if (localName.EndsWith(".Header"))
                {
                    child.Name = MAUI_NAMESPACE + "CollectionView.Header";
                    processed = true;
                }
                else if (localName.EndsWith(".Footer"))
                {
                    child.Name = MAUI_NAMESPACE + "CollectionView.Footer";
                    processed = true;
                }
                else if (localName.EndsWith(".ItemTemplate"))
                {
                    child.Name = MAUI_NAMESPACE + "CollectionView.ItemTemplate";
                    processed = true;
                }
                else if (localName.EndsWith(".ItemTemplateSelector"))
                {
                    child.Name = MAUI_NAMESPACE + "CollectionView.ItemTemplate";
                }
                
                if (processed)
                {
                    // Recursively process the contents of the header/footer
                    foreach (var nestedElement in child.DescendantsAndSelf())
                    {
                        ProcessRecursive(nestedElement);
                    }
                }

                collectionView.Add(child);
            }
        }

        // 3. IMPORTANT: Now process the attributes of the newly created elements.
        // This will correctly convert Visibility, ThemeResource, etc.
        if (wrapperElement != null)
        {
            ProcessAttributes(wrapperElement, wrapperElement.Name.LocalName);
        }
        ProcessAttributes(collectionView, collectionView.Name.LocalName);


        // 4. Perform the final replacement in the XAML tree
        if (wrapperElement != null)
        {
            wrapperElement.Add(collectionView);
            element.ReplaceWith(wrapperElement);
        }
        else
        {
            element.ReplaceWith(collectionView);
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
                hStack.SetAttributeValue(attr.Name, attr.Value);
            }
            else if (textProperties.Contains(attrName))
            {
                if (attrName == "Content")
                    newLabel.SetAttributeValue("Text", attrValue);
                else
                    newLabel.SetAttributeValue(attr.Name, attrValue);
            }
            else if (attrName == "Checked" || attrName == "Unchecked")
            {
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
        
        element.ReplaceWith(hStack);
    }

    private void HandleToggleSwitch(XElement element)
    {
        // 1. Create the new MAUI elements
        var hStack = new XElement(MAUI_NAMESPACE + "HorizontalStackLayout");
        var newSwitch = new XElement(MAUI_NAMESPACE + "Switch");
        var newLabel = new XElement(MAUI_NAMESPACE + "Label");

        // Define which properties belong to the text label vs. the overall layout
        var textProperties = new HashSet<string> { "OnContent", "OffContent", "FontSize", "FontWeight", "Foreground" };
        var layoutProperties = new HashSet<string> { "Grid.Column", "Grid.Row", "Grid.ColumnSpan", "Grid.RowSpan", "HorizontalAlignment", "VerticalAlignment", "Margin", "Style" };

        // 2. Distribute attributes from the old ToggleSwitch to the new elements
        foreach (var attr in element.Attributes())
        {
            string attrName = attr.Name.ToString();
            string attrValue = attr.Value;

            if (layoutProperties.Contains(attrName))
            {
                // Move layout properties to the parent stack, but ignore the incompatible UWP Style
                if (attrName != "Style")
                {
                    hStack.SetAttributeValue(attr.Name, attr.Value);
                }
            }
            else if (textProperties.Contains(attrName))
            {
                // Move and convert text-related properties to the Label
                if (attrName == "OnContent")
                    newLabel.SetAttributeValue("Text", attrValue);
                else if (attrName == "FontWeight")
                    newLabel.SetAttributeValue("FontAttributes", ProcessAttributeValue(attrValue));
                else if (attrName == "Foreground")
                    newLabel.SetAttributeValue("TextColor", ProcessAttributeValue(attrValue));
                else if (attrName != "OffContent") // Explicitly ignore OffContent
                    newLabel.SetAttributeValue(attr.Name, attrValue);
            }
            else
            {
                // Any remaining properties (like x:Name, Toggled event) belong to the Switch
                newSwitch.SetAttributeValue(attr.Name, attr.Value);
            }
        }

        // 3. Add a default vertical alignment to the stack to keep the Switch and Label aligned
        if (hStack.Attribute("VerticalOptions") == null)
        {
            hStack.SetAttributeValue("VerticalOptions", "Center");
        }

        // 4. Build the final structure
        hStack.Add(newSwitch);
        hStack.Add(newLabel);

        // 5. Run standard attribute processing on the newly created elements
        ProcessAttributes(hStack, "HorizontalStackLayout");
        ProcessAttributes(newSwitch, "Switch");
        ProcessAttributes(newLabel, "Label");

        // 6. Replace the original ToggleSwitch element with our new layout
        element.ReplaceWith(hStack);
    }

    private void HandleButtonWithIcon(XElement element)
    {
        var mauiButton = new XElement(MAUI_NAMESPACE + "Button");

        foreach (var attr in element.Attributes())
        {
            if (attr.Name.LocalName == "Content")
            {
                // UWP's Content becomes MAUI's Text
                mauiButton.SetAttributeValue("Text", attr.Value);
            }
            else if (attr.Name.LocalName == "Spacing" || attr.Name.LocalName == "FlowDirection")
            {
                // These properties are handled together to create the ContentLayout property
                continue;
            }
            else
            {
                // Copy other attributes directly for now.
                // ProcessAttributes will handle renaming them (e.g., Click -> Clicked)
                mauiButton.SetAttributeValue(attr.Name, attr.Value);
            }
        }

        // 3. Create the ContentLayout property from FlowDirection and Spacing
        var flowDirection = element.Attribute("FlowDirection")?.Value;
        var spacing = element.Attribute("Spacing")?.Value ?? "0";
        // In UWP RightToLeft places the icon first (on the left). This is MAUI's default.
        // LeftToRight would place the icon on the right.
        string imagePosition = (flowDirection == "LeftToRight") ? "Right" : "Left";
        mauiButton.SetAttributeValue("ContentLayout", $"{imagePosition},{spacing}");


        // 4. Find the <...Icon> child node and convert its FontIcon to a FontImageSource
        var iconNode = element.Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith(".Icon"));
        if (iconNode != null)
        {
            var fontIcon = iconNode.Elements().FirstOrDefault(e => e.Name.LocalName == "FontIcon");
            if (fontIcon != null)
            {
                var imageSourceNode = new XElement(mauiButton.Name + ".ImageSource");
                var fontImageSource = new XElement(MAUI_NAMESPACE + "FontImageSource");

                // Map FontIcon properties to FontImageSource properties
                var glyph = fontIcon.Attribute("Glyph")?.Value;
                var fontFamily = fontIcon.Attribute("FontFamily")?.Value;
                var fontSize = fontIcon.Attribute("FontSize")?.Value;
                var foreground = fontIcon.Attribute("Foreground")?.Value;

                if (glyph != null) fontImageSource.SetAttributeValue("Glyph", ProcessAttributeValue(glyph));
                if (fontFamily != null) fontImageSource.SetAttributeValue("FontFamily", ProcessAttributeValue(fontFamily));
                if (fontSize != null) fontImageSource.SetAttributeValue("Size", ProcessAttributeValue(fontSize));
                if (foreground != null) fontImageSource.SetAttributeValue("Color", ProcessAttributeValue(foreground));

                imageSourceNode.Add(fontImageSource);
                mauiButton.Add(imageSourceNode);
            }
        }

        // 5. Run standard attribute processing on the new button.
        // We pass "Button" to ensure rules like Click->Clicked are applied.
        ProcessAttributes(mauiButton, "Button");

        // 6. Replace the old custom control with our newly constructed Button
        element.ReplaceWith(mauiButton);
    }

    private void HandlePointerEvents(XElement element)
    {
        var pointerEventAttributes = element.Attributes()
            .Where(attr => PointerEventMap.Contains(attr.Name.LocalName))
            .ToList();

        if (pointerEventAttributes.Count != 0)
        {
            var gestureRecognizersNode = element.Element(element.Name + ".GestureRecognizers");
            if (gestureRecognizersNode == null)
            {
                gestureRecognizersNode = new XElement(element.Name + ".GestureRecognizers");
                PointerGestureRecognizerNodes[element] = gestureRecognizersNode;
            }

            var pgrNode = gestureRecognizersNode.Element(MAUI_NAMESPACE + "PointerGestureRecognizer");
            if (pgrNode == null)
            {
                pgrNode = new XElement(MAUI_NAMESPACE + "PointerGestureRecognizer");
                gestureRecognizersNode.Add(pgrNode);
            }

            if (element.Attribute(X_NAMESPACE + "Name") == null && element.Attribute("Name") != null)
            {
                var nameAttr = element.Attribute("Name");
                if (nameAttr != null)
                {
                    element.SetAttributeValue(X_NAMESPACE + "Name", nameAttr.Value);
                    nameAttr.Remove();
                }
            }

            foreach (var attr in pointerEventAttributes)
            {
                pgrNode.SetAttributeValue(attr.Name.LocalName, attr.Value);
                attr.Remove();
            }
        }
    }

    public void SaveTemplatedControlClasses()
    {
        var styles = Document.Descendants()
        .Where(e => e.Name.LocalName == "Style" && e.Attribute("TargetType") is not null)
        .Select(e => e.Attribute("TargetType").Value)
        .Where(x => x.Contains(':'))
        .ToArray();

        var namespace_maps = (doc.FirstNode as XElement)?.Attributes().ToDictionary(attr => attr.Name.LocalName, attr => attr.Value.Replace("clr-namespace:", string.Empty));
        
        if (namespace_maps is null) return;

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