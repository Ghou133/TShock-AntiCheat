using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AntiCheat.RuleExtraction;

/// <summary>Static syntax extraction only. This never compiles or invokes reference source.</summary>
public static class SourceExtractor
{
    public const string Commit = "e908905541edfa78fa3500bb9499f08c943c40d5";
    public const string SourcePath = "MKLP/Modules/SurvivalManager.cs";
    public static JsonObject Extract(string source, string configSource)
    {
        var syntax = Parse(source);
        var config = Parse(configSource);
        var method = syntax.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "GetIllegalItem");
        var calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(IsMutation).OrderBy(x => x.SpanStart).ToArray();
        var operations = new JsonArray(calls.Select((c, i) => (JsonNode)Operation(c, i + 1, method)).ToArray());
        var declarations = method.DescendantNodes().OfType<VariableDeclaratorSyntax>().Select(Declaration).ToArray();
        var overlaps = operations.OfType<JsonObject>()
            .SelectMany(o => o["itemDomain"]!["items"]?.AsArray().Select(n => (id: n!.GetValue<int>(), op: o)) ?? [])
            .GroupBy(x => x.id).Where(g => g.Count() > 1)
            .OrderBy(g => g.Key).Select(g => (JsonNode)new JsonObject
            {
                ["itemId"] = g.Key,
                ["operations"] = new JsonArray(g.Select(x => JsonValue.Create(x.op["id"]!.GetValue<string>())).ToArray()),
                ["hasAdd"] = g.Any(x => x.op["mutation"]!.GetValue<string>() == "Add"),
                ["hasRemove"] = g.Any(x => x.op["mutation"]!.GetValue<string>() == "Remove"),
                ["meaning"] = "Syntactic overlap only; evaluate branch, loop and dictionary history before calling it a conflict."
            }).ToArray();
        var configNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "AllowVanityCloth", "AllowDungeonRush", "AllowTempleRush", "AllowBanners", "AllowMusicBox",
            "AllowEaterOfWorlds", "AllowBrainOfCthulhu", "WhiteList_Survival_Code1",
            "Using_Survival_Code1", "AutoClear_IllegalItemDrops_SurvivalCode1"
        };
        var configFields = config.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables.Select(v => (field: f, variable: v)))
            .Where(x => configNames.Contains(x.variable.Identifier.ValueText))
            .Select(x => (JsonNode)new JsonObject
            {
                ["name"] = x.variable.Identifier.ValueText,
                ["declaringType"] = x.field.Ancestors().OfType<ClassDeclarationSyntax>().First().Identifier.ValueText,
                ["type"] = x.field.Declaration.Type.ToString(),
                ["initializer"] = x.variable.Initializer?.Value.ToString(),
                ["initializerAst"] = x.variable.Initializer is null ? null : Expression(x.variable.Initializer.Value),
                ["source"] = Location(x.field, "MKLP/Config.cs"),
                ["defaultIsDeploymentFact"] = false,
                ["fixNullStatements"] = new JsonArray(config.DescendantNodes().OfType<IfStatementSyntax>()
                    .Where(i => i.Condition.ToString() == x.variable.Identifier.ValueText + " == null")
                    .Select(i => (JsonNode)new JsonObject { ["source"] = Location(i, "MKLP/Config.cs"), ["text"] = i.ToString() }).ToArray())
            }).ToArray();
        if (configFields.Length != configNames.Count)
            throw new InvalidDataException($"Expected all {configNames.Count} relevant config fields; found {configFields.Length}.");
        var activeMutations = calls.Length;
        var enumeratedItems = operations.OfType<JsonObject>().SelectMany(o => o["itemDomain"]!["items"]?.AsArray().Select(n => n!.GetValue<int>()) ?? []).ToArray();
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["inventoryKind"] = "source_operations_not_flat_blacklist",
            ["source"] = new JsonObject
            {
                ["repository"] = "https://github.com/NightKLP/TShock-GSKLP-Moderation",
                ["commit"] = Commit, ["path"] = SourcePath, ["method"] = method.Identifier.ValueText,
                ["lineStart"] = Line(method), ["lineEnd"] = EndLine(method),
                ["sourceTextSha256Utf8"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))),
                ["configTextSha256Utf8"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configSource))),
                ["license"] = "MIT", ["licenseNotice"] = "../../reference/mklp/LICENSE"
            },
            ["qualification"] = Qualification(),
            ["semantics"] = new JsonObject
            {
                ["order"] = "sourceOrdinal is lexical order, not a flattened runtime sequence; controlFlow retains loops, branches, continue and break.",
                ["Add"] = "Dictionary.Add: duplicate present key throws; guarded Add skips when ContainsKey guard is false. No implicit union or last-wins rewrite.",
                ["Remove"] = "Dictionary.Remove: missing key is a no-op; existing key is removed. A !ContainsKey guard makes Remove ineffectual.",
                ["whitelist"] = "Final configured whitelist removes prior entries. Its default is metadata, never assumed deployment configuration.",
                ["scope"] = "GetIllegalItem only; commented-out difficulty block is not executable source and is listed separately.",
                ["unknown"] = "Runtime facts, NPC collection and configuration remain symbolic; this extractor does not execute or qualify any rule."
            },
            ["summary"] = new JsonObject
            {
                ["mutationStatements"] = activeMutations,
                ["addStatements"] = calls.Count(c => MutationName(c) == "Add"),
                ["removeStatements"] = calls.Count(c => MutationName(c) == "Remove"),
                ["forLoops"] = method.DescendantNodes().OfType<ForStatementSyntax>().Count(),
                ["foreachLoops"] = method.DescendantNodes().OfType<ForEachStatementSyntax>().Count(),
                ["ifStatements"] = method.DescendantNodes().OfType<IfStatementSyntax>().Count(),
                ["integerDomainOccurrences"] = enumeratedItems.Length,
                ["distinctIntegerDomainItems"] = enumeratedItems.Distinct().Count(),
                ["overlappingItemDomains"] = overlaps.Length,
                ["configFields"] = configFields.Length,
                ["unsupportedStatements"] = 0,
                ["referenceCodeExecuted"] = false
            },
            ["parameter"] = new JsonObject { ["name"] = "currentbossdefeated", ["type"] = "BossDType", ["default"] = "BossDType.NA", ["meaning"] = "Same-event defeated-boss exception; retain separately from world progression flags." },
            ["enumValues"] = new JsonArray(syntax.DescendantNodes().OfType<EnumDeclarationSyntax>()
                .Single(e => e.Identifier.ValueText == "BossDType").Members.Select(m => JsonValue.Create(m.Identifier.ValueText)).ToArray()),
            ["localDeclarations"] = new JsonArray(declarations),
            ["relevantConfiguration"] = new JsonArray(configFields),
            ["operations"] = operations,
            ["controlFlow"] = Statement(method.Body!, calls),
            ["syntacticItemOverlaps"] = new JsonArray(overlaps),
            ["commentedOutCode"] = new JsonArray(method.DescendantTrivia()
                .Where(t => t.IsKind(SyntaxKind.MultiLineCommentTrivia) && t.ToString().Contains("getillegalitems"))
                .Select(t => (JsonNode)new JsonObject { ["lineStart"] = t.GetLocation().GetLineSpan().StartLinePosition.Line + 1, ["text"] = t.ToString(), ["executable"] = false }).ToArray())
        };
    }

    private static CompilationUnitSyntax Parse(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new InvalidDataException(string.Join("; ", errors.Select(d => d.ToString())));
        return tree.GetCompilationUnitRoot();
    }
    private static bool IsMutation(InvocationExpressionSyntax call) =>
        call.Expression is MemberAccessExpressionSyntax member && member.Expression.ToString() == "getillegalitems" && member.Name.Identifier.ValueText is "Add" or "Remove";
    private static string MutationName(InvocationExpressionSyntax call) => ((MemberAccessExpressionSyntax)call.Expression).Name.Identifier.ValueText;
    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    private static int EndLine(SyntaxNode node) => node.GetLocation().GetLineSpan().EndLinePosition.Line + 1;
    private static JsonObject Location(SyntaxNode node, string path = SourcePath) => new() { ["path"] = path, ["lineStart"] = Line(node), ["lineEnd"] = EndLine(node), ["spanStart"] = node.SpanStart, ["spanLength"] = node.Span.Length };
    public static JsonObject Qualification() => new() { ["productionEligible"] = false, ["baseline1456"] = "acquisition_unverified", ["target1458"] = "blocked", ["status"] = "source_candidate_only" };
    private static JsonObject Declaration(VariableDeclaratorSyntax variable) => new()
    {
        ["name"] = variable.Identifier.ValueText, ["source"] = Location(variable),
        ["initializer"] = variable.Initializer?.Value.ToString(),
        ["initializerAst"] = variable.Initializer is null ? null : Expression(variable.Initializer.Value)
    };
    private static JsonObject Operation(InvocationExpressionSyntax call, int ordinal, MethodDeclarationSyntax method)
    {
        var mutation = MutationName(call);
        var key = call.ArgumentList.Arguments[0].Expression;
        var domain = Domain(key, call, method);
        var loops = call.Ancestors().Where(a => a is ForStatementSyntax or ForEachStatementSyntax).Reverse().ToArray();
        var branches = call.Ancestors().OfType<IfStatementSyntax>().Reverse().Select(i => (JsonNode)new JsonObject
        {
            ["branch"] = i.Statement.FullSpan.Contains(call.Span) ? "then" : "else",
            ["expression"] = i.Condition.ToString(), ["ast"] = Expression(i.Condition), ["source"] = Location(i.Condition)
        }).ToArray();
        var skips = loops.SelectMany(loop => loop.DescendantNodes().OfType<ContinueStatementSyntax>()
            .Where(c => c.SpanStart < call.SpanStart && c.Ancestors().First(a => a is ForStatementSyntax or ForEachStatementSyntax) == loop)
            .Select(c => (JsonNode)new JsonObject
            {
                ["source"] = Location(c),
                ["conditionPath"] = new JsonArray(c.Ancestors().OfType<IfStatementSyntax>().TakeWhile(i => loop.FullSpan.Contains(i.Span)).Reverse()
                    .Select(i => (JsonNode)new JsonObject { ["expression"] = i.Condition.ToString(), ["ast"] = Expression(i.Condition) }).ToArray()),
                ["meaning"] = "When this path holds, this loop iteration cannot reach the later operation."
            })).ToArray();
        var reason = mutation == "Add" ? call.ArgumentList.Arguments[1].Expression.ToString().Trim('"') : null;
        return new JsonObject
        {
            ["id"] = $"MKLP-OP-{ordinal:D3}", ["sourceOrdinal"] = ordinal, ["source"] = Location(call),
            ["mutation"] = mutation, ["originalInvocation"] = call.ToString(), ["reasonLabel"] = reason,
            ["classification"] = reason?.StartsWith("Unobtain", StringComparison.Ordinal) == true ? "natural_impossibility_candidate" : "server_policy",
            ["classificationRationale"] = "Source author intent only. No natural acquisition predicate has been validated; progression labels may encode server policy.",
            ["itemDomain"] = domain, ["conditionPath"] = new JsonArray(branches),
            ["loopPath"] = new JsonArray(loops.Select(l => (JsonNode)Loop(l, method)).ToArray()),
            ["priorContinuePaths"] = new JsonArray(skips),
            ["dictionaryGuard"] = string.Join(" && ", call.Ancestors().OfType<IfStatementSyntax>().Where(i => i.Condition.ToString().Contains("getillegalitems.ContainsKey")).Select(i => i.Condition.ToString())),
            ["qualification"] = Qualification()
        };
    }

    private static JsonObject Domain(ExpressionSyntax key, SyntaxNode at, MethodDeclarationSyntax method)
    {
        if (Int(key) is int literal) return new() { ["kind"] = "literal", ["expression"] = key.ToString(), ["items"] = IntArray([literal]) };
        if (key is IdentifierNameSyntax identifier)
        {
            var loop = at.Ancestors().FirstOrDefault(a => a is ForEachStatementSyntax f && f.Identifier.ValueText == identifier.Identifier.ValueText || a is ForStatementSyntax b && b.Declaration?.Variables.Any(v => v.Identifier.ValueText == identifier.Identifier.ValueText) == true);
            if (loop is ForEachStatementSyntax f)
            {
                var values = ResolveArray(f.Expression, f, method);
                return new() { ["kind"] = values is null ? "external_collection" : "array", ["expression"] = f.Expression.ToString(), ["items"] = values is null ? null : IntArray(values), ["iterationVariable"] = f.Identifier.ValueText, ["requiresRuntimeCollection"] = values is null };
            }
            if (loop is ForStatementSyntax b)
            {
                var start = Int(b.Declaration!.Variables.Single().Initializer!.Value) ?? throw new InvalidDataException("Nonconstant loop start");
                var end = b.Condition is BinaryExpressionSyntax cmp ? Int(cmp.Right) : null;
                if (end is null || !b.Condition!.IsKind(SyntaxKind.LessThanOrEqualExpression) || b.Incrementors.Single().Kind() != SyntaxKind.PostIncrementExpression || end - start > 10000 || end < start)
                    throw new InvalidDataException("Unsupported or unbounded reference loop");
                var all = Enumerable.Range(start, end.Value - start + 1).ToArray();
                var skipped = b.Statement.DescendantNodes().OfType<IfStatementSyntax>()
                    .Where(i => i.SpanStart < at.SpanStart && i.Statement is ContinueStatementSyntax && i.Condition is BinaryExpressionSyntax c && c.IsKind(SyntaxKind.EqualsExpression) && c.Left.ToString() == identifier.Identifier.ValueText && Int(c.Right) != null)
                    .Select(i => Int(((BinaryExpressionSyntax)i.Condition).Right)!.Value).ToArray();
                return new() { ["kind"] = "inclusive_range", ["expression"] = key.ToString(), ["first"] = start, ["last"] = end, ["step"] = 1, ["items"] = IntArray(all.Except(skipped)), ["definitelySkippedItems"] = IntArray(skipped), ["conditionalSkipsRemainInControlFlow"] = true };
            }
        }
        throw new InvalidDataException($"Unresolved mutation key at {Line(at)}: {key}");
    }
    private static int[]? ResolveArray(ExpressionSyntax expression, SyntaxNode at, MethodDeclarationSyntax method)
    {
        if (expression is not IdentifierNameSyntax name) return null;
        var candidate = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Identifier.ValueText == name.Identifier.ValueText && v.SpanStart < at.SpanStart && v.Ancestors().OfType<BlockSyntax>().First().FullSpan.Contains(at.Span))
            .OrderByDescending(v => v.SpanStart).FirstOrDefault();
        if (candidate?.Initializer?.Value is not InitializerExpressionSyntax init) return null;
        if (init.Expressions.Any(e => Int(e) is null)) throw new InvalidDataException("Nonconstant array member");
        return init.Expressions.Select(e => Int(e)!.Value).ToArray();
    }
    private static int? Int(ExpressionSyntax expression) => expression is LiteralExpressionSyntax l && l.Token.Value is int value ? value : null;
    private static JsonArray IntArray(IEnumerable<int> values) => new(values.Select(v => JsonValue.Create(v)).ToArray());
    private static JsonObject Loop(SyntaxNode loop, MethodDeclarationSyntax method) => loop switch
    {
        ForEachStatementSyntax f => new() { ["kind"] = "foreach", ["variable"] = f.Identifier.ValueText, ["type"] = f.Type.ToString(), ["collection"] = f.Expression.ToString(), ["resolvedCollection"] = ResolveArray(f.Expression, f, method) is int[] ids ? IntArray(ids) : null, ["source"] = Location(f) },
        ForStatementSyntax f => new() { ["kind"] = "for", ["declaration"] = f.Declaration?.ToString(), ["condition"] = f.Condition?.ToString(), ["conditionAst"] = f.Condition is null ? null : Expression(f.Condition), ["incrementors"] = f.Incrementors.ToString(), ["source"] = Location(f) },
        _ => throw new InvalidDataException("Unsupported loop")
    };
    private static JsonObject Statement(StatementSyntax statement, InvocationExpressionSyntax[] calls)
    {
        var node = new JsonObject { ["kind"] = statement.Kind().ToString(), ["source"] = Location(statement) };
        switch (statement)
        {
            case BlockSyntax block:
                node["statements"] = new JsonArray(block.Statements.Select(s => (JsonNode)Statement(s, calls)).ToArray()); break;
            case LocalDeclarationStatementSyntax local:
                node["type"] = local.Declaration.Type.ToString();
                node["variables"] = new JsonArray(local.Declaration.Variables.Select(Declaration).ToArray()); break;
            case IfStatementSyntax condition:
                node["condition"] = condition.Condition.ToString(); node["conditionAst"] = Expression(condition.Condition);
                node["then"] = Statement(condition.Statement, calls); node["else"] = condition.Else is null ? null : Statement(condition.Else.Statement, calls); break;
            case ForStatementSyntax loop:
                node["declaration"] = loop.Declaration?.ToString(); node["condition"] = loop.Condition?.ToString();
                node["conditionAst"] = loop.Condition is null ? null : Expression(loop.Condition);
                node["incrementors"] = loop.Incrementors.ToString(); node["body"] = Statement(loop.Statement, calls); break;
            case ForEachStatementSyntax loop:
                node["variable"] = loop.Identifier.ValueText; node["collection"] = loop.Expression.ToString();
                node["collectionAst"] = Expression(loop.Expression); node["body"] = Statement(loop.Statement, calls); break;
            case ExpressionStatementSyntax expression:
                node["expression"] = expression.Expression.ToString(); node["ast"] = Expression(expression.Expression);
                if (expression.Expression is InvocationExpressionSyntax call && IsMutation(call)) node["operationId"] = $"MKLP-OP-{Array.IndexOf(calls, call) + 1:D3}";
                break;
            case ContinueStatementSyntax: case BreakStatementSyntax: break;
            case ReturnStatementSyntax result: node["expression"] = result.Expression?.ToString(); break;
            default: throw new InvalidDataException($"Unsupported statement {statement.Kind()} at {Line(statement)}; extraction stopped.");
        }
        return node;
    }
    private static JsonNode Expression(ExpressionSyntax expression) => expression switch
    {
        ParenthesizedExpressionSyntax x => new JsonObject { ["op"] = "parenthesized", ["arg"] = Expression(x.Expression) },
        BinaryExpressionSyntax x => new JsonObject { ["op"] = "binary", ["operator"] = x.OperatorToken.Text, ["left"] = Expression(x.Left), ["right"] = Expression(x.Right) },
        PrefixUnaryExpressionSyntax x => new JsonObject { ["op"] = "unary", ["operator"] = x.OperatorToken.Text, ["arg"] = Expression(x.Operand) },
        CastExpressionSyntax x => new JsonObject { ["op"] = "cast", ["type"] = x.Type.ToString(), ["arg"] = Expression(x.Expression) },
        LiteralExpressionSyntax x => new JsonObject { ["op"] = "literal", ["value"] = JsonSerializerNode(x.Token.Value), ["text"] = x.Token.Text },
        InvocationExpressionSyntax x => new JsonObject { ["op"] = "call", ["target"] = x.Expression.ToString(), ["args"] = new JsonArray(x.ArgumentList.Arguments.Select(a => Expression(a.Expression)).ToArray()) },
        IdentifierNameSyntax or MemberAccessExpressionSyntax => new JsonObject { ["op"] = "reference", ["name"] = expression.ToString() },
        InitializerExpressionSyntax x => new JsonObject { ["op"] = "initializer", ["values"] = new JsonArray(x.Expressions.Select(Expression).ToArray()) },
        ImplicitObjectCreationExpressionSyntax x => new JsonObject { ["op"] = "new", ["text"] = x.ToString() },
        _ => new JsonObject { ["op"] = "syntax", ["syntaxKind"] = expression.Kind().ToString(), ["text"] = expression.ToString(), ["evaluable"] = false }
    };
    private static JsonNode? JsonSerializerNode(object? value) => value is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(value);
}
