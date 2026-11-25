"""Tests for deep_tools.py --check-record (GATE-RECORD.md consumer checks 1 and 3-10).

Run: python -I -B -m unittest discover -s tools/tests -v   (from the skill folder)
Self-contained: git, the tools version and the working-tree hash are injected, so no repository or commit is needed.
One good record passes; each bad record breaks exactly one check and must fail with that check's number.
"""
import copy
import datetime
import importlib.util
import json
import os
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location('deep_tools', os.path.join(os.path.dirname(HERE), 'deep_tools.py'))
dt = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(dt)

REPO = os.path.abspath(os.path.join(tempfile.gettempdir(), 'deep-tools-test-repo'))
SHA = 'a' * 40
TREE = 'b' * 40
HEAD = 'c' * 40
TOOLS = 'f' * 64
CONTENT = 'e' * 64
NONCE = 'nonce-123'
ISSUED = '2026-11-15T20:00:00Z'
NOW = datetime.datetime(2026, 11, 15, 21, 0, 0, tzinfo=datetime.timezone.utc)
FIXTURES = os.path.join(HERE, 'fixtures')
NO_LIST = os.path.join(FIXTURES, 'does-not-exist.json')
APPROVED = '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef'


def fake_git(repo, rev):
    return {SHA + '^{tree}': TREE, 'HEAD': HEAD}.get(rev)


def good_record(head=SHA):
    rec = {
        'schema': 1, 'kind': 'gate', 'gateMode': True, 'nonce': NONCE, 'repo': REPO, 'head': head, 'headTree': TREE,
        'toolsVersion': TOOLS, 'options': {'skip': [], 'base': None, 'manifests': [], 'affinityMask': '0xffffff'},
        'startedUtc': '2026-11-15T20:10:00Z', 'finishedUtc': '2026-11-15T20:20:00Z', 'fresh': True,
        'projectKind': 'dotnet', 'projectKinds': ['dotnet'], 'languages': {'C#': {'files': 3, 'checked': True, 'by': ['csproj build']}},
        'runtimes': ['csproj:App/App.csproj/net10.0'], 'verdict': 'PASS',
        'counts': {'issues': 0, 'gaps': 0, 'byCategory': {}, 'byRule': {}, 'byRuntime': {}},
        'issues': [], 'gaps': [], 'suppressions': [], 'suppressedDiagnostics': [], 'notCovered': [],
    }
    if head.startswith('WORKTREE@'):
        rec['headTree'] = head
        rec['contentSha256'] = CONTENT
    return rec


class CheckRecordTests(unittest.TestCase):
    def check(self, rec, head=SHA, nonce=NONCE, issued=ISSUED, repo=REPO, tools=TOOLS, content=CONTENT, approved=NO_LIST):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, 'gate.json')
            with open(path, 'w', encoding='utf-8') as f:
                json.dump(rec, f)
            return dt.check_record(path, nonce, issued, repo, head, now=NOW, tools_version_fn=lambda: tools,
                                   git_fn=fake_git, content_fn=lambda r: content, approved_path=approved)

    def assertFailsCheck(self, reasons, number):
        self.assertTrue(reasons, 'the record should fail')
        self.assertTrue(any(r.startswith(f'{number}:') for r in reasons), f'expected a check {number} failure, got {reasons}')

    def test_good_commit_record_passes(self):
        self.assertEqual(self.check(good_record()), [])

    def test_good_worktree_record_passes(self):
        self.assertEqual(self.check(good_record('WORKTREE@' + HEAD + '+dirty'), head='WORKTREE'), [])

    def test_wrong_nonce(self):
        self.assertFailsCheck(self.check(good_record(), nonce='other'), 3)

    def test_started_before_nonce_issued(self):
        rec = good_record()
        rec['startedUtc'] = '2026-11-15T19:59:59Z'
        self.assertFailsCheck(self.check(rec), 4)

    def test_finished_in_future(self):
        rec = good_record()
        rec['finishedUtc'] = '2026-11-15T22:00:00Z'
        self.assertFailsCheck(self.check(rec), 4)

    def test_not_fresh(self):
        rec = good_record()
        rec['fresh'] = False
        self.assertFailsCheck(self.check(rec), 5)

    def test_tools_version_differs(self):
        self.assertFailsCheck(self.check(good_record(), tools='0' * 64), 6)

    def test_repo_differs(self):
        self.assertFailsCheck(self.check(good_record(), repo=os.path.join(REPO, 'other')), 7)

    def test_head_differs(self):
        self.assertFailsCheck(self.check(good_record('d' * 40)), 8)

    def test_head_tree_differs(self):
        rec = good_record()
        rec['headTree'] = '9' * 40
        self.assertFailsCheck(self.check(rec), 8)

    def test_worktree_content_differs(self):
        self.assertFailsCheck(self.check(good_record('WORKTREE@' + HEAD), head='WORKTREE', content='1' * 64), 8)

    def test_worktree_head_sha_differs(self):
        self.assertFailsCheck(self.check(good_record('WORKTREE@' + SHA), head='WORKTREE'), 8)

    def test_dir_head_never_qualifies(self):
        rec = good_record('DIR')
        rec['headTree'] = 'DIR'
        rec['contentSha256'] = CONTENT
        self.assertFailsCheck(self.check(rec, head='WORKTREE'), 8)
        self.assertFailsCheck(self.check(rec, head=SHA), 8)

    def test_issues(self):
        rec = good_record()
        rec['counts']['issues'] = 1
        rec['issues'] = [{'category': 'analyzer', 'rule': 'CA2000', 'file': 'a.cs', 'line': 1, 'runtime': 'net10', 'text': 'x'}]
        rec['verdict'] = 'FAIL'
        self.assertFailsCheck(self.check(rec), 9)

    def test_issue_count_alone(self):
        rec = good_record()
        rec['counts']['issues'] = 2
        self.assertFailsCheck(self.check(rec), 9)

    def test_gap(self):
        rec = good_record()
        rec['counts']['gaps'] = 1
        rec['gaps'] = ['tests not on net48']
        self.assertFailsCheck(self.check(rec), 9)

    def test_suppression(self):
        rec = good_record()
        rec['suppressions'] = [{'kind': 'pragma', 'file': 'a.cs', 'line': 3, 'text': '#pragma warning disable CA2000'}]
        self.assertFailsCheck(self.check(rec), 9)

    def test_not_covered(self):
        rec = good_record()
        rec['notCovered'] = ['something']
        self.assertFailsCheck(self.check(rec), 9)

    def test_skip_used(self):
        rec = good_record()
        rec['options']['skip'] = ['tests']
        self.assertFailsCheck(self.check(rec), 9)

    def test_verdict_not_pass(self):
        rec = good_record()
        rec['verdict'] = 'ERROR'
        self.assertFailsCheck(self.check(rec), 9)

    def test_unchecked_language(self):
        rec = good_record()
        rec['languages']['Batch'] = {'files': 2, 'checked': False, 'by': []}
        self.assertFailsCheck(self.check(rec), 10)

    def test_gate_mode_false(self):
        rec = good_record()
        rec['gateMode'] = False
        self.assertFailsCheck(self.check(rec), 1)

    def test_kind_not_gate(self):
        rec = good_record()
        rec['kind'] = 'report'
        self.assertFailsCheck(self.check(rec), 1)

    def test_missing_field(self):
        rec = good_record()
        del rec['options']
        self.assertFailsCheck(self.check(rec), 1)

    def test_not_json(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, 'gate.json')
            with open(path, 'w', encoding='utf-8') as f:
                f.write('{not json')
            reasons = dt.check_record(path, NONCE, ISSUED, REPO, SHA, now=NOW, tools_version_fn=lambda: TOOLS, git_fn=fake_git,
                                      content_fn=lambda r: CONTENT)
        self.assertFailsCheck(reasons, 1)

    def test_each_bad_record_fails_only_where_expected(self):
        # A good record copied and changed in one field must not pick up unrelated failures.
        rec = copy.deepcopy(good_record())
        rec['fresh'] = False
        self.assertEqual([r.split(':')[0] for r in self.check(rec)], ['5'])

    # ---- step 6: the approved-tools list (Flow's C# gate port)

    def approved_record(self, version):
        rec = good_record()
        rec['toolsVersion'] = version
        return rec

    def test_approved_version_exact_match_passes(self):
        self.assertEqual(self.check(self.approved_record(APPROVED), approved=os.path.join(FIXTURES, 'approved-ok.json')), [])

    def test_approved_case_differs_fails(self):
        reasons = self.check(self.approved_record(APPROVED.upper()), approved=os.path.join(FIXTURES, 'approved-ok.json'))
        self.assertFailsCheck(reasons, 6)

    def test_approved_prefix_fails(self):
        reasons = self.check(self.approved_record(APPROVED[:16]), approved=os.path.join(FIXTURES, 'approved-ok.json'))
        self.assertFailsCheck(reasons, 6)

    def test_version_not_listed_fails(self):
        self.assertFailsCheck(self.check(self.approved_record('9' * 64), approved=os.path.join(FIXTURES, 'approved-ok.json')), 6)

    def test_empty_list_like_today_fails_unknown_version(self):
        self.assertFailsCheck(self.check(self.approved_record(APPROVED), approved=os.path.join(FIXTURES, 'approved-empty.json')), 6)

    def test_empty_list_still_accepts_installed_version(self):
        self.assertEqual(self.check(good_record(), approved=os.path.join(FIXTURES, 'approved-empty.json')), [])

    def test_missing_list_only_installed_version_counts(self):
        self.assertEqual(self.check(good_record(), approved=NO_LIST), [])
        self.assertFailsCheck(self.check(self.approved_record(APPROVED), approved=NO_LIST), 6)

    def unparseable(self, fixture):
        reasons = self.check(self.approved_record(APPROVED), approved=os.path.join(FIXTURES, fixture))
        self.assertFailsCheck(reasons, 6)
        self.assertTrue(any('cannot be parsed' in r for r in reasons), reasons)

    def test_approved_wrong_schema_is_unparseable(self):
        self.unparseable('approved-wrong-schema.json')

    def test_approved_no_array_is_unparseable(self):
        self.unparseable('approved-no-array.json')

    def test_approved_entry_without_version_is_unparseable(self):
        self.unparseable('approved-entry-no-version.json')

    def test_cli_hidden_approved_tools_option(self):
        orig_version, orig_git = dt.tools_version, dt._git_rev
        dt.tools_version, dt._git_rev = (lambda: TOOLS), fake_git
        try:
            rec = self.approved_record(APPROVED)
            rec['startedUtc'] = '2026-11-15T20:10:00Z'
            with tempfile.TemporaryDirectory() as d:
                path = os.path.join(d, 'gate.json')
                with open(path, 'w', encoding='utf-8') as f:
                    json.dump(rec, f)
                argv = ['--check-record', path, '--nonce', NONCE, '--nonce-issued', ISSUED, '--repo', REPO, '--head', SHA]
                self.assertEqual(dt.check_record_cli(argv + ['--approved-tools', os.path.join(FIXTURES, 'approved-ok.json')]), 0)
                self.assertEqual(dt.check_record_cli(argv + ['--approved-tools', os.path.join(FIXTURES, 'approved-empty.json')]), 3)
        finally:
            dt.tools_version, dt._git_rev = orig_version, orig_git

    def test_content_sha256_algorithm(self):
        with tempfile.TemporaryDirectory() as d:
            os.makedirs(os.path.join(d, 'bin'))
            for name, data in (('b.txt', b'two'), ('a.txt', b'one'), ('bin/c.txt', b'three')):
                with open(os.path.join(d, name), 'wb') as f:
                    f.write(data)
            import hashlib
            h = hashlib.sha256()
            for p, data in (('a.txt', b'one'), ('b.txt', b'two'), ('bin/c.txt', b'three')):
                h.update(p.encode() + b'\0' + hashlib.sha256(data).digest())
            self.assertEqual(dt.content_sha256(d, ['bin/c.txt', 'b.txt', 'a.txt']), h.hexdigest())


if __name__ == '__main__':
    unittest.main()
