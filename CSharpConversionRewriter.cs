using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UwpToMaui;

public class CSharpConversionRewriter : CSharpSyntaxRewriter
{
    private readonly Dictionary<string, string> _usingReplacements;
    public string NameSpaceStr { get; private set; }
    public string ClassNameStr { get; private set; }

    public CSharpConversionRewriter(Dictionary<string, string> usingReplacements)
    {
        _usingReplacements = usingReplacements;
    }

    public override SyntaxNode VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        if (node.Expression is AssignmentExpressionSyntax assignment)
        {
            if (assignment.Left.ToString() == "this.DefaultStyleKey")
            {
                return null;
            }
        }
        return base.VisitExpressionStatement(node);
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

    public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
    {
        NameSpaceStr = node.Name.ToString();
        return base.VisitNamespaceDeclaration(node);
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        List<ParameterSyntax> new_parameters = new List<ParameterSyntax>();
        foreach (var prm in node.ParameterList.Parameters)
        {
            if (UwpToMauiConverter.CSharpClassReplacements.TryGetValue(prm.Identifier.Text, out string replace_class))
            {
                new_parameters.Add(prm.WithIdentifier(SyntaxFactory.Identifier(replace_class).WithTriviaFrom(prm.Identifier)));
            }
            else
            {
                new_parameters.Add(prm);
            }
        }
        node = node.WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(new_parameters)));
        return base.VisitMethodDeclaration(node);
    }

    // Convert base classes like "public class MyView : Panel" to "... : Layout"
    public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        ClassNameStr = node.Identifier.Text;
        if (node.BaseList != null)
        {
            var newBaseListTypes = new List<BaseTypeSyntax>();
            bool listModified = false;
            foreach (var baseType in node.BaseList.Types)
            {
                string baseTypeName = baseType.Type.ToString();
                if (UwpToMauiConverter.CSharpClassReplacements.TryGetValue(baseTypeName, out string? value))
                {
                    newBaseListTypes.Add(baseType.WithType(SyntaxFactory.ParseTypeName(value)));
                    listModified = true;
                }
                else if (baseTypeName == "Control" && XamlConversionWriter.TemplatedControlClasses.Any(x => x.Class == ClassNameStr && x.NameSpace == NameSpaceStr))
                {
                    var cls_nm = XamlConversionWriter.TemplatedControlClasses.FirstOrDefault(x => x.Class == ClassNameStr && x.NameSpace == NameSpaceStr);
                    newBaseListTypes.Add(baseType.WithType(SyntaxFactory.ParseTypeName("TemplatedView")));
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
        if (UwpToMauiConverter.CSharpClassReplacements.TryGetValue(identifier, out string? value))
        {
            string newIdentifierText = value;

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
    public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (node.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return base.VisitInvocationExpression(node);
        }

        if (memberAccess.Name.Identifier.Text == "GoToState" &&
            memberAccess.Expression.ToString() == "VisualStateManager" &&
            node.ArgumentList.Arguments.Count == 3)
        {
            // UWP: GoToState(control, stateName, useTransitions)
            // MAUI: GoToState(control, stateName)
            // We remove the third argument.
            var newArguments = node.ArgumentList.Arguments.Take(2);
            var newArgumentList = SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(newArguments));
            return node.WithArgumentList(newArgumentList);
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

            node = node.WithIdentifier(newIdentifier).WithTypeArgumentList(newTypeArguments);
        }
        return base.VisitGenericName(node);
    }


    /// <summary>
    /// Converts UWP Visibility assignments to their .NET MAUI IsVisible equivalent.
    /// e.g., "element.Visibility = bool.ToVisibility()" becomes "element.IsVisible = bool;"
    /// </summary>
    public override SyntaxNode VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        var newLeft = node.Left;
        var newRight = node.Right;
        bool hasChanged = false;

        if (node.Left is MemberAccessExpressionSyntax leftMemberAccess &&
            leftMemberAccess.Name.Identifier.Text == "Visibility")
        {
            // If so, replace "Visibility" with "IsVisible".
            newLeft = leftMemberAccess.WithName(SyntaxFactory.IdentifierName("IsVisible")
                .WithTriviaFrom(leftMemberAccess.Name));
            hasChanged = true;
        }

        // Check if the right side is a call to ".ToVisibility()".
        if (node.Right is InvocationExpressionSyntax rightInvocation &&
            rightInvocation.Expression is MemberAccessExpressionSyntax rightMemberAccess &&
            rightMemberAccess.Name.Identifier.Text == "ToVisibility")
        {
            // If so, replace the entire call with the object it was called on.
            // For example, in "myBool.ToVisibility()", we just want "myBool".
            newRight = rightMemberAccess.Expression;
            hasChanged = true;
        }

        else if (node.Right is MemberAccessExpressionSyntax memberAccessExpressionSyntax &&
                memberAccessExpressionSyntax.Expression is IdentifierNameSyntax ins &&
                ins.Identifier.Text == "Visibility")
        {
            newRight = SyntaxFactory.ParseName(memberAccessExpressionSyntax.Name.Identifier.Text == "Visible" ? "true" : "false");
            hasChanged = true;
        }

        // If any changes were made, return a new assignment expression node.
        if (hasChanged)
        {
            node = node.WithLeft(newLeft).WithRight(newRight);
        }

        return base.VisitAssignmentExpression(node);
    }
}