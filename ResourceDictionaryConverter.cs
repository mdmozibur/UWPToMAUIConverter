using System.Xml.Linq;

namespace UwpToMaui; // Assuming a namespace for organization

public static class ResourceDictionaryConverter
{
    public static void Convert(XDocument doc, string destPath, bool is_generic_xaml)
    {
        // The root element is assumed to be <ResourceDictionary>
        // Namespaces and control names are handled by the calling method.
        // This converter focuses on Setter and VisualStateManager transformations.

        // Find all Setter elements in the document
        var allSetters = doc.Descendants()
            .Where(e => e.Name.LocalName == "Setter")
            .ToList();

        foreach (var setter in allSetters)
        {
            // Case 1: Standard Setter -> <Setter Property="Foreground" ... />
            var propertyAttr = setter.Attribute("Property");
            if (propertyAttr != null)
            {
                if (UwpToMauiConverter.XamlPropertyReplacements.TryGetValue(propertyAttr.Value, out var newPropName))
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
                    if (UwpToMauiConverter.XamlPropertyReplacements.TryGetValue(propertyName, out var newPropertyName))
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
        
        doc.Save(destPath);
    }
}