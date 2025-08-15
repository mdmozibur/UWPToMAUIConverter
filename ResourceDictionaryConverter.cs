using System.Xml.Linq;

namespace UwpToMaui; 

public static class ResourceDictionaryConverter
{
    public static void Convert(XElement doc, string destPath)
    {
        // Find all Setter elements in the document
        var allSetters = doc.Descendants()
            .Where(e => e.Name.LocalName == "Setter")
            .ToList();

        var theme_dictionary = doc.Descendants()
            .Where(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries")
            .FirstOrDefault();

        if (theme_dictionary is not null)
        {
            ProcessThemeDictionaries(theme_dictionary, Path.GetDirectoryName(destPath) + "/Styles");
            theme_dictionary.Remove();
        }

        foreach (var setter in allSetters)
        {
            // Case 1: Standard Setter -> <Setter Property="Foreground" ... />
            var propertyAttr = setter.Attribute("Property");
            if (propertyAttr != null)
            {
                if (XamlConversionWriter.XamlPropertyReplacements.TryGetValue(propertyAttr.Value, out var newPropName))
                {
                    propertyAttr.Value = newPropName;
                }
            }

            // Case 2: VisualState Setter -> <Setter Target="RootPanel.Background" ... />
            var targetAttr = setter.Attribute("Target");
            if (targetAttr != null)
            {
                var targetValue = targetAttr.Value;
                int dotIndex = targetValue.IndexOf('.');

                if (dotIndex > 0 && dotIndex < targetValue.Length - 1)
                {
                    // Split "ElementName.PropertyName"
                    string targetName = targetValue.Substring(0, dotIndex);
                    string propertyName = targetValue.Substring(dotIndex + 1);

                    // Check if the property itself needs renaming (e.g., Foreground -> TextColor)
                    if (XamlConversionWriter.XamlPropertyReplacements.TryGetValue(propertyName, out var newPropertyName))
                    {
                        propertyName = newPropertyName;
                    }

                    // Remove the old UWP 'Target' attribute
                    targetAttr.Remove();

                    // Add the new MAUI 'TargetName' and 'Property' attributes
                    setter.SetAttributeValue("TargetName", targetName);
                    setter.SetAttributeValue("Property", propertyName);
                }
            }
        }

        if (destPath.EndsWith("Generic.xaml"))
        {
            var generic_xaml_save_path = Directory.GetParent(Path.GetDirectoryName(destPath)).FullName + "/Styles";

            if (!Directory.Exists(generic_xaml_save_path))
                Directory.CreateDirectory(generic_xaml_save_path);

            doc.Save(Path.Combine(generic_xaml_save_path, "Styles.xaml"));
        }
        else
            doc.Save(destPath);
    }
    private static void ProcessThemeDictionaries(XElement themeDictionaries, string stylesDir)
    {
        var lightThemeDict = themeDictionaries.Elements()
            .FirstOrDefault(d => d.Attribute(XamlConversionWriter.X_WINFX_NAMESPACE + "Key")?.Value == "Light");

        var darkThemeDict = themeDictionaries.Elements()
            .FirstOrDefault(d => d.Attribute(XamlConversionWriter.X_WINFX_NAMESPACE + "Key")?.Value == "Dark");

        if (!Directory.Exists(stylesDir))
            Directory.CreateDirectory(stylesDir);

        if (lightThemeDict != null)
            {
                var lightRoot = new XElement(XamlConversionWriter.MAUI_NAMESPACE + "ResourceDictionary",
                    new XAttribute(XNamespace.Xmlns + "x", XamlConversionWriter.X_WINFX_NAMESPACE),
                    lightThemeDict.Nodes()); // Copy all child nodes (the actual resources)
                new XDocument(lightRoot).Save(Path.Combine(stylesDir, "LightTheme.xaml"));
            }

        if (darkThemeDict != null)
        {
            var darkRoot = new XElement(XamlConversionWriter.MAUI_NAMESPACE + "ResourceDictionary",
                new XAttribute(XNamespace.Xmlns + "x", XamlConversionWriter.X_WINFX_NAMESPACE),
                darkThemeDict.Nodes()); // Copy all child nodes
            new XDocument(darkRoot).Save(Path.Combine(stylesDir, "DarkTheme.xaml"));
        }
    }
}