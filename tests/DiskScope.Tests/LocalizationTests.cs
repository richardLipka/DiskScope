using System.Text.RegularExpressions;

namespace DiskScope.Tests;

/// <summary>Checks the UI texts in the source: every key used exists, none is defined twice, all have Czech and English.</summary>
public class LocalizationTests
{
    const string Literal = @"""(?:[^""\\]|\\.)*""";

    // ["Key"] = ("czech", "english")  — the two texts may be on separate lines
    static readonly Regex Definition = new(@"\[""(?<key>\w+)""\]\s*=\s*\(\s*(?<cs>" + Literal + @")\s*,\s*(?<en>" + Literal + @")\s*\)");
    static readonly Regex AnyDefinition = new(@"^\s*\[""(?<key>\w+)""\]\s*=", RegexOptions.Multiline);
    // L.T( … ) up to the first ')' or ',' — covers L.T("Key") and L.T(cond ? "KeyA" : "KeyB")
    static readonly Regex Use = new(@"L\.T\((?<expr>[^),]*)");
    static readonly Regex KeyLiteral = new(@"""(?<key>[A-Z]\w+)""");
    // Keys kept in arrays or tuples: string[] cols = ["ColName", …], ("ColPath", 330, …), (30, false, "Older30d")
    static readonly Regex KeyList = new(@"string\[\] cols = \[(?<list>[^\]]*)\]");
    static readonly Regex KeyTuple = new(@"\(""(?<key>Col\w+)"", \d+|\(\d+(?: \* \d+)?, (?:true|false), ""(?<key>\w+)""\)");

    static string SourceDir([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "DiskScope"));

    static string StringsFile => File.ReadAllText(Path.Combine(SourceDir(), "Strings.cs"));

    static HashSet<string> DefinedKeys() => AnyDefinition.Matches(StringsFile).Select(m => m.Groups["key"].Value).ToHashSet();

    [Fact]
    public void NoKeyIsDefinedTwice()
    {
        var keys = AnyDefinition.Matches(StringsFile).Select(m => m.Groups["key"].Value).ToList();
        Assert.True(keys.Count > 100);
        Assert.Empty(keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key));
    }

    [Fact]
    public void EveryTextHasCzechAndEnglish()
    {
        var complete = Definition.Matches(StringsFile)
            .Where(m => m.Groups["cs"].Length > 2 && m.Groups["en"].Length > 2)
            .Select(m => m.Groups["key"].Value).ToHashSet();
        Assert.Empty(DefinedKeys().Except(complete));
    }

    static readonly Regex Placeholder = new(@"\{(\d+)\}");

    /// <summary>A placeholder missing in one language, or a stray brace, would make string.Format throw at run time.</summary>
    [Fact]
    public void BothLanguagesUseTheSamePlaceholders()
    {
        var problems = new List<string>();
        foreach (Match m in Definition.Matches(StringsFile))
        {
            string cs = m.Groups["cs"].Value, en = m.Groups["en"].Value;
            var a = Placeholder.Matches(cs).Select(x => x.Value).ToHashSet();
            var b = Placeholder.Matches(en).Select(x => x.Value).ToHashSet();
            if (!a.SetEquals(b)) problems.Add(m.Groups["key"].Value + ": placeholders differ");
            foreach (string text in new[] { cs, en })
                if (Placeholder.Replace(text, "").IndexOfAny(['{', '}']) >= 0) problems.Add(m.Groups["key"].Value + ": stray brace");
        }
        Assert.Empty(problems);
    }

    [Fact]
    public void EveryUsedKeyExists()
    {
        var used = new HashSet<string>();
        foreach (string file in Directory.EnumerateFiles(SourceDir(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            string code = File.ReadAllText(file);
            foreach (Match m in Use.Matches(code))
            {
                string expr = m.Groups["expr"].Value;
                foreach (Match k in KeyLiteral.Matches(expr))
                {
                    // "Safety" + safety: a prefix of a key built at run time (those are listed below)
                    bool concatenated = expr[(k.Index + k.Length)..].TrimStart().StartsWith('+') || expr[..k.Index].TrimEnd().EndsWith('+');
                    if (!concatenated) used.Add(k.Groups["key"].Value);
                }
            }
            foreach (Match m in KeyList.Matches(code))
                foreach (Match k in KeyLiteral.Matches(m.Groups["list"].Value)) used.Add(k.Groups["key"].Value);
            foreach (Match m in KeyTuple.Matches(code)) used.Add(m.Groups["key"].Value);
        }
        // Keys built at run time from enum names.
        foreach (string s in new[] { "Safe", "Caution", "System" }) used.UnionWith([$"Safety{s}", $"Safety{s}Short"]);
        foreach (string a in new[] { "None", "DeleteContents", "Delete" }) used.Add($"Action{a}");
        foreach (string c in new[] { "Likely", "Possible" }) used.Add($"Orphan{c}");
        foreach (string k in new[] { "StoreApp", "ProgramFiles", "AppData", "ProgramData" }) used.UnionWith([$"OrphanKind{k}", $"OrphanWhy{k}"]);
        foreach (string s in new[] { "Waiting", "Running", "Done", "Cancelled" }) used.Add($"QueueState{s}");
        foreach (string k in new[] { "Video", "Image", "Audio", "Archive", "DiskImage", "Program", "Document", "Code", "Other" }) used.Add($"Kind{k}");

        Assert.True(used.Count > 100);
        Assert.Empty(used.Except(DefinedKeys()));
    }
}
