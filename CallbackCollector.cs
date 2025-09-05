
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UwpToMaui;

public class CallbackCollector : CSharpSyntaxWalker
{
    public Dictionary<string, string> PropertyChangedCallbacks { get; } = new();

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // We are looking for "RegisterPropertyChangedCallback(DependencyProperty, Callback)"
        if (node.Expression is IdentifierNameSyntax identifier &&
            identifier.Identifier.Text == "RegisterPropertyChangedCallback" &&
            node.ArgumentList.Arguments.Count == 2)
        {
            // First argument is the DependencyProperty field, e.g., "IsCheckedProperty"
            string propertyFieldName = node.ArgumentList.Arguments[0].Expression.ToString();

            // Second argument is the callback method name, e.g., "IsCheckedChanged"
            string callbackMethodName = node.ArgumentList.Arguments[1].Expression.ToString();

            if (!string.IsNullOrEmpty(propertyFieldName) && !string.IsNullOrEmpty(callbackMethodName))
            {
                PropertyChangedCallbacks[propertyFieldName] = callbackMethodName;
            }
        }

        base.VisitInvocationExpression(node);
    }
}