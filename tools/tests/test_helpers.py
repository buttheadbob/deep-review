"""Tests for deep_tools helpers that need no Docker, game files or network: MDK project detection and ignores, the MSBuild
evaluation reader behind compiles[], and the gate's whitelist, compile-id and host-test handling."""
import importlib.util
import json
import os
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location('deep_tools', os.path.join(os.path.dirname(HERE), 'deep_tools.py'))
dt = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(dt)


def write(root, rel_path, text):
    path = os.path.join(root, *rel_path.split('/'))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8') as f:
        f.write(text)
    return path


def report(**parts):
    rep = {'diagnostics': [], 'csprojBuildDiagnostics': [], 'analyzers': {'failed': []}, 'vulnerabilities': {'projects': []},
           'release': {}, 'tests': {'projects': []}, 'gaps': []}
    rep.update(parts)
    return rep


class MdkDetection(unittest.TestCase):
    def test_pb_ini_mod_analyzers_and_plain_projects(self):
        with tempfile.TemporaryDirectory() as root:
            pb = write(root, 'Pb/Pb.csproj', '<Project><ItemGroup><PackageReference Include="Mal.Mdk2.PbPackager" /></ItemGroup></Project>')
            write(root, 'Pb/Pb.mdk.ini', '[mdk]\ntype=programmableblock\n')
            mod = write(root, 'Mod/Mod.csproj', '<Project><ItemGroup><PackageReference Include="Mal.Mdk2.ModAnalyzers" /></ItemGroup></Project>')
            write(root, 'Mod/Data/Scripts/Thing/A.cs', 'class A { }')
            noini = write(root, 'NoIni/NoIni.csproj', '<Project><ItemGroup><PackageReference Include="Mal.Mdk2.PbAnalyzers" /></ItemGroup></Project>')
            modini = write(root, 'ModIni/ModIni.csproj', '<Project><ItemGroup><PackageReference Include="Mal.Mdk2.PbAnalyzers" /></ItemGroup></Project>')
            write(root, 'ModIni/ModIni.mdk.ini', '[mdk]\ntype=mod\n')
            kinds, mods, pbs = dt.detect_kinds(root, [], [pb, mod, noini, modini])
            self.assertEqual(sorted(os.path.basename(p) for p in pbs), ['NoIni.csproj', 'Pb.csproj'])
            self.assertEqual([os.path.basename(m) for m in mods], ['Mod'])
            self.assertIn('se-mod', kinds)
            self.assertIn('pb-script', kinds)

    def test_mdk_ignores(self):
        with tempfile.TemporaryDirectory() as root:
            proj = write(root, 'P/P.csproj', '<Project />')
            write(root, 'P/P.mdk.ini', '[mdk]\nignores=obj/**/*,MDK/**/*,**/*.debug.cs\n')
            ignored, pats = dt.mdk_ignored(proj)
            self.assertEqual(pats, ['obj/**/*', 'MDK/**/*', '**/*.debug.cs'])
            p = os.path.join(root, 'P')
            self.assertTrue(ignored(os.path.join(p, 'obj', 'Release', 'x.cs')))
            self.assertTrue(ignored(os.path.join(p, 'MDK', 'Bootstrapper.cs')))
            self.assertTrue(ignored(os.path.join(p, 'Tools', 'Log.debug.cs')))
            self.assertTrue(ignored(os.path.join(p, 'Log.debug.cs')))
            self.assertFalse(ignored(os.path.join(p, 'Program.cs')))
            self.assertFalse(ignored(os.path.join(p, 'Sub', 'Program.cs')))

    def test_mod_jobs_per_script_folder(self):
        with tempfile.TemporaryDirectory() as root:
            a = write(root, 'M/Data/Scripts/One/A.cs', '')
            b = write(root, 'M/Data/Scripts/One/Sub/B.cs', '')
            c = write(root, 'M/Data/Scripts/Two/C.cs', '')
            jobs = dt.whitelist_jobs([{'name': 'mod:M', 'kind': 'se-mod', 'sources': [a, b, c]}], root)
            self.assertEqual([(j['id'], j['target'], len(j['files'])) for j in jobs], [('mod:M:One', 'mod', 2), ('mod:M:Two', 'mod', 1)])


class EvaluatedCompile(unittest.TestCase):
    def test_reads_msbuild_json(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, 'eval-0.json')
            with open(path, 'w', encoding='utf-8') as f:
                json.dump({'Properties': {'Nullable': 'enable', 'LangVersion': 'latest', 'DefineConstants': 'TRACE;RELEASE;NET10_0',
                                          'AllowUnsafeBlocks': 'True', 'TargetFramework': 'net10.0'},
                           'Items': {'Compile': [{'Identity': 'A.cs'}, {'Identity': '..\\Shared\\B.cs'}]}}, f)
            e = dt.evaluated_compile({'compile': 'csproj:P/P.csproj/net10.0', 'project': 'P/P.csproj', 'framework': 'net10.0', 'status': 'built'}, path)
            self.assertEqual((e['nullable'], e['langVersion'], e['defines'], e['unsafe'], e['fileCount'], e['evaluated']),
                             ('enable', 'latest', ['TRACE', 'RELEASE', 'NET10_0'], True, 2, True))

    def test_defaults_and_failure(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, 'eval-1.json')
            with open(path, 'w', encoding='utf-8') as f:
                json.dump({'Properties': {'TargetFramework': 'net48'}, 'Items': {'Compile': []}}, f)
            e = dt.evaluated_compile({'compile': 'c', 'project': 'p', 'framework': 'net48 (old-style)'}, path)
            self.assertEqual((e['nullable'], e['langVersion'], e['defines'], e['unsafe'], e['tfm']), ('disable', 'default', [], False, 'net48'))
            bad = os.path.join(d, 'eval-2.json')
            with open(bad, 'w', encoding='utf-8') as f:
                f.write('')
            with open(bad[:-5] + '.err', 'w', encoding='utf-8') as f:
                f.write('x.csproj : error MSB4057: The target "AddImplicitDefineConstants" does not exist in the project.\n')
            e = dt.evaluated_compile({'compile': 'c', 'project': 'p', 'framework': 'net48'}, bad)
            self.assertFalse(e['evaluated'])
            self.assertIn('MSB4057', e['evaluationError'])


class GateHandling(unittest.TestCase):
    def test_whitelist_category_and_compile_ids(self):
        diags = [{'id': 'ProhibitedMemberRule', 'tool': 'whitelist', 'message': "The type or member 'File' is prohibited", 'file': 'a.cs',
                  'line': 3, 'runtimes': ['net48'], 'compiles': ['whitelist:mod:One']},
                 {'id': 'CS0246', 'tool': 'whitelist', 'message': 'missing', 'file': 'a.cs', 'line': 4, 'runtimes': ['net48'],
                  'compiles': ['whitelist:mod:One']},
                 {'id': 'CA1000', 'tool': 'NetAnalyzers', 'message': 'm', 'file': 'b.cs', 'line': 1, 'runtimes': ['net10', 'net48'],
                  'compiles': ['magnetar/net10', 'magnetar/net48']}]
        verdict, issues, counts = dt.gate_verdict(report(diagnostics=diags), True, set(), [])
        self.assertEqual(verdict, 'FAIL')
        self.assertEqual(counts['byCategory'], {'whitelist': 1, 'compile': 1, 'analyzer': 1})
        self.assertEqual([i['compile'] for i in issues], ['whitelist:mod:One', 'whitelist:mod:One', 'magnetar/net10, magnetar/net48'])

    def test_host_test_results(self):
        tests = {'projects': [{'project': 'P', 'framework': 'net48', 'status': 'ran', 'total': 2, 'passed': 2, 'failed': 0, 'exit': 0},
                              {'project': 'F', 'framework': 'net48', 'status': 'ran', 'total': 2, 'passed': 1, 'failed': 1, 'exit': 1},
                              {'project': 'H', 'framework': 'net48', 'status': 'ran', 'total': 0, 'exit': 1, 'aborted': True},
                              {'project': 'K', 'framework': 'net48', 'status': 'ran', 'aborted': True, 'killedAtLimit': True, 'exit': None}]}
        verdict, issues, counts = dt.gate_verdict(report(tests=tests), True, set(), [])
        self.assertEqual([(i['rule'], i['file'], i['runtime']) for i in issues],
                         [('failed', 'F', 'net48'), ('aborted', 'H', 'net48'), ('aborted', 'K', 'net48')])
        self.assertIn('killed at the time limit', issues[2]['text'])

    def test_host_env_drops_secrets_and_game_paths(self):
        old = dict(os.environ)
        try:
            os.environ.update({'PULSAR_GITHUB_TOKEN': 'x', 'GITHUB_TOKEN': 'x', 'GH_TOKEN': 'x', 'SE_SERVER_ROOT': 'x', 'MY_API_KEY': 'x',
                               'PATH': os.pathsep.join([r'C:\Windows', r'E:\Torch', r'C:\Games\SpaceEngineers\Bin64'])})
            with tempfile.TemporaryDirectory() as d:
                env = dt.host_test_env(d)
                for k in ('PULSAR_GITHUB_TOKEN', 'GITHUB_TOKEN', 'GH_TOKEN', 'SE_SERVER_ROOT', 'MY_API_KEY'):
                    self.assertNotIn(k, env)
                self.assertEqual(env['PATH'], r'C:\Windows')
                self.assertTrue(env['TEMP'].startswith(d))
                self.assertEqual(env['DOTNET_CLI_TELEMETRY_OPTOUT'], '1')
        finally:
            os.environ.clear()
            os.environ.update(old)


class PathIndependence(unittest.TestCase):
    """Moving the tools folder or using a deep output folder must not break a run."""

    def test_pack_json_has_no_absolute_folders(self):
        with tempfile.TemporaryDirectory() as d:
            old = dt.PACK_MARKER
            dt.PACK_MARKER = os.path.join(d, 'pack.json')
            try:
                dt.write_pack_info({'hash': 'h', 'packs': {'Some.Pack': {'version': '1', 'files': [], 'dir': r'C:\old\place\some.pack'}}})
                stored = json.load(open(dt.PACK_MARKER, encoding='utf-8'))
            finally:
                dt.PACK_MARKER = old
        self.assertEqual(stored['packs']['Some.Pack']['dir'], 'some.pack')
        self.assertEqual(dt.pack_dir('Some.Pack'), os.path.join(dt.CACHE, 'analyzers', 'some.pack'))

    def test_target_names_become_safe_path_parts(self):
        self.assertEqual(dt.fs_name('mod:Tests'), 'mod_Tests')
        self.assertEqual(dt.fs_name('a/b\\c'), 'a_b_c')

    def test_nuget_folder_version(self):
        for given, folder in (('4.4.12', '4.4.12'), ('1.0', '1.0.0'), ('2.0.0.0', '2.0.0'), ('1.2.3.4', '1.2.3.4'), ('1.0.0-Beta2', '1.0.0-beta2')):
            self.assertEqual(dt.nuget_folder_version(given), folder)

    def test_framework_from_directory_build_props(self):
        with tempfile.TemporaryDirectory() as d:
            write(d, 'Directory.Build.props', "<Project><PropertyGroup><Tfm Condition=\"'$(Tfm)' == ''\">net10.0</Tfm></PropertyGroup></Project>")
            proj = write(d, 'src/P/P.csproj', '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>$(Tfm)</TargetFramework>'
                                               '</PropertyGroup></Project>')
            self.assertEqual(dt.parse_csproj(proj, d)['frameworks'], ['net10.0'])

    def test_mdk_local_ini_in_copy(self):
        with tempfile.TemporaryDirectory() as d:
            write(d, 'Script.csproj', '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Mal.Mdk2.References" '
                                      'Version="2.2.8" /></ItemGroup></Project>')
            self.assertEqual(dt.mdk_local_ini(d), ['Script.csproj'])
            self.assertIn('binarypath=/refs/ds', open(os.path.join(d, 'Script.mdk.local.ini'), encoding='utf-8').read())



class Suppressions(unittest.TestCase):
    """Every way a repository can turn a warning off is a gate issue (deep.globalconfig is the only place allowed)."""

    def kinds(self, d, compiled=()):
        return sorted((s['kind'], s['file']) for s in dt.find_suppressions(d, list(compiled)))

    def test_project_file_forms(self):
        with tempfile.TemporaryDirectory() as d:
            write(d, 'P/P.csproj', """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <NoWarn>$(NoWarn)</NoWarn>
    <NoWarn Condition="'$(TargetFramework)' == 'net48'">$(NoWarn);CS0618</NoWarn>
    <MSBuildWarningsAsMessages>MSB3277</MSBuildWarningsAsMessages>
    <MSBuildWarningsNotAsErrors>MSB3243</MSBuildWarningsNotAsErrors>
    <TreatWarningsAsErrors Condition="'$(Configuration)' == 'Debug'">false</TreatWarningsAsErrors>
    <WarningLevel Condition="true">2</WarningLevel>
    <RunAnalyzers Condition="true">false</RunAnalyzers>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Old" Version="1.0.0" NoWarn="NU1701" />
    <PackageReference Include="Other" Version="1.0.0"><NoWarn>NU1603</NoWarn></PackageReference>
  </ItemGroup>
</Project>""")
            found = self.kinds(d)
            # The empty <NoWarn>$(NoWarn)</NoWarn> is not a suppression; everything else is.
            self.assertEqual(found.count(('NoWarn', 'P/P.csproj')), 3)
            for kind in ('MSBuildWarningsAsMessages', 'MSBuildWarningsNotAsErrors', 'TreatWarningsAsErrors=false',
                         'WarningLevel', 'RunAnalyzers=false'):
                self.assertIn((kind, 'P/P.csproj'), found)

    def test_response_files_and_build_scripts(self):
        with tempfile.TemporaryDirectory() as d:
            write(d, 'Directory.Build.rsp', '-nowarn:CS1591\n')
            write(d, 'build.ps1', "dotnet build P.csproj -c Release '-p:NoWarn=CS0618'\n")
            write(d, 'ci.yml', 'run: dotnet build /p:NoWarn="CS1591;CS0618"\n')
            write(d, 'clean.ps1', 'dotnet build P.csproj -c Release\n')
            found = self.kinds(d)
            self.assertIn(('rsp-nowarn', 'Directory.Build.rsp'), found)
            self.assertIn(('script-nowarn', 'build.ps1'), found)
            self.assertIn(('script-nowarn', 'ci.yml'), found)
            self.assertNotIn(('script-nowarn', 'clean.ps1'), found)

    def test_source_and_editorconfig_bypasses(self):
        with tempfile.TemporaryDirectory() as d:
            write(d, 'A.cs', '// <auto-generated/>\npublic class A { }\n')
            write(d, 'B.cs', '#nullable disable warnings\npublic class B { }\n')
            write(d, 'C.cs', '#nullable disable\n[System.CodeDom.Compiler.GeneratedCode("x", "1")]\npublic class C { }\n')
            write(d, 'D.cs', '// A comment that mentions auto-generated code is fine.\npublic class D { }\n')
            write(d, '.editorconfig', '[*.cs]\ngenerated_code = true\n')
            found = self.kinds(d, ['A.cs', 'B.cs', 'C.cs', 'D.cs'])
            self.assertIn(('auto-generated', 'A.cs'), found)
            self.assertIn(('nullable-disable-warnings', 'B.cs'), found)
            self.assertIn(('GeneratedCode', 'C.cs'), found)
            self.assertNotIn(('nullable-disable', 'C.cs'), found)
            self.assertFalse(any(f == 'D.cs' for _, f in found))
            self.assertIn(('analyzer-config', '.editorconfig'), found)


if __name__ == '__main__':
    unittest.main()
