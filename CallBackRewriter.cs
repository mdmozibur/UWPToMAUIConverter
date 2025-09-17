using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UwpToMaui;

/// <summary>
/// Rewrites the body of a property changed callback to use a projected instance variable.
/// </summary>
public class CallbackBodyRewriter : CSharpSyntaxRewriter
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