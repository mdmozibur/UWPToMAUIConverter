using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UwpToMaui;

public class InstanceMemberCollector : CSharpSyntaxWalker
{
    public HashSet<string> InstanceMemberNames { get; } = new();
    public HashSet<string> InstanceMethodNames { get; } = new();

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        if (!node.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            InstanceMemberNames.Add(node.Identifier.Text);
        }
        base.VisitPropertyDeclaration(node);
    }

    public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        if (!node.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            foreach (var variable in node.Declaration.Variables)
            {
                InstanceMemberNames.Add(variable.Identifier.Text);
            }
        }
        base.VisitFieldDeclaration(node);
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        // Collect non-static, non-constructor method names
        if (!node.Modifiers.Any(SyntaxKind.StaticKeyword) && node.Identifier.Text != node.Parent.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.Text)
        {
            InstanceMethodNames.Add(node.Identifier.Text);
        }
        base.VisitMethodDeclaration(node);
    }
}

public class CSharpConversionRewriter : CSharpSyntaxRewriter
{
    private readonly Dictionary<string, string> _propertyChangedCallbacks;
    private readonly Dictionary<string, string> _usingReplacements;
    private readonly Dictionary<string, string> _elementToPgrMap = [];
    private HashSet<string> _instanceMemberNames;
    private HashSet<string> _instanceMethodNames;

    public string NameSpaceStr { get; private set; }
    public string ClassNameStr { get; private set; }

    public CSharpConversionRewriter(Dictionary<string, string> usingReplacements, Dictionary<string, string> propertyChangedCallbacks)
    {
        _usingReplacements = usingReplacements;
        _propertyChangedCallbacks = propertyChangedCallbacks;
    }

    public override SyntaxNode VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        if (node.Expression is AssignmentExpressionSyntax assignment && assignment.Left.ToString() == "this.DefaultStyleKey")
        {
            return null;
        }

        if (node.Expression is InvocationExpressionSyntax invocation &&
            invocation.Expression is IdentifierNameSyntax identifier &&
            identifier.Identifier.Text == "RegisterPropertyChangedCallback")
        {
            // The logic from this line has been moved to the BindableProperty.
            // Return null to remove the entire statement.
            return null;
        }
        return base.VisitExpressionStatement(node);
    }


    public override SyntaxNode VisitBlock(BlockSyntax node)
    {
        var newStatements = new List<StatementSyntax>();
        bool blockModified = false;

        foreach (var statement in node.Statements)
        {
            // Check if the statement is an event subscription like: element.PointerEvent += handler;
            if (statement is ExpressionStatementSyntax exprStatement &&
                exprStatement.Expression is AssignmentExpressionSyntax assignment &&
                assignment.IsKind(SyntaxKind.AddAssignmentExpression) && // Checks for "+="
                assignment.Left is MemberAccessExpressionSyntax memberAccess &&
                XamlConversionWriter.PointerEventMap.Contains(memberAccess.Name.Identifier.Text))
            {
                blockModified = true; 

                var elementIdentifier = memberAccess.Expression.ToString();

                // Check if we've already created a PointerGestureRecognizer for this element.
                if (!_elementToPgrMap.TryGetValue(elementIdentifier, out var pgrVariableName))
                {
                    // First time: Create a unique variable name for the PGR.
                    pgrVariableName = $"pgr_{elementIdentifier.Replace(".", "_")}";
                    _elementToPgrMap[elementIdentifier] = pgrVariableName;

                    // 1. Create the PGR variable: var pgr_RootGrid = new PointerGestureRecognizer();
                    var pgrDeclaration = SyntaxFactory.ParseStatement($"var {pgrVariableName} = new PointerGestureRecognizer();")
                        .WithLeadingTrivia(statement.GetLeadingTrivia()); // Preserve formatting.

                    // 2. Add the PGR to the element's GestureRecognizers collection.
                    var addPgrStatement = SyntaxFactory.ParseStatement($"{elementIdentifier}.GestureRecognizers.Add({pgrVariableName});");

                    // 3. Assign the event handler to the new PGR.
                    var newAssignment = assignment.WithLeft(
                        memberAccess.WithExpression(SyntaxFactory.IdentifierName(pgrVariableName))
                    );
                    var eventStatement = SyntaxFactory.ExpressionStatement(newAssignment)
                        .WithTrailingTrivia(statement.GetTrailingTrivia());

                    // Add all three new statements to our list.
                    newStatements.Add(pgrDeclaration);
                    newStatements.Add(addPgrStatement);
                    newStatements.Add(eventStatement);
                }
                else
                {
                    // PGR already exists for this element, so just attach the new event handler.
                    var newAssignment = assignment.WithLeft(
                        memberAccess.WithExpression(SyntaxFactory.IdentifierName(pgrVariableName))
                    );
                    var eventStatement = SyntaxFactory.ExpressionStatement(newAssignment)
                        .WithTriviaFrom(statement);

                    newStatements.Add(eventStatement);
                }
            }
            else
            {
                // This is not a pointer event subscription, so add it to the list as is.
                newStatements.Add(statement);
            }
        }

        if (blockModified)
        {
            // If we made changes, create a new block with the updated list of statements.
            var newBlock = node.WithStatements(SyntaxFactory.List(newStatements));
            // It's crucial to still call base.VisitBlock on the new block to process nested blocks.
            return base.VisitBlock(newBlock);
        }

        // If no changes were made, continue the visit as normal.
        return base.VisitBlock(node);
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

    public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        _elementToPgrMap.Clear();
        // Check if this method is a known property changed callback.
        if (_propertyChangedCallbacks.ContainsValue(node.Identifier.Text))
        {
            // MAUI callbacks are static.
            var newModifiers = node.Modifiers;
            if (!newModifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            {
                newModifiers = newModifiers.Insert(0, SyntaxFactory.Token(SyntaxKind.StaticKeyword));
            }

            // MAUI callbacks have the signature (BindableObject, object, object).
            var newParameters = SyntaxFactory.ParseParameterList("(BindableObject sender, object oldValue, object newValue)")
                .WithTriviaFrom(node.ParameterList);

            // Create a projected instance variable, e.g., "cli" for "ColorListItem".
            var instanceVarName = ClassNameStr.Length > 1
                ? new string(ClassNameStr.Where(char.IsUpper).ToArray()).ToLower()
                : ClassNameStr.ToLower();
            if (string.IsNullOrEmpty(instanceVarName)) instanceVarName = "instance"; // Fallback

            // Create the guard statement: "if (sender is not ColorListItem cli) return;"
            var guardStatement = SyntaxFactory.ParseStatement($"if (sender is not {ClassNameStr} {instanceVarName}) return;")
                .WithLeadingTrivia(node.Body.GetLeadingTrivia());

            // Use a dedicated rewriter to update the method body.
            var bodyRewriter = new CallbackBodyRewriter(instanceVarName, _instanceMemberNames, _instanceMethodNames, node);
            var newBody = (BlockSyntax)bodyRewriter.Visit(node.Body);

            // Add the guard statement to the top of the rewritten body.
            newBody = newBody.WithStatements(newBody.Statements.Insert(0, guardStatement));

            // Return the completely transformed method.
            return node.WithModifiers(newModifiers)
                        .WithParameterList(newParameters)
                        .WithBody(newBody);
        }

        return base.VisitMethodDeclaration(node);
    }
    

    public override SyntaxNode VisitParameter(ParameterSyntax node)
    {
        // Check if the parameter's type is the UWP pointer event argument type.
        string typeName = node.Type.ToString();
        if (typeName == "PointerRoutedEventArgs" || typeName == "Windows.UI.Xaml.Input.PointerRoutedEventArgs")
        {
            // Create the new MAUI type "PointerEventArgs", preserving the original's formatting.
            var newType = SyntaxFactory.ParseTypeName("PointerEventArgs")
                .WithTriviaFrom(node.Type);

            // Return a new parameter node with the updated type.
            return node.WithType(newType);
        }

        return base.VisitParameter(node);
    }
    
    // Convert base classes like "public class MyView : Panel" to "... : Layout"
    public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        ClassNameStr = node.Identifier.Text;

        // Before visiting the rest of the class, collect all instance-level fields and properties.
        var collector = new InstanceMemberCollector();
        collector.Visit(node);
        _instanceMemberNames = collector.InstanceMemberNames;
        _instanceMethodNames = collector.InstanceMethodNames;

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

            // Step 1: Find the callback method, regardless of its source.
            ArgumentSyntax callbackArgument = null;
            if (uwpArguments.Count > 3 && uwpArguments[3].Expression is ObjectCreationExpressionSyntax metadataCreation)
            {
                if (metadataCreation.ArgumentList?.Arguments.Count > 1)
                {
                    // A callback exists in the PropertyMetadata, e.g., new PropertyMetadata(0, OnFooChanged)
                    callbackArgument = metadataCreation.ArgumentList.Arguments[1];
                }
            }

            // If not found in metadata, check our collected callbacks from RegisterPropertyChangedCallback
            if (callbackArgument == null)
            {
                var declarator = node.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault();
                if (declarator != null && _propertyChangedCallbacks.TryGetValue(declarator.Identifier.Text, out var callbackName))
                {
                    callbackArgument = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(callbackName));
                }
            }

            // Step 2: Build the complete argument list for BindableProperty.Create.

            // Add defaultValue if it exists.
            if (uwpArguments.Count > 3 && uwpArguments[3].Expression is ObjectCreationExpressionSyntax metaWithDefault)
            {
                if (metaWithDefault.ArgumentList?.Arguments.Any() == true)
                {
                    mauiArguments.Add(metaWithDefault.ArgumentList.Arguments[0]); // Add defaultValue
                }
                else
                {
                    // Case for `new PropertyMetadata()` with no arguments.
                    mauiArguments.Add(SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)));
                }
            }

            // If a callback was found, add it to the arguments, padding with defaults if necessary.
            if (callbackArgument != null)
            {
                // Ensure a defaultValue argument exists first.
                if (mauiArguments.Count < 4)
                {
                    mauiArguments.Add(SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)));
                }

                // Add defaultBindingMode and validateValue before the callback.
                mauiArguments.Add(SyntaxFactory.Argument(SyntaxFactory.ParseExpression("Microsoft.Maui.Controls.BindingMode.OneWay")));
                mauiArguments.Add(SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)));
                mauiArguments.Add(callbackArgument);
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


    /// <summary>
    /// Rewrites the body of a property changed callback to use a projected instance variable.
    /// </summary>
    private class CallbackBodyRewriter : CSharpSyntaxRewriter
    {
        private readonly string _instanceName;
        private readonly HashSet<string> _instanceMembers;
        private readonly HashSet<string> _instanceMethods;
        private readonly HashSet<string> _localAndParamNames = new();

        public CallbackBodyRewriter(string instanceName, HashSet<string> instanceMembers, HashSet<string> instanceMethods, MethodDeclarationSyntax method)
        {
            _instanceName = instanceName;
            _instanceMembers = instanceMembers;
            _instanceMethods = instanceMethods;

            // Collect all local variable and parameter names to avoid replacing them.
            foreach (var param in method.ParameterList.Parameters)
            {
                _localAndParamNames.Add(param.Identifier.Text);
            }
            foreach (var variable in method.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                _localAndParamNames.Add(variable.Identifier.Text);
            }
        }

        // Replace "this" with the new instance variable, e.g., "cli".
        public override SyntaxNode VisitThisExpression(ThisExpressionSyntax node)
        {
            return SyntaxFactory.IdentifierName(_instanceName).WithTriviaFrom(node);
        }

        // Replace instance member access (e.g., "Label") with projected access (e.g., "cli.Label").
        public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
        {
            var identifier = node.Identifier.Text;

            // Only replace if it's a known instance member and NOT a local variable or parameter.
            if (_instanceMembers.Contains(identifier) && !_localAndParamNames.Contains(identifier))
            {
                // Avoid replacing the "Name" part of "Expression.Name" (e.g., don't change "Root.Label" to "Root.cli.Label").
                if (node.Parent is MemberAccessExpressionSyntax mae && mae.Name == node)
                {
                    return base.VisitIdentifierName(node);
                }

                // Prepend the instance name.
                return SyntaxFactory.ParseExpression($"{_instanceName}.{identifier}").WithTriviaFrom(node);
            }
            return base.VisitIdentifierName(node);
        }
        public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            // Check if this is a simple method call like "MyMethod()" rather than "obj.MyMethod()"
            if (node.Expression is IdentifierNameSyntax identifierName)
            {
                var methodName = identifierName.Identifier.Text;

                // If the method name is in our list of instance methods...
                if (_instanceMethods.Contains(methodName))
                {
                    // ...rewrite it from "MyMethod(...)" to "instance.MyMethod(...)"
                    var newExpression = SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(_instanceName),
                        identifierName
                    ).WithTriviaFrom(identifierName);

                    return node.WithExpression(newExpression);
                }
            }
            return base.VisitInvocationExpression(node);
        }
    }
}