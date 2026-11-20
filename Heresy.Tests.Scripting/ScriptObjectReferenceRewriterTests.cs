using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Scripting.Analysis;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class ScriptObjectReferenceRewriterTests
{
    [Test]
    public void RewritesOnlySemanticNumericLiteralTokensAndPreservesTrivia()
    {
        const string original = """
            var quoted = "_O(42)";
            // _O(42)
            /* _O(42) */
            var a = _O(/* left */ 0x2au /* right */);
            var b = _O(42);
            var c = _O(43);
            """;
        Dictionary<ObjectId, ObjectId> map = new()
        {
            [new ObjectId(42)] = new ObjectId(4000000000),
        };

        string revised = ScriptObjectReferenceRewriter.Rewrite(original, map);
        Assert.That(revised, Does.Contain("var quoted = \"_O(42)\";"));
        Assert.That(revised, Does.Contain("// _O(42)"));
        Assert.That(revised, Does.Contain("/* _O(42) */"));
        Assert.That(revised, Does.Contain(
            "_O(/* left */ 4000000000 /* right */)"));
        Assert.That(revised, Does.Contain("var b = _O(4000000000);"));
        Assert.That(revised, Does.Contain("var c = _O(43);"));
        Assert.That(ScriptReferenceAnalyzer.Analyze(revised).IsReliable, Is.True);
        Assert.That(ScriptReferenceAnalyzer.Analyze(revised).References
            .Select(item => item.Id),
            Is.EqualTo(new[] { new ObjectId(4000000000),
                new ObjectId(4000000000), new ObjectId(43) }));
    }

    [Test]
    public void UserDefinedShadowOfLookupIsNeitherMappedNorDiscovered()
    {
        const string original = """
            object _O(uint id) => id;
            var value = _O(42);
            """;
        string revised = ScriptObjectReferenceRewriter.Rewrite(original,
            new Dictionary<ObjectId, ObjectId>
            {
                [new ObjectId(42)] = new ObjectId(999),
            });
        Assert.That(revised, Is.EqualTo(original));
        Assert.That(ScriptReferenceAnalyzer.Analyze(revised).References, Is.Empty);
    }

    [TestCase("_O(1 + 2);")]
    [TestCase("_O();")]
    [TestCase("var v = _O(7")]
    [TestCase("_O(0);")]
    public void UnreliableSourceIsRefusedWithoutGuessing(string source)
    {
        Assert.That(() => ScriptObjectReferenceRewriter.Rewrite(source,
            new Dictionary<ObjectId, ObjectId>()),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void UnmappedReferencesRemainByteIdentical()
    {
        const string source = "_O(0x2au); _O(15U);";
        string revised = ScriptObjectReferenceRewriter.Rewrite(source,
            new Dictionary<ObjectId, ObjectId>());
        Assert.That(revised, Is.EqualTo(source));
    }

    [Test]
    public void InvalidReplacementIdIsRefused()
    {
        Assert.That(() => ScriptObjectReferenceRewriter.Rewrite("_O(1)",
            new Dictionary<ObjectId, ObjectId>
            {
                [new ObjectId(1)] = ObjectId.None,
            }), Throws.TypeOf<InvalidOperationException>());
    }
}
