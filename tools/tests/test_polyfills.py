"""Tests for the compiler polyfill rule: MA0182 (unused internal type) is not reported on the types the C# compiler itself
needs on netstandard2.0/net48 (IsExternalInit, RequiredMemberAttribute, CompilerFeatureRequiredAttribute,
SetsRequiredMembersAttribute), and only when they have the real type's shape."""
import importlib.util
import os
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location('deep_tools', os.path.join(os.path.dirname(HERE), 'deep_tools.py'))
dt = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(dt)


def recognised(source, line):
    code, masked = dt._cs_strip(source)
    return dt.compiler_polyfill(code, masked, line)


IS_EXTERNAL_INIT = """// Needed by init accessors and records on netstandard2.0.
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
"""

REQUIRED_MEMBERS = """using System.ComponentModel;

namespace System.Runtime.CompilerServices;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
[EditorBrowsable(EditorBrowsableState.Never)]
internal sealed class RequiredMemberAttribute : Attribute
{
}

/* The compiler emits this on types with required members. */
[AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
internal sealed class CompilerFeatureRequiredAttribute : Attribute
{
    public CompilerFeatureRequiredAttribute(string featureName)
    {
        FeatureName = featureName;
    }

    public string FeatureName { get; }
    public bool IsOptional { get; init; }
    public const string RefStructs = nameof(RefStructs);
    public const string RequiredMembers = "RequiredMembers";
}
"""

SETS_REQUIRED = """namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute { }
}
"""


class Recognised(unittest.TestCase):
    def test_real_polyfills_pass(self):
        self.assertEqual('System.Runtime.CompilerServices.IsExternalInit', recognised(IS_EXTERNAL_INIT, 5))
        self.assertEqual('System.Runtime.CompilerServices.RequiredMemberAttribute', recognised(REQUIRED_MEMBERS, 7))
        self.assertEqual('System.Runtime.CompilerServices.CompilerFeatureRequiredAttribute', recognised(REQUIRED_MEMBERS, 13))
        self.assertEqual('System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute', recognised(SETS_REQUIRED, 4))

    def test_only_the_declaration_on_the_reported_line(self):
        self.assertIsNone(recognised(IS_EXTERNAL_INIT, 4))

    def test_wrong_namespace_fails(self):
        self.assertIsNone(recognised('namespace MyPlugin.Compat\n{\n    internal static class IsExternalInit { }\n}\n', 3))
        self.assertIsNone(recognised('internal static class IsExternalInit { }\n', 1))
        self.assertIsNone(recognised('namespace System.Runtime.CompilerServices.Extra;\ninternal static class IsExternalInit { }\n', 2))
        self.assertIsNone(recognised('namespace System.Runtime.CompilerServices\n{\n    internal static class Outer\n    {\n'
                                     '        internal static class IsExternalInit { }\n    }\n}\n', 5))
        self.assertIsNone(recognised('namespace System.Runtime.CompilerServices\n{\n    internal sealed class SetsRequiredMembersAttribute : Attribute { }\n}\n', 3))

    def test_public_or_other_modifiers_fail(self):
        self.assertIsNone(recognised('namespace System.Runtime.CompilerServices\n{\n    public static class IsExternalInit { }\n}\n', 3))
        self.assertIsNone(recognised('namespace System.Runtime.CompilerServices\n{\n    static class IsExternalInit { }\n}\n', 3))
        self.assertIsNone(recognised('namespace System.Runtime.CompilerServices\n{\n    internal static partial class IsExternalInit { }\n}\n', 3))

    def test_extra_members_attributes_or_bases_fail(self):
        self.assertIsNone(recognised('namespace System.Runtime.CompilerServices\n{\n    internal static class IsExternalInit\n    {\n'
                                     '        public static void Run() { }\n    }\n}\n', 3))
        self.assertIsNone(recognised('namespace System.Runtime.CompilerServices\n{\n    [Serializable]\n    internal static class IsExternalInit { }\n}\n', 4))
        self.assertIsNone(recognised('namespace System.Runtime.CompilerServices\n{\n    internal sealed class RequiredMemberAttribute : Attribute\n    {\n'
                                     '        public int Count;\n    }\n}\n', 3))
        self.assertIsNone(recognised('namespace System.Runtime.CompilerServices\n{\n    internal sealed class RequiredMemberAttribute { }\n}\n', 3))
        extra = REQUIRED_MEMBERS.replace('    public string FeatureName { get; }', '    public string FeatureName { get; }\n    public string Note { get; set; }')
        self.assertIsNone(recognised(extra, 13))
        no_ctor = REQUIRED_MEMBERS.replace('    public CompilerFeatureRequiredAttribute(string featureName)\n    {\n        FeatureName = featureName;\n    }\n', '')
        self.assertIsNone(recognised(no_ctor, 13))

    def test_other_names_fail(self):
        self.assertIsNone(recognised('namespace System.Runtime.CompilerServices\n{\n    internal static class Unused { }\n}\n', 3))


class Dropped(unittest.TestCase):
    def test_only_ma0182_on_polyfills_leaves_the_list(self):
        with tempfile.TemporaryDirectory() as root:
            os.makedirs(os.path.join(root, 'src', 'Api'))
            with open(os.path.join(root, 'src', 'Api', 'IsExternalInit.cs'), 'w', encoding='utf-8') as f:
                f.write(IS_EXTERNAL_INIT)
            with open(os.path.join(root, 'src', 'Api', 'Unused.cs'), 'w', encoding='utf-8') as f:
                f.write('namespace Api\n{\n    internal sealed class Unused { }\n}\n')
            with open(os.path.join(root, 'src', 'Api', 'Lookalike.cs'), 'w', encoding='utf-8') as f:
                f.write('namespace Api\n{\n    internal static class IsExternalInit { }\n}\n')
            diags = [
                {'id': 'MA0182', 'file': 'src/Api/IsExternalInit.cs', 'line': 5, 'seenOn': ['csproj-build/net48']},
                {'id': 'MA0182', 'file': 'src/Api/Unused.cs', 'line': 3},
                {'id': 'MA0182', 'file': 'src/Api/Lookalike.cs', 'line': 3},
                {'id': 'CA1812', 'file': 'src/Api/IsExternalInit.cs', 'line': 5},
                {'id': 'MA0182', 'file': 'src/Api/Missing.cs', 'line': 1},
            ]
            dropped = []
            kept = dt.drop_compiler_polyfills(diags, root, dropped)
            self.assertEqual([('MA0182', 'src/Api/Unused.cs'), ('MA0182', 'src/Api/Lookalike.cs'), ('CA1812', 'src/Api/IsExternalInit.cs'),
                              ('MA0182', 'src/Api/Missing.cs')], [(d['id'], d['file']) for d in kept])
            self.assertEqual([{'id': 'MA0182', 'file': 'src/Api/IsExternalInit.cs', 'line': 5, 'type': 'System.Runtime.CompilerServices.IsExternalInit',
                               'seenOn': ['csproj-build/net48']}], dropped)


if __name__ == '__main__':
    unittest.main()
