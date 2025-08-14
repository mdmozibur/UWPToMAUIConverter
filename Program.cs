using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
    internal static readonly Dictionary<string, ValueTuple<string, string>> XamlPropertyConditionalReplacements = new()
    {
        { "FontWeight", ("FontAttributes",  "TextBlock")},
        { "FontWeight", ("FontAttributes",  "FontIcon")},
        { "Glyph", ("Text", "FontIcon") },
        { "BorderThickness", ("StrokeThickness", "Border") },
        { "BorderBrush", ("Stroke", "Border") },
        { "CornerRadius", ("StrokeShape", "Border") },
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
        { "Panel", "Layout" },
        { "ListViewItem", "ViewCell" },
        { "DependencyObject", "BindableObject" },
        { "RoutedEventArgs", "EventArgs" },
        { "Windows.UI.Color", "Microsoft.Maui.Graphics.Color" },
        { "Windows.UI.Text.FontWeights", "Microsoft.Maui.FontAttributes" }, // UWP FontWeights is a class of static properties, MAUI is an enum
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

            
            // Iterate through ALL descendants to perform replacements
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
                                  (XamlPropertyConditionalReplacements.ContainsKey(attr.Name.LocalName) && XamlPropertyConditionalReplacements[attr.Name.LocalName].Item2 == prevElemName)
                    )
                    .ToList();
                
                foreach (var attr in attributesToRename)
                {
                    attr.Remove(); // Remove the old attribute
                    var newAttributeName = XamlPropertyReplacements.ContainsKey(attr.Name.LocalName) ? XamlPropertyReplacements[attr.Name.LocalName] : XamlPropertyConditionalReplacements[attr.Name.LocalName].Item1;
                    
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

            doc.Save(destPath);
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
        catch(Exception ex)
        {
            Console.WriteLine($"-- Could not process C# file {sourcePath}: {ex.Message}. File was copied without conversion.");
            File.Copy(sourcePath, destPath, true);
        }
    }
}

public class CSharpConversionRewriter : CSharpSyntaxRewriter
{
    private readonly Dictionary<string, string> _usingReplacements;

    public CSharpConversionRewriter(Dictionary<string, string> usingReplacements)
    {
        _usingReplacements = usingReplacements;
    }

    // Convert using statements like "using Windows.UI.Xaml;"
    public override SyntaxNode VisitUsingDirective(UsingDirectiveSyntax node)
    {
        string namespaceName = node.Name.ToString();
        if (_usingReplacements.ContainsKey(namespaceName))
        {
            var newName = SyntaxFactory.ParseName(_usingReplacements[namespaceName]);
            return node.WithName(newName);
        }
        return base.VisitUsingDirective(node);
    }

    // Convert base classes like "public class MyView : Panel" to "... : Layout"
    public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        if (node.BaseList != null)
        {
            var newBaseListTypes = new List<BaseTypeSyntax>();
            bool listModified = false;
            foreach (var baseType in node.BaseList.Types)
            {
                string baseTypeName = baseType.Type.ToString();
                if (UwpToMauiConverter.CSharpClassReplacements.ContainsKey(baseTypeName))
                {
                    newBaseListTypes.Add(baseType.WithType(SyntaxFactory.ParseTypeName(UwpToMauiConverter.CSharpClassReplacements[baseTypeName])));
                    listModified = true;
                }
                else
                {
                    newBaseListTypes.Add(baseType);
                }
            }

            if (listModified)
            {
                var newSeparatedList = SyntaxFactory.SeparatedList<BaseTypeSyntax>(newBaseListTypes);
                var newBaseList = node.BaseList.WithTypes(newSeparatedList);
                node = node.WithBaseList(newBaseList);
            }
        }
        return base.VisitClassDeclaration(node);
    }


    public override SyntaxNode VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        var declaration = node.Declaration;
        // Check if the field type is DependencyProperty
        if (declaration.Type is IdentifierNameSyntax typeName && typeName.Identifier.Text == "DependencyProperty")
        {
            // Create the new type "BindableProperty", preserving the original's formatting (trivia)
            var newTypeName = SyntaxFactory.IdentifierName("BindableProperty")
                .WithTrailingTrivia(typeName.GetTrailingTrivia());

            // Create a new declaration with the updated type
            var newDeclaration = declaration.WithType(newTypeName);

            // Return the original field node with the new declaration
            node = node.WithDeclaration(newDeclaration);
        }

        return base.VisitFieldDeclaration(node);
    }
    // Convert types used within code, e.g., "DependencyObject" to "BindableObject"
    public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
    {
        string identifier = node.Identifier.Text;
        if (UwpToMauiConverter.CSharpClassReplacements.ContainsKey(identifier))
        {
            string newIdentifierText = UwpToMauiConverter.CSharpClassReplacements[identifier];

            // Create a new token with the new text, but carry over the trivia (spaces, comments) from the old one.
            var newIdentifierToken = SyntaxFactory.Identifier(
                node.Identifier.LeadingTrivia,
                newIdentifierText,
                node.Identifier.TrailingTrivia);

            // Replace the identifier token within the existing node to preserve its structure and formatting.
            node = node.WithIdentifier(newIdentifierToken);
        }
        return base.VisitIdentifierName(node);
    }

    // Convert DependencyProperty.Register to BindableProperty.Create
    // Replace your existing VisitInvocationExpression method with this one
    public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (node.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return base.VisitInvocationExpression(node);
        }

        bool isRegister = memberAccess.Name.Identifier.Text == "Register";
        bool isRegisterAttached = memberAccess.Name.Identifier.Text == "RegisterAttached";

        // Now this check will succeed because VisitIdentifierName hasn't changed it
        if ((isRegister || isRegisterAttached) && memberAccess.Expression.ToString() == "DependencyProperty")
        {
            var newExpressionName = isRegisterAttached ? "BindableProperty.CreateAttached" : "BindableProperty.Create";
            var newExpression = SyntaxFactory.ParseExpression(newExpressionName)
                                            .WithTriviaFrom(node.Expression); // Preserve formatting

            var uwpArguments = node.ArgumentList.Arguments;

            if (uwpArguments.Count < 3)
            {
                return base.VisitInvocationExpression(node);
            }

            var mauiArguments = new List<ArgumentSyntax>
            {
                uwpArguments[0], // propertyName
                uwpArguments[1], // returnType
                uwpArguments[2]  // declaringType
            };

            // Check for PropertyMetadata to extract default value and propertyChanged handler
            if (uwpArguments.Count > 3 && uwpArguments[3].Expression is ObjectCreationExpressionSyntax metadataCreation)
            {
                if (metadataCreation.ArgumentList != null && metadataCreation.ArgumentList.Arguments.Any())
                {
                    var metadataArgs = metadataCreation.ArgumentList.Arguments;

                    // Argument 1: defaultValue
                    mauiArguments.Add(metadataArgs[0]);

                    // Argument 2 (if it exists): propertyChanged callback
                    if (metadataArgs.Count > 1)
                    {
                        // To add propertyChanged, we must provide the parameters that come before it.
                        // Add defaultBindingMode:
                        mauiArguments.Add(
                            SyntaxFactory.Argument(SyntaxFactory.ParseExpression("Microsoft.Maui.Controls.BindingMode.OneWay"))
                        );

                        // Add validateValue:
                        mauiArguments.Add(
                            SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))
                        );

                        // Add the propertyChanged delegate itself
                        mauiArguments.Add(metadataArgs[1]);
                    }
                }
            }

            var newArgumentList = SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(mauiArguments));

            node = node.WithExpression(newExpression).WithArgumentList(newArgumentList.WithTriviaFrom(node.ArgumentList));
        }

        return base.VisitInvocationExpression(node);
    }
    
    // Add this method to CSharpConversionRewriter
    public override SyntaxNode VisitGenericName(GenericNameSyntax node)
    {
        // Specifically targets TypedEventHandler<T, U>
        if (node.Identifier.Text == "TypedEventHandler" && node.TypeArgumentList.Arguments.Count == 2)
        {
            // Replace "TypedEventHandler" with "EventHandler"
            var newIdentifier = SyntaxFactory.Identifier("EventHandler")
                .WithTriviaFrom(node.Identifier);

            // UWP: TypedEventHandler<Sender, Args>
            // MAUI: EventHandler<Args>
            // We need to get the second type argument from the UWP definition.
            var eventArgsType = node.TypeArgumentList.Arguments[1];
            
            // Create a new type argument list containing only the second argument.
            var newTypeArguments = SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(eventArgsType));

            return node.WithIdentifier(newIdentifier).WithTypeArgumentList(newTypeArguments);
        }
        
        // For FontWeights.Bold -> FontAttributes.Bold
        if (node.Identifier.Text == "FontWeights")
        {
            return SyntaxFactory.IdentifierName("FontAttributes");
        }

        return base.VisitGenericName(node);
    }
}