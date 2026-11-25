#!/usr/bin/env python3
"""Runtime matrix: builds a harness for net48 and net10.0, runs both (and Mono in Docker when a local mono image exists),
and compares exit code, output and exceptions per check.

usage:
  python run_matrix.py <harness dir> [--out <dir>] [--affinity-mask 0x00FFFFFF] [--timeout 300]
  python run_matrix.py --new <name> [--dest <dir>]      copy the template to start a new harness

Only harness code runs here (code written for the harness, with game types stubbed); it runs natively for net48
(Windows has .NET Framework 4.8) and on the local .NET 10 runtime, with no window (CREATE_NO_WINDOW).
Writes <out>/matrix.json and <out>/matrix.txt. Exit codes: 0 ran on every runtime, 2 ran with a runtime missing,
1 could not build or run.
"""
import argparse
import ctypes
import json
import os
import re
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
TEMPLATE = os.path.join(HERE, 'template')
ENV = {'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'TESTINGPLATFORM_TELEMETRY_OPTOUT': '1', 'DOTNET_NOLOGO': '1'}
CREATE_NO_WINDOW = 0x08000000 if os.name == 'nt' else 0
RESULT = re.compile(r'^RESULT (\S.*?) (PASS|FAIL)(?: (.*))?$')


def run(cmd, cwd=None, timeout=600):
    env = dict(os.environ)
    env.update(ENV)
    try:
        r = subprocess.run(cmd, cwd=cwd, env=env, timeout=timeout, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                           stdin=subprocess.DEVNULL, creationflags=CREATE_NO_WINDOW)
        return r.returncode, r.stdout.decode('utf-8', 'replace'), r.stderr.decode('utf-8', 'replace')
    except subprocess.TimeoutExpired as e:
        return 124, (e.stdout or b'').decode('utf-8', 'replace'), f'timed out after {timeout}s'
    except FileNotFoundError as e:
        return 127, '', str(e)


def set_affinity(mask):
    if os.name != 'nt':
        try:
            os.sched_setaffinity(0, {i for i in range(64) if mask >> i & 1})
        except (AttributeError, OSError):
            pass
        return
    k = ctypes.WinDLL('kernel32', use_last_error=True)
    k.GetCurrentProcess.restype = ctypes.c_void_p
    k.SetProcessAffinityMask.argtypes = [ctypes.c_void_p, ctypes.c_size_t]
    k.SetProcessAffinityMask(k.GetCurrentProcess(), mask)


def cpuset(mask):
    cores = [i for i in range(64) if mask >> i & 1]
    return f'{cores[0]}-{cores[-1]}' if cores and cores == list(range(cores[0], cores[-1] + 1)) else ','.join(map(str, cores))


def parse(output):
    checks = {}
    for line in output.splitlines():
        m = RESULT.match(line.strip())
        if m:
            checks[m.group(1)] = {'status': m.group(2), 'detail': (m.group(3) or '').strip()}
    summary = next((l for l in output.splitlines() if l.startswith('SUMMARY')), None)
    return checks, summary


def mono_image():
    code, out, _ = run(['docker', 'image', 'ls', '--format', '{{.Repository}}:{{.Tag}}'], timeout=60)
    if code:
        return None
    return next((i for i in out.split() if i.split(':')[0] in ('mono', 'library/mono')), None)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('harness', nargs='?')
    ap.add_argument('--out')
    ap.add_argument('--new')
    ap.add_argument('--dest')
    ap.add_argument('--affinity-mask', type=lambda s: int(s, 0), default=0x00FFFFFF)
    ap.add_argument('--timeout', type=int, default=300)
    args = ap.parse_args()
    if args.new:
        dest = os.path.abspath(args.dest or os.path.join(os.getcwd(), args.new))
        shutil.copytree(TEMPLATE, dest)
        print(dest)
        return 0
    if not args.harness:
        ap.error('give a harness folder or --new')
    set_affinity(args.affinity_mask)
    harness = os.path.abspath(args.harness)
    out = os.path.abspath(args.out or os.path.join(harness, 'out'))
    build = os.path.join(out, 'build')
    shutil.rmtree(build, ignore_errors=True)
    shutil.copytree(harness, build, ignore=shutil.ignore_patterns('bin', 'obj', 'out'))
    if not os.path.isfile(os.path.join(build, 'Matrix.cs')):
        shutil.copy2(os.path.join(TEMPLATE, 'Matrix.cs'), build)
    report = {'harness': harness, 'runtimes': {}, 'checks': {}, 'differences': []}
    code, so, se = run(['dotnet', 'build', 'Harness.csproj', '-c', 'Release', '-m:4', '-nologo', '-v:quiet'], cwd=build, timeout=900)
    run(['dotnet', 'build-server', 'shutdown'], timeout=120)
    report['build'] = {'exit': code, 'output': (so + se)[-3000:]}
    if code:
        write(out, report)
        print(f'build failed:\n{(so + se)[-3000:]}')
        return 1
    bin_ = os.path.join(build, 'bin', 'Release')
    plans = []
    exe48 = os.path.join(bin_, 'net48', 'Harness.exe')
    if os.name == 'nt' and os.path.isfile(exe48):
        plans.append(('net48', [exe48]))
    else:
        report['runtimes']['net48'] = {'status': 'not run: needs Windows with .NET Framework 4.8 (or Mono)'}
    dll10 = os.path.join(bin_, 'net10.0', 'Harness.dll')
    if os.path.isfile(dll10):
        plans.append(('net10', ['dotnet', dll10]))
    mono = mono_image()
    if mono and os.path.isfile(exe48):
        plans.append(('mono', ['docker', 'run', '--rm', '--network', 'none', f'--cpuset-cpus={cpuset(args.affinity_mask)}',
                               '-v', f'{os.path.join(bin_, "net48")}:/app:ro', mono, 'mono', '/app/Harness.exe']))
    else:
        report['runtimes']['mono'] = {'status': 'not run: no local mono image (never pulled)'}
    for name, cmd in plans:
        code, so, se = run(cmd, timeout=args.timeout)
        checks, summary = parse(so)
        report['runtimes'][name] = {'status': 'ran', 'exit': code, 'summary': summary, 'stderr': se[-1500:],
                                    'output': so[-6000:]}
        for check, res in checks.items():
            report['checks'].setdefault(check, {})[name] = res
    ran = [n for n, r in report['runtimes'].items() if r.get('status') == 'ran']
    for check, per in report['checks'].items():
        statuses = {per.get(r, {}).get('status', 'MISSING') for r in ran}
        if len(statuses) > 1:
            report['differences'].append({'check': check, **{r: per.get(r, {}).get('status', 'MISSING') for r in ran}})
    write(out, report)
    print(open(os.path.join(out, 'matrix.txt'), encoding='utf-8').read())
    return 0 if 'net48' in ran and 'net10' in ran else 2


def write(out, report):
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, 'matrix.json'), 'w', encoding='utf-8') as f:
        json.dump(report, f, indent=1)
    ran = [n for n, r in report['runtimes'].items() if r.get('status') == 'ran']
    lines = [f'harness {report["harness"]}', 'runtimes: ' + ', '.join(f'{n} ({r.get("summary") or r.get("status")})' for n, r in report['runtimes'].items()), '']
    width = max([len(c) for c in report['checks']] + [10])
    lines.append(f'{"check":<{width}}  ' + '  '.join(f'{r:<6}' for r in ran))
    for check, per in report['checks'].items():
        lines.append(f'{check:<{width}}  ' + '  '.join(f'{per.get(r, {}).get("status", "-"):<6}' for r in ran))
        for r in ran:
            d = per.get(r, {}).get('detail')
            if d and per[r]['status'] == 'FAIL':
                lines.append(f'{"":<{width}}    {r}: {d[:200]}')
    lines.append('')
    if report['differences']:
        lines.append('DIFFERS BETWEEN RUNTIMES: ' + '; '.join(
            d['check'] + ' (' + ', '.join(f'{r} {d[r]}' for r in ran if r in d) + ')' for d in report['differences']))
    else:
        lines.append('no check differs between runtimes')
    with open(os.path.join(out, 'matrix.txt'), 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines) + '\n')


if __name__ == '__main__':
    sys.exit(main())
