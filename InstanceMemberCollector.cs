
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
    
    public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
    {
        // If the event is not static, add its name to our list of instance members.
        if (!node.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            foreach (var variable in node.Declaration.Variables)
            {
                InstanceMemberNames.Add(variable.Identifier.Text);
            }
        }
        base.VisitEventFieldDeclaration(node);
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
