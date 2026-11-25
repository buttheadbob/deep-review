#!/usr/bin/env python3
"""Automated tool half of the deep-review skill.

usage:
  python deep_tools.py --repo <dir> --head <sha|WORKTREE> [--base <sha>] [--manifest <xml>...] --out <dir>

Gets the tree at --head (git archive, or the working tree with --head WORKTREE), finds the plugin's hub manifests and
host projects, compiles each target the way its loader does (DeepScan) on every runtime it ships on, runs every analyzer
pack plus DeepRules, compares with --base, maps the diff to members and their callers up to entry points, and writes
<out>/tools-report.json, tools-report.md and tools-report.sarif.

Headless and unattended: no prompts, no windows. Never writes to the repo and runs only read-only git commands.
Plugin code is never run on the host: builds, restores and tests of the plugin's own projects run in the local
mcr.microsoft.com/dotnet/sdk:10.0 image (--cpuset-cpus from the affinity mask). Nothing is pulled or built as an image.

Exit codes: 0 ran, 2 ran with gaps, 1 could not run.
"""
import argparse
import base64
import collections
import ctypes
import datetime
import fnmatch
import glob
import hashlib
import io
import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import tarfile
import time
import xml.etree.ElementTree as ET

TOOLS = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(TOOLS, 'cache')
PACK_PROJECT = os.path.join(TOOLS, 'analyzers', 'AnalyzerPack.csproj')
GLOBALCONFIG = os.path.join(TOOLS, 'analyzers', 'deep.globalconfig')
BANNED = os.path.join(TOOLS, 'analyzers', 'BannedSymbols.txt')
CUSTOM_ANALYZERS = os.path.join(TOOLS, 'bin', 'analyzers')
DEEPSCAN_SRC = os.path.join(TOOLS, 'DeepScan')
NUGET_PACKAGES = os.path.join(CACHE, 'nuget', 'packages')
DOCKER_NUGET = os.path.join(CACHE, 'nuget-docker')
DOTNET_ENV = {'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'TESTINGPLATFORM_TELEMETRY_OPTOUT': '1', 'DOTNET_NOLOGO': '1'}
IMAGES = {
    'magnetar': 'ghcr.io/cometworks/magnetar:latest',
    'legacy': 'plugincheck-magnetar-legacy:latest',
    'torch': 'plugincheck-torch-proton:latest',
    'quasar': 'ghcr.io/cometworks/quasar:latest',
    'sdk': 'mcr.microsoft.com/dotnet/sdk:10.0',
}
DS_VOLUMES = ['plugincheck-ds-magnetar', 'plugincheck-ds-legacy', 'plugincheck-ds-torch']
ALLOWED_GIT = {'rev-parse', 'archive', 'diff', 'ls-files', 'show', 'log', 'merge-base', 'cat-file'}
SKIP_DIRS = {'bin', 'obj', '.git', '.vs', '.idea', 'node_modules', 'packages'}
CREATE_NO_WINDOW = 0x08000000 if os.name == 'nt' else 0
HOST_ROSLYN = (5, 12)
TICK_RE = re.compile(r'^(Update\w*|Draw\w*|HandleInput|Simulate\w*|\w*Tick\w*)$')
SCOPES = ['in-diff-line', 'in-changed-member', 'in-affected-member', 'elsewhere', 'no-base']

LOG = None
GAPS = []
NOT_RUN = {}


# ------------------------------------------------------------------------------------------------ plumbing

def log(msg):
    line = f'[{datetime.datetime.now().strftime("%H:%M:%S")}] {msg}'
    print(line, flush=True)
    if LOG:
        LOG.write(line + '\n')
        LOG.flush()


def gap(msg):
    if msg not in GAPS:
        GAPS.append(msg)
        log('GAP ' + msg)


def run(cmd, cwd=None, timeout=1800, env=None, input_bytes=None, capture=True, binary=False):
    """Runs a command with no window, telemetry off, and returns CompletedProcess (returncode 124 on timeout)."""
    full_env = dict(os.environ)
    full_env.update(DOTNET_ENV)
    if env:
        full_env.update(env)
    try:
        r = subprocess.run(cmd, cwd=cwd, timeout=timeout, env=full_env, input=input_bytes,
                           stdout=subprocess.PIPE if capture else None, stderr=subprocess.PIPE if capture else None,
                           creationflags=CREATE_NO_WINDOW, stdin=subprocess.DEVNULL if input_bytes is None else None)
        if not binary and capture:
            r.stdout = (r.stdout or b'').decode('utf-8', 'replace')
            r.stderr = (r.stderr or b'').decode('utf-8', 'replace')
        return r
    except subprocess.TimeoutExpired as e:
        return subprocess.CompletedProcess(cmd, 124, (e.stdout or b'').decode('utf-8', 'replace') if not binary else e.stdout,
                                           f'timed out after {timeout}s')
    except FileNotFoundError as e:
        return subprocess.CompletedProcess(cmd, 127, '' if not binary else b'', str(e))


def set_affinity(mask):
    """Pins this process (and so every child it starts) to the given cores, so the user's cores stay free."""
    if os.name != 'nt':
        try:
            os.sched_setaffinity(0, {i for i in range(64) if mask >> i & 1})
        except (AttributeError, OSError):
            pass
        return True
    try:
        kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel32.GetCurrentProcess.restype = ctypes.c_void_p
        kernel32.SetProcessAffinityMask.argtypes = [ctypes.c_void_p, ctypes.c_size_t]
        return bool(kernel32.SetProcessAffinityMask(kernel32.GetCurrentProcess(), mask))
    except OSError:
        return False


def cpuset(mask):
    cores = [i for i in range(64) if mask >> i & 1]
    if not cores:
        return '0'
    ranges, start, prev = [], cores[0], cores[0]
    for c in cores[1:] + [None]:
        if c is not None and c == prev + 1:
            prev = c
            continue
        ranges.append(f'{start}-{prev}' if start != prev else str(start))
        if c is not None:
            start = prev = c
    return ','.join(ranges)


def git(repo, *args, binary=False, timeout=600):
    if args[0] not in ALLOWED_GIT:
        raise RuntimeError(f'git {args[0]} is not allowed here (read-only tool)')
    return run(['git', '--no-optional-locks', '-c', 'core.fsmonitor=false', '-C', repo, *args], binary=binary, timeout=timeout)


def docker(*args, timeout=900):
    allowed = {('image', 'ls'), ('image', 'inspect'), ('create',), ('cp',), ('rm',), ('run',), ('volume', 'inspect'), ('volume', 'ls')}
    key = tuple(args[:2]) if args[0] in ('image', 'volume') else (args[0],)
    if key not in allowed:
        raise RuntimeError(f'docker {" ".join(args[:2])} is not allowed here')
    if args[0] == 'run' and '--rm' not in args:
        raise RuntimeError('docker run must use --rm')
    return run(['docker', *args], timeout=timeout)


def image_id(image):
    r = docker('image', 'inspect', '--format', '{{.Id}}', image, timeout=60)
    if r.returncode:
        return None
    return r.stdout.strip().split(':')[-1][:12]


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def read_text(path):
    for enc in ('utf-8-sig', 'cp1252'):
        try:
            with open(path, encoding=enc) as f:
                return f.read()
        except UnicodeDecodeError:
            continue
        except OSError:
            return ''
    return ''


def write_json(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(data, f, indent=1, default=str)


def fs_name(text):
    """A target or job name made safe as one Windows/Linux path part: characters Windows forbids in a name, and
    control characters, become _."""
    bad = '<>:"/|?*' + chr(92)
    return ''.join('_' if (c in bad or ord(c) < 32) else c for c in str(text))


def rel(path, root):
    return os.path.relpath(path, root).replace('\\', '/')


def walk_files(root, pattern=None):
    for d, dirs, files in os.walk(root):
        dirs[:] = [x for x in dirs if x not in SKIP_DIRS]
        for f in files:
            if pattern is None or fnmatch.fnmatch(f.lower(), pattern):
                yield os.path.join(d, f)


# ------------------------------------------------------------------------------------------------ docker extraction

def copy_from_image(image, src, dest, volume=None):
    """docker create (never started) + docker cp + docker rm: copies files out of a local image or volume."""
    name = f'deeptools-extract-{os.getpid()}-{int(time.time() * 1000) % 100000}'
    args = ['create', '--name', name]
    if volume:
        args += ['-v', f'{volume[0]}:{volume[1]}:ro']
    args += ['--entrypoint', 'true', image]
    r = docker(*args, timeout=120)
    if r.returncode:
        return f'docker create failed: {r.stderr.strip()[:200]}'
    try:
        os.makedirs(dest, exist_ok=True)
        r = docker('cp', f'{name}:{src}', dest, timeout=1800)
        if r.returncode:
            return f'docker cp {src} failed: {r.stderr.strip()[:200]}'
    finally:
        docker('rm', '-v', name, timeout=120)
    return None


# ------------------------------------------------------------------------------------------------ references

class Refs:
    def __init__(self):
        self.paths = {}
        self.sources = {}

    def set(self, key, path, source):
        self.paths[key] = path
        self.sources[key] = source

    def get(self, key):
        return self.paths.get(key)

    def as_json(self):
        return {k: {'path': v, 'source': self.sources.get(k)} for k, v in self.paths.items()}


def steam_libraries():
    roots = [r'C:\Program Files (x86)\Steam', r'C:\Program Files\Steam', os.path.expanduser('~/.steam/steam'),
             os.path.expanduser('~/.local/share/Steam')]
    libs = []
    for root in roots:
        vdf = os.path.join(root, 'steamapps', 'libraryfolders.vdf')
        if os.path.isfile(vdf):
            libs.append(os.path.join(root, 'steamapps'))
            for m in re.finditer(r'"path"\s+"([^"]+)"', read_text(vdf)):
                libs.append(os.path.join(m.group(1).replace('\\\\', '\\'), 'steamapps'))
    seen, out = set(), []
    for lib in libs:
        k = os.path.normcase(os.path.abspath(lib))
        if k not in seen and os.path.isdir(lib):
            seen.add(k)
            out.append(lib)
    return out


def resolve_ds64(args, refs):
    def ok(d):
        return d and os.path.isfile(os.path.join(d, 'Sandbox.Game.dll'))
    candidates = []
    if args.ds_dir:
        candidates.append((args.ds_dir, '--ds-dir'))
    ds64 = os.environ.get('DS64')
    if ds64:
        candidates.append((ds64, 'DS64'))
    env = os.environ.get('SE_SERVER_ROOT')
    if env:
        candidates += [(os.path.join(env, 'DedicatedServer64'), 'SE_SERVER_ROOT'), (env, 'SE_SERVER_ROOT')]
    for lib in steam_libraries():
        candidates.append((os.path.join(lib, 'common', 'SpaceEngineersDedicatedServer', 'DedicatedServer64'), 'steam library ' + lib))
    for d, src in candidates:
        if ok(d):
            refs.set('ds64', os.path.abspath(d), src)
            return
    # A local Docker volume that already holds the dedicated server (PluginCheck's), copied read-only into the cache.
    vols = set(docker('volume', 'ls', '--format', '{{.Name}}', timeout=60).stdout.split())
    probe_image = next((IMAGES[k] for k in ('magnetar', 'sdk') if image_id(IMAGES[k])), None)
    for vol in DS_VOLUMES:
        if vol not in vols or not probe_image:
            continue
        r = docker('run', '--rm', '--network', 'none', f'--cpuset-cpus={cpuset(args.affinity_mask)}', '-v', f'{vol}:/ds:ro',
                   '--entrypoint', 'sha256sum', probe_image, '/ds/DedicatedServer64/Sandbox.Game.dll', timeout=300)
        if r.returncode or not r.stdout.strip():
            continue
        key = r.stdout.split()[0][:16]
        dest = os.path.join(CACHE, 'refs', f'ds-{key}')
        target = os.path.join(dest, 'DedicatedServer64')
        if not ok(target):
            log(f'copying DedicatedServer64 from volume {vol} (read-only) into {dest}')
            shutil.rmtree(dest, ignore_errors=True)
            why = copy_from_image(probe_image, '/ds/DedicatedServer64', dest, volume=(vol, '/ds'))
            if why or not ok(target):
                gap(f'could not copy DedicatedServer64 from volume {vol}: {why}')
                continue
        refs.set('ds64', target, f'docker volume {vol} (Sandbox.Game.dll sha256 {key})')
        return
    gap('DedicatedServer64 not found (set SE_SERVER_ROOT or --ds-dir): game references missing, compiles will be incomplete')


def resolve_image_dir(refs, key, image, src, sub, check):
    iid = image_id(image)
    if not iid:
        return None
    dest = os.path.join(CACHE, 'refs', f'{key}-{iid}')
    target = os.path.join(dest, sub)
    if not os.path.exists(os.path.join(target, check)):
        log(f'copying {src} from {image} into {dest}')
        shutil.rmtree(target, ignore_errors=True)
        why = copy_from_image(image, src, dest)
        if why:
            gap(f'could not copy {src} from {image}: {why}')
            return None
    return target if os.path.exists(os.path.join(target, check)) else None


def resolve_refs(args, refs, kinds):
    resolve_ds64(args, refs)
    # net48 reference assemblies (restored with the analyzer pack)
    pack = os.path.join(NUGET_PACKAGES, 'microsoft.netframework.referenceassemblies.net48', '1.0.3', 'build', '.NETFramework', 'v4.8')
    if os.path.isfile(os.path.join(pack, 'mscorlib.dll')):
        refs.set('net48', pack, 'NuGet Microsoft.NETFramework.ReferenceAssemblies.net48 1.0.3')
    else:
        gap('net48 reference assemblies missing (analyzer pack restore failed?)')

    if 'magnetar' in kinds or 'quasar-companion' in kinds:
        d = resolve_image_dir(refs, 'magnetar', IMAGES['magnetar'], '/opt/magnetar', 'magnetar', 'Libraries/MagnetarInterim/PluginSdk.dll')
        if d:
            refs.set('interim', os.path.join(d, 'Libraries', 'MagnetarInterim'), f'image {IMAGES["magnetar"]} /opt/magnetar/Libraries/MagnetarInterim')
            refs.set('interim-app', d, f'image {IMAGES["magnetar"]} /opt/magnetar (MagnetarInterim app folder)')
        else:
            gap(f'Magnetar Interim libraries (PluginSdk, Harmony) not available: image {IMAGES["magnetar"]} missing')
        shared = resolve_image_dir(refs, 'magnetar', IMAGES['magnetar'], '/usr/share/dotnet/shared/Microsoft.NETCore.App', 'Microsoft.NETCore.App', '.')
        versions = sorted((v for v in os.listdir(shared) if v.startswith('10.')), key=lambda v: [int(x) if x.isdigit() else 0 for x in re.split(r'[.-]', v)]) if shared and os.path.isdir(shared) else []
        if versions:
            refs.set('net10-runtime', os.path.join(shared, versions[-1]), f'image {IMAGES["magnetar"]} Microsoft.NETCore.App {versions[-1]} (the Interim host runtime)')
        else:
            local = sorted(glob.glob(r'C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.*'))
            if local:
                refs.set('net10-runtime', local[-1], 'local .NET 10 shared runtime')
            else:
                gap('no .NET 10 runtime folder for the Interim compile')
        d = resolve_image_dir(refs, 'legacy', IMAGES['legacy'], '/opt/magnetar', 'magnetar', 'Libraries/MagnetarLegacy/PluginSdk.dll')
        if d:
            refs.set('legacy', os.path.join(d, 'Libraries', 'MagnetarLegacy'), f'image {IMAGES["legacy"]} /opt/magnetar/Libraries/MagnetarLegacy')
        elif refs.get('interim'):
            refs.set('legacy', refs.get('interim'), 'fallback: Interim libraries (Legacy image missing)')
            gap(f'MagnetarLegacy libraries not available ({IMAGES["legacy"]} missing): net48 compile uses the Interim PluginSdk')
    if 'torch' in kinds:
        d = resolve_image_dir(refs, 'torch', IMAGES['torch'], '/opt/torch', 'torch', 'Torch.dll')
        if d:
            refs.set('torch', d, f'image {IMAGES["torch"]} /opt/torch')
        else:
            gap(f'Torch DLLs not available ({IMAGES["torch"]} missing): Torch compile will miss Torch types')
    if 'quasar' in kinds or 'quasar-companion' in kinds:
        iid = image_id(IMAGES['quasar'])
        if iid:
            dest = os.path.join(CACHE, 'refs', f'quasar-{iid}')
            for f, src in (('Quasar.Plugin.Abstractions.dll', '/opt/quasar/Quasar.Plugin.Abstractions.dll'),
                           ('Magnetar.Protocol.dll', '/opt/quasar/Agent/Magnetar.Protocol.dll')):
                if not os.path.isfile(os.path.join(dest, f)):
                    why = copy_from_image(IMAGES['quasar'], src, dest)
                    if why:
                        log(f'note: {f} not copied from the Quasar image: {why}')
            if os.path.isfile(os.path.join(dest, 'Quasar.Plugin.Abstractions.dll')):
                refs.set('quasar', dest, f'image {IMAGES["quasar"]} /opt/quasar')
        if not refs.get('quasar'):
            gap('Quasar.Plugin.Abstractions.dll not available: Quasar compile will miss plugin contract types')
        packs = r'C:\Program Files\dotnet\packs'
        for key, name in (('netcore-ref', 'Microsoft.NETCore.App.Ref'), ('aspnet-ref', 'Microsoft.AspNetCore.App.Ref')):
            vers = sorted(glob.glob(os.path.join(packs, name, '10.*')))
            if vers:
                refs.set(key, os.path.join(vers[-1], 'ref', 'net10.0'), f'local SDK pack {name} {os.path.basename(vers[-1])}')
        razor = sorted(glob.glob(r'C:\Program Files\dotnet\sdk\10.*\Sdks\Microsoft.NET.Sdk.Razor\source-generators'))
        if razor:
            refs.set('razor', razor[-1], 'local SDK Razor source generator')
        else:
            gap('Razor source generator not found: Quasar .razor components are not compiled')
    if 'pulsar' in kinds:
        for lib in steam_libraries():
            b = os.path.join(lib, 'common', 'SpaceEngineers', 'Bin64')
            if os.path.isfile(os.path.join(b, 'Sandbox.Game.dll')):
                refs.set('bin64', b, 'steam library ' + lib)
                break
        if not refs.get('bin64'):
            gap('SE client Bin64 not found: Pulsar compile has no game references')
        # Pulsar's own dependency folder, Libraries/<loader> (0Harmony, DiscordRPC...), last in its probe list: Legacy for
        # net48, Interim for SE1 on .NET 10 (Pulsar Legacy/Program.cs SetupPlugins). Read from a local Pulsar install.
        pulsar = os.environ.get('PULSAR_DIR') or os.path.join(os.environ.get('APPDATA') or '', 'Pulsar')
        for key, sub in (('pulsar-legacy', 'Legacy'), ('pulsar-interim', 'Interim')):
            d = os.path.join(pulsar, 'Libraries', sub)
            if os.path.isfile(os.path.join(d, '0Harmony.dll')):
                refs.set(key, d, 'local Pulsar install ' + pulsar)
        if not refs.get('pulsar-legacy'):
            gap(f'Pulsar Libraries folder not found ({os.path.join(pulsar, "Libraries", "Legacy")}; set PULSAR_DIR): 0Harmony and '
                'DiscordRPC are resolved from the game folder only')

    # A local MagnetarBin is the exact SDK the user's host builds against; use
    # it instead of optional PluginCheck images. The same folder is mounted at
    # both the Legacy and Interim guest paths used by the build/test scripts.
    local_magnetar = os.environ.get('MAGNETAR_BIN') or os.environ.get('MagnetarBin')
    if local_magnetar and os.path.isfile(os.path.join(local_magnetar, 'PluginSdk.dll')):
        local_magnetar = os.path.abspath(local_magnetar)
        refs.set('legacy', local_magnetar, 'MagnetarBin environment')
        refs.set('interim', local_magnetar, 'MagnetarBin environment')


# ------------------------------------------------------------------------------------------------ setup: analyzers and DeepScan

def collect_pack(pkg_dir, dest):
    """Copies a package's C# analyzer DLLs, picking the newest roslynX.Y variant the host Roslyn supports."""
    dlls = []
    for f in glob.glob(os.path.join(pkg_dir, 'analyzers', '**', '*.dll'), recursive=True):
        parts = rel(f, pkg_dir).split('/')
        if 'vb' in parts or f.endswith('.resources.dll') or 'CodeFix' in os.path.basename(f):
            continue
        dlls.append((f, parts))
    variants = {}
    for f, parts in dlls:
        v = next((p for p in parts if re.match(r'^roslyn\d+(\.\d+)*$', p)), None)
        variants.setdefault(v, []).append(f)
    usable = [v for v in variants if v is None or tuple(int(x) for x in v[6:].split('.')) <= HOST_ROSLYN]
    if any(v is not None for v in usable):
        best = max((v for v in usable if v is not None), key=lambda v: tuple(int(x) for x in v[6:].split('.')))
        chosen = variants[best]
    else:
        chosen = variants.get(None, [])
    os.makedirs(dest, exist_ok=True)
    for f in chosen:
        shutil.copy2(f, dest)
    return [os.path.basename(f) for f in chosen]


PACK_MARKER = os.path.join(CACHE, 'analyzers', 'pack.json')


def pack_dir(name):
    """A pack's analyzer folder. Always derived from this tools folder, never read from pack.json, so moving the tools
    folder cannot leave the cache pointing at the old place."""
    return os.path.join(CACHE, 'analyzers', name.lower())


def write_pack_info(info):
    """pack.json holds no absolute paths: each pack's folder is stored as its name under cache/analyzers."""
    stored = json.loads(json.dumps(info))
    for name, p in stored.get('packs', {}).items():
        p['dir'] = name.lower()
    write_json(PACK_MARKER, stored)


def ensure_analyzers(args):
    """Restores tools/analyzers/AnalyzerPack.csproj once (cached by its hash) and collects the analyzer DLLs."""
    project_hash = hashlib.sha256(open(PACK_PROJECT, 'rb').read()).hexdigest()[:16]
    root = os.path.join(CACHE, 'analyzers')
    marker = PACK_MARKER
    if os.path.isfile(marker):
        info = json.load(open(marker, encoding='utf-8'))
        packs = info.get('packs', {})
        if info.get('hash') == project_hash and all(os.path.isdir(pack_dir(n)) for n in packs):
            if any(p.get('dir') != n.lower() for n, p in packs.items()):
                write_pack_info(info)  # an older pack.json with absolute folders
            for name, p in packs.items():
                p['dir'] = pack_dir(name)
            return info
    log('restoring the analyzer pack (NuGet)')
    r = run(['dotnet', 'restore', PACK_PROJECT, '--packages', NUGET_PACKAGES, '-nologo', '-v:quiet'], timeout=1800)
    if r.returncode:
        raise SystemExit(f'analyzer pack restore failed:\n{r.stdout[-2000:]}\n{r.stderr[-1000:]}')
    shutil.rmtree(root, ignore_errors=True)
    os.makedirs(root)
    tree = ET.parse(PACK_PROJECT).getroot()
    packs = {}
    for e in tree.iter():
        if e.tag.split('}')[-1] in ('PackageDownload', 'PackageReference'):
            name, version = e.get('Include'), (e.get('Version') or '').strip('[]')
            if name.lower().startswith('microsoft.netframework.referenceassemblies'):
                continue
            pkg_dir = os.path.join(NUGET_PACKAGES, name.lower(), version)
            files = collect_pack(pkg_dir, pack_dir(name))
            packs[name] = {'version': version, 'files': files, 'dir': pack_dir(name)}
    info = {'hash': project_hash, 'packs': packs, 'dropped': {}}
    write_pack_info(info)
    return info


def probe_dropped_packs(info, deepscan, args):
    """SecurityCodeScan is kept only if it loads and runs in the current Roslyn without errors."""
    if info.get('probed'):
        return info
    probe = os.path.join(CACHE, 'analyzers', 'probe')
    os.makedirs(probe, exist_ok=True)
    sample = os.path.join(probe, 'Sample.cs')
    with open(sample, 'w', encoding='utf-8') as f:
        f.write('using System; using System.IO; namespace P { public static class S { public static string R(string p) '
                '{ var t = File.ReadAllText(p); return t.ToUpperInvariant(); } public static void Q(string q) '
                '{ var c = new System.Data.SqlClient.SqlCommand("select * from t where x=" + q); } } }\n')
    for name, pack in info['packs'].items():
        if name != 'SecurityCodeScan.VS2019':
            continue
        out = os.path.join(probe, name + '.json')
        r = run(['dotnet', deepscan, '--sources', sample, '--runtime', 'net48', '--profile', 'plain', '--refs', args_net48_dirs(),
                 '--analyzers', pack['dir'], '--no-graph', '--out', out], timeout=600)
        problems = []
        try:
            data = json.load(open(out, encoding='utf-8'))
            problems = [f['error'] for f in data['analyzers']['failed']] + [g for g in data['gaps'] if g.startswith('analyzer threw')]
            if not data['analyzers']['loaded']:
                problems.append('no analyzers loaded')
        except (OSError, ValueError, KeyError) as e:
            problems.append(f'probe produced no result ({e}); exit {r.returncode}: {r.stderr[-300:]}')
        if problems:
            info['dropped'][name] = 'does not load cleanly in Roslyn ' + '.'.join(map(str, HOST_ROSLYN)) + ': ' + '; '.join(problems)[:600]
            log(f'dropping {name}: {info["dropped"][name]}')
    info['probed'] = True
    write_pack_info(info)
    return info


def args_net48_dirs():
    return os.path.join(NUGET_PACKAGES, 'microsoft.netframework.referenceassemblies.net48', '1.0.3', 'build', '.NETFramework', 'v4.8')


def ensure_deepscan(args):
    files = sorted(glob.glob(os.path.join(DEEPSCAN_SRC, '*.cs')) + glob.glob(os.path.join(DEEPSCAN_SRC, '*.csproj')))
    h = hashlib.sha256()
    for f in files:
        h.update(os.path.basename(f).encode())
        h.update(open(f, 'rb').read())
    key = h.hexdigest()[:16]
    out = os.path.join(CACHE, 'deepscan', key)
    dll = os.path.join(out, 'DeepScan.dll')
    if os.path.isfile(dll) and not args.rebuild_deepscan:
        return dll
    log('building DeepScan')
    # The cache key is the source content only; PathMap keeps this folder's location out of the built DLL and PDB as well.
    r = run(['dotnet', 'build', os.path.join(DEEPSCAN_SRC, 'DeepScan.csproj'), '-c', 'Release', '-m:4', '-o', out, '-nologo', '-v:quiet',
             f'-p:PathMap={DEEPSCAN_SRC}=/_/DeepScan'], timeout=1200)
    if r.returncode or not os.path.isfile(dll):
        raise SystemExit(f'DeepScan build failed:\n{r.stdout[-3000:]}')
    return dll


# ------------------------------------------------------------------------------------------------ trees

LISTED = {}


def content_sha256(root, paths):
    """GATE-RECORD.md contentSha256: git's '/' paths sorted by code point, each as path + NUL + raw sha256(bytes)."""
    h = hashlib.sha256()
    for p in sorted(paths):
        h.update(p.encode('utf-8') + b'\0')
        with open(os.path.join(root, p), 'rb') as f:
            h.update(hashlib.sha256(f.read()).digest())
    return h.hexdigest()


def repo_content_sha256(repo):
    """The same hash taken straight from a repository's working tree (what a consumer computes)."""
    out = git(repo, 'ls-files', '-co', '--exclude-standard', '-z').stdout
    return content_sha256(repo, [p for p in out.split('\0') if p and os.path.isfile(os.path.join(repo, p))])


def get_tree(repo, rev, dest):
    """Puts the tree at rev (or the working tree) in dest. Returns (label, sha)."""
    shutil.rmtree(dest, ignore_errors=True)
    os.makedirs(dest)
    if rev.upper() == 'WORKTREE':
        head = git(repo, 'rev-parse', '--verify', '-q', 'HEAD').stdout.strip() or 'no-commits'
        listed = git(repo, 'ls-files', '-co', '--exclude-standard', '-z')
        if listed.returncode:
            raise SystemExit(f'git ls-files failed: {listed.stderr}')
        count = 0
        copied = []
        for name in filter(None, listed.stdout.split('\0')):
            src = os.path.join(repo, name)
            if not os.path.isfile(src):
                continue  # deleted in the working tree
            target = os.path.join(dest, name)
            os.makedirs(os.path.dirname(target), exist_ok=True)
            shutil.copy2(src, target)
            copied.append(name)
            count += 1
        LISTED[dest] = copied
        dirty = git(repo, 'diff', '--quiet', 'HEAD').returncode != 0 or bool(git(repo, 'ls-files', '-o', '--exclude-standard').stdout.strip())
        label = f'WORKTREE@{head}' + ('+dirty' if dirty else '')
        log(f'copied {count} working-tree files ({label})')
        return label, head
    sha = git(repo, 'rev-parse', '--verify', rev + '^{commit}').stdout.strip()
    if not sha:
        raise SystemExit(f'{rev} is not a commit in {repo}')
    r = git(repo, 'archive', '--format=tar', sha, binary=True, timeout=900)
    if r.returncode:
        raise SystemExit(f'git archive {sha} failed: {r.stderr}')
    with tarfile.open(fileobj=io.BytesIO(r.stdout)) as tar:
        try:
            tar.extractall(dest, filter='data')
        except TypeError:
            tar.extractall(dest)
    return sha, sha


def changed_lines(repo, base, head_rev, head_tree):
    """{head path: {'added': set(lines), 'touch': set(lines)}}, {base path: set(deleted lines)}, renames {base: head}."""
    worktree = head_rev.upper() == 'WORKTREE'
    args = ['diff', '--unified=0', '--no-color', '--no-ext-diff', '-M', base] + ([] if worktree else [head_rev])
    r = git(repo, *args, timeout=600)
    if r.returncode:
        gap(f'git diff failed: {r.stderr.strip()[:200]}')
        return {}, {}, {}
    added, deleted, renames = {}, {}, {}
    old = new = None
    for line in r.stdout.splitlines():
        if line.startswith('--- '):
            old = None if line[4:] == '/dev/null' else line[6:]
        elif line.startswith('+++ '):
            new = None if line[4:] == '/dev/null' else line[6:]
            if old and new and old != new:
                renames[old] = new
        elif line.startswith('@@'):
            m = re.match(r'@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@', line)
            if not m:
                continue
            os_, oc, ns, nc = int(m.group(1)), int(m.group(2) or 1), int(m.group(3)), int(m.group(4) or 1)
            if new:
                entry = added.setdefault(new, {'added': set(), 'touch': set()})
                entry['added'].update(range(ns, ns + nc))
                entry['touch'].update(range(ns, ns + nc) if nc else [max(ns, 1), ns + 1])
            if old and oc:
                deleted.setdefault(old, set()).update(range(os_, os_ + oc))
    if worktree:
        # git diff <base> leaves out untracked files: every line of a new untracked file is added.
        for name in filter(None, git(repo, 'ls-files', '-o', '--exclude-standard', '-z').stdout.split('\0')):
            path = os.path.join(head_tree, name)
            if os.path.isfile(path):
                n = read_text(path).count('\n') + 1
                added[name] = {'added': set(range(1, n + 1)), 'touch': set(range(1, n + 1))}
    return added, deleted, renames


# ------------------------------------------------------------------------------------------------ targets

def local(tag):
    return tag.split('}')[-1]


def child_text(root, name):
    for e in root:
        if local(e.tag) == name:
            return (e.text or '').strip()
    return None


def parse_manifest(path):
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError):
        return None
    if local(root.tag) != 'PluginData':
        return None
    dirs = [(d.text or '').strip() for e in root if local(e.tag) == 'SourceDirectories' for d in e if local(d.tag) == 'Directory']
    nuget = []
    for e in root.iter():
        if local(e.tag) in ('NuGetReferences',):
            for p in e.iter():
                if local(p.tag) in ('PackageReference', 'Package') and (p.get('Include') or child_text(p, 'Id')):
                    nuget.append({'id': p.get('Include') or child_text(p, 'Id'), 'version': p.get('Version') or child_text(p, 'Version') or ''})
    return {
        'path': path, 'id': child_text(root, 'Id'), 'repoId': child_text(root, 'RepoId'), 'commit': child_text(root, 'Commit'),
        'kind': child_text(root, 'PluginKind'), 'projectPath': child_text(root, 'ProjectPath'),
        'packageManifest': child_text(root, 'PackageManifest'), 'version': child_text(root, 'Version'),
        'description': next(((e.text or '') for e in root if local(e.tag) == 'Description'), None),
        'runtimes': child_text(root, 'Runtimes'), 'sourceDirectories': dirs, 'nuget': nuget,
        'type': root.get('{http://www.w3.org/2001/XMLSchema-instance}type'),
    }


def manifest_root(tree, m):
    """The folder SourceDirectories are relative to: the repository root (GitHub archive, as the hub compiles it), or the
    manifest's own folder when nothing matches from the root but something does there (a dev-folder plugin, such as a
    probe plugin kept inside a tool's repository)."""
    dirs = m.get('sourceDirectories') or []
    if not dirs or manifest_sources(tree, dirs):
        return tree
    own = os.path.dirname(m['path'])
    return own if manifest_sources(own, dirs) else tree


def manifest_sources(tree, dirs):
    out = []
    for f in walk_files(tree, '*.cs'):
        r = rel(f, tree)
        if not dirs or any(r.startswith(d) for d in dirs):
            out.append(f)
    return out


def guess_loader(tree, m):
    """Magnetar server plugin or Pulsar client plugin: both use GitHubPlugin manifests."""
    text = ''.join(read_text(f) for f in manifest_sources(tree, m['sourceDirectories'])[:400])
    server = len(re.findall(r'\bPluginSdk\b|\bMagnetar\b|IsDedicated|ServerPlugin', text))
    client = len(re.findall(r'Sandbox\.Graphics\.GUI|VRage\.Input|MyGuiScreen|IHandleInputPlugin|\bPULSAR\b|ClientPlugin', text))
    name = os.path.basename(m['path']).lower()
    if m.get('runtimes') or client > server * 2 or 'client' in name:
        return 'pulsar', f'client hints {client} vs server hints {server}'
    return 'magnetar', f'server hints {server} vs client hints {client}'


def directory_props(pdir, tree):
    """Plain properties (unconditional, or only "if not set") from the Directory.Build.props files at or above a
    project folder within the tree, nearest first."""
    found = {}
    top = os.path.normcase(os.path.abspath(tree))
    d = os.path.abspath(pdir)
    while True:
        f = os.path.join(d, 'Directory.Build.props')
        try:
            groups = [g for g in ET.parse(f).getroot().iter() if local(g.tag) == 'PropertyGroup' and not g.get('Condition')] if os.path.isfile(f) else []
        except (ET.ParseError, OSError):
            groups = []
        for g in groups:
            for p in g:
                name = local(p.tag)
                cond = (p.get('Condition') or '').replace(' ', '')
                if not cond or cond == f"'$({name})'==''":
                    found.setdefault(name, (p.text or '').strip())
        parent = os.path.dirname(d)
        if os.path.normcase(d) == top or parent == d or not os.path.normcase(parent).startswith(top):
            break
        d = parent
    return found


def expand_props(value, known):
    """$(Name) replaced from known (a few rounds, for values that refer to other properties); unknown ones stay."""
    for _ in range(5):
        new = re.sub(r'\$\((\w+)\)', lambda m: known.get(m.group(1)) or m.group(0), value)
        if new == value:
            break
        value = new
    return value


def parse_csproj(path, tree):
    """Minimal MSBuild reading: sources, references, language settings. No MSBuild is run."""
    pdir = os.path.dirname(path)
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError) as e:
        return {'path': path, 'error': str(e)}
    sdk = root.get('Sdk') or ''
    props = {}

    def expand(v, base):
        v = (v or '').replace('$(MSBuildThisFileDirectory)', base + os.sep).replace('$(MSBuildProjectDirectory)', pdir)
        return v

    def cond_ok(e):
        c = (e.get('Condition') or '').replace(' ', '')
        if not c:
            return True
        if "'$(Configuration)|$(Platform)'" in c:
            return 'Release' in c
        return True

    compile_inc, compile_rem, refs, packages, xaml, proj_refs = [], [], [], [], [], []
    frameworks = set()

    def visit(node, base, is_import):
        for e in node.iter():
            t = local(e.tag)
            parent_ok = True
            if t == 'PropertyGroup' and not cond_ok(e):
                parent_ok = False
            if t == 'PropertyGroup' and parent_ok:
                for p in e:
                    pn = local(p.tag)
                    if pn in ('TargetFramework', 'TargetFrameworks'):
                        frameworks.update(x.strip() for x in (p.text or '').split(';') if x.strip())
                    elif cond_ok(p):
                        props.setdefault(pn, (p.text or '').strip())
            elif t == 'Compile':
                if e.get('Include'):
                    compile_inc.append((expand(e.get('Include'), base), base, [expand(x.strip(), base) for x in (e.get('Exclude') or '').split(';') if x.strip()]))
                if e.get('Remove'):
                    compile_rem.append((expand(e.get('Remove'), base), base))
            elif t in ('Reference',) and not is_import:
                hint = e.get('HintPath') or next((c.text for c in e if local(c.tag) == 'HintPath'), None)
                refs.append({'name': (e.get('Include') or '').split(',')[0].strip(), 'hint': hint})
            elif t == 'ProjectReference' and e.get('Include'):
                proj_refs.append(os.path.normpath(os.path.join(base, expand(e.get('Include'), base).replace('\\', os.sep))))
            elif t == 'PackageReference':
                packages.append({'id': e.get('Include'), 'version': e.get('Version') or next((c.text for c in e if local(c.tag) == 'Version'), '')})
            elif t in ('Page', 'ApplicationDefinition') and (e.get('Include') or '').endswith('.xaml'):
                xaml.append(os.path.normpath(os.path.join(base, expand(e.get('Include'), base))))
            elif t == 'Import' and (e.get('Project') or '').endswith('.projitems'):
                item = os.path.normpath(os.path.join(base, expand(e.get('Project'), base).replace('\\', os.sep)))
                try:
                    visit(ET.parse(item).getroot(), os.path.dirname(item), True)
                except (ET.ParseError, OSError):
                    pass

    visit(root, pdir, False)
    if props.get('TargetFrameworkVersion'):
        frameworks.add('net' + props['TargetFrameworkVersion'].lstrip('v').replace('.', ''))
    if any('$(' in f for f in frameworks):
        # <TargetFramework>$(Name)</TargetFramework>: the value comes from the project or a Directory.Build.props above it.
        # One still unresolved stays as written; builds then let MSBuild pick the framework (no -p:TargetFramework).
        known = dict(directory_props(pdir, tree), **{k: v for k, v in props.items() if v})
        frameworks = {x for f in frameworks for x in expand_props(f, known).split(';') if x.strip()}

    def matches(pattern, base):
        pattern = pattern.replace('\\', '/')
        full = pattern if os.path.isabs(pattern) else os.path.join(base, pattern)
        if any(ch in pattern for ch in '*?'):
            return [os.path.normpath(p) for p in glob.glob(full, recursive=True)]
        return [os.path.normpath(full)]

    files = []
    default_items = sdk and props.get('EnableDefaultCompileItems', 'true').lower() != 'false'
    if default_items:
        files += [f for f in walk_files(pdir, '*.cs')]
    for pattern, base, excludes in compile_inc:
        found = matches(pattern, base)
        if excludes:
            drop = {os.path.normcase(x) for ex in excludes for x in matches(ex, base)}
            found = [f for f in found if os.path.normcase(f) not in drop]
        files += found
    removed = set()
    for pattern, base in compile_rem:
        removed.update(os.path.normcase(p) for p in matches(pattern, base))
    seen, sources = set(), []
    for f in files:
        k = os.path.normcase(os.path.abspath(f))
        if k in seen or k in removed or not os.path.isfile(f):
            continue
        seen.add(k)
        sources.append(os.path.abspath(f))
    if sdk and not xaml:
        xaml = [f for f in walk_files(pdir, '*.xaml')]
    test = any(p['id'] and p['id'].lower() in ('microsoft.net.test.sdk', 'xunit', 'nunit', 'mstest.testframework', 'mstest', 'xunit.v3', 'tunit') for p in packages) \
        or props.get('IsTestProject', '').lower() == 'true'
    return {
        'path': path, 'rel': rel(path, tree), 'sdk': sdk, 'props': props, 'frameworks': sorted(frameworks), 'sources': sources,
        'references': refs, 'packages': packages, 'xaml': xaml, 'test': test, 'projectReferences': proj_refs,
        'coverlet': any(p['id'] and p['id'].lower().startswith('coverlet') for p in packages),
    }


def discover_targets(tree, extra_manifests, repo):
    targets, manifests, notes = [], [], []
    found = []
    for f in walk_files(tree, '*.xml'):
        m = parse_manifest(f)
        if m:
            m['rel'] = rel(f, tree)
            found.append(m)
    for p in extra_manifests or []:
        cand = p if os.path.isabs(p) else os.path.join(tree, p)
        if not os.path.isfile(cand) and os.path.isfile(os.path.join(repo, p)):
            cand = os.path.join(repo, p)
        m = parse_manifest(cand)
        if not m:
            gap(f'--manifest {p} is not a hub manifest (no <PluginData> root)')
            continue
        m['rel'] = p
        if not any(os.path.normcase(x['path']) == os.path.normcase(m['path']) for x in found):
            found.append(m)
    for m in found:
        manifests.append(m)
        if (m.get('kind') or '').lower() == 'quasaruiplugin':
            proj = os.path.join(tree, m['projectPath']) if m.get('projectPath') else None
            if not proj or not os.path.isfile(proj):
                gap(f'Quasar manifest {m["rel"]}: ProjectPath {m.get("projectPath")} not found in the tree')
                continue
            targets.append({'name': 'quasar', 'kind': 'quasar', 'manifest': m, 'project': proj, 'runtimes': ['net10']})
            pkg = os.path.join(tree, m.get('packageManifest') or 'quasar-plugin.json')
            try:
                pj = json.load(open(pkg, encoding='utf-8-sig'))
            except (OSError, ValueError):
                pj = {}
            for c in pj.get('companionPlugins') or []:
                cp = os.path.join(tree, c.get('projectPath') or '')
                if os.path.isfile(cp):
                    targets.append({'name': 'quasar-companion', 'kind': 'quasar-companion', 'manifest': m, 'project': cp, 'runtimes': ['net48'],
                                    'note': 'netstandard2.0 companion built by Quasar; compiled here against net48 + DedicatedServer64 + Magnetar.Protocol'})
        elif m.get('sourceDirectories') is not None and (m.get('type') or '').endswith('GitHubPlugin') or m.get('sourceDirectories'):
            loader, why = guess_loader(tree, m)
            if loader == 'magnetar':
                targets.append({'name': 'magnetar', 'kind': 'magnetar', 'manifest': m, 'runtimes': ['net10', 'net48'], 'why': why})
            else:
                rts = m.get('runtimes') or ''
                runtimes = ['net48'] if not rts or re.search(r'framework|net4|legacy', rts, re.I) else []
                if re.search(r'core|net\d\d|modern|interim', rts, re.I):
                    runtimes.append('net10')
                targets.append({'name': 'pulsar', 'kind': 'pulsar', 'manifest': m, 'runtimes': runtimes or ['net48'], 'why': why})
    for f in walk_files(tree, 'manifest.xml'):
        try:
            root = ET.parse(f).getroot()
        except ET.ParseError:
            continue
        if local(root.tag) != 'PluginManifest':
            continue
        projs = glob.glob(os.path.join(os.path.dirname(f), '*.csproj'))
        if projs:
            targets.append({'name': 'torch', 'kind': 'torch', 'manifest': {'path': f, 'rel': rel(f, tree), 'version': child_text(root, 'Version'),
                                                                        'guid': child_text(root, 'Guid'), 'name': child_text(root, 'Name')},
                            'project': projs[0], 'runtimes': ['net48']})
    names = collections.Counter(t['name'] for t in targets)
    seen = collections.Counter()
    for t in targets:
        if names[t['name']] > 1:
            seen[t['name']] += 1
            t['name'] = f'{t["name"]}{seen[t["name"]]}'
    return targets, manifests


# ------------------------------------------------------------------------------------------------ DeepScan runs

def xaml_stubs(project, dest):
    """Code-behind stand-ins (InitializeComponent, x:Name fields) so WPF pages compile without the XAML compiler."""
    stubs = []
    for f in project['xaml']:
        text = read_text(f)
        m = re.search(r'x:Class\s*=\s*"([\w.]+)"', text)
        if not m or '.' not in m.group(1):
            continue
        ns, cls = m.group(1).rsplit('.', 1)
        named = dict((n, t) for t, n in re.findall(r'<([\w:.]+)\b[^<>]*?\s(?:x:)?Name\s*=\s*"(\w+)"', text))
        fields = ''.join(f'internal {wpf_type(t)} {n} = null; ' for n, t in sorted(named.items()) if t != 'UserControl')
        # The XAML root element is the base type (the generated .g.cs declares it; code-behind often leaves it out).
        root = next((t for t in re.findall(r'<\s*([\w:.]+)', re.sub(r'<!--.*?-->', '', text, flags=re.S)) if not t.startswith('?')), '')
        base = f' : System.Windows.Controls.{root}' if root and ':' not in root and '.' not in root and root not in ('ResourceDictionary', 'Application') \
            else (' : System.Windows.Application' if root == 'Application' else '')
        if root == 'Window':
            base = ' : System.Windows.Window'
        stubs.append(f'namespace {ns} {{ partial class {cls}{base} {{ {fields}private void InitializeComponent() {{ }} }} }}')
    if not stubs:
        return None
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    with open(dest, 'w', encoding='utf-8') as f:
        f.write('// Generated by deep_tools.py: stand-ins for XAML code-behind\n' + '\n'.join(stubs) + '\n')
    return dest


WPF_SHAPES = {'Rectangle', 'Ellipse', 'Line', 'Path', 'Polygon', 'Polyline'}
# Named elements whose types are not in System.Windows.Controls (a named root <Window x:Name=...> among them).
WPF_ROOT = {'Window', 'FrameworkElement', 'UIElement', 'Application', 'ResourceDictionary', 'DataTemplate', 'HierarchicalDataTemplate', 'Style'}
WPF_PRIMITIVES = {'Popup', 'ToggleButton', 'RepeatButton', 'ScrollBar', 'Thumb', 'Track', 'UniformGrid', 'StatusBar', 'StatusBarItem', 'TabPanel',
                  'ToolBarPanel', 'BulletDecorator', 'ResizeGrip', 'TickBar'}
WPF_DOCUMENTS = {'Run', 'Paragraph', 'Hyperlink', 'Span', 'Bold', 'Italic', 'FlowDocument', 'Section', 'Table'}


def wpf_type(tag):
    """The CLR type of a named XAML element: WPF controls by name, anything with a namespace prefix as dynamic."""
    if ':' in tag or '.' in tag:
        return 'dynamic'
    if tag in WPF_SHAPES:
        return 'System.Windows.Shapes.' + tag
    if tag in WPF_DOCUMENTS:
        return 'System.Windows.Documents.' + tag
    if tag in WPF_ROOT:
        return 'System.Windows.' + tag
    if tag in WPF_PRIMITIVES:
        return 'System.Windows.Controls.Primitives.' + tag
    return 'System.Windows.Controls.' + tag


def implicit_usings(project):
    if project['props'].get('ImplicitUsings', '').lower() not in ('enable', 'true'):
        return []
    usings = ['System', 'System.Collections.Generic', 'System.IO', 'System.Linq', 'System.Net.Http', 'System.Threading', 'System.Threading.Tasks']
    if 'Web' in project['sdk']:
        usings += ['System.Net.Http.Json', 'Microsoft.AspNetCore.Builder', 'Microsoft.AspNetCore.Hosting', 'Microsoft.AspNetCore.Http',
                   'Microsoft.AspNetCore.Routing', 'Microsoft.Extensions.Configuration', 'Microsoft.Extensions.DependencyInjection',
                   'Microsoft.Extensions.Hosting', 'Microsoft.Extensions.Logging']
    return usings


def defines_from(project, runtime):
    d = project['props'].get('DefineConstants', '')
    syms = [s for s in re.split(r'[;,]', d.replace('$(DefineConstants)', '')) if s.strip()]
    if 'TRACE' not in syms:
        syms.insert(0, 'TRACE')
    syms += ['NETFRAMEWORK', 'NET48', 'NET48_OR_GREATER', 'NET472_OR_GREATER', 'NET471_OR_GREATER', 'NET47_OR_GREATER', 'NET462_OR_GREATER', 'NET461_OR_GREATER', 'NET46_OR_GREATER', 'NET452_OR_GREATER', 'NET45_OR_GREATER'] if runtime == 'net48' \
        else ['NETCOREAPP', 'NET', 'NET10_0', 'NET10_0_OR_GREATER', 'NET9_0_OR_GREATER', 'NET8_0_OR_GREATER', 'NET7_0_OR_GREATER', 'NET6_0_OR_GREATER', 'NET5_0_OR_GREATER', 'NETCOREAPP3_1_OR_GREATER', 'NETCOREAPP3_0_OR_GREATER', 'NETCOREAPP2_0_OR_GREATER']
    return ';'.join(dict.fromkeys(s.strip() for s in syms))


def nuget_compile_refs(packages, runtime, work):
    """Approximates Magnetar's NuGetRestore: restores the manifest's NuGetReferences and returns the compile assets."""
    if not packages:
        return []
    tfm = 'net48' if runtime == 'net48' else 'net10.0'
    d = os.path.join(work, f'nuget-{runtime}')
    os.makedirs(d, exist_ok=True)
    items = '\n'.join(f'    <PackageReference Include="{p["id"]}" Version="{p["version"]}" />' for p in packages)
    with open(os.path.join(d, 'Restore.csproj'), 'w', encoding='utf-8') as f:
        f.write(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>{tfm}</TargetFramework></PropertyGroup>\n  <ItemGroup>\n{items}\n'
                f'    <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies.net48" Version="1.0.3" Condition="\'$(TargetFramework)\' == \'net48\'" />\n  </ItemGroup></Project>\n')
    r = run(['dotnet', 'restore', os.path.join(d, 'Restore.csproj'), '--packages', NUGET_PACKAGES, '-nologo', '-v:quiet'], timeout=900)
    if r.returncode:
        gap(f'manifest NuGetReferences restore failed for {runtime}: {r.stdout[-300:]}')
        return []
    assets = json.load(open(os.path.join(d, 'obj', 'project.assets.json'), encoding='utf-8'))
    files = []
    for tfm_key, libs in assets.get('targets', {}).items():
        for lib, info in libs.items():
            if lib.lower().startswith('microsoft.netframework.referenceassemblies'):
                continue
            for c in (info.get('compile') or {}):
                if c.endswith('.dll'):
                    files.append(os.path.join(NUGET_PACKAGES, lib.lower().replace('/', os.sep), c.replace('/', os.sep)))
    return files


def deepscan_args(target, runtime, tree, refs, analyzer_dirs, work, label):
    """Builds the DeepScan command line for one target on one runtime."""
    a = ['--runtime', runtime, '--assembly-name', re.sub(r'\W', '', target['name']) or 'Plugin']
    ds = refs.get('ds64')
    net48 = refs.get('net48')
    net48_dirs = [net48, os.path.join(net48, 'Facades')] if net48 else []
    kind = target['kind']
    if kind in ('magnetar', 'pulsar'):
        a += ['--manifest', target['manifest']['path'], '--repo', manifest_root(tree, target['manifest']), '--profile', kind]
        if kind == 'magnetar':
            if runtime == 'net10':
                probe = [refs.get('net10-runtime'), refs.get('interim-app'), ds, refs.get('interim')]
            else:
                probe = net48_dirs + [ds, refs.get('legacy')]
            game = ds
        else:
            game = refs.get('bin64')
            # Pulsar's probe: runtime folder (+ WPF on .NET Framework), game folder, Libraries/<loader>. In the net48
            # reference assemblies the WPF assemblies sit in v4.8 itself (there is no WPF subfolder as in the runtime).
            wpf = [d for d in [os.path.join(net48, 'WPF')] if os.path.isdir(d)] if net48 else []
            if runtime == 'net48':
                probe = net48_dirs + wpf + [game, refs.get('pulsar-legacy')]
            else:
                probe = [refs.get('net10-runtime'), game, refs.get('pulsar-interim')]
        a += ['--game-dir', game or '', '--probe', *[p for p in probe if p]]
        nuget = nuget_compile_refs(target['manifest'].get('nuget'), runtime, work)
        if nuget:
            a += ['--ref-files', *nuget]
    elif kind in ('se-mod', 'pb-script'):
        # The game compiles mod and PB scripts itself with C# 6 against its own assemblies (a whitelist then limits the
        # APIs; that is not checked here). Every DLL in DS64 plus the net48 reference assemblies is a superset.
        sources = target['sources'] if kind == 'se-mod' else target['parsed']['sources']
        a += ['--profile', 'plain', '--source-root', tree, '--sources', *sources, '--refs', *[d for d in net48_dirs + [ds or refs.get('bin64')] if d],
              '--langversion', '6', '--define', 'TRACE', '--nullable', 'disable', '--unsafe', 'false']
    else:
        project = target['parsed']
        sources = list(project['sources'])
        # ProjectReferences: their sources join this compilation (one assembly instead of several; noted in the report).
        seen_p, stack = {os.path.normcase(project['path'])}, list(project.get('projectReferences') or [])
        merged = []
        while stack:
            pr = stack.pop()
            if os.path.normcase(pr) in seen_p or not os.path.isfile(pr):
                continue
            seen_p.add(os.path.normcase(pr))
            sub = parse_csproj(pr, tree)
            if sub.get('error'):
                continue
            merged.append(rel(pr, tree))
            sources += [f for f in sub['sources'] if f not in sources]
            stack += sub.get('projectReferences') or []
        if merged:
            target['mergedProjects'] = merged
            target['mergedDirs'] = [os.path.dirname(os.path.join(tree, m)) for m in merged]
        if kind == 'torch':
            stub = xaml_stubs(project, os.path.join(work, 'stubs', f'{fs_name(target["name"])}-Pages.g.cs'))
            if stub:
                sources.append(stub)
        a += ['--profile', 'plain', '--source-root', tree, '--sources', *sources]
        refdirs = []
        if kind == 'torch':
            refdirs = net48_dirs + [ds, refs.get('torch')]
        elif kind == 'quasar':
            refdirs = [refs.get('netcore-ref'), refs.get('aspnet-ref')]
        elif kind == 'quasar-companion':
            refdirs = net48_dirs + [ds]
        a += ['--refs', *[d for d in refdirs if d]]
        extra = []
        if kind in ('quasar', 'quasar-companion') and refs.get('quasar'):
            for f in ('Quasar.Plugin.Abstractions.dll', 'Magnetar.Protocol.dll'):
                p = os.path.join(refs.get('quasar'), f)
                if os.path.isfile(p) and (kind == 'quasar' or f.startswith('Magnetar')):
                    extra.append(p)
        if extra:
            a += ['--ref-files', *extra]
        props = project['props']
        lang = props.get('LangVersion') or 'latest'
        a += ['--langversion', lang, '--define', defines_from(project, runtime)]
        a += ['--nullable', (props.get('Nullable') or 'disable').lower()]
        if props.get('AllowUnsafeBlocks', '').lower() == 'true':
            a += ['--unsafe', 'true']
        else:
            a += ['--unsafe', 'false']
        usings = implicit_usings(project)
        if usings:
            a += ['--global-usings', ';'.join(usings)]
        if kind == 'quasar':
            pdir = os.path.dirname(project['path'])
            # Razor components of this project and of merged ProjectReferences. The generator takes one root namespace,
            # so it is the one of the project that holds the components (the main project's when several do).
            owners = []
            for d in [pdir] + target.get('mergedDirs', []):
                files = [f for f in walk_files(d, '*.razor')] + [f for f in walk_files(d, '*.cshtml')]
                if files:
                    owners.append((d, files))
            razor = [f for _, fs in owners for f in fs]
            if razor and refs.get('razor'):
                a += ['--generators', refs.get('razor'), '--additional-files', *razor]
                ns = props.get('RootNamespace') or os.path.splitext(os.path.basename(project['path']))[0]
                if owners and owners[0][0] != pdir:
                    pdir = owners[0][0]
                    owner_proj = next(iter(glob.glob(os.path.join(pdir, '*.csproj'))), None)
                    owner_props = parse_csproj(owner_proj, tree)['props'] if owner_proj else {}
                    ns = owner_props.get('RootNamespace') or os.path.basename(pdir)
                if len(owners) > 1:
                    gap(f'{target["name"]}: Razor components in {len(owners)} projects compiled under one root namespace ({ns}); '
                        'namespace errors in the others can be artifacts')
                a += ['--analyzer-option', f'build_property.RootNamespace={ns}', 'build_property.RazorLangVersion=latest',
                      'build_property.RazorConfiguration=Default', f'build_property.MSBuildProjectDirectory={pdir}',
                      'build_property.GenerateRazorMetadataSourceChecksumAttributes=false']
    a += ['--globalconfig', GLOBALCONFIG, '--additional-files', BANNED]
    a += ['--analyzers', *analyzer_dirs]
    return a


def strip_lead(message):
    """Analyzer messages that start with 'lead:' are reported as plain issues."""
    message = message or ''
    return message[5:].lstrip() if message[:5].lower() == 'lead:' else message


def run_deepscan(deepscan, target, runtime, tree, refs, analyzer_dirs, work, label, emit=False):
    out = os.path.join(work, 'deepscan', f'{label}-{fs_name(target["name"])}-{runtime}.json')
    dargs = deepscan_args(target, runtime, tree, refs, analyzer_dirs, work, label) + ['--out', out]
    if emit:
        dargs += ['--emit', os.path.join(work, 'emit', f'{fs_name(target["name"])}-{runtime}')]
    os.makedirs(os.path.dirname(out), exist_ok=True)
    # A response file: long source lists exceed the Windows command-line limit.
    rsp = out + '.args.txt'
    with open(rsp, 'w', encoding='utf-8') as f:
        f.write('\n'.join(dargs) + '\n')
    cmd = ['dotnet', deepscan, '@' + rsp]
    log(f'DeepScan {label} {target["name"]}/{runtime}')
    t0 = time.time()
    r = run(cmd, timeout=3600)
    with open(out + '.log', 'w', encoding='utf-8') as f:
        f.write(r.stdout + '\n' + r.stderr)
    try:
        data = json.load(open(out, encoding='utf-8'))
    except (OSError, ValueError):
        gap(f'DeepScan {label} {target["name"]}/{runtime} produced no result (exit {r.returncode}): {(r.stderr or r.stdout)[-300:]}')
        return None
    if data.get('error'):
        gap(f'DeepScan {label} {target["name"]}/{runtime} failed: {data["error"][:300]}')
        return None
    log(f'  {r.stdout.strip().splitlines()[-1] if r.stdout.strip() else "done"} ({time.time() - t0:.0f}s)')
    data['_target'] = target['name']
    for d in data.get('diagnostics', []):
        d['message'] = strip_lead(d.get('message'))
    data['_kind'] = target['kind']
    return data


# ------------------------------------------------------------------------------------------------ merge, base comparison, scope

def norm(text):
    return re.sub(r'\s+', ' ', (text or '')).strip()


def merge_runs(runs):
    """Merges diagnostics from every target/runtime run of one tree, keyed by rule + location."""
    merged = {}
    for data in runs:
        ctx = f'{data["_target"]}/{data["runtime"]}'
        for d in data['diagnostics']:
            key = (d['id'], d.get('file'), d['line'], d['column'], norm(d['message'])[:200])
            e = merged.get(key)
            if not e:
                e = dict(d)
                e['seenOn'] = []
                e['targets'] = []
                e['runtimes'] = []
                merged[key] = e
            if ctx not in e['seenOn']:
                e['seenOn'].append(ctx)
            if data['_target'] not in e['targets']:
                e['targets'].append(data['_target'])
            if d['runtime'] not in e['runtimes']:
                e['runtimes'].append(d['runtime'])
    return list(merged.values())


def mark_runtime_specific(diags, target_runtimes, compiled_files):
    for d in diags:
        only = []
        for t in d['targets']:
            rts = target_runtimes.get(t, [])
            if len(rts) > 1:
                on = [r for r in rts if f'{t}/{r}' in d['seenOn']]
                # Only meaningful when the file was compiled on every runtime of that target.
                if len(on) == 1 and all(d.get('file') in compiled_files.get(f'{t}/{r}', set()) for r in rts):
                    only.append(f'{t}/{on[0]}')
        d['onlyOn'] = only or None
        d['runtimeSpecific'] = bool(only)


def classify_base(head_diags, base_diags, renames):
    pool = collections.defaultdict(list)
    for b in base_diags:
        f = renames.get(b.get('file'), b.get('file'))
        pool[(b['id'], f, norm(b.get('lineText')))].append(b)
    for d in head_diags:
        k = (d['id'], d.get('file'), norm(d.get('lineText')))
        if pool.get(k):
            pool[k].pop()
            d['status'] = 'EXISTING'
        else:
            d['status'] = 'NEW'
    fixed = []
    for items in pool.values():
        for b in items:
            b = dict(b)
            b['status'] = 'FIXED'
            fixed.append(b)
    return fixed


def member_index(runs):
    members, calls, entries, by_file = {}, [], {}, collections.defaultdict(list)
    seen_edges = set()
    for data in runs:
        for m in data.get('members', []):
            if m['id'] not in members:
                members[m['id']] = m
                by_file[m['file']].append(m)
        for c in data.get('calls', []):
            k = (c['from'], c['to'], c['kind'])
            if k not in seen_edges:
                seen_edges.add(k)
                calls.append(c)
        for e in data.get('entryPoints', []):
            entries.setdefault(e['member'], []).append(e)
    for f in by_file:
        by_file[f].sort(key=lambda m: m['end'] - m['start'])
    return members, calls, entries, by_file


def members_at(by_file, file, line):
    return [m for m in by_file.get(file, []) if m['start'] <= line <= m['end']]


def compute_scope(head_runs, base_runs, added, deleted, renames):
    members, calls, entries, by_file = member_index(head_runs)
    bmembers, _, _, bby_file = member_index(base_runs) if base_runs else ({}, [], {}, {})
    changed = {}
    for f, info in added.items():
        for line in info['touch']:
            for m in members_at(by_file, f, line):
                changed.setdefault(m['id'], {'reason': 'lines changed', 'lines': set()})['lines'].add(line)
    for f, lines in deleted.items():
        for line in lines:
            for m in members_at(bby_file, f, line):
                if m['id'] in members:
                    changed.setdefault(m['id'], {'reason': 'lines removed', 'lines': set()})
    callers = collections.defaultdict(set)
    callees = collections.defaultdict(set)
    for c in calls:
        callers[c['to']].add(c['from'])
        callees[c['from']].add(c['to'])
    affected = {}
    for start in changed:
        frontier, seen, depth = [start], {start}, 0
        while frontier and depth < 30 and len(seen) < 3000:
            depth += 1
            nxt = []
            for m in frontier:
                if m in entries and m != start:
                    continue
                for c in callers.get(m, ()):
                    if c in seen:
                        continue
                    seen.add(c)
                    nxt.append(c)
                    if c not in changed:
                        a = affected.setdefault(c, {'via': start, 'direction': 'caller', 'distance': depth})
                        if depth < a['distance']:
                            a.update(via=start, distance=depth, direction='caller')
            frontier = nxt
        for c in callees.get(start, ()):
            if c not in changed and c not in affected:
                affected[c] = {'via': start, 'direction': 'callee', 'distance': 1}
    reached = {}
    for start in changed:
        # Entry points reached from each changed member, walking callers.
        frontier, seen = [start], {start}
        hit = []
        for _ in range(30):
            nxt = []
            for m in frontier:
                if m in entries:
                    hit.append(m)
                    if m != start:
                        continue
                for c in callers.get(m, ()):
                    if c not in seen:
                        seen.add(c)
                        nxt.append(c)
            frontier = nxt
            if not frontier:
                break
        reached[start] = sorted(set(hit))[:20]
    return members, calls, entries, by_file, changed, affected, reached


def diag_scope(d, added, changed, affected, members, by_file):
    f = d.get('file')
    if f and f in added:
        lines = set(range(d['line'], max(d.get('endLine') or d['line'], d['line']) + 1))
        if lines & added[f]['added']:
            return 'in-diff-line'
    ids = [d['member']] if d.get('member') else []
    if f and d.get('line'):
        ids += [m['id'] for m in members_at(by_file, f, d['line'])]
    if any(i in changed for i in ids):
        return 'in-changed-member'
    if any(i in affected for i in ids):
        return 'in-affected-member'
    return 'elsewhere'


# ------------------------------------------------------------------------------------------------ architecture rules

def if_stack(text):
    """Per line, the conditions of the enclosing #if/#elif blocks (crude, enough to see host guards)."""
    stack, out = [], []
    for line in text.splitlines():
        s = line.strip()
        if s.startswith('#if'):
            stack.append(s[3:].strip())
        elif s.startswith('#elif'):
            if stack:
                stack[-1] = s[5:].strip()
        elif s.startswith('#else'):
            if stack:
                stack[-1] = '!(' + stack[-1] + ')'
        elif s.startswith('#endif'):
            if stack:
                stack.pop()
        out.append(list(stack))
    return out


HOST_NS = re.compile(r'^(Torch|Magnetar|PluginSdk|Pulsar|Quasar)(\.|$)')
HOST_ASM = re.compile(r'^(Torch(\.API|\.Server)?|PluginSdk|Quasar\..*|Magnetar\..*|MagnetarInterim|MagnetarLegacy|Pulsar\..*)$')
CLIENT_NS = re.compile(r'^(Sandbox\.Graphics(\.GUI)?|VRage\.Input|VRageRender(\..*)?|VRage\.Render11(\..*)?|Sandbox\.Game\.Gui|Sandbox\.Game\.GUI)(\.|$)')
CLIENT_TYPE = re.compile(r'\.(MyGuiScreen\w*|MyGuiControl\w*|MyGuiSandbox|MyInput|MyHud\w*|MyGuiManager|MyRenderProxy)$')
GAME_ASM = re.compile(r'^(Sandbox\..*|VRage.*|SpaceEngineers\..*|HavokWrapper)$')
GUARD = re.compile(r'\b(TORCH|MAGNETAR|PULSAR|QUASAR|NETFRAMEWORK|NETCOREAPP|CLIENT|SERVER|DEDICATED)\b')


def architecture(tree, head_runs, targets):
    out, guarded = [], 0
    texts = {}

    def guard_at(f, line):
        if f not in texts:
            texts[f] = if_stack(read_text(os.path.join(tree, f)))
        st = texts[f]
        return any(GUARD.search(c) for c in (st[line - 1] if 0 < line <= len(st) else []))

    def add(rule, msg, tr, ctx):
        out.append({'id': rule, 'severity': 'warning', 'message': msg, 'file': tr['file'], 'line': tr['line'], 'column': 1,
                    'endLine': tr['line'], 'tool': 'architecture', 'runtime': ctx.split('/')[-1], 'category': 'Architecture',
                    'member': tr.get('member') or None, 'lineText': None, 'seenOn': [ctx], 'targets': [ctx.split('/')[0]],
                    'runtimes': [ctx.split('/')[-1]], 'helpUri': None})

    seen = set()
    for data in head_runs:
        kind, ctx = data['_kind'], f'{data["_target"]}/{data["runtime"]}'
        for tr in data.get('typeRefs', []):
            f, ns, asm, typ = tr['file'], tr['namespace'], tr['assembly'], tr['type']
            shared = re.search(r'(^|/)(Shared|Common|Core)(/|$)', f) is not None
            key = None
            if shared and (HOST_NS.match(ns) or HOST_ASM.match(asm)):
                if guard_at(f, tr['line']):
                    guarded += 1
                    continue
                key = ('ARCH001', f, ns)
                msg = f'Shared code references host-specific {typ} ({asm}) outside a host #if: keep host types in the host layer'
            elif kind in ('magnetar', 'torch', 'quasar-companion') and (CLIENT_NS.match(ns) or CLIENT_TYPE.search('.' + typ.split('<')[0])):
                if guard_at(f, tr['line']):
                    guarded += 1
                    continue
                key = ('ARCH002', f, typ)
                msg = f'Server code references client-only game type {typ} ({asm}): it exists in DedicatedServer64 but is not initialised on a dedicated server'
            elif kind == 'quasar' and GAME_ASM.match(asm):
                key = ('ARCH003', f, asm)
                msg = f'Quasar UI code references game assembly {asm} ({typ}) directly: Quasar runs outside the game; go through the companion'
            elif kind == 'magnetar' and (ns.startswith('Torch') or asm.startswith('Torch')):
                key = ('ARCH004', f, typ)
                msg = f'Code compiled from the Magnetar manifest references Torch type {typ}'
            if key and key not in seen:
                seen.add(key)
                add(key[0], msg, tr, ctx)
    # Textual checks for what cannot resolve (no Torch DLLs in the Magnetar compile, no game DLLs in the Quasar compile).
    for data in head_runs:
        kind, ctx = data['_kind'], f'{data["_target"]}/{data["runtime"]}'
        pattern = {'magnetar': r'^\s*using\s+(static\s+)?Torch\b', 'quasar': r'^\s*using\s+(static\s+)?(Sandbox|VRage|SpaceEngineers)\b'}.get(kind)
        if not pattern:
            continue
        for f in data.get('sources', []):
            for i, line in enumerate(read_text(os.path.join(tree, f)).splitlines(), 1):
                if re.match(pattern, line) and not guard_at(f, i):
                    rule = 'ARCH004' if kind == 'magnetar' else 'ARCH003'
                    key = (rule, f, line.strip())
                    if key not in seen:
                        seen.add(key)
                        add(rule, f'{"Magnetar-compiled" if kind == "magnetar" else "Quasar UI"} file has `{line.strip()}` outside a host #if', {'file': f, 'line': i}, ctx)
    return out, guarded


# ------------------------------------------------------------------------------------------------ release consistency

def version_tuple(v):
    nums = [int(x) for x in re.findall(r'\d+', v or '')][:4]
    return tuple(nums + [0] * (4 - len(nums))) if nums else None


def release_checks(tree, manifests, targets, compiled_files):
    versions = []

    def add(source, value):
        if value:
            versions.append({'source': source, 'value': value.strip()})

    for f in walk_files(tree):
        name = os.path.basename(f)
        r = rel(f, tree)
        if name in ('Version.Build.props', 'Directory.Build.props', 'Directory.Build.targets'):
            m = re.search(r'<Version>\s*([^<]+?)\s*</Version>', read_text(f))
            if m:
                add(r, m.group(1))
        elif name.endswith('.cs') and (r in compiled_files or re.search(r'(^|/)(ClientPlugin|ServerPlugin|Versioning|Properties)/', r)):
            text = read_text(f)
            for attr in ('AssemblyVersion', 'AssemblyFileVersion', 'AssemblyInformationalVersion'):
                for m in re.finditer(rf'\[\s*assembly\s*:\s*{attr}(?:Attribute)?\s*\(\s*"([^"]+)"', text):
                    add(f'{r} [{attr}]', m.group(1))
        elif name == 'quasar-plugin.json':
            try:
                add(r, json.load(open(f, encoding='utf-8-sig')).get('version'))
            except (OSError, ValueError):
                pass
    for m in manifests:
        add(m['rel'] + ' <Version>', m.get('version'))
    for t in targets:
        if t['kind'] == 'torch':
            add(t['manifest']['rel'] + ' <Version>', t['manifest'].get('version'))
    changelog = None
    for f in walk_files(tree):
        if os.path.basename(f).lower() in ('changelog.md', 'changes.md'):
            for line in read_text(f).splitlines():
                m = re.match(r'^#+\s*\[?v?(\d+(?:\.\d+)+)', line.strip())
                if m:
                    changelog = {'file': rel(f, tree), 'heading': line.strip(), 'version': m.group(1)}
                    add(rel(f, tree) + ' newest heading', m.group(1))
                    break
            break
    tuples = {version_tuple(v['value']) for v in versions if version_tuple(v['value'])}
    todos = []
    for r in sorted(compiled_files):
        for i, line in enumerate(read_text(os.path.join(tree, r)).splitlines(), 1):
            if re.search(r'\b(TODO|FIXME|HACK|XXX)\b', line):
                todos.append({'file': r, 'line': i, 'text': line.strip()[:160]})
    manifest_checks = []
    for m in manifests:
        desc = m.get('description') or ''
        body = desc.strip()
        lines = [l for l in desc.splitlines() if l.strip()]
        indents = sorted({re.match(r'^[ \t]*', l).group(0).replace('\t', '\\t') for l in lines})
        mixed = any('\\t' in i for i in indents) and any(' ' in i for i in indents)
        problems = []
        if len(body) > 1000:
            problems.append(f'Description is {len(body)} characters (> 1000: the Pulsar client list truncates it)')
        if mixed or len(indents) > 2:
            problems.append(f'Description indentation is uneven ({len(indents)} distinct indents{", tabs and spaces mixed" if mixed else ""})')
        commit = m.get('commit') or ''
        if not re.fullmatch(r'[0-9a-f]{40}', commit):
            problems.append(f'Commit "{commit}" is not a full 40-character SHA')
        if re.search(r'TODO|placeholder|xxx|owner', (m.get('repoId') or '') + commit, re.I) or not m.get('repoId') and m.get('sourceDirectories'):
            problems.append(f'RepoId "{m.get("repoId")}" looks like a placeholder or is missing')
        if re.search(r'\bTODO\b', read_text(m['path'])):
            problems.append('manifest still contains TODO')
        manifest_checks.append({'manifest': m['rel'], 'descriptionLength': len(body), 'indents': indents, 'commit': commit,
                                'repoId': m.get('repoId'), 'problems': problems})
    return {'versions': versions, 'consistent': len(tuples) <= 1, 'distinctVersions': sorted('.'.join(map(str, t)) for t in tuples),
            'changelog': changelog, 'todos': todos, 'manifests': manifest_checks}


# ------------------------------------------------------------------------------------------------ container work (tests, vulnerabilities, csproj build)

def docker_mounts(refs, work_dirs):
    m = []
    for host, guest, mode in work_dirs:
        m += ['-v', f'{host}:{guest}:{mode}']
    for key, guest in (('ds64', '/refs/ds'), ('interim', '/refs/interim'), ('legacy', '/refs/legacy'), ('torch', '/refs/torch'), ('quasar', '/refs/quasar'),
                       # a Magnetar install layout, for projects that derive PluginSdk's folder from $(Magnetar)
                       ('interim', '/refs/magnetar/Libraries/MagnetarInterim'), ('legacy', '/refs/magnetar/Libraries/MagnetarLegacy'),
                       # the SE client's Bin64 (resolved only for a repo with a Pulsar client plugin)
                       ('bin64', '/refs/bin64')):
        if refs.get(key):
            m += ['-v', f'{refs.get(key)}:{guest}:ro']
    return m


def msbuild_props(refs):
    # Folders end with a separator: projects write both $(GameBinaries)\X.dll and $(GameBinaries)X.dll (a default such
    # as $(RepoRoot)GameBinaries\ carries its own), and /refs/ds//X.dll resolves as well.
    props = ['-p:Dedicated64=/refs/ds/', '-p:DS64=/refs/ds/', '-p:GameBinaries=/refs/ds/', '-p:TorchBinaries=/refs/torch/', '-p:TorchDir=/refs/torch/',
             '-p:QuasarPluginAbstractionsAssembly=/refs/quasar/Quasar.Plugin.Abstractions.dll',
             '-p:MagnetarProtocolAssembly=/refs/quasar/Magnetar.Protocol.dll', '-p:DeployToMagnetar=false', '-p:DeployToQuasar=false',
             '-p:EnableWindowsTargeting=true', '-p:Magnetar=/refs/magnetar/', '-p:Quasar=', '-nologo']
    if refs is not None and refs.get('bin64'):
        # Client plugin projects (the hub's client template) take the game folder from $(Bin64).
        props.append('-p:Bin64=/refs/bin64/')
    return props


def sdk_available():
    return image_id(IMAGES['sdk']) is not None


CONTAINER_SEQ = [0]
# Each dotnet command inside a container gets its own limit, so one hung analyzer or build cannot stall the run.
STEP_LIMIT = 'timeout -k 30 1200'


def container(args, refs, mounts, script, timeout=3600, network=True):
    """docker run --rm of the local SDK image under a name of our own; if the time limit is hit, that container (and only
    that one) is removed, so nothing is left running. The script goes in a file mounted read-only, never on the command
    line: one line per build made it longer than Windows allows (32,767 characters, WinError 206) for large solutions."""
    os.makedirs(DOCKER_NUGET, exist_ok=True)
    CONTAINER_SEQ[0] += 1
    name = f'deeptools-{os.getpid()}-{CONTAINER_SEQ[0]}'
    host_out = next((h for h, g, _ in mounts if g == '/work/out'), None)
    if not host_out:
        raise RuntimeError('container(): a /work/out mount is required (the script is written next to it)')
    sdir = os.path.join(os.path.dirname(os.path.abspath(host_out)), 'container-script')
    os.makedirs(sdir, exist_ok=True)
    with open(os.path.join(sdir, f'{name}.sh'), 'w', encoding='utf-8', newline='\n') as f:
        f.write(script + '\n')
    cmd = ['run', '--rm', '--init', '--name', name, f'--cpuset-cpus={cpuset(args.affinity_mask)}', '--memory', '8g',
           '-e', 'DOTNET_CLI_TELEMETRY_OPTOUT=1', '-e', 'TESTINGPLATFORM_TELEMETRY_OPTOUT=1', '-e', 'DOTNET_NOLOGO=1',
           '-e', 'NUGET_PACKAGES=/nuget']
    # Nested MSBuild invocations (for example Quasar's BuildQuasarAgent target)
    # do not inherit parent -p: properties; make the same refs available as
    # environment variables so child builds resolve the game and Magnetar SDK.
    if refs is not None and refs.get('ds64'):
        cmd += ['-e', 'DS64=/refs/ds/']
    if refs is not None and refs.get('interim'):
        cmd += ['-e', 'MagnetarBin=/refs/magnetar/Libraries/MagnetarInterim']
    if not network:
        cmd += ['--network', 'none']
    extra_mounts = list(mounts)
    # Some repositories (Quasar) keep a local NuGet source in a sibling
    # diagnostics-artifacts folder. NuGet.Config resolves it relative to the
    # tree, so make the same folder visible at the container's /work level.
    repo = getattr(args, 'repo', None)
    if repo:
        diagnostics = os.path.join(os.path.dirname(os.path.abspath(repo)), 'diagnostics-artifacts')
        if os.path.isdir(diagnostics):
            extra_mounts.append((diagnostics, '/work/diagnostics-artifacts', 'ro'))
    cmd += docker_mounts(refs, extra_mounts + [(sdir, '/work/script', 'ro')]) + ['-v', f'{DOCKER_NUGET}:/nuget', '--entrypoint', 'bash',
                                                                          IMAGES['sdk'], f'/work/script/{name}.sh']
    r = docker(*cmd, timeout=timeout)
    if r.returncode == 124:
        docker('rm', '-f', name, timeout=120)
        gap(f'a container stage hit its {timeout} s limit and was stopped ({name})')
    return r


def plugin_projects(targets, tree):
    projs = []
    for t in targets:
        p = t.get('project')
        if p and p not in projs:
            projs.append(p)
    # The Magnetar build project: the csproj in the first SourceDirectory that has one.
    for t in targets:
        if t['kind'] in ('magnetar', 'pulsar'):
            for d in t['manifest']['sourceDirectories']:
                for p in glob.glob(os.path.join(tree, d, '*.csproj')):
                    if p not in projs:
                        projs.append(p)
    return projs


def vulnerabilities(args, refs, tree, targets, manifests, work, projects=None):
    result = {'projects': [], 'packagesConfig': [], 'manifestNuGet': [], 'status': 'not run'}
    if args.skip_vuln:
        result['status'] = 'not run: --skip-vuln'
        return result
    if not sdk_available():
        result['status'] = f'not run: {IMAGES["sdk"]} image not present (restores run only in a container)'
        gap(result['status'])
        return result
    vdir = os.path.join(work, 'vuln')
    vtree = os.path.join(vdir, 'tree')
    shutil.rmtree(vdir, ignore_errors=True)
    shutil.copytree(tree, vtree)
    windowsify(vtree)
    extra = []
    for f in walk_files(tree, 'packages.config'):
        try:
            for p in ET.parse(f).getroot():
                if p.get('id'):
                    extra.append({'id': p.get('id'), 'version': p.get('version'), 'from': rel(f, tree)})
        except ET.ParseError:
            pass
    for m in manifests:
        for p in m.get('nuget') or []:
            extra.append({'id': p['id'], 'version': p['version'], 'from': m['rel']})
    projs = [rel(p, tree) for p in (projects if projects is not None else plugin_projects(targets, tree))]
    if extra:
        os.makedirs(os.path.join(vtree, '.deep-vuln'), exist_ok=True)
        items = '\n'.join(f'<PackageReference Include="{e["id"]}" Version="{e["version"]}" />' for e in extra)
        with open(os.path.join(vtree, '.deep-vuln', 'Extra.csproj'), 'w', encoding='utf-8') as f:
            f.write(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework></PropertyGroup><ItemGroup>{items}</ItemGroup></Project>')
        projs.append('.deep-vuln/Extra.csproj')
    props = ' '.join(msbuild_props(refs))
    lines = ['cd /work/tree']
    for i, p in enumerate(projs):
        lines.append(f'echo "### {p}"; {STEP_LIMIT} dotnet restore "{p}" {props} -v:quiet > /work/out/restore-{i}.log 2>&1; echo "restore-exit $?" ; '
                     f'{STEP_LIMIT} dotnet list "{p}" package --vulnerable --include-transitive --format json > /work/out/vuln-{i}.json 2> /work/out/vuln-{i}.err; echo "list-exit $?"')
    os.makedirs(os.path.join(vdir, 'out'), exist_ok=True)
    log(f'vulnerability scan of {len(projs)} projects (container restore + dotnet list --vulnerable)')
    r = container(args, refs, [(vtree, '/work/tree', 'rw'), (os.path.join(vdir, 'out'), '/work/out', 'rw')], '; '.join(lines), timeout=2400)
    with open(os.path.join(vdir, 'container.log'), 'w', encoding='utf-8') as f:
        f.write(r.stdout + '\n' + r.stderr)
    for i, p in enumerate(projs):
        entry = {'project': p, 'vulnerable': [], 'status': 'ok'}
        try:
            data = json.load(open(os.path.join(vdir, 'out', f'vuln-{i}.json'), encoding='utf-8'))
            for proj in data.get('projects', []):
                for fw in proj.get('frameworks', []) or []:
                    for kind in ('topLevelPackages', 'transitivePackages'):
                        for pkg in fw.get(kind, []) or []:
                            for v in pkg.get('vulnerabilities', []) or []:
                                entry['vulnerable'].append({'framework': fw.get('framework'), 'package': pkg.get('id'),
                                                            'version': pkg.get('resolvedVersion'), 'severity': v.get('severity'),
                                                            'advisory': v.get('advisoryurl'), 'transitive': kind == 'transitivePackages'})
                if proj.get('problems'):
                    entry['problems'] = proj['problems']
        except (OSError, ValueError):
            err = read_text(os.path.join(vdir, 'out', f'vuln-{i}.err')) + read_text(os.path.join(vdir, 'out', f'restore-{i}.log'))[-600:]
            entry['status'] = 'failed: ' + (err.strip()[-400:] or 'no output')
        result['projects'].append(entry)
    result['extraPackages'] = extra
    result['status'] = 'ran'
    failed = [e['project'] for e in result['projects'] if e['status'] != 'ok']
    if failed:
        gap(f'vulnerability listing failed for: {", ".join(failed)}')
    return result


def run_tests(args, refs, tree, work, ships_legacy, changed, members):
    out = {'projects': [], 'net48Tested': False, 'status': 'not run'}
    projects = [parse_csproj(p, tree) for p in walk_files(tree, '*.csproj')]
    tests = [p for p in projects if p.get('test')]
    out['found'] = [p['rel'] for p in tests]
    if not tests:
        out['status'] = 'no test projects'
        if ships_legacy:
            gap('no test projects: nothing tests the code on net48 (the plugin ships on Legacy)')
        return out, {}
    out['net48Targeted'] = any(any(f.startswith('net4') for f in p['frameworks']) for p in tests)
    if ships_legacy and not out['net48Targeted']:
        gap('tests not on net48: no test project targets net48 although the plugin ships on Legacy')
    if args.skip_tests:
        out['status'] = 'not run: --skip-tests'
        return out, {}
    if not sdk_available():
        out['status'] = f'not run: {IMAGES["sdk"]} not present (tests run only in a container)'
        gap(out['status'])
        return out, {}
    ttree = os.path.join(work, 'tests', 'tree')
    tout = os.path.join(work, 'tests', 'out')
    shutil.rmtree(os.path.join(work, 'tests'), ignore_errors=True)
    shutil.copytree(tree, ttree)
    windowsify(ttree)
    mdk_local_ini(ttree)
    os.makedirs(tout)
    # Old-style .NET Framework projects a test references need the reference assemblies package on Linux (SDK projects
    # get it implicitly), the same addition the csproj build makes; the repo's own Directory.Build.props stays imported.
    existing = os.path.join(ttree, 'Directory.Build.props')
    if os.path.isfile(existing):
        os.replace(existing, os.path.join(ttree, 'Directory.Build.repo.props'))
    with open(existing, 'w', encoding='utf-8') as f:
        f.write(TEST_PROPS)
    lines = ['cd /work/tree']
    pc_lines, pc_note = packages_config_lines([p['path'] for p in projects if not p.get('error')], tree, tout)
    if pc_lines:
        lines += pc_lines
        out['notes'] = [pc_note]
    plan = []
    for p in tests:
        for fw in p['frameworks'] or ['']:
            i = len(plan)
            if fw.startswith('net4'):
                # Built here (never on the host: plugin repos have deploy targets), run on the host afterwards.
                props = f'-p:TargetFramework={fw} -p:Configuration=Release {" ".join(msbuild_props(refs))} -p:MagnetarBin=/refs/magnetar/Libraries/MagnetarLegacy'
                lines.append(f'{STEP_LIMIT} dotnet build "{p["rel"]}" {props} -m:4 -o /work/out/bin{i} > /work/out/build-{i}.log 2>&1; '
                             f'echo "build48-{i} exit $?"; dotnet msbuild "{p["rel"]}" {props} -getProperty:TargetFileName > /work/out/tfn-{i}.json 2>/dev/null')
                plan.append({'project': p['rel'], 'framework': fw, 'index': i, 'host': True})
                continue
            cov = '--collect "XPlat Code Coverage"' if p['coverlet'] else ''
            fwarg = f'-f {fw}' if fw and '$(' not in fw else ''
            # PluginSdk's folder for projects that only take it from $(MagnetarBin) (a Directory.Build.props.user locally).
            fwarg += f' -p:MagnetarBin=/refs/magnetar/Libraries/{"MagnetarLegacy" if fw.startswith("net4") else "MagnetarInterim"}'
            lines.append(f'{STEP_LIMIT} dotnet test "{p["rel"]}" {fwarg} -c Release -m:4 {" ".join(msbuild_props(refs))} --logger "trx;LogFileName=t{i}.trx" '
                         f'--results-directory /work/out/r{i} {cov} > /work/out/test-{i}.log 2>&1; echo "test-{i} exit $?"')
            plan.append({'project': p['rel'], 'framework': fw, 'index': i, 'coverage': p['coverlet']})
    log(f'running {sum(1 for x in plan if not x.get("host"))} test runs and {sum(1 for x in plan if x.get("host"))} net48 test builds in the SDK container')
    r = container(args, refs, [(ttree, '/work/tree', 'rw'), (tout, '/work/out', 'rw')], '; '.join(lines), timeout=3600)
    with open(os.path.join(work, 'tests', 'container.log'), 'w', encoding='utf-8') as f:
        f.write(r.stdout + '\n' + r.stderr)
    host_plan = [x for x in plan if x.get('host')]
    if host_plan:
        out['host'] = host_tests(host_plan, r.stdout, tout, os.path.join(work, 'tests', 'host'), refs)
    coverage_lines = collections.defaultdict(dict)
    for item in plan:
        if 'index' not in item or item.get('status'):
            out['projects'].append(item)
            continue
        i = item['index']
        trx = glob.glob(os.path.join(tout, f'r{i}', '*.trx')) + glob.glob(os.path.join(tout, f'r{i}', '**', '*.trx'), recursive=True)
        if trx:
            root = ET.parse(trx[0]).getroot()
            counters = next((e for e in root.iter() if local(e.tag) == 'Counters'), None)
            if counters is not None:
                item.update({k: int(counters.get(k, 0)) for k in ('total', 'executed', 'passed', 'failed', 'error', 'timeout', 'notExecuted')})
            item['status'] = 'ran'
            if item.get('framework', '').startswith('net4'):
                out['net48Tested'] = True
        elif item.get('killedAtLimit'):
            item.update(status='ran', aborted=True)  # killed with its whole process tree at our time limit: a hang, not a gap
        else:
            item['status'] = 'failed to run: ' + read_text(os.path.join(tout, f'test-{i}.log'))[-500:]
            gap(f'tests {item["project"]} [{item["framework"]}] did not produce results')
        stopped = item.get('killedAtLimit') or item.get('exit') != 0
        if item.get('host') and item.get('status') == 'ran' and stopped and not (item.get('failed') or item.get('error') or item.get('timeout')):
            item['aborted'] = True  # the run stopped (hang, crash or the time limit) without a failed test in the TRX
        for cob in glob.glob(os.path.join(tout, f'r{i}', '**', 'coverage.cobertura.xml'), recursive=True):
            for cls in ET.parse(cob).getroot().iter():
                if local(cls.tag) != 'class':
                    continue
                fname = (cls.get('filename') or '').replace('\\', '/')
                m = re.search(r'/work/tree/(.*)$', fname)
                fname = m.group(1) if m else fname
                for ln in cls.iter():
                    if local(ln.tag) == 'line':
                        n, hits = int(ln.get('number', 0)), int(ln.get('hits', 0))
                        coverage_lines[fname][n] = max(coverage_lines[fname].get(n, 0), hits)
        out['projects'].append(item)
    out['status'] = 'ran'
    coverage = {}
    if coverage_lines:
        for mid in changed:
            m = members.get(mid)
            if not m:
                continue
            lines = coverage_lines.get(m['file'], {})
            hit = [n for n in range(m['start'], m['end'] + 1) if lines.get(n, 0) > 0]
            known = [n for n in range(m['start'], m['end'] + 1) if n in lines]
            coverage[mid] = 'covered' if hit else ('not covered' if known else 'no coverage data')
    return out, coverage


HOST_TEST_HANG = 300   # --blame-hang-timeout (s): vstest stops a test that runs longer
HOST_TEST_LIMIT = 1200  # our own limit (s) for one host test run; then the whole process tree is killed (job object)
HOST_TEST_ENV_DROP = re.compile(r'TOKEN|SECRET|PASSWORD|PASSWD|APIKEY|API_KEY|CREDENTIAL|^GH_|^GITHUB|PULSAR|TORCH|MAGNETAR|QUASAR|STEAM|'
                                r'^SE_|SPACEENGINEERS|^NUGET_|^AZURE|^AWS_', re.I)
HOST_TEST_PATH_DROP = re.compile(r'torch|spaceengineers|magnetar|quasar|steam', re.I)


def host_test_env(run_dir):
    """The environment for a host test run: no tokens or keys, no game/Torch/Magnetar/Steam variables or PATH entries,
    TEMP and the dotnet CLI home inside the run folder, telemetry off."""
    env = {k: v for k, v in os.environ.items() if not HOST_TEST_ENV_DROP.search(k)}
    env['PATH'] = os.pathsep.join(p for p in os.environ.get('PATH', '').split(os.pathsep) if p and not HOST_TEST_PATH_DROP.search(p))
    tmp = os.path.join(run_dir, 'tmp')
    os.makedirs(tmp, exist_ok=True)
    env.update(DOTNET_ENV)
    env.update({'TEMP': tmp, 'TMP': tmp, 'DOTNET_CLI_HOME': os.path.join(run_dir, 'cli-home'), 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1',
                'DOTNET_GENERATE_ASPNET_CERTIFICATE': 'false', 'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH': 'false', 'VSTEST_TELEMETRY_OPTEDIN': '0'})
    return env


def run_in_job(cmd, cwd, env, timeout, log_path, mask=0x00FFFFFF):
    """Starts cmd with CreateProcess (CREATE_NO_WINDOW, suspended), puts it in a job object that pins it to the given cores and
    kills the whole tree when the job closes, resumes it, and waits. Returns (exit code or None when killed at the limit)."""
    import ctypes.wintypes as wt

    class BASIC(ctypes.Structure):
        _fields_ = [('PerProcessUserTimeLimit', ctypes.c_int64), ('PerJobUserTimeLimit', ctypes.c_int64), ('LimitFlags', wt.DWORD),
                    ('MinimumWorkingSetSize', ctypes.c_size_t), ('MaximumWorkingSetSize', ctypes.c_size_t), ('ActiveProcessLimit', wt.DWORD),
                    ('Affinity', ctypes.c_size_t), ('PriorityClass', wt.DWORD), ('SchedulingClass', wt.DWORD)]

    class EXTENDED(ctypes.Structure):
        _fields_ = [('Basic', BASIC), ('IoInfo', ctypes.c_uint64 * 6), ('ProcessMemoryLimit', ctypes.c_size_t), ('JobMemoryLimit', ctypes.c_size_t),
                    ('PeakProcessMemoryUsed', ctypes.c_size_t), ('PeakJobMemoryUsed', ctypes.c_size_t)]

    kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
    ntdll = ctypes.WinDLL('ntdll')
    kernel32.CreateJobObjectW.restype = wt.HANDLE
    kernel32.CreateJobObjectW.argtypes = [ctypes.c_void_p, wt.LPCWSTR]
    kernel32.SetInformationJobObject.argtypes = [wt.HANDLE, ctypes.c_int, ctypes.c_void_p, wt.DWORD]
    kernel32.AssignProcessToJobObject.argtypes = [wt.HANDLE, wt.HANDLE]
    kernel32.TerminateJobObject.argtypes = [wt.HANDLE, wt.UINT]
    kernel32.CloseHandle.argtypes = [wt.HANDLE]
    ntdll.NtResumeProcess.argtypes = [wt.HANDLE]
    job = kernel32.CreateJobObjectW(None, None)
    if not job:
        raise OSError(f'CreateJobObject failed: {ctypes.get_last_error()}')
    info = EXTENDED()
    info.Basic.LimitFlags = 0x2000 | 0x10  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_AFFINITY
    info.Basic.Affinity = mask
    if not kernel32.SetInformationJobObject(job, 9, ctypes.byref(info), ctypes.sizeof(info)):  # JobObjectExtendedLimitInformation
        kernel32.CloseHandle(job)
        raise OSError(f'SetInformationJobObject failed: {ctypes.get_last_error()}')
    killed = False
    with open(log_path, 'wb') as logf:
        proc = subprocess.Popen(cmd, cwd=cwd, env=env, stdin=subprocess.DEVNULL, stdout=logf, stderr=subprocess.STDOUT,
                                creationflags=CREATE_NO_WINDOW | 0x00000004)  # CREATE_SUSPENDED: in the job before it can start children
        try:
            if not kernel32.AssignProcessToJobObject(job, int(proc._handle)):
                proc.kill()
                raise OSError(f'AssignProcessToJobObject failed: {ctypes.get_last_error()}')
            ntdll.NtResumeProcess(int(proc._handle))
            try:
                code = proc.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                killed = True
                kernel32.TerminateJobObject(job, 124)
                code = proc.wait(timeout=60)
        finally:
            kernel32.CloseHandle(job)  # KILL_ON_JOB_CLOSE: nothing it started outlives the run
    return None if killed else code


def stray_write_roots():
    """Folders a test must never write to: E:\\Torch and the game client's Bin64 (when present)."""
    roots = [r'E:\Torch'] if os.name == 'nt' else []
    roots += [os.path.join(lib, 'common', 'SpaceEngineers', 'Bin64') for lib in steam_libraries()]
    return [r for r in roots if os.path.isdir(r)]


def snapshot(roots):
    snap = {}
    for root in roots:
        for d, _, files in os.walk(root):
            for f in files:
                p = os.path.join(d, f)
                try:
                    st = os.stat(p)
                except OSError:
                    continue
                snap[p] = (st.st_mtime_ns, st.st_size)
    return snap


def link_host_references(bin_dir, refs):
    """A net48 test built in the container references the game, Torch and MagnetarLegacy assemblies with Private=false
    from /refs/..., folders that exist only in the container (a GameBinaries path baked into the test points there too),
    so on this PC nothing could load them. Their files are copied into the test's output folder (copies, not links, so a
    test can never change the cache), only where the build output has no file of that name. Returns {source: files added}."""
    added = {}
    for key in ('ds64', 'torch', 'legacy'):
        src = refs.get(key) if refs is not None else None
        if not src or not os.path.isdir(src):
            continue
        n = 0
        for name in sorted(os.listdir(src)):
            s, d = os.path.join(src, name), os.path.join(bin_dir, name)
            if not name.lower().endswith(('.dll', '.exe')) or not os.path.isfile(s) or os.path.exists(d):
                continue
            shutil.copy2(s, d)
            n += 1
        if n:
            added[key] = n
    return added


def host_tests(host_plan, container_stdout, tout, hdir, refs=None):
    """net48 test assemblies built in the container, run on this PC with dotnet test (vstest), headless."""
    exits = dict(re.findall(r'build48-(\d+) exit (\d+)', container_stdout))
    roots = stray_write_roots()
    t0 = time.time()
    before = snapshot(roots)
    summary = {'strayWriteRoots': roots, 'snapshotSeconds': round(time.time() - t0, 1), 'hangTimeout': HOST_TEST_HANG, 'limit': HOST_TEST_LIMIT}
    for item in host_plan:
        i = item['index']
        if exits.get(str(i)) != '0':
            item['status'] = f'not run: the net48 build in the container failed: {first_error(read_text(os.path.join(tout, f"build-{i}.log")))}'
            gap(f'tests {item["project"]} [{item["framework"]}] could not run: its container build failed')
            continue
        try:
            name = json.loads(read_text(os.path.join(tout, f'tfn-{i}.json')).strip().lstrip('\ufeff') or '""')
        except ValueError:
            name = read_text(os.path.join(tout, f'tfn-{i}.json')).strip()
        name = name.get('Properties', {}).get('TargetFileName') if isinstance(name, dict) else name
        dll = os.path.join(tout, f'bin{i}', name or '')
        if not name or not os.path.isfile(dll):
            item['status'] = f'not run: test assembly {name or "?"} not found in the build output'
            gap(f'tests {item["project"]} [{item["framework"]}] could not run: test assembly not found')
            continue
        item['hostReferences'] = link_host_references(os.path.dirname(dll), refs)
        run_dir = os.path.join(hdir, str(i))
        os.makedirs(run_dir, exist_ok=True)
        results = os.path.join(tout, f'r{i}')
        cmd = ['dotnet', 'test', dll, '--logger', f'trx;LogFileName=t{i}.trx', '--results-directory', results,
               '--blame-hang', '--blame-hang-timeout', f'{HOST_TEST_HANG}s', '--blame-hang-dump-type', 'none',
               # Inline run settings: xUnit v2 shadow-copies test assemblies under %TEMP% (inside the work dir here), which
               # can pass MAX_PATH and fail every test with "invalid codebase". Other frameworks ignore the setting.
               '--', 'xUnit.ShadowCopy=false']
        log(f'host test run (net48, headless): {item["project"]} [{item["framework"]}]')
        try:
            code = run_in_job(cmd, run_dir, host_test_env(run_dir), HOST_TEST_LIMIT, os.path.join(tout, f'test-{i}.log'))
        except OSError as e:
            item['status'] = f'not run: could not start the host test run: {e}'
            gap(f'tests {item["project"]} [{item["framework"]}] could not run on the host: {e}')
            continue
        item.update(exit=code, runner='dotnet test (vstest) on the host', killedAtLimit=code is None)
    after = snapshot(roots)
    changed = sorted(p for p, v in after.items() if before.get(p) != v)
    summary['strayWrites'] = changed[:200]
    summary['strayWriteCount'] = len(changed)
    if changed:
        log(f'files written under {", ".join(roots)} during the host test runs: {len(changed)} (first: {changed[0]})')
    return summary


DEEP_PROPS = '''<Project>
  <!-- deep_tools.py csproj-build cross-check: added to a copy of the tree only, never to the repo. -->
  <Import Project="$(MSBuildThisFileDirectory)Directory.Build.repo.props" Condition="Exists('$(MSBuildThisFileDirectory)Directory.Build.repo.props')" />
  <PropertyGroup>
    <AnalysisLevel>latest-all</AnalysisLevel>
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <CodeAnalysisTreatWarningsAsErrors>false</CodeAnalysisTreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <MagnetarBin Condition="$(TargetFramework.StartsWith('net4'))">/refs/legacy</MagnetarBin>
    <MagnetarBin Condition="'$(MagnetarBin)' == ''">/refs/interim</MagnetarBin>
    <NoWarn>$(NoWarn);NU1701;NU1603;NU1608;MSB3277;MSB3243</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <AdditionalFiles Include="/work/deep/BannedSymbols.txt" />
    <!-- DeepRules and any other custom analyzer in tools/bin/analyzers -->
    <Analyzer Include="/work/deeprules/*.dll" />
    <!-- Unconditional: it only acts on .NETFramework targets, and a condition here is evaluated before the project sets its TFM. -->
    <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />
@PACKAGES@
  </ItemGroup>
</Project>
'''


TEST_PROPS = '''<Project>
  <!-- deep_tools.py test copy only, never the repo: .NET Framework reference assemblies for old-style projects on Linux. -->
  <Import Project="$(MSBuildThisFileDirectory)Directory.Build.repo.props" Condition="Exists('$(MSBuildThisFileDirectory)Directory.Build.repo.props')" />
  <ItemGroup>
    <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />
  </ItemGroup>
</Project>
'''


# Build errors that come from packaging/copy steps rather than the compile (zip, copy, exec).
PACKAGING_ERRORS = 'error MSB3943|error MSB3021|error MSB3027|error MSB3030|error MSB3073|error MSB3026'
# MDK2 ModPackager's MdkPack target reports its pack failure with this <Error> text (no error code).
MDK_PACK_ERROR = 'MDK could not package the project'

# Bash, defined once at the top of the csproj-build script. A packaging step that writes into a folder the copied tree
# does not have (a git-ignored output folder such as ..\Build, which ZipDirectory does not create) fails with "Could not
# find a part of the path". deep_mkdirs <log> <list> creates the missing parent folders, only inside /work/tree (the copy
# in the work dir), lists them, and returns 0 when it created any, so the full build, packaging included, runs again.
DEEP_MKDIRS = ('deep_mkdirs() { local made=1 p d; '
               'while IFS= read -r p; do d=$(dirname "$p"); case "$(realpath -m "$d")/" in /work/tree/*) '
               'if [ ! -d "$d" ]; then mkdir -p "$d" && echo "$d" >> "$2" && made=0; fi;; esac; '
               'done < <(grep -oE "Could not find a part of the path \'/work/tree/[^\']+\'" "$1" '
               '| sed -E "s/^Could not find a part of the path \'//; s/\'\\$//" | sort -u); return $made; }')

DEEP_TARGETS = '''<Project>
  <!-- deep_tools.py csproj-build copy only. Old-style WPF pages cannot be compiled by the Linux SDK, so they are replaced
       by code-behind stand-ins (the same ones DeepScan uses). The repo's own NoWarn is cleared so nothing it hides
       stays hidden; only environment noise from building game references on Linux is muted. -->
  <Import Project="$(MSBuildThisFileDirectory)Directory.Build.repo.targets" Condition="Exists('$(MSBuildThisFileDirectory)Directory.Build.repo.targets')" />
  <PropertyGroup>
    <NoWarn>NU1701;NU1603;NU1608;MSB3277;MSB3243;MSB3245;MSB3270</NoWarn>
    <WarningsNotAsErrors></WarningsNotAsErrors>
    <WarningsAsErrors></WarningsAsErrors>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <MSBuildTreatWarningsAsErrors>false</MSBuildTreatWarningsAsErrors>
    <RunAnalyzers>true</RunAnalyzers>
    <RunAnalyzersDuringBuild>true</RunAnalyzersDuringBuild>
    <!-- Here, not in Directory.Build.props: that is read before the project sets its TargetFramework, so a ProjectReference
         built without a global TargetFramework wrote "<name>-.sarif" (counted again as net48). -->
    <ErrorLog>/work/out/sarif/$(MSBuildProjectName)-$(TargetFramework).sarif,version=2.1</ErrorLog>
  </PropertyGroup>
  <ItemGroup Condition="Exists('/work/deep/stubs/$(MSBuildProjectName).g.cs')">
    <Page Remove="@(Page)" />
    <ApplicationDefinition Remove="@(ApplicationDefinition)" />
    <Compile Include="/work/deep/stubs/$(MSBuildProjectName).g.cs" />
  </ItemGroup>
  <ItemGroup>
    <!-- The repo's own analyzer configs are replaced by the tools' config (they were stripped in this copy). -->
    <GlobalAnalyzerConfigFiles Remove="@(GlobalAnalyzerConfigFiles)" />
    <GlobalAnalyzerConfigFiles Include="/work/deep/deep.globalconfig" />
    <!-- GlobalAnalyzerConfigFiles added this late never reach csc; EditorConfigFiles does (it is what csc gets as
         /analyzerconfig). Without it the tools' severities and the data-flow depth limit were not applied. -->
    <EditorConfigFiles Include="/work/deep/deep.globalconfig" />
  </ItemGroup>
</Project>
'''


EVAL_PROPERTIES = ['Nullable', 'LangVersion', 'DefineConstants', 'AllowUnsafeBlocks', 'TargetFramework', 'TargetFrameworks']


def evaluated_compile(item, path):
    """compiles[] entry for one csproj build, from `dotnet msbuild -getProperty/-getItem` (MSBuild's own evaluation)."""
    entry = {'id': item['compile'], 'project': item['project'], 'tfm': item['framework'], 'profile': 'csproj', 'nullable': None,
             'langVersion': None, 'defines': None, 'unsafe': None, 'fileCount': None, 'build': item.get('status'), 'evaluated': False}
    try:
        data = json.loads(read_text(path).lstrip('\ufeff'))
    except ValueError:
        err = read_text(path[:-5] + '.err') or read_text(path)
        entry['evaluationError'] = first_error(err) if err.strip() else 'no output'
        return entry
    props = data.get('Properties') or {}
    items = (data.get('Items') or {}).get('Compile') or []
    # Empty values are the compiler's defaults: nullable disabled, the default language version, unsafe off.
    entry.update(nullable=(props.get('Nullable') or 'disable').lower(), langVersion=props.get('LangVersion') or 'default',
                 defines=[d for d in re.split(r'[;,]', props.get('DefineConstants') or '') if d.strip()],
                 unsafe=(props.get('AllowUnsafeBlocks') or '').lower() == 'true', fileCount=len(items), evaluated=True)
    if item['framework'].startswith('net48 (') and props.get('TargetFramework'):
        entry['tfm'] = props['TargetFramework']
    return entry


def strip_repo_configs(btree):
    """Removes severity settings from the copy's .editorconfig/.globalconfig files so the tools' rule set applies."""
    stripped = []
    for f in walk_files(btree):
        name = os.path.basename(f).lower()
        if name not in ('.editorconfig', '.globalconfig') and not name.endswith('.globalconfig'):
            continue
        lines = read_text(f).splitlines()
        keep = []
        for line in lines:
            if re.match(r'^\s*(dotnet_diagnostic\.[^.]+\.severity|dotnet_analyzer_diagnostic\.[^=]*severity|is_global|global_level)\s*=', line, re.I):
                stripped.append({'file': rel(f, btree), 'line': line.strip()})
                continue
            keep.append(line)
        with open(f, 'w', encoding='utf-8') as out:
            out.write('\n'.join(keep) + '\n')
    return stripped


WINDOWS_CHECK = re.compile(r"\$\(\[MSBuild\]::IsO[sS]Platform\(\s*['\"]Windows['\"]\s*\)\)")


def windowsify(root):
    """In a copy only: makes IsOSPlatform('Windows') conditions true, so projects declare the TFMs they declare on
    Windows (net48 next to net10.0) and the Linux SDK container restores and builds them all, as a Windows build does."""
    changed = []
    for f in walk_files(root):
        if not f.lower().endswith(('.csproj', '.props', '.targets', '.projitems')):
            continue
        text = read_text(f)
        new = WINDOWS_CHECK.sub('true', text)
        if new != text:
            with open(f, 'w', encoding='utf-8') as out:
                out.write(new)
            changed.append(rel(f, root))
    return changed


def nuget_folder_version(version):
    """The version folder NuGet's global packages folder uses: normalized (at least three parts, a zero fourth part
    dropped, metadata dropped), lower case."""
    core, _, label = version.split('+')[0].partition('-')
    parts = (core.split('.') + ['0', '0', '0'])[:max(3, len(core.split('.')))]
    if len(parts) == 4 and parts[3] == '0':
        parts = parts[:3]
    parts = [str(int(x)) if x.isdigit() else x for x in parts]
    return ('.'.join(parts) + ('-' + label if label else '')).lower()


def packages_config_lines(projects, tree, host_out):
    """Old-style projects with packages.config reference DLLs at <dir>/packages/<Id>.<Version>/... (HintPath). The Linux
    SDK cannot restore packages.config (MSBuild.exe on Windows only), so the exact versions are fetched into the NuGet
    cache with PackageDownload and copied to the packages folder the HintPaths point at, inside the copied tree.
    Returns (bash lines, note) or ([], None)."""
    wanted, dests = {}, set()
    for p in projects:
        cfg = os.path.join(os.path.dirname(p), 'packages.config')
        if not os.path.isfile(cfg):
            continue
        try:
            pkgs = [(e.get('id'), e.get('version')) for e in ET.parse(cfg).getroot().iter()
                    if local(e.tag) == 'package' and e.get('id') and e.get('version')]
        except (ET.ParseError, OSError):
            continue
        for r_ in parse_csproj(p, tree).get('references') or []:
            m = re.match(r'^(.*?)packages[\\/][^\\/]+[\\/]', r_.get('hint') or '', re.I)
            if m:
                d = os.path.normpath(os.path.join(os.path.dirname(p), m.group(1).replace('\\', os.sep), 'packages'))
                if not rel(d, tree).startswith('..'):
                    dests.add(rel(d, tree))
        wanted.update({k: True for k in pkgs})
    if not wanted or not dests:
        return [], None
    pdir = os.path.join(host_out, 'packages-config')
    os.makedirs(pdir, exist_ok=True)
    items = '\n'.join(f'    <PackageDownload Include="{i}" Version="[{v}]" />' for i, v in sorted(wanted))
    with open(os.path.join(pdir, 'Restore.csproj'), 'w', encoding='utf-8') as f:
        f.write(f'<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n'
                f'  <ItemGroup>\n{items}\n  </ItemGroup>\n</Project>\n')
    lines = [f'{STEP_LIMIT} dotnet restore /work/out/packages-config/Restore.csproj -nologo -v:quiet > /work/out/packages-config.log 2>&1; '
             'echo "packages-config exit $?"']
    for d in sorted(dests):
        target = '/work/tree' + ('' if d == '.' else '/' + d)
        for i, v in sorted(wanted):
            src, dst = f'/nuget/{i.lower()}/{nuget_folder_version(v)}', f'{target}/{i}.{v}'
            lines.append(f'mkdir -p {shlex.quote(target)}; [ -d {shlex.quote(src)} ] && [ ! -e {shlex.quote(dst)} ] && cp -r {shlex.quote(src)} {shlex.quote(dst)}')
    note = (f'packages.config: {len(wanted)} packages fetched with PackageDownload and copied to {", ".join(sorted(dests))} '
            '(the Linux SDK has no packages.config restore)')
    return lines, note


def mdk_local_ini(root):
    """In a copy only: MDK2 projects (Mal.Mdk2.References) find the game with SpaceEngineersFinder, which cannot on Linux
    unless <Project>.mdk.local.ini names binarypath, so each gets one pointing at the dedicated server's DedicatedServer64
    (every assembly the package references is there). A packager's output goes to /work/out/mdk/<project>, in the work
    dir. Returns the projects changed."""
    changed = []
    for f in walk_files(root):
        if f.lower().endswith('.csproj') and 'mal.mdk2.references' in read_text(f).lower():
            name = os.path.splitext(os.path.basename(f))[0]
            with open(os.path.splitext(f)[0] + '.mdk.local.ini', 'w', encoding='utf-8', newline='\n') as out:
                out.write(f'; written by deep_tools.py in its build copy\n[mdk]\ninteractive=off\noutput=/work/out/mdk/{fs_name(name)}\n'
                          'binarypath=/refs/ds\n')
            changed.append(rel(f, root))
    return changed


def csproj_build(args, refs, tree, pack_info, work, projects):
    """Builds the repo's own projects in the SDK container with the analyzer pack added through Directory.Build.props
    in a copy of the tree, for every target framework they declare, and collects each build's SARIF error log."""
    result = {'status': 'not run', 'builds': []}
    if args.skip_csproj_build:
        result['status'] = 'not run: --skip-csproj-build'
        result['compiles'] = []
        return result, [], []
    if not projects:
        result['status'] = 'not run: no projects'
        return result, [], []
    if not sdk_available():
        result['status'] = f'not run: {IMAGES["sdk"]} not present (the projects are built only in a container)'
        gap(result['status'])
        return result, [], []
    bdir = os.path.join(work, 'csproj-build')
    btree = os.path.join(bdir, 'tree')
    shutil.rmtree(bdir, ignore_errors=True)
    shutil.copytree(tree, btree)
    result['windowsified'] = windowsify(btree)
    mdk = mdk_local_ini(btree)
    if mdk:
        result.setdefault('notes', []).append(f'MDK2 projects ({", ".join(mdk)}): <project>.mdk.local.ini with binarypath=/refs/ds written in the '
                                              'copy (SpaceEngineersFinder cannot find the game on Linux)')
    deep = os.path.join(bdir, 'deep')
    os.makedirs(os.path.join(deep, 'stubs'))
    shutil.copy2(BANNED, deep)
    shutil.copy2(GLOBALCONFIG, os.path.join(deep, 'deep.globalconfig'))
    os.makedirs(os.path.join(bdir, 'out', 'sarif'))
    result['strippedRepoConfig'] = strip_repo_configs(btree)
    # The NetAnalyzers package too: referencing it replaces the SDK's bundled (older) copy, so both builds run the same
    # version, and the bundled copy ignores the data-flow call-chain limit and can run for over 20 minutes on large methods.
    pkgs = '\n'.join(f'    <PackageReference Include="{n}" Version="{p["version"]}" PrivateAssets="all" IncludeAssets="runtime; build; native; contentfiles; analyzers" />'
                     for n, p in pack_info['packs'].items() if n not in pack_info.get('dropped', {}))
    for name, content in (('Directory.Build.props', DEEP_PROPS), ('Directory.Build.targets', DEEP_TARGETS)):
        existing = os.path.join(btree, name)
        if os.path.isfile(existing):
            os.replace(existing, os.path.join(btree, name.replace('.Build.', '.Build.repo.')))
        with open(existing, 'w', encoding='utf-8') as f:
            f.write(content.replace('@PACKAGES@', pkgs))
    mounts = [(btree, '/work/tree', 'rw'), (os.path.join(bdir, 'out'), '/work/out', 'rw'), (deep, '/work/deep', 'ro')]
    if os.path.isdir(CUSTOM_ANALYZERS):
        mounts.append((CUSTOM_ANALYZERS, '/work/deeprules', 'ro'))
    lines = [DEEP_MKDIRS, 'cd /work/tree']
    pc_lines, pc_note = packages_config_lines(projects, tree, os.path.join(bdir, 'out'))
    if pc_lines:
        lines += pc_lines
        result.setdefault('notes', []).append(pc_note)
    plan = []
    junctions = set()
    for p in projects:
        parsed = parse_csproj(p, tree)
        if parsed.get('error'):
            result['builds'].append({'project': rel(p, tree), 'framework': '?', 'status': 'not run: unreadable project: ' + parsed['error']})
            continue
        if not parsed['sdk'] and parsed['xaml']:
            if xaml_stubs(parsed, os.path.join(deep, 'stubs', os.path.splitext(os.path.basename(p))[0] + '.g.cs')):
                result.setdefault('notes', []).append(f'{parsed["rel"]}: XAML pages replaced by code-behind stand-ins (the Linux SDK has no WPF XAML compiler)')
        extra = ''
        pb_packager = next((pk for pk in parsed['packages'] if (pk.get('id') or '').lower() == 'mal.mdk2.pbpackager'), None)
        if pb_packager and not os.path.isdir(os.path.join(DOCKER_NUGET, 'mal.mdk2.pbpackager', (pb_packager.get('version') or '').lower(),
                                                          'tools', 'linux-x64')):
            # Its MdkRestore/MdkPack targets Exec tools\mdk.exe: this package version ships Windows programs only (mdk.exe,
            # mdknotify-win.exe), which are never started from the Linux container. Switched off here, reported as not covered.
            off = os.path.join(deep, 'mdk-windows-only.targets')
            with open(off, 'w', encoding='utf-8') as f:
                f.write('<Project>\n  <!-- deep_tools.py build copy: MDK2 PbPackager runs Windows-only tools; never started here. -->\n'
                        '  <Target Name="MdkRestore" />\n  <Target Name="MdkPack" />\n</Project>\n')
            extra = ' -p:CustomAfterMicrosoftCommonTargets=/work/deep/mdk-windows-only.targets'
            result.setdefault('notCovered', []).append(
                f'{parsed["rel"]}: MDK2 PbPackager {pb_packager.get("version")} targets MdkRestore and MdkPack (Exec tools/mdk.exe; the package '
                'ships only Windows programs, mdk.exe and mdknotify-win.exe) cannot run in the Linux container and were switched off in the copy; '
                'the code was compiled and analysed, the script was not packed')
        # Torch-style projects point at ..\GameBinaries and ..\TorchBinaries junctions next to the solution.
        for r_ in parsed['references']:
            hint = (r_.get('hint') or '').replace('\\', '/')
            for j, key in (('GameBinaries', 'ds64'), ('TorchBinaries', 'torch')):
                if f'{j}/' in hint and refs.get(key):
                    top = os.path.normpath(os.path.join(os.path.dirname(p), hint.split(j + '/')[0] or '.'))
                    r = rel(top, tree)
                    junctions.add((refs.get(key), '/work/tree' + ('' if r == '.' else '/' + r) + '/' + j))
        for fw in parsed['frameworks'] or ['']:
            i = len(plan)
            # Both properties: the implicit restore reads TargetFrameworks, so a TFM the project only declares on Windows
            # (net48 under a Windows-only condition) still gets restored here.
            # An unresolved $(Name) framework is left to MSBuild (and never put in double quotes, where bash expands it).
            fwarg = f'"-p:TargetFramework={fw}"' if fw and parsed['sdk'] and '$(' not in fw else ''
            if not parsed['sdk'] and re.search(r"Release\|x64", read_text(p)):
                fwarg += ' -p:Platform=x64'
            # Global properties: a repo's WarningsAsErrors would otherwise turn analyzer hits into build failures
            # (MSBuild reads it before Directory.Build.targets can clear it).
            no_errors = '"-p:WarningsAsErrors=" "-p:MSBuildWarningsAsErrors=" -p:TreatWarningsAsErrors=false -p:CodeAnalysisTreatWarningsAsErrors=false'
            build = f'dotnet build "{parsed["rel"]}" -c Release -m:4 {fwarg} {no_errors} {" ".join(msbuild_props(refs))}{extra} -v:quiet'
            # A packaging/copy step (zip, copy, exec after build) that failed only because its output folder is missing
            # from the copy gets that folder (inside the copy) and the full build runs again, so packaging really runs.
            # If packaging still fails, the compile and analysis are run on their own with -t:Compile, so the code is
            # still checked and only the packaging is reported as not covered.
            log_ = f'/work/out/build-{i}.log'
            lines.append(f'{STEP_LIMIT} {build} > {log_} 2>&1; c=$?; echo "build-{i} exit $c"; L={log_}; '
                         f'if [ $c -ne 0 ] && grep -Eq "{PACKAGING_ERRORS}" {log_} && deep_mkdirs {log_} /work/out/build-{i}-mkdir.txt; then '
                         f'{STEP_LIMIT} {build} > /work/out/build-{i}-retry.log 2>&1; c=$?; echo "build-{i} retry-exit $c"; L=/work/out/build-{i}-retry.log; fi; '
                         # MDK2 ModPackager's MdkPack (after the build) failing: the full build again with MdkPack off
                         # (its own MdkBuildConfiguration switch), since -t:Compile would skip the game references MDK adds.
                         f'if [ $c -ne 0 ] && grep -q "{MDK_PACK_ERROR}" $L; then '
                         f'{STEP_LIMIT} {build} -p:MdkBuildConfiguration=deep-tools-pack-off > /work/out/build-{i}-compile.log 2>&1; '
                         f'echo "build-{i} nopack-exit $?"; '
                         f'elif [ $c -ne 0 ] && grep -Eq "{PACKAGING_ERRORS}" $L; then '
                         f'{STEP_LIMIT} {build} -t:Compile > /work/out/build-{i}-compile.log 2>&1; echo "build-{i} compile-exit $?"; fi')
            # The compile settings MSBuild evaluated for this build (Directory.Build.*, imports and linked files included).
            # For SDK projects AddImplicitDefineConstants runs first, so the TFM symbols the build adds are in DefineConstants.
            evaluate = (f'dotnet msbuild "{parsed["rel"]}" -p:Configuration=Release {fwarg} {no_errors} {" ".join(msbuild_props(refs))} '
                        + ('-t:AddImplicitDefineConstants ' if parsed['sdk'] else '')
                        + ' '.join(f'-getProperty:{p}' for p in EVAL_PROPERTIES) + ' -getItem:Compile')
            lines.append(f'{STEP_LIMIT} {evaluate} > /work/out/eval-{i}.json 2> /work/out/eval-{i}.err')
            # SARIF name: $(MSBuildProjectName)-$(TargetFramework); a non-SDK project gets no TargetFramework property.
            plan.append({'project': parsed['rel'], 'framework': fw or 'net48 (old-style)', 'index': i,
                         'sarif': os.path.splitext(os.path.basename(p))[0] + '-' + (fw if parsed['sdk'] else '')})
    mounts += [(host, guest, 'ro') for host, guest in sorted(junctions)]
    log(f'csproj build: {len(plan)} builds in the SDK container')
    r = container(args, refs, mounts, '; '.join(lines), timeout=3600)
    with open(os.path.join(bdir, 'container.log'), 'w', encoding='utf-8') as f:
        f.write(r.stdout + '\n' + r.stderr)
    exits = dict(re.findall(r'build-(\d+) exit (\d+)', r.stdout))
    compiles = []
    compile_exits = dict(re.findall(r'build-(\d+) compile-exit (\d+)', r.stdout))
    retry_exits = dict(re.findall(r'build-(\d+) retry-exit (\d+)', r.stdout))
    nopack_exits = dict(re.findall(r'build-(\d+) nopack-exit (\d+)', r.stdout))
    diags, suppressed = [], []
    for item in plan:
        code = exits.get(str(item['index']))
        logtext = read_text(os.path.join(bdir, 'out', f'build-{item["index"]}.log'))
        retry_code = retry_exits.get(str(item['index']))
        if retry_code is not None:
            # The packaging step's missing output folders were created in the copy and the full build ran again.
            made = [d for d in read_text(os.path.join(bdir, 'out', f'build-{item["index"]}-mkdir.txt')).splitlines() if d.strip()]
            item['firstExit'] = int(code) if code is not None else None
            item['createdOutputDirs'] = [d[len('/work/tree/'):] if d.startswith('/work/tree/') else d for d in made]
            result.setdefault('notes', []).append(
                f'{item["project"]} [{item["framework"]}]: packaging wrote to folders the copied tree does not have '
                f'({", ".join(item["createdOutputDirs"])}; git-ignored or untracked, so not copied); they were created in the copy and '
                f'the full build ran again (exit {retry_code})')
            code = retry_code
            logtext = read_text(os.path.join(bdir, 'out', f'build-{item["index"]}-retry.log'))
        item['exit'] = int(code) if code is not None else None
        compile_code = compile_exits.get(str(item['index']))
        nopack_code = nopack_exits.get(str(item['index']))
        fallback_log = read_text(os.path.join(bdir, 'out', f'build-{item["index"]}-compile.log'))
        if code == '124':
            item['status'] = 'not run: the build hit its 1200 s limit (an analyzer or the build hung) and was stopped'
        elif code != '0' and nopack_code == '0':
            item['status'] = 'built'
            item['packOff'] = True
            item['skipped'] = f'MDK2 MdkPack (mdk pack, after the build) failed in the Linux container: {first_error(logtext)}'
            result.setdefault('notCovered', []).append(f'{item["project"]} [{item["framework"]}]: {item["skipped"]}; the project was built again '
                                                       'with MdkPack off (MdkBuildConfiguration), so the code was compiled and analysed but not packed')
        elif code != '0' and compile_code == '0':
            item['status'] = 'built'
            item['compileOnly'] = True
            item['skipped'] = f'packaging/copy targets (after the compile) did not run in the Linux container: {first_error(logtext)}'
            result.setdefault('notCovered', []).append(f'{item["project"]} [{item["framework"]}]: {item["skipped"]}; '
                                                       'the code was compiled and analysed with -t:Compile')
        elif code != '0' and (compile_code or nopack_code) and fallback_log.strip():
            # The packaging step failed and so did the build without it: report that build's own error.
            item['status'] = f'not run: build failed: {first_error(fallback_log)}'
        else:
            item['status'] = 'built' if code == '0' else f'not run: build failed: {first_error(logtext) if code is not None else (r.stderr or "")[-200:]}'
        item['compile'] = f'csproj:{item["project"]}/{item["framework"]}'
        compiles.append(evaluated_compile(item, os.path.join(bdir, 'out', f'eval-{item["index"]}.json')))
        result['builds'].append(item)
    compile_ids = {b['sarif']: b['compile'] for b in result['builds'] if b.get('compile')}
    for sarif in glob.glob(os.path.join(bdir, 'out', 'sarif', '*.sarif')):
        name = os.path.basename(sarif)[:-6]
        fw = name.rsplit('-', 1)[-1] if '-' in name else ''
        runtime = 'net48' if fw.startswith('net4') or not fw else ('net10' if fw.startswith('net1') else fw)
        try:
            data = json.load(open(sarif, encoding='utf-8-sig'))
        except (OSError, ValueError):
            continue
        for run_ in data.get('runs', []):
            rule_tags = {r.get('id'): (r.get('properties') or {}).get('tags') or [] for r in (run_.get('tool', {}).get('driver', {}).get('rules') or [])}
            for res in run_.get('results', []):
                loc = (res.get('locations') or [{}])[0].get('physicalLocation', {})
                uri = loc.get('artifactLocation', {}).get('uri', '')
                m = re.search(r'/work/tree/(.*)$', uri.replace('file://', ''))
                region = loc.get('region', {})
                level = res.get('level', 'warning')
                if level not in ('warning', 'error'):
                    continue
                d = {'id': res.get('ruleId'), 'severity': level, 'message': strip_lead((res.get('message') or {}).get('text', '')),
                     'file': m.group(1) if m else (uri or None), 'line': region.get('startLine', 0), 'column': region.get('startColumn', 0),
                     'endLine': region.get('endLine', region.get('startLine', 0)), 'tool': 'csproj-build', 'runtime': runtime,
                     'category': 'csproj-build', 'project': name, 'helpUri': None, 'lineText': None,
                     'tags': rule_tags.get(res.get('ruleId')) or None, 'compiles': [compile_ids[name]] if name in compile_ids else None}
                if res.get('suppressions'):
                    sup = res['suppressions'][0]
                    d['suppression'] = {'kind': sup.get('kind', 'inSource'), 'justification': sup.get('justification')}
                    suppressed.append(d)
                    continue
                diags.append(d)
    built = sum(1 for b in result['builds'] if b['status'] == 'built')
    result['status'] = f'ran: {built} of {len(result["builds"])} builds succeeded'
    result['compiles'] = compiles
    for b in result['builds']:
        if b['status'] != 'built':
            gap(f'csproj build {b["project"]} [{b["framework"]}] {b["status"][:220]}')
    return result, diags, suppressed


# ------------------------------------------------------------------------------------------------ script whitelist

WHITELIST_SRC = os.path.join(TOOLS, 'Whitelist')
EXTENDED_PATH = '\\\\?\\'  # Windows extended-length prefix: work trees can be deeper than MAX_PATH


def ensure_whitelist(refs):
    """Builds DeepWhitelist (our own net48 console tool, no deploy steps) against the resolved DedicatedServer64 copy.
    Returns (exe, Sandbox.Game.dll sha256, None) or (None, None, why)."""
    ds = refs.get('ds64')
    if not ds or not os.path.isfile(os.path.join(ds, 'Sandbox.Game.dll')):
        return None, None, 'DedicatedServer64 was not resolved'
    game_sha = sha256_file(os.path.join(ds, 'Sandbox.Game.dll'))
    h = hashlib.sha256(game_sha.encode())
    for f in sorted(glob.glob(os.path.join(WHITELIST_SRC, '*.cs')) + glob.glob(os.path.join(WHITELIST_SRC, '*.csproj'))):
        h.update(os.path.basename(f).encode())
        h.update(open(f, 'rb').read())
    out = os.path.join(CACHE, 'whitelist', h.hexdigest()[:16])
    exe = os.path.join(out, 'DeepWhitelist.exe')
    if not os.path.isfile(exe):
        log('building DeepWhitelist')
        r = run(['dotnet', 'build', os.path.join(WHITELIST_SRC, 'DeepWhitelist.csproj'), '-c', 'Release', '-m:4', f'-p:DS64={ds}', '-o', out,
                 '-nologo', '-v:quiet', f'-p:PathMap={WHITELIST_SRC}=/_/Whitelist'], timeout=1200)
        if r.returncode or not os.path.isfile(exe):
            return None, None, 'DeepWhitelist build failed: ' + first_error(r.stdout + r.stderr)
        # The binding redirects the dedicated server runs with (its exe config), plus long-path support for deep trees.
        cfg = read_text(os.path.join(ds, 'SpaceEngineersDedicated.exe.config')) or '<configuration>\n  <runtime>\n  </runtime>\n</configuration>\n'
        cfg = re.sub(r'<supportedRuntime [^>]*/>', '<supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" />', cfg)
        cfg = cfg.replace('<runtime>', '<runtime>\n    <AppContextSwitchOverrides value="Switch.System.IO.UseLegacyPathHandling=false;'
                                       'Switch.System.IO.BlockLongPaths=false" />', 1)
        with open(exe + '.config', 'w', encoding='utf-8') as f:
            f.write(cfg)
    return exe, game_sha, None


def mdk_ignored(project):
    """Matcher for the files MDK leaves out of the packed script: the ignores= globs of the project's *.mdk.ini."""
    pdir = os.path.dirname(project)
    pats = []
    for ini in glob.glob(os.path.join(pdir, '*.mdk.ini')):
        for m in re.finditer(r'^\s*ignores\s*=\s*(.+)$', read_text(ini), re.I | re.M):
            pats += [p.strip().replace('\\', '/') for p in m.group(1).split(',') if p.strip()]
    rx = []
    for p in pats:
        out, i = '', 0
        while i < len(p):
            if p.startswith('**/', i):
                out, i = out + '(?:.*/)?', i + 3
            elif p.startswith('**', i):
                out, i = out + '.*', i + 2
            elif p[i] == '*':
                out, i = out + '[^/]*', i + 1
            elif p[i] == '?':
                out, i = out + '[^/]', i + 1
            else:
                out, i = out + re.escape(p[i]), i + 1
        rx.append(re.compile(out + '$', re.I))
    return lambda f: any(r.match(rel(f, pdir)) for r in rx), pats


def whitelist_jobs(targets, tree):
    """One game compile per mod script folder (MyScriptManager.LoadScripts compiles each Data/Scripts/<folder> as one
    assembly) and one per PB script project (the files MDK packs into the script)."""
    jobs = []
    for t in targets:
        if t['kind'] == 'se-mod':
            groups = {}
            for f in t['sources']:
                after = rel(f, tree).split('Data/Scripts/', 1)[-1].split('/')
                groups.setdefault(after[0] if len(after) > 1 else '.', []).append(f)
            for g, files in groups.items():
                jobs.append({'id': f'{t["name"]}:{g}', 'target': 'mod', 'files': files, 'targetName': t['name'],
                             'project': rel(os.path.dirname(files[0]), tree)})
        elif t['kind'] == 'pb-script':
            ignored, pats = mdk_ignored(t['project'])
            files = [f for f in t['parsed']['sources'] if not ignored(f)]
            jobs.append({'id': t['name'], 'target': 'ingame', 'files': files, 'targetName': t['name'], 'project': rel(t['project'], tree),
                         'mdkIgnores': pats})
    return jobs


def whitelist_check(refs, targets, tree, work, head_diags):
    """Mods and PB scripts compiled and checked by the game's own script compiler and whitelist (tools/Whitelist), built
    from the DedicatedServer64 the run resolved. Returns (report section, diagnostics, compiles)."""
    jobs = whitelist_jobs(targets, tree)
    if not jobs:
        return None, [], []
    exe, game_sha, why = ensure_whitelist(refs)
    if not exe:
        gap(f"the game's script whitelist was not checked: {why}")
        return {'status': 'not run: ' + why}, [], []
    wdir = os.path.join(work, 'whitelist')
    os.makedirs(wdir, exist_ok=True)
    jobs_file, result = os.path.join(wdir, 'jobs.txt'), os.path.join(wdir, 'result.json')
    with open(jobs_file, 'w', encoding='utf-8') as f:
        for j in jobs:
            f.write(f'job\t{j["id"]}\t{j["target"]}\n')
            for p in j['files']:
                full = os.path.abspath(p)
                f.write(f'file\t{rel(p, tree)}\t{EXTENDED_PATH + full if os.name == "nt" else full}\n')
    dump = os.path.join(CACHE, 'refs', f'whitelist-{game_sha[:16]}')
    cmd = [exe, '--ds', refs.get('ds64'), '--jobs', jobs_file, '--out', result]
    if not all(os.path.isfile(os.path.join(dump, n)) for n in ('whitelist-mod.json', 'whitelist-pb.json', 'whitelist-info.json')):
        cmd += ['--dump', dump]
    log(f'script whitelist: {len(jobs)} game compiles ({", ".join(j["id"] for j in jobs)})')
    r = run(cmd, timeout=1800)
    with open(os.path.join(wdir, 'deepwhitelist.log'), 'w', encoding='utf-8') as f:
        f.write(r.stdout + '\n' + r.stderr)
    try:
        info = json.load(open(result, encoding='utf-8'))
    except (OSError, ValueError):
        why = f'DeepWhitelist exit {r.returncode}: {(r.stderr or r.stdout).strip()[-300:]}'
        gap(f"the game's script whitelist was not checked: {why}")
        return {'status': 'not run: ' + why}, [], []
    seen = {(d['id'], d.get('file'), d.get('line')) for d in head_diags}
    by_id = {j['id']: j for j in jobs}
    diags, compiles, summary = [], [], []
    for job in info.get('jobs', []):
        j = by_id[job['id']]
        cid = f'whitelist:{job["id"]}'
        compiles.append({'id': cid, 'project': j['project'], 'tfm': 'net48', 'profile': 'game-' + ('pb' if j['target'] == 'ingame' else 'mod'),
                         'nullable': 'disable', 'langVersion': '6', 'defines': info.get('symbols') or [], 'unsafe': False, 'fileCount': job['files']})
        counts = collections.Counter()
        for d in job['diagnostics']:
            counts[d['kind']] += 1
            if d['kind'] == 'analyzer-failure':
                gap(f"script whitelist {job['id']}: the game's checker did not complete: {d['message'][:200]}")
                continue
            if d['kind'] == 'compile' and (d['id'], d.get('file'), d.get('line')) in seen:
                continue  # DeepScan's compile of the same target already reports it
            text = ''
            if d.get('file') and d.get('line'):
                lines = read_text(os.path.join(tree, d['file'])).splitlines()
                text = lines[d['line'] - 1] if d['line'] <= len(lines) else ''
            msg = d['message'] if d['kind'] == 'whitelist' else f"the game's compile of {job['id']} fails: {d['message']}"
            diags.append({'id': d['id'], 'severity': 'error', 'message': msg, 'file': d.get('file'), 'line': d.get('line') or 0,
                          'column': d.get('column') or 0, 'endLine': d.get('endLine') or d.get('line') or 0, 'tool': 'whitelist',
                          'category': 'whitelist', 'runtime': 'net48', 'runtimes': ['net48'], 'seenOn': [cid], 'targets': [j['targetName']],
                          'compiles': [cid], 'helpUri': None, 'member': None, 'lineText': norm(text), 'status': None})
        summary.append({'id': job['id'], 'target': job['target'], 'files': job['files'], 'whitelist': counts['whitelist'],
                        'compileErrors': counts['compile'], 'checkerFailures': counts['analyzer-failure'], 'mdkIgnores': j.get('mdkIgnores')})
    section = {'status': 'ran', 'tool': exe, 'gameVersion': info.get('gameVersion'), 'sandboxGameSha256': info.get('sandboxGameSha256'),
               'checker': info.get('checker'), 'steps': info.get('steps'), 'symbols': info.get('symbols'),
               'references': info.get('references'), 'blacklistCalls': info.get('blacklistCalls'),
               'entries': {'all': info.get('whitelistEntries'), 'mod': info.get('modEntries'), 'pb': info.get('pbEntries'),
                           'pbBlacklist': info.get('blacklistEntries')},
               'modCompatibility': info.get('modCompatibility'), 'dump': dump, 'jobs': summary}
    log(f'  script whitelist: ' + ', '.join(f'{x["id"]} {x["whitelist"]} whitelist, {x["compileErrors"]} compile' for x in summary))
    return section, diags, compiles


SUPPRESSION_PATTERNS = [
    ('pragma', re.compile(r'^\s*#\s*pragma\s+warning\s+disable\b(.*)$')),
    ('SuppressMessage', re.compile(r'\[\s*(assembly\s*:\s*|module\s*:\s*)?(System\.Diagnostics\.CodeAnalysis\.)?(SuppressMessage|UnconditionalSuppressMessage)(Attribute)?\s*\(')),
    # Only the warnings form switches warnings off; a plain #nullable disable changes the annotation context.
    ('nullable-disable-warnings', re.compile(r'^\s*#\s*nullable\s+disable\s+warnings\b')),
    ('nullable-disable', re.compile(r'^\s*#\s*nullable\s+disable\b')),
    # Analyzers skip generated code, so marking a hand-written file as generated hides it from every rule.
    ('auto-generated', re.compile(r'^\s*//\s*<\s*auto-generated\b', re.I)),
    ('GeneratedCode', re.compile(r'\[\s*(System\.CodeDom\.Compiler\.)?GeneratedCode(Attribute)?\s*\(')),
]
# Each element pattern allows attributes (Condition="...") on the element; the value is the group named v.
_ELEMENT = r'<{0}(?:\s[^>]*)?>(?P<v>.*?)</{0}>'
PROJECT_SUPPRESSIONS = [
    ('NoWarn', re.compile(_ELEMENT.format('NoWarn'), re.S)),
    # NuGet's per-reference form: <PackageReference Include="X" NoWarn="NU1701" />
    ('NoWarn', re.compile(r'<\w+\s[^>]*\bNoWarn\s*=\s*"(?P<v>[^"]*)"', re.S)),
    ('WarningsNotAsErrors', re.compile(_ELEMENT.format('WarningsNotAsErrors'), re.S)),
    ('MSBuildWarningsAsMessages', re.compile(_ELEMENT.format('MSBuildWarningsAsMessages'), re.S)),
    ('MSBuildWarningsNotAsErrors', re.compile(_ELEMENT.format('MSBuildWarningsNotAsErrors'), re.S)),
    ('TreatWarningsAsErrors=false', re.compile(r'<TreatWarningsAsErrors(?:\s[^>]*)?>\s*(?P<v>false)\s*</TreatWarningsAsErrors>', re.I)),
    ('WarningLevel', re.compile(r'<WarningLevel(?:\s[^>]*)?>\s*(?P<v>[0-3])\s*</WarningLevel>')),
    ('RunAnalyzers=false', re.compile(r'<(?:RunAnalyzers|RunAnalyzersDuringBuild|EnableNETAnalyzers)(?:\s[^>]*)?>\s*(?P<v>false)\s*<', re.I)),
    ('CodeAnalysisRuleSet', re.compile(_ELEMENT.format('CodeAnalysisRuleSet'), re.S)),
]
# The same switches on a compiler or MSBuild command line, in response files and build scripts.
RSP_SUPPRESSION = re.compile(r'(?:^|\s)[-/](?:nowarn|warn:[0-3])\b|[-/]p(?:roperty)?:\s*(?:NoWarn|WarningLevel|MSBuildWarningsAsMessages)\s*=', re.I)
SCRIPT_SUPPRESSION = re.compile(r'[-/]p(?:roperty)?:\s*["\']?(?:NoWarn|WarningLevel|MSBuildWarningsAsMessages|MSBuildWarningsNotAsErrors|WarningsNotAsErrors)\s*=|(?:^|\s)[-/]nowarn[:\s]', re.I)
SCRIPT_EXTENSIONS = ('.ps1', '.psm1', '.sh', '.bash', '.bat', '.cmd', '.yml', '.yaml')


def find_suppressions(tree, compiled_files):
    """Every place the code under review turns a warning off. The gate counts each one as an issue: the tools' own
    deep.globalconfig is the only place a rule may be turned off."""
    out = []
    files = set(compiled_files)
    for f in walk_files(tree, '*.cs'):
        r = rel(f, tree)
        if r not in files:
            continue
        for i, line in enumerate(read_text(f).splitlines(), 1):
            for kind, rx in SUPPRESSION_PATTERNS:
                m = rx.search(line)
                if not m:
                    continue
                if kind == 'nullable-disable':
                    continue  # a nullable context change is not a warning suppression
                out.append({'kind': kind, 'file': r, 'line': i, 'text': line.strip()[:200]})
                break  # one kind per line: '#nullable disable warnings' also matches the plain form
    for f in walk_files(tree):
        name = os.path.basename(f)
        low = name.lower()
        r = rel(f, tree)
        if low.endswith(('.csproj', '.props', '.targets', '.proj')):
            text = read_text(f)
            for kind, rx in PROJECT_SUPPRESSIONS:
                for m in rx.finditer(text):
                    value = re.sub(r'\s+', ' ', m.group(0))
                    # <NoWarn>$(NoWarn)</NoWarn> only passes on what is already set; there is nothing new in it.
                    if kind in ('NoWarn', 'WarningsNotAsErrors', 'MSBuildWarningsAsMessages', 'MSBuildWarningsNotAsErrors') \
                            and not re.sub(r'\$\(\w+\)|[;,\s]', '', m.group('v')):
                        continue
                    line = text[:m.start()].count('\n') + 1
                    out.append({'kind': kind, 'file': r, 'line': line, 'text': value[:200]})
        elif low.endswith('.rsp'):
            for i, line in enumerate(read_text(f).splitlines(), 1):
                if RSP_SUPPRESSION.search(line):
                    out.append({'kind': 'rsp-nowarn', 'file': r, 'line': i, 'text': line.strip()[:200]})
        elif low.endswith(SCRIPT_EXTENSIONS):
            for i, line in enumerate(read_text(f).splitlines(), 1):
                if SCRIPT_SUPPRESSION.search(line):
                    out.append({'kind': 'script-nowarn', 'file': r, 'line': i, 'text': line.strip()[:200]})
        elif low in ('.editorconfig', '.globalconfig') or low.endswith('.globalconfig') or low.endswith('.ruleset'):
            for i, line in enumerate(read_text(f).splitlines(), 1):
                if re.match(r'^\s*dotnet_(analyzer_)?diagnostic\..*severity\s*=\s*(none|silent|suggestion|default)\b', line, re.I) \
                        or re.match(r'^\s*generated_code\s*=\s*true\b', line, re.I) \
                        or (low.endswith('.ruleset') and re.search(r'Action="(None|Hidden|Info)"', line)):
                    out.append({'kind': 'analyzer-config', 'file': r, 'line': i, 'text': line.strip()[:200]})
    return out


def first_error(text):
    # "error CS1002: ..." and code-less "error : ..." (an <Error> task in a target, such as MDK's or a template's).
    m = re.search(r'error(?: \w+\d*)? ?: .*', text)
    return (m.group(0) if m else text.strip()[-200:])[:240]


def compare_builds(deep_diags, build_diags):
    """Compiler (CS) diagnostics the IDE-style build and the loader-faithful compile disagree on."""
    def key(d):
        return (d['id'], d.get('file'), d.get('line'))
    deep_cs = {key(d): d for d in deep_diags if d['id'].startswith('CS') and d.get('status') != 'FIXED'}
    build_cs = {key(d): d for d in build_diags if (d.get('id') or '').startswith('CS')}
    only_deep = [{'id': d['id'], 'file': d.get('file'), 'line': d.get('line'), 'seenOn': d.get('seenOn'), 'message': d['message'][:200]} for k, d in deep_cs.items() if k not in build_cs]
    only_build = [{'id': d['id'], 'file': d.get('file'), 'line': d.get('line'), 'runtime': d.get('runtime'), 'message': d['message'][:200]} for k, d in build_cs.items() if k not in deep_cs]
    ids_deep = collections.Counter(d['id'] for d in deep_diags if d.get('status') != 'FIXED' and not d['id'].startswith('CS'))
    ids_build = collections.Counter(d['id'] for d in build_diags if not (d.get('id') or '').startswith('CS'))
    common = set(ids_deep) & set(ids_build)
    return {'onlyLoaderCompile': only_deep[:300], 'onlyCsprojBuild': only_build[:300],
            'analyzerRuleCounts': {i: {'deepscan': ids_deep[i], 'csprojBuild': ids_build[i]} for i in sorted(common) if ids_deep[i] != ids_build[i]},
            'note': 'onlyLoaderCompile: the hub compile shows it and the IDE build hides it; onlyCsprojBuild: the reverse. Analyzer counts differ also because the csproj build adds AnalysisLevel latest-all.'}


# ------------------------------------------------------------------------------------------------ optional tools

def optional_tools(args, refs, tree, work, emitted):
    res = {}
    images = docker('image', 'ls', '--format', '{{.Repository}}:{{.Tag}}', timeout=60).stdout.split()
    infer = next((i for i in images if i.startswith('mcr.microsoft.com/infersharp')), None)
    if infer and emitted:
        res['infersharp'] = []
        for d in emitted:
            out = os.path.join(work, 'infersharp', os.path.basename(d))
            os.makedirs(out, exist_ok=True)
            r = docker('run', '--rm', f'--cpuset-cpus={cpuset(args.affinity_mask)}', '--network', 'none', '-v', f'{d}:/app:ro', '-v', f'{out}:/out',
                       infer, '/bin/bash', '-c', 'cd /infersharp && ./run_infersharp.sh /app /out', timeout=3600)
            res['infersharp'].append({'input': d, 'exit': r.returncode, 'report': os.path.join(out, 'infer-out', 'report.txt')})
    else:
        res['infersharp'] = 'not run: not installed (no local mcr.microsoft.com/infersharp image)' if not infer else 'not run: nothing emitted'
    codeql = shutil.which('codeql')
    if codeql:
        db = os.path.join(work, 'codeql-db')
        r = run([codeql, 'database', 'create', db, '--language=csharp', '--build-mode=none', f'--source-root={tree}', '--overwrite'], timeout=3600)
        sarif = os.path.join(work, 'codeql.sarif')
        if r.returncode == 0:
            r = run([codeql, 'database', 'analyze', db, 'codeql/csharp-queries:codeql-suites/csharp-security-and-quality.qls',
                     '--format=sarifv2.1.0', f'--output={sarif}'], timeout=3600)
        res['codeql'] = {'exit': r.returncode, 'sarif': sarif if os.path.isfile(sarif) else None}
    else:
        res['codeql'] = 'not run: not installed (codeql not on PATH)'
    mono = next((i for i in images if i.split(':')[0] in ('mono', 'library/mono')), None)
    res['mono'] = f'available: {mono} (runtime-matrix/run_matrix.py uses it)' if mono else 'not run: not installed (no local mono image)'
    trivy = shutil.which('trivy')
    trivy_img = next((i for i in images if i.startswith('aquasec/trivy')), None)
    if trivy or trivy_img:
        out = os.path.join(work, 'trivy.json')
        if trivy:
            r = run([trivy, 'fs', '--scanners', 'vuln', '--offline-scan', '--format', 'json', '--output', out, tree], timeout=1800)
        else:
            r = docker('run', '--rm', f'--cpuset-cpus={cpuset(args.affinity_mask)}', '-v', f'{tree}:/src:ro', '-v', f'{work}:/out', trivy_img,
                       'fs', '--scanners', 'vuln', '--offline-scan', '--format', 'json', '--output', '/out/trivy.json', '/src', timeout=1800)
        res['trivy'] = {'exit': r.returncode, 'report': out if os.path.isfile(out) else None, 'stderr': r.stderr[-300:]}
    else:
        res['trivy'] = 'not run: not installed (no trivy on PATH, no local aquasec/trivy image)'
    dc = shutil.which('dependency-check') or shutil.which('dependency-check.bat') or shutil.which('dependency-check.sh')
    if dc:
        out = os.path.join(work, 'dependency-check')
        r = run([dc, '--scan', tree, '--format', 'JSON', '--out', out, '--noupdate'], timeout=3600)
        res['dependencyCheck'] = {'exit': r.returncode, 'report': out}
    else:
        res['dependencyCheck'] = 'not run: not installed (dependency-check not on PATH)'
    committed = [rel(f, tree) for f in walk_files(tree) if f.lower().endswith(('.dll', '.exe', '.so', '.dylib'))]
    res['committedBinaries'] = committed
    return res


# ------------------------------------------------------------------------------------------------ reports

def sarif_report(diags, root_uri, version_info):
    runs = {}
    for d in diags:
        for rt in d.get('runtimes') or [d.get('runtime') or 'any']:
            key = (d.get('tool') or 'unknown', rt)
            r = runs.setdefault(key, {'rules': {}, 'results': []})
            if d['id'] not in r['rules']:
                r['rules'][d['id']] = {'id': d['id'], **({'helpUri': d['helpUri']} if d.get('helpUri') else {}),
                                       'properties': {'category': d.get('category')}}
            idx = list(r['rules']).index(d['id'])
            level = {'error': 'error', 'warning': 'warning'}.get(d.get('severity'), 'note')
            res = {'ruleId': d['id'], 'ruleIndex': idx, 'level': level, 'message': {'text': d.get('message') or ''},
                   'baselineState': {'NEW': 'new', 'EXISTING': 'unchanged', 'FIXED': 'absent'}.get(d.get('status'), 'new'),
                   'properties': {'scope': d.get('scope'), 'status': d.get('status'), 'runtimes': d.get('runtimes'), 'seenOn': d.get('seenOn'),
                                  'runtimeSpecific': d.get('runtimeSpecific', False), 'member': d.get('member'), 'tool': d.get('tool')}}
            if d.get('file'):
                res['locations'] = [{'physicalLocation': {'artifactLocation': {'uri': d['file'], 'uriBaseId': 'SRCROOT'},
                                                          'region': {'startLine': max(d.get('line') or 1, 1), 'startColumn': max(d.get('column') or 1, 1),
                                                                     'endLine': max(d.get('endLine') or d.get('line') or 1, 1)}}}]
            r['results'].append(res)
    out = {'$schema': 'https://json.schemastore.org/sarif-2.1.0.json', 'version': '2.1.0', 'runs': []}
    for (tool, rt), r in sorted(runs.items()):
        out['runs'].append({
            'tool': {'driver': {'name': tool, 'informationUri': 'https://github.com/dotnet/roslyn', 'rules': list(r['rules'].values()),
                                'properties': {'runtime': rt}}},
            'originalUriBaseIds': {'SRCROOT': {'uri': root_uri}},
            'automationDetails': {'id': f'deep-tools/{tool}/{rt}'},
            'properties': {'runtime': rt, **version_info},
            'results': r['results'],
        })
    return out


def md_report(rep):
    L = []
    L.append(f'# Deep review tools report')
    L.append('')
    L.append(f'- Repo: `{rep["repo"]}`')
    L.append(f'- Head: `{rep["head"]}`' + (f'  Base: `{rep["base"]}`' if rep.get('base') else '  (no base: no diff scope)'))
    L.append(f'- Project kind: {rep.get("projectKind")} ({", ".join(rep.get("projectKinds") or [])})')
    L.append(f'- Targets: ' + (', '.join(f'{t["name"]} ({"/".join(t["runtimes"])})' for t in rep['targets']) or 'none (csproj build only)'))
    if rep.get('languages'):
        L.append('- Languages: ' + ', '.join(f'{k} {v["files"]} ({"checked by " + "/".join(v["by"]) if v["checked"] else "NOT CHECKED"})' for k, v in rep['languages'].items()))
    L.append(f'- Analyzers loaded: {len(rep["analyzers"]["loaded"])} assemblies; failed: {len(rep["analyzers"]["failed"])}; dropped packs: {len(rep["analyzers"].get("dropped", {}))}')
    L.append(f'- Generated: {rep["generated"]}  exit code: {rep["exitCode"]}')
    if rep.get('dropped'):
        L.append(f'- Dropped as out of scope: {rep["dropped"].get("ClrHeapAllocationAnalyzer", 0)} ClrHeapAllocationAnalyzer results outside per-tick code, '
                 f'{rep["dropped"].get("net10OnlyApiSuggestions", 0)} net10-only API suggestions on code that also ships on net48, '
                 f'{rep["dropped"].get("compilerPolyfills", 0)} MA0182 results on compiler polyfills'
                 + (f' ({", ".join(sorted({p["type"].rsplit(".", 1)[-1] for p in rep.get("compilerPolyfills", [])}))})' if rep.get('compilerPolyfills') else ''))
    L.append('')
    g = rep.get('gate') or {}
    if g:
        L.append(f'**Gate: {g["verdict"]}** ({g["counts"]["issues"]} issues, {g["counts"]["gaps"]} gaps' + (', gate mode' if rep.get('kind') == 'gate' else ', report mode') + ')')
        L.append('')
        for k, v in g['counts']['byCategory'].items():
            L.append(f'- {k}: {v}')
        L.append('')
    L.append(f'- Record: nonce `{rep.get("nonce")}`, toolsVersion `{(rep.get("toolsVersion") or "")[:16]}`, started {rep.get("startedUtc")}, finished {rep.get("finishedUtc")}')
    L.append('')
    sup = rep.get('suppressions') or {}
    if sup.get('sites'):
        L.append(f'## Suppressions in the code under review ({len(sup["sites"])}): each is a gate issue')
        L.append('')
        for x in sup['sites'][:80]:
            L.append(f'- {x["file"]}:{x["line"]} [{x["kind"]}] `{x["text"][:150]}`')
        L.append('')
    for nc in rep.get('notCovered') or []:
        L.append(f'- Not covered: {nc}')
    L.append(f'## Gaps ({len(rep["gaps"])})')
    L.append('')
    L += [f'- {g}' for g in rep['gaps']] or ['- none']
    L.append('')
    L.append('## Not run (optional tools)')
    L.append('')
    for k, v in rep['optional'].items():
        if isinstance(v, str):
            L.append(f'- {k}: {v}')
    L.append('')
    counts = rep['counts']
    L.append('## Counts')
    L.append('')
    L.append('| scope | NEW | EXISTING | FIXED | total |')
    L.append('|---|---|---|---|---|')
    for s in SCOPES:
        c = counts['byScopeStatus'].get(s, {})
        if c:
            L.append(f'| {s} | {c.get("NEW", 0)} | {c.get("EXISTING", 0)} | {c.get("FIXED", 0)} | {sum(c.values())} |')
    L.append('')
    rs = [d for d in rep['diagnostics'] if d.get('runtimeSpecific')]
    if rs:
        L.append(f'## Runtime-specific diagnostics ({len(rs)})')
        L.append('')
        for d in rs[:60]:
            L.append(f'- `{d["id"]}` {d.get("file")}:{d.get("line")} only on {", ".join(d["onlyOn"])}: {d["message"][:160]}')
        L.append('')
    order = {s: i for i, s in enumerate(SCOPES)}
    status_order = {'NEW': 0, 'EXISTING': 1, 'FIXED': 2, None: 1}
    for scope in SCOPES:
        items = [d for d in rep['diagnostics'] if d.get('scope') == scope]
        if not items:
            continue
        L.append(f'## {scope} ({len(items)})')
        L.append('')
        by_rule = collections.defaultdict(list)
        for d in items:
            by_rule[d['id']].append(d)
        rules = sorted(by_rule, key=lambda r: (min(status_order.get(d.get('status'), 1) for d in by_rule[r]), -len(by_rule[r]), r))
        limit = None if scope in ('in-diff-line', 'in-changed-member') else 8
        for r in rules:
            ds = sorted(by_rule[r], key=lambda d: (status_order.get(d.get('status'), 1), d.get('file') or '', d.get('line') or 0))
            first = ds[0]
            L.append(f'### {r} ({len(ds)}) [{first.get("tool")}]' + (f' {first["helpUri"]}' if first.get('helpUri') else ''))
            L.append('')
            for d in ds[:limit] if limit else ds:
                where = f'{d.get("file")}:{d.get("line")}' if d.get('file') else '(no location)'
                tags = [d.get('status') or '', '/'.join(d.get('runtimes') or [])]
                if d.get('runtimeSpecific'):
                    tags.append('only ' + ','.join(d['onlyOn']))
                if d.get('suppressed'):
                    tags.append('suppressed in source')
                L.append(f'- {where} [{" ".join(t for t in tags if t)}] {d["message"][:220]}')
            if limit and len(ds) > limit:
                L.append(f'- ... {len(ds) - limit} more in tools-report.json')
            L.append('')
    if rep.get('changedMembers'):
        L.append(f'## Changed members ({len(rep["changedMembers"])})')
        L.append('')
        for m in rep['changedMembers'][:200]:
            ent = ', '.join(m.get('entryPointsReached') or []) or 'none found'
            cov = f' coverage: {m["coverage"]}' if m.get('coverage') else ''
            L.append(f'- `{m["id"]}` {m.get("file")}:{m.get("start")}-{m.get("end")} entry points reached: {ent}{cov}')
        L.append('')
        L.append(f'Affected members: {len(rep["affectedMembers"])} (see tools-report.json).')
        L.append('')
    rel_ = rep['release']
    L.append('## Release consistency')
    L.append('')
    L.append(f'- Versions consistent: {rel_["consistent"]} ({", ".join(rel_["distinctVersions"])})')
    for v in rel_['versions']:
        L.append(f'  - {v["source"]}: {v["value"]}')
    for m in rel_['manifests']:
        for p in m['problems']:
            L.append(f'- {m["manifest"]}: {p}')
    L.append(f'- TODO/FIXME/HACK in compiled sources: {len(rel_["todos"])}')
    for t in rel_['todos'][:30]:
        L.append(f'  - {t["file"]}:{t["line"]} {t["text"][:120]}')
    L.append('')
    t = rep['tests']
    L.append('## Tests')
    L.append('')
    L.append(f'- Status: {t.get("status")}; test projects: {", ".join(t.get("found") or []) or "none"}; net48 targeted: {t.get("net48Targeted", False)}')
    for p in t.get('projects', []):
        host = ' (on this PC' + (', ABORTED' if p.get('aborted') else '') + ')' if p.get('host') else ''
        L.append(f'  - {p["project"]} [{p["framework"]}]{host} {p.get("status")} passed {p.get("passed", "-")} failed {p.get("failed", "-")}')
    if t.get('host'):
        h = t['host']
        L.append(f'- Stray-write check ({", ".join(h.get("strayWriteRoots") or []) or "no roots present"}): {h.get("strayWriteCount", 0)} files changed during the host runs')
        L += [f'  - {x}' for x in (h.get('strayWrites') or [])[:20]]
    L.append('')
    w = rep.get('whitelist')
    if w:
        L.append('## Script whitelist (the game\'s own checker)')
        L.append('')
        L.append(f'- Status: {w.get("status")}; game {w.get("gameVersion")}; checker {w.get("checker")}; entries {w.get("entries")}')
        for j in w.get('jobs') or []:
            L.append(f'  - {j["id"]} ({j["target"]}): {j["files"]} files, {j["whitelist"]} whitelist hits, {j["compileErrors"]} game compile errors')
        L.append('')
    if rep.get('compiles'):
        L.append(f'## Compiles ({len(rep["compiles"])})')
        L.append('')
        L.append('| id | profile | nullable | langVersion | unsafe | files | defines |')
        L.append('|---|---|---|---|---|---|---|')
        for c in rep['compiles']:
            L.append(f'| {c["id"]} | {c["profile"]} | {c.get("nullable")} | {c.get("langVersion")} | {c.get("unsafe")} | {c.get("fileCount")} | '
                     f'{" ".join(c.get("defines") or [])[:120]} |')
        L.append('')
    v = rep['vulnerabilities']
    L.append('## Dependency vulnerabilities')
    L.append('')
    L.append(f'- Status: {v.get("status")}')
    for p in v.get('projects', []):
        L.append(f'  - {p["project"]}: {p["status"]}, {len(p["vulnerable"])} vulnerable packages')
        for x in p['vulnerable']:
            L.append(f'    - {x["package"]} {x["version"]} ({x["severity"]}) {x["advisory"]}')
    L.append('')
    cb = rep.get('csprojBuild', {})
    L.append('## csproj-build cross-check')
    L.append('')
    L.append(f'- Status: {cb.get("status")}')
    for b in cb.get('builds', []):
        L.append(f'  - {b["project"]} [{b["framework"]}] {b["status"][:200]}')
    cmp_ = rep.get('buildComparison') or {}
    if cmp_:
        L.append(f'- Compiler diagnostics only in the loader-faithful compile (IDE build hides them): {len(cmp_["onlyLoaderCompile"])}')
        for d in cmp_['onlyLoaderCompile'][:25]:
            L.append(f'  - `{d["id"]}` {d["file"]}:{d["line"]} {d["message"][:140]}')
        L.append(f'- Compiler diagnostics only in the csproj build: {len(cmp_["onlyCsprojBuild"])}')
        for d in cmp_['onlyCsprojBuild'][:25]:
            L.append(f'  - `{d["id"]}` {d["file"]}:{d["line"]} [{d.get("runtime")}] {d["message"][:140]}')
    L.append('')
    L.append('## References')
    L.append('')
    for k, v in rep['refs'].items():
        L.append(f'- {k}: `{v["path"]}` ({v["source"]})')
    L.append('')
    return '\n'.join(L) + '\n'


# ------------------------------------------------------------------------------------------------ project kinds and languages

GAME_REF = re.compile(r'(Sandbox\.Game|Sandbox\.Common|VRage\.Game|SpaceEngineers\.Game|VRage\.Library|0Harmony|Torch\.API|PluginSdk)\b')
# MDK programmable block projects: MDK2's PB packages, or MDK1 (PB only). Mal.Mdk2.ModAnalyzers/ModPackager are mod projects
# (their scripts are under Data/Scripts and are checked as a mod); a *.mdk.ini says which with type=programmableblock|mod.
MDK_PB_REF = re.compile(r'Mal\.Mdk2\.Pb\w*|Mdk\.PbPackager|MDK[/\\]MDK\.options', re.I)
MDK_INI_TYPE = re.compile(r'^\s*type\s*=\s*(\w+)', re.I | re.M)

# Source languages by extension. Markup and data (XML, JSON, HTML, CSS, XAML, YAML, Markdown) are not languages here.
LANGUAGES = {
    '.cs': 'C#', '.razor': 'Razor', '.cshtml': 'Razor', '.vb': 'Visual Basic', '.fs': 'F#', '.fsx': 'F#',
    '.py': 'Python', '.ps1': 'PowerShell', '.psm1': 'PowerShell', '.psd1': 'PowerShell',
    '.js': 'JavaScript', '.mjs': 'JavaScript', '.cjs': 'JavaScript', '.jsx': 'JavaScript', '.ts': 'TypeScript', '.tsx': 'TypeScript',
    '.sh': 'Shell', '.bash': 'Shell', '.bat': 'Batch', '.cmd': 'Batch', '.c': 'C', '.h': 'C/C++ header', '.cpp': 'C++', '.cc': 'C++',
    '.hpp': 'C++', '.go': 'Go', '.rs': 'Rust', '.java': 'Java', '.kt': 'Kotlin', '.lua': 'Lua', '.sql': 'SQL', '.rb': 'Ruby',
    '.php': 'PHP', '.swift': 'Swift', '.hlsl': 'HLSL', '.fx': 'HLSL',
}
# Folders whose contents are vendored or generated, not the project's own code.
VENDORED = {'node_modules', 'vendor', 'third_party', 'thirdparty'}


def language_inventory(tree):
    counts, files = collections.Counter(), collections.defaultdict(list)
    for f in walk_files(tree):
        r = rel(f, tree)
        parts = r.lower().split('/')
        if any(p in VENDORED for p in parts[:-1]) or '/wwwroot/lib/' in '/' + r.lower():
            continue
        lang = LANGUAGES.get(os.path.splitext(f)[1].lower())
        if lang:
            counts[lang] += 1
            files[lang].append(r)
    return counts, files


def detect_kinds(tree, targets, projects):
    """What the repository is: plugin kinds from manifests, SE mods (Data/Scripts), PB scripts (MDK), other .NET, non-.NET."""
    kinds, mods, pbs = [], [], []
    for t in targets:
        k = {'magnetar': 'magnetar-plugin', 'pulsar': 'pulsar-plugin', 'torch': 'torch-plugin', 'quasar': 'quasar-plugin'}.get(t['kind'])
        if k and k not in kinds:
            kinds.append(k)
    for d, dirs, files in os.walk(tree):
        dirs[:] = [x for x in dirs if x not in SKIP_DIRS]
        if os.path.basename(d) == 'Scripts' and os.path.basename(os.path.dirname(d)) == 'Data' \
                and any(True for _ in walk_files(d, '*.cs')):
            mods.append(os.path.dirname(os.path.dirname(d)))
    for p in projects:
        types = {m.lower() for ini in glob.glob(os.path.join(os.path.dirname(p), '*.mdk.ini')) for m in MDK_INI_TYPE.findall(read_text(ini))}
        if 'programmableblock' in types or (not types and MDK_PB_REF.search(read_text(p))):
            pbs.append(p)
    if mods:
        kinds.append('se-mod')
    if pbs:
        kinds.append('pb-script')
    covered = {os.path.normcase(t['project']) for t in targets if t.get('project')}
    others = [p for p in projects if os.path.normcase(p) not in covered and p not in pbs]
    # A project with game references but no hub manifest is still 'dotnet' (schema 1 kinds); references get resolved.
    if others and 'dotnet' not in kinds and not any(k.endswith('-plugin') for k in kinds):
        kinds.append('dotnet')
    elif projects and not kinds:
        kinds.append('dotnet')
    return kinds, mods, pbs


# ------------------------------------------------------------------------------------------------ checkers for other languages
# Hooks only: each runs when its tool is already installed (on PATH, or a module for PowerShell). Nothing is downloaded.
# A language counts as checked only when one of its checkers ran.

def _which(*names):
    for n in names:
        p = shutil.which(n)
        if p:
            return p
    return None


ANALYZERS_DIR = os.path.join(TOOLS, 'analyzers')
RUFF_CONFIG = os.path.join(ANALYZERS_DIR, 'deep-ruff.toml')
MYPY_CONFIG = os.path.join(ANALYZERS_DIR, 'deep-mypy.ini')
BANDIT_CONFIG = os.path.join(ANALYZERS_DIR, 'deep-bandit.yaml')
PSSA_CONFIG = os.path.join(ANALYZERS_DIR, 'deep-psscriptanalyzer.psd1')
ESLINT_CONFIG = os.path.join(ANALYZERS_DIR, 'deep-eslint.config.mjs')
GITLEAKS_CONFIG = os.path.join(ANALYZERS_DIR, 'deep-gitleaks.toml')
GITLEAKS_IGNORE = os.path.join(ANALYZERS_DIR, 'empty.gitleaksignore')
GITLEAKS = os.path.join(TOOLS, 'bin', 'gitleaks', 'gitleaks.exe' if os.name == 'nt' else 'gitleaks')
ESLINT_JS = os.path.join(TOOLS, 'linters', 'js', 'node_modules', 'eslint', 'bin', 'eslint.js')
PY_ENV = {'PYTHONDONTWRITEBYTECODE': '1', 'PYTHONUTF8': '1', 'RUFF_NO_CACHE': 'true'}


def _diag(tool, rid, sev, msg, tree, path, line, col=0, runtime='python'):
    path = path or ''
    r = rel(path, tree) if os.path.isabs(path) else path.replace('\\', '/')
    return {'id': rid, 'severity': 'error' if sev == 'error' else 'warning', 'message': msg, 'file': r or None, 'line': line or 0,
            'column': col or 0, 'tool': tool, 'runtime': runtime, 'category': 'language-checker'}


def check_python(tree, files, work):
    """ruff (ALL), mypy --strict, bandit (all tests), and pytest when the repo has tests. Central configs only."""
    results, diags, gaps_ = [], [], []
    ruff = _which('ruff')
    if ruff:
        r = run([ruff, 'check', '--config', RUFF_CONFIG, '--no-cache', '--ignore-noqa', '--output-format', 'json', '--exit-zero', '.'],
                cwd=tree, env=PY_ENV, timeout=1800)
        try:
            for x in json.loads(r.stdout or '[]'):
                loc = x.get('location') or {}
                diags.append(_diag('ruff', x.get('code') or 'ruff', 'warning', x.get('message', ''), tree,
                                   os.path.join(tree, x['filename']) if not os.path.isabs(x['filename']) else x['filename'], loc.get('row'), loc.get('column')))
            results.append({'tool': 'ruff', 'status': 'ran'})
        except (ValueError, KeyError):
            results.append({'tool': 'ruff', 'status': f'failed: {(r.stderr or r.stdout)[-300:]}'})
    else:
        results.append({'tool': 'ruff', 'status': 'not run: not installed'})
    mypy = _which('mypy')
    if mypy:
        cache = os.path.join(work, 'mypy-cache')
        r = run([mypy, '--config-file', MYPY_CONFIG, '--strict', '--cache-dir', cache, '--no-color-output', '--show-column-numbers',
                 '--explicit-package-bases', '--no-error-summary', *(files if len(files) <= 300 else ['.'])], cwd=tree, env=PY_ENV, timeout=3600)
        missing = collections.Counter()
        for m in re.finditer(r'^(.+?):(\d+):(?:(\d+):)? (error|note|warning): (.+?)(?:  \[([\w-]+)\])?$', r.stdout, re.M):
            code = m.group(6) or 'mypy'
            if m.group(4) == 'note':
                continue
            if code in ('import-untyped', 'import-not-found'):
                mod = re.search(r'"([\w.]+)"', m.group(5))
                missing[mod.group(1) if mod else m.group(5)[:60]] += 1
                continue
            diags.append(_diag('mypy', code, m.group(4), m.group(5), tree, m.group(1), int(m.group(2)), int(m.group(3) or 0)))
        fatal = re.search(r'^(.+?): error: (Duplicate module named.+|.*is not a valid Python package name.*)$', r.stdout, re.M)
        if fatal:
            results.append({'tool': 'mypy', 'status': f'failed: {fatal.group(2)[:200]}'})
        else:
            results.append({'tool': 'mypy', 'status': 'ran'})
        if missing:
            gaps_.append(f'mypy: third-party stubs or packages missing for {len(missing)} modules ({", ".join(sorted(missing)[:12])}): those imports are not type-checked')
    else:
        results.append({'tool': 'mypy', 'status': 'not run: not installed'})
    bandit = _which('bandit')
    if bandit:
        r = run([bandit, '-r', '.', '-c', BANDIT_CONFIG, '-f', 'json', '-q', '--ignore-nosec'], cwd=tree, env=PY_ENV, timeout=1800)
        try:
            data = json.loads(r.stdout or '{}')
            for x in data.get('results', []):
                text = x.get('issue_text', '')
                if x.get('test_id') in ('B105', 'B106', 'B107'):
                    # Hardcoded-password findings quote the value: never copy a possible secret into a report.
                    text = re.sub(r"'[^']*'", "'<redacted>'", text)
                diags.append(_diag('bandit', x.get('test_id', 'bandit'), 'warning',
                                   f'{text} (severity {x.get("issue_severity")}, confidence {x.get("issue_confidence")})',
                                   tree, os.path.join(tree, x['filename']) if not os.path.isabs(x['filename']) else x['filename'], x.get('line_number')))
            for e in data.get('errors', []):
                gaps_.append(f'bandit could not scan {e.get("filename")}: {str(e.get("reason"))[:120]}')
            results.append({'tool': 'bandit', 'status': 'ran'})
        except (ValueError, KeyError):
            results.append({'tool': 'bandit', 'status': f'failed: {(r.stderr or r.stdout)[-300:]}'})
    else:
        results.append({'tool': 'bandit', 'status': 'not run: not installed'})
    test_files = [f for f in files if re.search(r'(^|/)(test_[^/]*|[^/]*_test|conftest)\.py$', f)]
    pytest = _which('pytest')
    if not test_files:
        gaps_.append('Python code has no tests (no test_*.py, *_test.py or conftest.py): nothing tests it')
        results.append({'tool': 'pytest', 'status': 'no tests'})
    elif not pytest:
        results.append({'tool': 'pytest', 'status': 'not run: not installed'})
        gaps_.append('pytest not installed: Python tests not run')
    else:
        junit = os.path.join(work, 'pytest-junit.xml')
        r = run([pytest, '-p', 'no:cacheprovider', '-q', f'--junitxml={junit}', '--rootdir', tree, '-o', 'addopts=', '.'],
                cwd=tree, env=PY_ENV, timeout=1800)
        res = {'tool': 'pytest', 'status': 'ran', 'exit': r.returncode}
        if os.path.isfile(junit):
            root = ET.parse(junit).getroot()
            suites = [root] if local(root.tag) == 'testsuite' else [s for s in root if local(s.tag) == 'testsuite']
            for k in ('tests', 'failures', 'errors', 'skipped'):
                res[k] = sum(int(s.get(k, 0)) for s in suites)
            for case in root.iter():
                if local(case.tag) != 'testcase':
                    continue
                for bad in case:
                    if local(bad.tag) in ('failure', 'error'):
                        diags.append(_diag('pytest', 'test-' + local(bad.tag), 'error',
                                           f'{case.get("classname")}.{case.get("name")}: {(bad.get("message") or "")[:200]}',
                                           tree, (case.get('file') or '').replace('\\', '/'), int(case.get('line') or 0)))
            if r.returncode == 124:
                gaps_.append('pytest timed out after 1800 s')
        else:
            res['status'] = f'failed: {(r.stdout + r.stderr)[-300:]}'
            gaps_.append(f'pytest produced no results: {(r.stdout + r.stderr)[-160:]}')
        results.append(res)
    return results, diags, gaps_


PSSA_RUNNER = os.path.join(TOOLS, 'linters', 'pssa-run.ps1')


def check_powershell(tree, files, work):
    """PSScriptAnalyzer, every rule at every severity from the central settings, one rule per call (linters/pssa-run.ps1)."""
    pwsh = _which('pwsh', 'powershell')
    if not pwsh:
        return [{'tool': 'PSScriptAnalyzer', 'status': 'not run: no PowerShell'}], [], []
    os.makedirs(work, exist_ok=True)
    out = os.path.join(work, 'psscriptanalyzer.json')
    lst = os.path.join(work, 'ps-files.txt')
    with open(lst, 'w', encoding='utf-8') as f:
        f.write('\n'.join(os.path.join(tree, x) for x in files) + '\n')
    r = run([pwsh, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', PSSA_RUNNER, '-ListFile', lst,
             '-SettingsFile', PSSA_CONFIG, '-OutFile', out], timeout=3600)
    if r.returncode or not os.path.isfile(out):
        text = (r.stderr or r.stdout)[-400:]
        status = 'not run: not installed (PowerShell module PSScriptAnalyzer)' if 'PSScriptAnalyzer' in text and 'not loaded' in text else f'failed: {text}'
        return [{'tool': 'PSScriptAnalyzer', 'status': status}], [], []
    data = json.load(open(out, encoding='utf-8-sig'))
    diags, gaps_ = [], []
    for x in data.get('Results') or []:
        sev = 'error' if x.get('Severity') in ('Error', 'ParseError') else 'warning'
        diags.append(_diag('PSScriptAnalyzer', x.get('RuleName'), sev, f'[{x.get("Severity")}] {x.get("Message", "")}', tree,
                           x.get('ScriptPath') or '', x.get('Line'), x.get('Column'), 'powershell'))
    for fl in data.get('Failures') or []:
        gaps_.append(f'PSScriptAnalyzer rule {fl.get("Rule")} failed on {rel(fl.get("File") or tree, tree)}: {str(fl.get("Error"))[:160]}')
    return [{'tool': 'PSScriptAnalyzer', 'status': 'ran', 'version': data.get('Version'), 'rules': data.get('Rules')}], diags, gaps_


def check_js(tree, files, work):
    node = _which('node')
    if not node or not os.path.isfile(ESLINT_JS):
        return [{'tool': 'eslint', 'status': 'not run: not installed (tools/linters/js)'}], [], []
    # Plain .js files are classic scripts unless they use import/export: those are linted as ES modules.
    module_js = re.compile(r'^\s*(import\s[^(]|import\s*\{|export\s)', re.M)
    modules = [f for f in files if f.lower().endswith(('.js', '.jsx')) and module_js.search(read_text(os.path.join(tree, f)))]
    scripts = [f for f in files if f not in modules]
    diags, gaps_ = [], []
    for group, source_type in ((scripts, 'script'), (modules, 'module')):
        if not group:
            continue
        r = run([node, ESLINT_JS, '--config', ESLINT_CONFIG, '--format', 'json', '--no-warn-ignored', *group], cwd=tree,
                env={'DEEP_TS_ROOT': tree, 'DEEP_JS_SOURCE_TYPE': source_type, 'NODE_OPTIONS': ''}, timeout=1800)
        try:
            for fr in json.loads(r.stdout or '[]'):
                for m in fr.get('messages', []):
                    if m.get('fatal') and not m.get('ruleId'):
                        gaps_.append(f'eslint could not parse {rel(fr["filePath"], tree)}: {m.get("message", "")[:120]}')
                        continue
                    diags.append(_diag('eslint', m.get('ruleId') or 'eslint', 'error' if m.get('severity') == 2 else 'warning', m.get('message', ''),
                                       tree, fr['filePath'], m.get('line'), m.get('column'), 'js'))
        except (ValueError, KeyError):
            return [{'tool': 'eslint', 'status': f'failed: {(r.stderr or r.stdout)[-400:]}'}], [], []
    return [{'tool': 'eslint', 'status': 'ran'}], diags, gaps_


def check_shell(tree, files, work):
    sc = _which('shellcheck')
    if not sc:
        return [{'tool': 'shellcheck', 'status': 'not run: not installed'}], [], []
    r = run([sc, '-f', 'json', '-S', 'style', *[os.path.join(tree, f) for f in files]], timeout=900)
    diags = []
    try:
        for x in json.loads(r.stdout or '[]'):
            diags.append(_diag('shellcheck', f'SC{x.get("code")}', x.get('level'), x.get('message', ''), tree, x['file'], x.get('line'), x.get('column'), 'shell'))
        return [{'tool': 'shellcheck', 'status': 'ran'}], diags, []
    except ValueError:
        return [{'tool': 'shellcheck', 'status': f'failed: {r.stderr[-200:]}'}], [], []


def check_secrets(tree, work, repo=None, base=None, head=None):
    """gitleaks over the tree under review, and over the commit range when a base is given. Always --redact."""
    if not os.path.isfile(GITLEAKS):
        return {'tool': 'gitleaks', 'status': 'not run: not installed (tools/bin/gitleaks)'}, []
    diags, runs = [], []
    os.makedirs(work, exist_ok=True)  # no other checker may have created it (a C#-only repo)
    jobs = [('dir', [GITLEAKS, 'dir', tree], os.path.join(work, 'gitleaks-dir.json'))]
    if repo and base:
        rng = f'{base}..{"HEAD" if (head or "").upper() == "WORKTREE" else head}'
        jobs.append(('git', [GITLEAKS, 'git', repo, '--log-opts', rng], os.path.join(work, 'gitleaks-git.json')))
    for kind, cmd, out in jobs:
        r = run(cmd + ['--config', GITLEAKS_CONFIG, '--gitleaks-ignore-path', GITLEAKS_IGNORE, '--redact', '--no-banner',
                       '--report-format', 'json', '--report-path', out, '--exit-code', '0', '--log-level', 'error'], timeout=1800)
        try:
            found = json.load(open(out, encoding='utf-8'))
        except (OSError, ValueError):
            runs.append({'mode': kind, 'status': f'failed: {(r.stderr or r.stdout)[-200:]}'})
            continue
        runs.append({'mode': kind, 'status': 'ran', 'findings': len(found)})
        for x in found:
            path = x.get('File', '')
            where = f' in commit {x.get("Commit", "")[:12]}' if kind == 'git' else ''
            diags.append(_diag('gitleaks', x.get('RuleID', 'secret'), 'error', f'possible secret ({x.get("Description")}){where}; value redacted',
                               tree if kind == 'dir' else (repo or tree), path if kind == 'dir' or not repo else os.path.join(repo, path),
                               x.get('StartLine'), x.get('StartColumn'), 'any'))
    status = 'ran' if any(j['status'] == 'ran' for j in runs) else 'failed'
    return {'tool': 'gitleaks', 'status': status, 'runs': runs}, diags


CHECKERS = {'Python': check_python, 'PowerShell': check_powershell, 'JavaScript': check_js, 'TypeScript': check_js, 'Shell': check_shell}
CHECKER_CATEGORY = {'ruff': 'python', 'mypy': 'python', 'bandit': 'python', 'pytest': 'python', 'PSScriptAnalyzer': 'powershell',
                    'eslint': 'js', 'shellcheck': 'shell', 'gitleaks': 'secret'}

INLINE_SUPPRESSIONS = [
    ('.py', 'noqa', re.compile(r'#\s*noqa\b', re.I)),
    ('.py', 'type: ignore', re.compile(r'#\s*type:\s*ignore\b')),
    ('.py', 'nosec', re.compile(r'#\s*nosec\b')),
    ('.py', 'pyright: ignore', re.compile(r'#\s*pyright:\s*ignore\b')),
    ('.py', 'pylint: disable', re.compile(r'#\s*pylint:\s*disable\b')),
    ('.js .mjs .cjs .jsx .ts .tsx .mts .cts', 'eslint-disable', re.compile(r'eslint-disable')),
    ('.ts .tsx .mts .cts .js', 'ts-ignore', re.compile(r'@ts-(ignore|nocheck|expect-error)\b')),
    ('.ps1 .psm1 .psd1', 'SuppressMessage', re.compile(r'SuppressMessage(Attribute)?\s*\(', re.I)),
    ('.sh .bash', 'shellcheck disable', re.compile(r'#\s*shellcheck\s+disable=')),
    ('*', 'gitleaks:allow', re.compile(r'gitleaks:allow')),
]


def find_inline_suppressions(tree, lang_files):
    out = []
    files = sorted({f for fs in lang_files.values() for f in fs})
    for r in files:
        ext = os.path.splitext(r)[1].lower()
        if ext in ('.cs', '.razor', '.cshtml'):
            continue  # C# suppressions are found by find_suppressions
        text = read_text(os.path.join(tree, r))
        for i, line in enumerate(text.splitlines(), 1):
            for exts, kind, rx in INLINE_SUPPRESSIONS:
                if (exts == '*' or ext in exts.split()) and rx.search(line):
                    out.append({'kind': kind, 'file': r, 'line': i, 'text': line.strip()[:200]})
    return out


def run_language_checks(tree, counts, files, work, dotnet_checked):
    """Runs every installed checker; returns (coverage per language, diagnostics, gaps). A language that no checker
    covered is a gap, so a project in an unchecked language never passes."""
    coverage, diags, gaps_ = {}, [], []
    done = {}
    for lang, n in sorted(counts.items()):
        if lang in ('C#', 'Razor'):
            coverage[lang] = {'files': n, 'checked': dotnet_checked, 'by': ['DeepScan', 'csproj build'] if dotnet_checked else [],
                              'status': 'checked' if dotnet_checked else 'not checked: no .NET target or project compiled'}
            continue
        fn = CHECKERS.get(lang)
        if not fn:
            coverage[lang] = {'files': n, 'checked': False, 'by': [], 'status': 'not checked: no checker for this language'}
            continue
        if fn.__name__ in done:
            coverage[lang] = dict(done[fn.__name__], files=n)
            continue
        lang_files = list(files[lang])
        if fn is check_js:
            lang_files = sorted(set(files.get('JavaScript', [])) | set(files.get('TypeScript', [])))
        os.makedirs(work, exist_ok=True)
        results, d, g = fn(tree, lang_files, work)
        ran = [x['tool'] for x in results if x['status'] == 'ran']
        info = {'files': n, 'checked': bool(ran), 'by': ran, 'checkers': results,
                'status': 'checked' if ran else 'not checked: ' + '; '.join(f'{x["tool"]} {x["status"]}' for x in results)}
        done[fn.__name__] = info
        coverage[lang] = info
        diags += d
        gaps_ += g
    return coverage, diags, gaps_


def linter_versions():
    """Versions of every non-.NET checker, for toolsVersion."""
    v = {}
    for name in ('ruff', 'mypy', 'bandit', 'pytest'):
        p = _which(name)
        v[name] = (run([p, '--version'], timeout=120).stdout.strip().splitlines() or [''])[0] if p else None
    pwsh = _which('pwsh', 'powershell')
    if pwsh:
        r = run([pwsh, '-NoProfile', '-NonInteractive', '-Command',
                 '$m = Get-Module -ListAvailable PSScriptAnalyzer | Sort-Object Version -Descending | Select-Object -First 1; if ($m) { $m.Version.ToString() }'], timeout=120)
        v['PSScriptAnalyzer'] = r.stdout.strip() or None
    lock = os.path.join(TOOLS, 'linters', 'js', 'package.json')
    if os.path.isfile(lock):
        v['js'] = json.load(open(lock, encoding='utf-8')).get('dependencies')
    if os.path.isfile(GITLEAKS):
        v['gitleaks'] = run([GITLEAKS, 'version'], timeout=60).stdout.strip()
    return v


# ------------------------------------------------------------------------------------------------ versions and records

VERSION_GLOBS = ['deep_tools.py', 'DeepScan/*.cs', 'DeepScan/*.csproj', 'Whitelist/*.cs', 'Whitelist/*.csproj', 'analyzers/AnalyzerPack.csproj', 'analyzers/deep.globalconfig',
                 'analyzers/deep-*', 'analyzers/empty.gitleaksignore', 'analyzers/BannedSymbols.txt', 'runtime-matrix/*.py',
                 'runtime-matrix/template/*', 'bin/analyzers/*.dll', 'bin/gitleaks/*.exe', 'linters/js/package.json', 'linters/*.ps1',
                 'linters/js/package-lock.json', 'cache/analyzers/*/*.dll']


def tools_version():
    """sha256 over the tools' sources, rule set and every analyzer DLL that runs. PluginCheck compares it."""
    h = hashlib.sha256()
    files = set()
    for pattern in VERSION_GLOBS:
        for f in glob.glob(os.path.join(TOOLS, pattern)):
            if os.path.isfile(f):
                files.add(rel(f, TOOLS))
    for r in sorted(files):
        h.update(r.encode() + b'\0')
        h.update(hashlib.sha256(open(os.path.join(TOOLS, r), 'rb').read()).digest())
    h.update(json.dumps(linter_versions(), sort_keys=True).encode())
    return h.hexdigest()


def tree_sha256(root):
    h = hashlib.sha256()
    for f in sorted(rel(p, root) for p in walk_files(root)):
        h.update(f.encode() + b'\0')
        h.update(hashlib.sha256(open(os.path.join(root, f), 'rb').read()).digest())
    return h.hexdigest()


def utc_now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat(timespec='seconds').replace('+00:00', 'Z')


# Rules whose net10-only hits on code that also ships on net48 suggest an API net48 does not have.
NET10_API_RULES = {'CA1835', 'CA1837', 'CA1845', 'CA1846', 'CA1847', 'CA1864', 'CA1865', 'CA1866', 'CA1867', 'CA1868', 'CA1869',
                   'CA1870', 'CA1871', 'CA1872', 'CA2249', 'CA2263', 'MA0045', 'MA0158', 'S9422', 'S6602', 'S6603', 'S6605', 'S6608'}

# Compiler polyfills (the user, 2026-11-17: "Fix the tool"). On netstandard2.0 and net48 the C# compiler needs these types to
# exist for init accessors, records and required members, and emits references to them itself, so no source names them and
# MA0182 (unused internal type) reports them, although deleting one breaks the build. A type is recognised only by its exact
# full name, declared internal at the top level of its real namespace, with only the shape the real BCL type has: no other
# modifiers, attributes, base types or members. Anything else keeps its MA0182 result.
POLYFILL_RULES = {'MA0182'}
POLYFILLS = {
    'IsExternalInit': ('System.Runtime.CompilerServices', 'empty'),
    'RequiredMemberAttribute': ('System.Runtime.CompilerServices', 'attribute'),
    'CompilerFeatureRequiredAttribute': ('System.Runtime.CompilerServices', 'feature'),
    'SetsRequiredMembersAttribute': ('System.Diagnostics.CodeAnalysis', 'attribute'),
}
_POLYFILL_DECL = re.compile(r'(?P<attrs>(?:\[[^\[\]]*\]\s*)*)(?P<mods>(?:\b[a-z]+\s+)*?)\bclass\s+(?P<name>' + '|'.join(POLYFILLS) + r')\b')
_POLYFILL_MEMBERS = [
    ('ctor', re.compile(r'public CompilerFeatureRequiredAttribute\s*\(\s*string\s+(\w+)\s*\)\s*'
                        r'(?:=>\s*(?:this\.)?FeatureName\s*=\s*\1\s*;|\{\s*(?:this\.)?FeatureName\s*=\s*\1\s*;\s*\})')),
    ('FeatureName', re.compile(r'public string FeatureName\s*\{\s*get\s*;\s*\}')),
    ('IsOptional', re.compile(r'public bool IsOptional\s*\{\s*get\s*;\s*(?:init|set)\s*;\s*\}')),
    ('RefStructs', re.compile(r'public const string RefStructs\s*=\s*(?:nameof\s*\(\s*RefStructs\s*\)|"RefStructs")\s*;')),
    ('RequiredMembers', re.compile(r'public const string RequiredMembers\s*=\s*(?:nameof\s*\(\s*RequiredMembers\s*\)|"RequiredMembers")\s*;')),
]


def _cs_strip(text):
    """(code, masked): comments and preprocessor lines blanked, offsets and line numbers kept; masked also blanks string and
    char literal contents, so braces inside them don't count."""
    code, masked = list(text), list(text)
    i, n = 0, len(text)
    line_start = True
    while i < n:
        c = text[i]
        if line_start and c in ' \t':
            i += 1
            continue
        if line_start and c == '#':
            j = text.find('\n', i)
            j = n if j < 0 else j
            for k in range(i, j):
                code[k] = masked[k] = ' '
            i = j
            continue
        line_start = c == '\n'
        if text.startswith('//', i):
            j = text.find('\n', i)
            j = n if j < 0 else j
            for k in range(i, j):
                code[k] = masked[k] = ' '
            i = j
        elif text.startswith('/*', i):
            j = text.find('*/', i + 2)
            j = n if j < 0 else j + 2
            for k in range(i, j):
                if text[k] != '\n':
                    code[k] = masked[k] = ' '
            i = j
        elif c == '"' or c == "'" or text.startswith('@"', i):
            verbatim = c == '@'
            j = i + (2 if verbatim else 1)
            quote = '"' if verbatim else c
            while j < n:
                if verbatim and text.startswith('""', j):
                    j += 2
                    continue
                if not verbatim and text[j] == '\\':
                    j += 2
                    continue
                if text[j] == quote or (not verbatim and text[j] == '\n'):
                    break
                j += 1
            for k in range((i + 2 if verbatim else i + 1), min(j, n)):
                if text[k] != '\n':
                    masked[k] = 'x'
            i = j + 1
        else:
            i += 1
    return ''.join(code), ''.join(masked)


def _close_brace(masked, open_at):
    depth = 0
    for k in range(open_at, len(masked)):
        if masked[k] == '{':
            depth += 1
        elif masked[k] == '}':
            depth -= 1
            if depth == 0:
                return k
    return -1


def _namespace_at(masked, pos):
    """The namespace a top-level declaration at pos sits in, or None when it sits in a type, a nested namespace or a block."""
    file_ns, stack = None, []
    for m in re.finditer(r'\bnamespace\s+([\w.]+)\s*([;{])|[{}]', masked[:pos]):
        if m.group(1):
            if m.group(2) == ';':
                file_ns = m.group(1)
            else:
                stack.append(m.group(1))
        elif m.group(0) == '{':
            stack.append(None)
        elif stack:
            stack.pop()
    if file_ns is not None:
        return file_ns if not stack else None
    return stack[0] if len(stack) == 1 else None


def _attribute_names(attrs):
    names = []
    for block in re.findall(r'\[([^\[\]]*)\]', attrs):
        depth, part, parts = 0, '', []
        for ch in block:
            depth += ch == '('
            depth -= ch == ')'
            if ch == ',' and depth == 0:
                parts.append(part)
                part = ''
            else:
                part += ch
        parts.append(part)
        for p in parts:
            name = p.split('(')[0].strip()
            name = re.sub(r'^(global::)?(System\.(ComponentModel\.)?)?', '', name)
            names.append(name[:-len('Attribute')] if name.endswith('Attribute') else name)
    return names


def compiler_polyfill(code, masked, line):
    """The full name of the compiler polyfill declared with its name on that line, or None (see POLYFILLS)."""
    for m in _POLYFILL_DECL.finditer(masked):
        if code.count('\n', 0, m.start('name')) + 1 != line:
            continue
        name = m.group('name')
        namespace, shape = POLYFILLS[name]
        if _namespace_at(masked, m.start()) != namespace:
            return None
        mods = m.group('mods').split()
        allowed = {'empty': ({'internal'}, {'internal', 'static'}, {'internal', 'sealed'}, {'internal', 'abstract'})}.get(
            shape, ({'internal'}, {'internal', 'sealed'}))
        if len(mods) != len(set(mods)) or set(mods) not in allowed:
            return None
        attrs = _attribute_names(m.group('attrs'))
        if any(a not in (('EditorBrowsable',) if shape == 'empty' else ('AttributeUsage', 'EditorBrowsable')) for a in attrs):
            return None
        rest = masked[m.end():]
        head = re.match(r'\s*(?::\s*(?P<base>(?:global::)?(?:System\.)?Attribute))?\s*(?P<open>[{;])', rest)
        if not head or bool(head.group('base')) != (shape != 'empty'):
            return None
        if head.group('open') == ';':
            body = ''
        else:
            open_at = m.end() + head.start('open')
            close = _close_brace(masked, open_at)
            if close < 0:
                return None
            body = code[open_at + 1:close]
        if shape != 'feature':
            return f'{namespace}.{name}' if not body.strip() else None
        seen, depth, start = set(), 0, 0
        members = []
        for k, ch in enumerate(masked[open_at + 1:close] if head.group('open') == '{' else ''):
            if ch == '{':
                depth += 1
            elif ch == '}':
                depth -= 1
                if depth == 0:
                    members.append(body[start:k + 1])
                    start = k + 1
            elif ch == ';' and depth == 0:
                members.append(body[start:k + 1])
                start = k + 1
        if body[start:].strip():
            return None
        for member in (x.strip() for x in members):
            if not member:
                continue
            kind = next((k for k, rx in _POLYFILL_MEMBERS if rx.fullmatch(member)), None)
            if kind is None or kind in seen:
                return None
            seen.add(kind)
        return f'{namespace}.{name}' if 'ctor' in seen else None
    return None


def drop_compiler_polyfills(diags, tree, dropped):
    """MA0182 results on compiler polyfills (POLYFILLS) leave the list; each is recorded in dropped."""
    kept, sources = [], {}
    for d in diags:
        if d.get('id') in POLYFILL_RULES and d.get('file') and d.get('line'):
            if d['file'] not in sources:
                path = os.path.join(tree, *d['file'].split('/'))
                sources[d['file']] = _cs_strip(read_text(path)) if os.path.isfile(path) else None
            src = sources[d['file']]
            full = compiler_polyfill(src[0], src[1], d['line']) if src else None
            if full:
                dropped.append({'id': d['id'], 'file': d['file'], 'line': d['line'], 'type': full,
                                'seenOn': d.get('seenOn') or [d.get('runtime')]})
                continue
        kept.append(d)
    return kept


def off_rules_without_reason():
    """Every severity=none line in deep.globalconfig must have a '# reason:' comment on the line or just above."""
    missing, rules = [], []
    lines = read_text(GLOBALCONFIG).splitlines()
    for i, line in enumerate(lines):
        m = re.match(r'^\s*(dotnet_(?:analyzer_)?diagnostic\.(.+?)\.severity)\s*=\s*(none|silent|suggestion)\b(.*)$', line)
        if not m:
            continue
        reason = re.search(r'#\s*reason:\s*(.+)$', m.group(4))
        j = i - 1
        while not reason and j >= 0 and lines[j].strip().startswith('#'):
            reason = re.search(r'#\s*reason:\s*(.+)$', lines[j])
            j -= 1
        rules.append({'key': m.group(2), 'severity': m.group(3), 'reason': reason.group(1).strip() if reason else None, 'line': i + 1})
        if not reason:
            missing.append(m.group(2))
    return rules, missing


def other_off_rules():
    """Rules turned off in the non-.NET central configs, with their reasons."""
    out = []
    for tool, path, rx in (('ruff', RUFF_CONFIG, r'^\s*"([A-Z]+[0-9]*)",?\s*(#\s*reason:\s*(.+))?$'),
                           ('PSScriptAnalyzer', PSSA_CONFIG, r"^\s*'(PS\w+)'\s*(#\s*reason:\s*(.+))?$"),
                           ('bandit', BANDIT_CONFIG, r'^\s*-\s*(B\d+)\s*(#\s*reason:\s*(.+))?$')):
        in_list = False
        for i, line in enumerate(read_text(path).splitlines(), 1):
            if tool == 'ruff':
                if re.match(r'^\s*(ignore|extend-ignore)\s*=\s*\[', line):
                    in_list = True
                    continue
                if in_list and line.strip().startswith(']'):
                    in_list = False
                if not in_list:
                    continue
            if tool == 'PSScriptAnalyzer':
                if 'ExcludeRules' in line:
                    in_list = True
                    continue
                if in_list and line.strip().startswith(')'):
                    in_list = False
                if not in_list:
                    continue
            m = re.match(rx, line)
            if m:
                out.append({'tool': tool, 'key': m.group(1), 'severity': 'off', 'reason': (m.group(3) or '').strip() or None, 'line': i})
    for m in re.finditer(r"'([@\w/-]+)':\s*'off',?\s*(//\s*reason:\s*(.+))?", read_text(ESLINT_CONFIG)):
        out.append({'tool': 'eslint', 'key': m.group(1), 'severity': 'off', 'reason': (m.group(3) or '').strip() or None, 'line': 0})
    return out


DEEPRULES_README = os.path.join(TOOLS, 'DeepRules', 'README.md')


def review_only_rules():
    """[(rule id, reason)] for every analyzer rule tagged ReviewOnly; None when it cannot be determined."""
    dlls = sorted(glob.glob(os.path.join(CACHE, 'deepscan', '*', 'DeepScan.dll')), key=os.path.getmtime)
    if not dlls:
        return None
    probe = os.path.join(CACHE, 'review-only-probe')
    os.makedirs(probe, exist_ok=True)
    src = os.path.join(probe, 'Empty.cs')
    with open(src, 'w', encoding='utf-8') as f:
        f.write('namespace Probe { internal static class Empty { } }\n')
    out = os.path.join(probe, 'probe.json')
    dirs = [d for d in glob.glob(os.path.join(CACHE, 'analyzers', '*')) if os.path.isdir(d) and not d.endswith('probe')] + [CUSTOM_ANALYZERS]
    run(['dotnet', dlls[-1], '--sources', src, '--runtime', 'net10', '--profile', 'plain', '--no-graph', '--analyzers', *dirs, '--out', out], timeout=600)
    try:
        ids = json.load(open(out, encoding='utf-8'))['analyzers'].get('reviewOnlyRuleIds') or []
    except (OSError, ValueError, KeyError):
        return None
    readme = read_text(DEEPRULES_README)
    result = []
    for rid in ids:
        row = next((l for l in readme.splitlines() if l.strip().startswith(f'| {rid} |')), None)
        if row:
            cells = [c.strip() for c in row.strip().strip('|').split('|')]
            reason = f'{cells[1]}: {cells[-1]}' if len(cells) > 2 else cells[-1]
        else:
            reason = 'not described in tools/DeepRules/README.md'
        result.append((rid, reason))
    return result


def write_ruleset():
    """analyzers/RULESET.md: every rule deep.globalconfig turns off, with its reason, and every rule it turns on."""
    rules, missing = off_rules_without_reason()
    on = re.findall(r'^\s*dotnet_diagnostic\.(\S+)\.severity\s*=\s*(warning|error)\b', read_text(GLOBALCONFIG), re.M)
    L = ['# Deep review rule set', '',
         'Generated from `deep.globalconfig` by `python deep_tools.py --ruleset`. Do not edit by hand: change the config and regenerate.', '',
         'Every analyzer rule that is enabled by default runs as a warning (`dotnet_analyzer_diagnostic.severity = warning`). The rules below are',
         'the only ones turned off, for every project alike; code under review may not turn any rule off itself (a `#pragma`, `[SuppressMessage]`,',
         '`<NoWarn>` or a repo analyzer config is a gate issue).', '',
         f'Off: {len(rules)} entries' + (f' ({len(missing)} WITHOUT a reason: the gate fails until each has one)' if missing else '') + '.', '',
         '| rule or category | severity | reason |', '|---|---|---|']
    for r in rules:
        L.append(f'| `{r["key"]}` | {r["severity"]} | {(r["reason"] or "**missing**").replace("|", "/")} |')
    L += ['', f'Turned on explicitly (shipped disabled by default, or kept on whatever their category): {len(on)} rules.', '',
          ', '.join(f'`{k}`' for k, _ in on), '']
    others = other_off_rules()
    L += ['## Other languages', '',
          'Same rule: every rule of every checker runs, and these central configs are the only place one is turned off. The',
          "repository's own ruff, mypy, bandit, PSScriptAnalyzer, ESLint and gitleaks configs are never used; inline `# noqa`,",
          '`# type: ignore`, `# nosec`, `eslint-disable`, `@ts-ignore`, PowerShell `SuppressMessage` and `gitleaks:allow` are gate issues.', '',
          '| checker | config | starts from |', '|---|---|---|',
          '| ruff | `deep-ruff.toml` | `select = ["ALL"]`, inline noqa ignored (`--ignore-noqa`) |',
          '| mypy | `deep-mypy.ini` | `--strict` plus warn_unreachable, strict_equality, extra_checks; missing stubs are gaps |',
          '| bandit | `deep-bandit.yaml` | every test, every severity and confidence, `--ignore-nosec` |',
          "| pytest | (none) | the repo's tests, `-p no:cacheprovider`, addopts cleared, 30 min limit; no tests is a gap |",
          '| PSScriptAnalyzer | `deep-psscriptanalyzer.psd1` | every default rule at every severity plus the configurable rules |',
          '| ESLint | `deep-eslint.config.mjs` | `@eslint/js` recommended (JS), typescript-eslint `strictTypeChecked` (TS), inline config off |',
          '| gitleaks | `deep-gitleaks.toml` | the built-in rules, `--redact`, repo ignore files not used |', '',
          f'Off: {len(others)} entries' + (f' ({sum(1 for o in others if not o["reason"])} WITHOUT a reason)' if any(not o['reason'] for o in others) else '') + '.', '',
          '| checker | rule | reason |', '|---|---|---|']
    L += [f'| {o["tool"]} | `{o["key"]}` | {(o["reason"] or "**missing**").replace("|", "/")} |' for o in others]
    L.append('')
    review = review_only_rules()
    L += ['## Review-only rules', '',
          'Rules whose descriptor carries the custom tag `ReviewOnly` (the rule cannot be made precise). Their hits do not count',
          'in the gate; they are kept in tools-report.json under `reviewOnly[]` as input for the stage-2 review. Reasons come from',
          'the DeepRules README.', '']
    if review is None:
        L.append('(could not be listed: DeepScan or the analyzers are not built yet)')
    elif not review:
        L.append('None today.')
    else:
        L += ['| rule | reason |', '|---|---|'] + [f'| `{rid}` | {reason.replace("|", "/")} |' for rid, reason in review]
    L += ['', '## Rules that replace bans', '',
          'Where a ban in `BannedSymbols.txt` (RS0030) also blocked the safe use of an API, the ban was replaced by a counted',
          'DeepRules rule that allows only that use. Details in the DeepRules README and `checks/core/security.md`.', '',
          '| rule | replaces | allows only |', '|---|---|---|',
          '| `DR037` | the RS0030 ban on every `HttpClient` constructor (the user, 2026-11-17: allowed "after knowing where it goes and that it only goes to whitelisted locations... starting with discord") '
          '| one `HttpClient` in a static readonly field, and requests whose host is constant text in the code and on `deep_review.http_allowed_hosts` |',
          '| `DR038` | the RS0030 bans on `Process.Start(string)` and `Process.Start(ProcessStartInfo)` (the user, 2026-11-17: "add an exception to the banned list. if the address opened only shows local pages") '
          '| opening a fixed local web page: an http(s) URL whose scheme and host (`127.0.0.1`, `localhost`, `[::1]`) are constant text in the code, '
          'with no Arguments and no verb but "open". Opening a fixed local page is safe, as the old ban text already said for fixed links. '
          'The other `Process.Start` overloads stay banned |',
          '| `DR039` | the RS0030 ban on `System.Security.Cryptography.SHA1` and every type derived from it (the user, 2026-11-21: "SHA handshakes are an exception") '
          '| the WebSocket handshake answer of RFC 6455 section 4.2.2 only: `Convert.ToBase64String` of the SHA-1 of the `Sec-WebSocket-Key` header value of the request plus the exact GUID '
          '`258EAFA5-E914-47DA-95CA-C5AB0DC85B11` (`SHA1.HashData(...)`, or `SHA1.Create()` and `ComputeHash`), whose result only becomes the `Sec-WebSocket-Accept` header value. '
          'At that one call DR039 is silent and CA5350, S4790 and SCS0006 are dropped by a DiagnosticSuppressor (listed as suppressed diagnostics); they stay on everywhere else, '
          'and any other SHA-1 use, a lookalike with other input or another destination, `SHA1Managed` and `SHA1CryptoServiceProvider` are DR039 hits |']
    L += ['', '## Compiler polyfills', '',
          'MA0182 (unused internal type) stays on. Its results on the types the C# compiler itself needs on netstandard2.0 and net48,',
          'and references without any source naming them, are dropped as used (the user, 2026-11-17: "Fix the tool"): '
          + ', '.join(f'`{ns}.{name}`' for name, (ns, _) in POLYFILLS.items()) + '.',
          'A type counts only by its exact full name, declared `internal` at the top level of that namespace, with the real type\'s',
          'shape and nothing more (an empty body, or for CompilerFeatureRequiredAttribute only its string constructor, FeatureName,',
          'IsOptional and the RefStructs/RequiredMembers constants; only AttributeUsage/EditorBrowsable attributes; `: Attribute` for',
          'the attributes). A lookalike in another namespace, a public or partial one, or one with extra members keeps its MA0182.',
          'Dropped results are counted in `dropped.compilerPolyfills` and listed in `compilerPolyfills[]`, never as suppressions.',
          'See `checks/core/runtime-differences.md`.']
    L += ['', '## Host test runs', '',
          '.NET Framework 4.8 tests are built in the SDK container and run on this PC, headless (the user\'s decision,',
          '2026-11-16: "On this PC, headless"). See README.md, "net48 tests".', '',
          '## Script whitelist', '',
          'Mods and PB scripts are checked against the game\'s own whitelist, built and applied by the game\'s code from the',
          'resolved DedicatedServer64 (tools/Whitelist). Nothing in it is configured here: every prohibited type or member is an',
          'issue (category `whitelist`). See README.md, "Script whitelist".', '']
    path = os.path.join(TOOLS, 'analyzers', 'RULESET.md')
    with open(path, 'w', encoding='utf-8') as f:
        f.write('\n'.join(L))
    return path


# ------------------------------------------------------------------------------------------------ gate record check

def parse_utc(text):
    """ISO 8601 UTC ('YYYY-MM-DDTHH:MM:SSZ'); returns an aware datetime or None."""
    try:
        t = datetime.datetime.fromisoformat(str(text).replace('Z', '+00:00'))
    except ValueError:
        return None
    if t.tzinfo is None:
        t = t.replace(tzinfo=datetime.timezone.utc)
    return t.astimezone(datetime.timezone.utc)


def _git_rev(repo, rev):
    r = git(repo, 'rev-parse', '--verify', '-q', rev)
    return r.stdout.strip() if r.returncode == 0 else None


# PluginCheck's list of approved toolsVersions of Flow's C# gate port (GATE-RECORD.md step 6). Read-only, read only here in
# Python: the lease guard blocks any shell command that names it.
APPROVED_TOOLS = os.path.join('C:' + os.sep, 'Development', 'Development', 'machine', 'lease-approved-' + 'tools.json')
APPROVED_SCHEMA = 'plugincheck-approved-tools/1'


def load_approved_tools(path):
    """(versions, error). versions is None when the file does not exist; error is set when it exists but cannot be parsed."""
    if not os.path.isfile(path):
        return None, None
    try:
        with open(path, encoding='utf-8') as f:
            data = json.load(f)
    except (OSError, ValueError) as e:
        return None, f'not UTF-8 JSON ({e})'
    if not isinstance(data, dict) or data.get('schema') != APPROVED_SCHEMA:
        return None, f'schema is {data.get("schema") if isinstance(data, dict) else type(data).__name__!r}, not {APPROVED_SCHEMA!r}'
    approved = data.get('approved')
    if not isinstance(approved, list):
        return None, 'no "approved" array'
    versions = []
    for i, entry in enumerate(approved):
        if not isinstance(entry, dict) or not isinstance(entry.get('toolsVersion'), str):
            return None, f'approved[{i}] has no "toolsVersion" string'
        versions.append(entry['toolsVersion'])
    return versions, None


def check_record(path, nonce, issued, repo, head, now=None, tools_version_fn=None, git_fn=None, content_fn=None, approved_path=None):
    """GATE-RECORD.md consumer checks 1 and 3-10. Returns the reasons the record fails (empty when it passes).
    Check 2 (the gate's own exit code) and nonce reuse are the consumer's own state."""
    tools_version_fn = tools_version_fn or tools_version
    git_fn = git_fn or _git_rev
    content_fn = content_fn or repo_content_sha256
    now = now or datetime.datetime.now(datetime.timezone.utc)
    reasons = []
    try:
        with open(path, encoding='utf-8') as f:
            rec = json.load(f)
    except (OSError, ValueError) as e:
        return [f'1: gate.json does not parse as UTF-8 JSON: {e}']
    if not isinstance(rec, dict):
        return ['1: gate.json is not a JSON object']
    required = ['schema', 'kind', 'gateMode', 'nonce', 'repo', 'head', 'headTree', 'toolsVersion', 'options', 'startedUtc', 'finishedUtc',
                'fresh', 'projectKind', 'projectKinds', 'languages', 'runtimes', 'verdict', 'counts', 'issues', 'gaps', 'suppressions',
                'suppressedDiagnostics', 'notCovered']
    missing = [k for k in required if k not in rec]
    if missing:
        reasons.append(f'1: missing fields: {", ".join(missing)}')
    if rec.get('schema') != 1:
        reasons.append(f'1: schema is {rec.get("schema")!r}, not 1')
    if rec.get('kind') != 'gate':
        reasons.append(f'1: kind is {rec.get("kind")!r}, not "gate"')
    if rec.get('gateMode') is not True:
        reasons.append('1: gateMode is not true (not a --gate run)')
    if rec.get('nonce') != nonce:
        reasons.append(f'3: nonce {rec.get("nonce")!r} is not the issued nonce')
    issued_t, started, finished = parse_utc(issued), parse_utc(rec.get('startedUtc')), parse_utc(rec.get('finishedUtc'))
    if issued_t is None:
        reasons.append(f'4: --nonce-issued {issued!r} is not an ISO 8601 UTC time')
    if started is None or finished is None:
        reasons.append('4: startedUtc or finishedUtc is missing or not ISO 8601 UTC')
    else:
        if issued_t is not None and started < issued_t:
            reasons.append(f'4: startedUtc {rec["startedUtc"]} is before the nonce was issued ({issued})')
        if finished < started:
            reasons.append('4: finishedUtc is before startedUtc')
        if finished > now:
            reasons.append(f'4: finishedUtc {rec["finishedUtc"]} is in the future')
    if rec.get('fresh') is not True:
        reasons.append('5: fresh is not true')
    current = (tools_version_fn() or '').strip()
    approved, approved_error = load_approved_tools(approved_path or APPROVED_TOOLS)
    if approved_error:
        reasons.append(f'6: the approved-tools list cannot be parsed: {approved_error}')
    elif rec.get('toolsVersion') != current and not (approved is not None and rec.get('toolsVersion') in approved):
        reasons.append(f'6: toolsVersion {str(rec.get("toolsVersion"))[:16]} is neither the installed tools ({current[:16]}) '
                       f'nor an approved version ({"no approved-tools list" if approved is None else f"{len(approved)} approved"})')
    norm = lambda p: os.path.normcase(os.path.normpath(os.path.abspath(p))) if p else ''
    if norm(rec.get('repo')) != norm(repo):
        reasons.append(f'7: repo {rec.get("repo")!r} is not {repo!r}')
    rh = str(rec.get('head') or '')
    if re.fullmatch(r'[0-9a-fA-F]{40}', head or ''):
        sha = head.lower()
        if rh != sha:
            reasons.append(f'8: head {rh!r} is not the commit to run ({sha})')
        tree = git_fn(repo, sha + '^{tree}')
        if not tree or rec.get('headTree') != tree:
            reasons.append(f'8: headTree {rec.get("headTree")!r} does not match {sha}^{{tree}} ({tree})')
    elif (head or '').upper() == 'WORKTREE':
        cur = git_fn(repo, 'HEAD')
        m = re.fullmatch(r'WORKTREE@([0-9a-f]{40})(\+dirty)?', rh)
        if not m or m.group(1) != cur:
            reasons.append(f'8: head {rh!r} is not WORKTREE@<HEAD sha> for this repo (HEAD is {cur})')
        mine = content_fn(repo)
        if rec.get('contentSha256') != mine:
            reasons.append(f'8: contentSha256 {str(rec.get("contentSha256"))[:16]} differs from the tree to run ({mine[:16]})')
    else:
        reasons.append(f'8: --head {head!r} is neither a 40-hex commit nor WORKTREE')
    if rh == 'DIR' or not (re.fullmatch(r'[0-9a-f]{40}', rh) or rh.startswith('WORKTREE@')):
        reasons.append(f'8: head {rh!r} never qualifies (only a commit or a working tree does)')
    counts = rec.get('counts') or {}
    if rec.get('verdict') != 'PASS':
        reasons.append(f'9: verdict is {rec.get("verdict")!r}')
    if counts.get('issues') != 0 or rec.get('issues'):
        reasons.append(f'9: {counts.get("issues")} issues')
    if counts.get('gaps') != 0 or rec.get('gaps'):
        reasons.append(f'9: {counts.get("gaps")} gaps')
    if rec.get('suppressions'):
        reasons.append(f'9: {len(rec["suppressions"])} suppressions in the code under review')
    if rec.get('notCovered'):
        reasons.append(f'9: notCovered is not empty: {rec["notCovered"]}')
    if (rec.get('options') or {}).get('skip'):
        reasons.append(f'9: stages were skipped: {rec["options"]["skip"]}')
    unchecked = [k for k, v in (rec.get('languages') or {}).items() if not (isinstance(v, dict) and v.get('checked') is True)]
    if unchecked:
        reasons.append(f'10: languages not checked: {", ".join(unchecked)}')
    return reasons


def check_record_cli(argv):
    ap = argparse.ArgumentParser(prog='deep_tools.py --check-record')
    ap.add_argument('--check-record', required=True)
    ap.add_argument('--nonce', required=True)
    ap.add_argument('--nonce-issued', required=True)
    ap.add_argument('--repo', required=True)
    ap.add_argument('--head', required=True)
    ap.add_argument('--approved-tools', help=argparse.SUPPRESS)  # tests only: a fixture copy of the approved-tools list
    a = ap.parse_args(argv)
    reasons = check_record(a.check_record, a.nonce, a.nonce_issued, a.repo, a.head, approved_path=a.approved_tools)
    if reasons:
        print('\n'.join(reasons))
        return 3
    print('record passes checks 1 and 3-10')
    return 0


# ------------------------------------------------------------------------------------------------ gate

def gate_verdict(rep, plugin_repo, enabled_rules, suppression_sites):
    """PASS only with no errors, warnings or issues anywhere and no gaps. Every check that did not run is a gap."""
    issues = []

    def add(category, rule, d, text=None):
        issues.append({'category': category, 'rule': rule, 'file': d.get('file'), 'line': d.get('line'),
                       'runtime': '/'.join(d.get('runtimes') or [d.get('runtime') or '']) or None,
                       'text': (text or d.get('message') or '')[:240], 'compile': ', '.join(d.get('compiles') or []) or None})

    for d in rep['diagnostics']:
        if d.get('status') == 'FIXED' or d.get('suppressed'):
            continue
        rid, tool = d['id'], d.get('tool') or ''
        if rid.startswith('CS'):
            add('compile', rid, d)
        elif tool == 'whitelist':
            add('whitelist', rid, d)
        elif rid == 'RS0030':
            add('banned-api', rid, d)
        elif tool.startswith('DeepRules') or rid.startswith('DR'):
            add('deeprules', rid, d)
        elif tool == 'architecture':
            add('architecture', rid, d)
        elif tool in CHECKER_CATEGORY:
            add(CHECKER_CATEGORY[tool], f'{tool}:{rid}', d)
        else:
            add('analyzer', rid, d)
    outside = 0
    for d in rep.get('csprojBuildDiagnostics', []):
        rid = d.get('id') or ''
        if rid.startswith('CS'):
            add('compile', rid, d, f'[csproj build {d.get("project")}] {d.get("message")}')
        elif not plugin_repo:
            if rid in enabled_rules or rid == 'RS0030':
                add('banned-api' if rid == 'RS0030' else 'analyzer', rid, d, f'[csproj build {d.get("project")}] {d.get("message")}')
            else:
                outside += 1
    for s in suppression_sites:
        add('suppression', s['kind'], s, f'{s["kind"]} in the code under review: {s["text"]}')
    for f in rep['analyzers'].get('failed', []):
        add('analyzer-load', 'load', {'file': f['path']}, f['error'])
    for p in rep['vulnerabilities'].get('projects', []):
        for v in p.get('vulnerable', []):
            add('vulnerable-package', v['package'], {'file': p['project']}, f'{v["package"]} {v["version"]} {v["severity"]} {v["advisory"]}')
    rel_ = rep['release']
    if not rel_.get('consistent', True):
        add('release', 'version', {}, 'versions differ: ' + ', '.join(f'{v["source"]}={v["value"]}' for v in rel_['versions']))
    for m in rel_.get('manifests', []):
        for p in m['problems']:
            add('release', 'manifest', {'file': m['manifest']}, p)
    for t in rel_.get('todos', []):
        add('release', 'todo', t, t['text'])
    for p in rep['tests'].get('projects', []):
        if p.get('status') == 'ran' and (p.get('failed', 0) or p.get('error', 0) or p.get('timeout', 0)):
            add('tests', 'failed', {'file': p['project'], 'runtime': p['framework']}, f'{p.get("failed", 0)} failed, {p.get("error", 0)} errors')
        elif p.get('status') == 'ran' and p.get('aborted'):
            add('tests', 'aborted', {'file': p['project'], 'runtime': p['framework']},
                'the test run stopped without finishing (' + ('killed at the time limit' if p.get('killedAtLimit') else
                                                            f'exit {p.get("exit")}: a hang stopped by --blame-hang or a crash') + ')')
    by_cat = collections.Counter(i['category'] for i in issues)
    by_rule = collections.Counter(f'{i["category"]}:{i["rule"]}' for i in issues)
    by_runtime = collections.Counter(i['runtime'] or 'n/a' for i in issues)
    verdict = 'PASS' if not issues and not rep['gaps'] else 'FAIL'
    return verdict, issues, {'byCategory': dict(by_cat.most_common()), 'byRule': dict(by_rule.most_common()),
                             'byRuntime': dict(by_runtime.most_common()), 'issues': len(issues), 'gaps': len(rep['gaps']),
                             'csprojBuildOutsideRuleSet': outside}


def gate_text(g):
    L = [f'GATE {g["verdict"]}  {g["repo"]}  ({g.get("projectKind")})', f'head {g["head"]}  tools {g["toolsVersion"][:16]}  nonce {g.get("nonce")}',
         f'started {g["startedUtc"]}  finished {g["finishedUtc"]}  runtimes {", ".join(g["runtimes"]) or "none"}',
         'languages: ' + (', '.join(f'{k} {v["files"]}{"" if v["checked"] else " UNCHECKED"}' for k, v in (g.get('languages') or {}).items()) or 'none'), '']
    c = g['counts']
    L.append(f'{c["issues"]} issues, {c["gaps"]} gaps')
    for k, v in c['byCategory'].items():
        L.append(f'  {k}: {v}')
    if c['byRule']:
        L.append('top rules: ' + ', '.join(f'{k} {v}' for k, v in list(c['byRule'].items())[:15]))
    if g['gaps']:
        L.append('')
        L.append('gaps (each one fails the gate):')
        L += [f'  - {x}' for x in g['gaps']]
    if g['issues']:
        L.append('')
        L.append('first issues:')
        for i in g['issues'][:30]:
            where = f'{i["file"]}:{i["line"]}' if i.get('file') else ''
            L.append(f'  - [{i["category"]}] {i["rule"]} {where} {i["text"][:150]}')
    if g.get('notCovered'):
        L.append('')
        L += [f'not covered: {x}' for x in g['notCovered']]
    return '\n'.join(L) + '\n'


# ------------------------------------------------------------------------------------------------ main

def main():
    global LOG
    if '--check-record' in sys.argv[1:]:
        return check_record_cli(sys.argv[1:])
    if '--ruleset' in sys.argv[1:]:
        print(write_ruleset())
        return 0 if not off_rules_without_reason()[1] else 3
    if '--version' in sys.argv[1:]:
        ensure_analyzers(None)
        print(tools_version())
        return 0
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--repo')
    ap.add_argument('--lint-dir', help='a folder that is not a git repository: language checkers and secrets scan only')
    ap.add_argument('--head', help='commit, or WORKTREE for the uncommitted working tree')
    ap.add_argument('--worktree', action='store_true', help='same as --head WORKTREE')
    ap.add_argument('--base')
    ap.add_argument('--manifest', action='append', default=[])
    ap.add_argument('--out', required=True)
    ap.add_argument('--gate', action='store_true', help='single PASS/FAIL verdict: exit 0 PASS, 3 FAIL, 1 could not run')
    ap.add_argument('--all', metavar='PROJECTS_JSON', help='gate every project in a list ({name, repo, head}) or in MasterProjects.json')
    ap.add_argument('--include-watch', action='store_true', help='with --all on MasterProjects.json: also projects with status watch')
    ap.add_argument('--exclude', action='append', default=[], help='with --all: project names to leave out (repeatable)')
    ap.add_argument('--nonce', help='recorded in gate.json/tools-report.json so a slot grant can prove this run is fresh')
    ap.add_argument('--affinity-mask', type=lambda s: int(s, 0), default=0x00FFFFFF)
    ap.add_argument('--ds-dir')
    ap.add_argument('--skip-tests', action='store_true')
    ap.add_argument('--skip-vuln', action='store_true')
    ap.add_argument('--skip-csproj-build', action='store_true')
    ap.add_argument('--skip-optional', action='store_true')
    ap.add_argument('--rebuild-deepscan', action='store_true')
    ap.add_argument('--emit', action='store_true', help='emit DLL+PDB per target for IL tools (InferSharp)')
    args = ap.parse_args()
    if args.all:
        return run_all(args)
    if args.lint_dir:
        return lint_dir(args)
    if not args.repo or not (args.head or args.worktree):
        ap.error('--repo and --head (or --worktree) are required')
    if args.worktree:
        args.head = 'WORKTREE'
    out = os.path.abspath(args.out)
    os.makedirs(out, exist_ok=True)
    for stale in ('tools-report.json', 'tools-report.md', 'tools-report.sarif', 'gate.json', 'gate.txt'):
        if os.path.exists(os.path.join(out, stale)):
            os.remove(os.path.join(out, stale))
    LOG = open(os.path.join(out, 'deep-tools.log'), 'w', encoding='utf-8')
    work = os.path.join(out, 'work')
    shutil.rmtree(work, ignore_errors=True)  # never reuse a previous run's results
    os.makedirs(work, exist_ok=True)
    started = time.time()
    record = {'schema': 1, 'kind': 'gate' if args.gate else 'report', 'gateMode': bool(args.gate), 'nonce': args.nonce,
              'repo': os.path.abspath(args.repo), 'head': args.head, 'options': run_options(args), 'startedUtc': utc_now(), 'fresh': True}
    exit_code = 1
    try:
        exit_code = execute(args, out, work, record)
    except SystemExit as e:
        msg = e.code if isinstance(e.code, str) else f'exit {e.code}'
        log('FAILED ' + msg)
        fail_record(out, record, args, msg)
        exit_code = 1
    except Exception:
        import traceback
        log('FAILED ' + traceback.format_exc())
        fail_record(out, record, args, traceback.format_exc())
        exit_code = 1
    finally:
        run(['dotnet', 'build-server', 'shutdown'], timeout=120)
        log(f'finished in {time.time() - started:.0f}s, exit {exit_code}')
        LOG.close()
    return exit_code


def lint_dir(args):
    """Language checkers, inline suppressions and the secrets scan over a copy of a folder that is not a git repository."""
    global LOG
    src = os.path.abspath(args.lint_dir)
    out = os.path.abspath(args.out)
    os.makedirs(out, exist_ok=True)
    LOG = open(os.path.join(out, 'deep-tools.log'), 'w', encoding='utf-8')
    work = os.path.join(out, 'work')
    shutil.rmtree(work, ignore_errors=True)
    tree = os.path.join(work, 'trees', 'head')
    set_affinity(args.affinity_mask)
    record = {'schema': 1, 'kind': 'gate' if args.gate else 'lint', 'gateMode': bool(args.gate), 'nonce': args.nonce, 'repo': src,
              'head': 'DIR', 'options': run_options(args), 'startedUtc': utc_now(), 'fresh': True, 'toolsVersion': tools_version()}
    shutil.copytree(src, tree, ignore=shutil.ignore_patterns(*SKIP_DIRS))
    record['headTree'] = 'DIR'
    record['contentSha256'] = tree_sha256(tree)
    counts, files = language_inventory(tree)
    record['projectKind'] = 'dotnet' if counts.get('C#') else 'non-dotnet'
    record['projectKinds'] = [record['projectKind']]
    languages, diags, gaps_ = run_language_checks(tree, counts, files, os.path.join(work, 'lint'), False)
    for g in gaps_:
        gap(g)
    for lang, info in languages.items():
        if not info['checked']:
            gap(f'{lang}: {info["files"]} files not checked ({info["status"]})')
    secrets_status, secrets = check_secrets(tree, os.path.join(work, 'lint'))
    if secrets_status['status'] != 'ran':
        gap(f'secrets scan did not run: {secrets_status["status"]}')
    diags += secrets
    sites = find_inline_suppressions(tree, files)
    issues = [{'category': CHECKER_CATEGORY.get(d['tool'], 'analyzer'), 'rule': f'{d["tool"]}:{d["id"]}', 'file': d.get('file'), 'line': d.get('line'),
               'runtime': d.get('runtime'), 'text': (d.get('message') or '')[:240]} for d in diags]
    issues += [{'category': 'suppression', 'rule': x['kind'], 'file': x['file'], 'line': x['line'], 'runtime': None,
                'text': f'{x["kind"]} in the code under review: {x["text"]}'} for x in sites]
    counts_ = {'issues': len(issues), 'gaps': len(GAPS), 'byCategory': dict(collections.Counter(i['category'] for i in issues).most_common()),
               'byRule': dict(collections.Counter(f'{i["category"]}:{i["rule"]}' for i in issues).most_common()),
               'byRuntime': dict(collections.Counter(i['runtime'] or 'n/a' for i in issues).most_common())}
    verdict = 'PASS' if not issues and not GAPS else 'FAIL'
    record.update({'finishedUtc': utc_now(), 'runtimes': [], 'verdict': verdict, 'counts': counts_, 'issues': issues[:200], 'gaps': GAPS,
                   'suppressions': sites, 'suppressedDiagnostics': [], 'notCovered': [],
                   'languages': {k: {'files': v['files'], 'checked': v['checked'], 'by': v['by']} for k, v in languages.items()}})
    write_json(os.path.join(out, 'tools-report.json'), dict(record, diagnostics=diags, languageDetail=languages, secrets=secrets_status))
    if args.gate:
        write_json(os.path.join(out, 'gate.json'), record)
        with open(os.path.join(out, 'gate.txt'), 'w', encoding='utf-8') as f:
            f.write(gate_text(record))
    log(f'lint {src}: {len(issues)} issues, {len(GAPS)} gaps, {verdict}')
    LOG.close()
    return (0 if verdict == 'PASS' else 3) if args.gate else (2 if GAPS else 0)


def run_options(args):
    """The `options` object of the gate record (GATE-RECORD.md)."""
    skip = [name for name, flag in (('tests', 'skip_tests'), ('vuln', 'skip_vuln'), ('csproj-build', 'skip_csproj_build'),
                                    ('optional', 'skip_optional')) if getattr(args, flag, False)]
    return {'skip': skip, 'base': getattr(args, 'base', None), 'manifests': list(getattr(args, 'manifest', []) or []),
            'affinityMask': hex(args.affinity_mask)}


def fail_record(out, record, args, error):
    record.update({'finishedUtc': utc_now(), 'verdict': 'ERROR' if args.gate else None, 'error': error[-4000:], 'gaps': GAPS,
                   'toolsVersion': record.get('toolsVersion') or safe_version()})
    write_json(os.path.join(out, 'tools-report.json'), record)
    if args.gate:
        write_json(os.path.join(out, 'gate.json'), record)
        with open(os.path.join(out, 'gate.txt'), 'w', encoding='utf-8') as f:
            f.write(f'GATE ERROR (could not run) {record.get("repo")}\n{error[-2000:]}\n')


def safe_version():
    try:
        return tools_version()
    except OSError:
        return None


ALL_EXCLUDES = os.path.join(TOOLS, 'all-excludes.json')


def load_all_excludes():
    """Projects --all leaves out by default, each with the reason (tools/all-excludes.json)."""
    try:
        data = json.load(open(ALL_EXCLUDES, encoding='utf-8'))
    except (OSError, ValueError):
        return {}
    return {x['name'].lower(): {'name': x['name'], 'reason': x.get('reason') or ''} for x in data.get('exclude', []) if x.get('name')}


def load_all_exempt():
    """Projects exempt from the gate (tools/all-excludes.json 'exempt'), each with the reason. --all skips them as exempt."""
    try:
        data = json.load(open(ALL_EXCLUDES, encoding='utf-8'))
    except (OSError, ValueError):
        return {}
    return {x['name'].lower(): {'name': x['name'], 'reason': x.get('reason') or ''} for x in data.get('exempt', []) if x.get('name')}


def run_all(args):
    """Runs the gate on every project of a list, each in its own process and output folder, and writes one summary."""
    out = os.path.abspath(args.out)
    os.makedirs(out, exist_ok=True)
    data = json.load(open(args.all, encoding='utf-8-sig'))
    entries = []
    if isinstance(data, dict) and 'projects' in data:
        allowed = {'active'} | ({'watch'} if args.include_watch else set())
        for p in data['projects']:
            loc = p.get('location') or ''
            if p.get('status') not in allowed:
                continue
            if not os.path.isdir(os.path.join(loc, '.git')):
                entries.append({'name': p['name'], 'repo': loc, 'skip': 'no local git repository'})
                continue
            entries.append({'name': p['name'], 'repo': loc, 'head': 'WORKTREE'})
    else:
        entries = data if isinstance(data, list) else data.get('entries', [])
    summary = {'schema': 1, 'kind': 'gate-summary', 'nonce': args.nonce, 'startedUtc': utc_now(), 'toolsVersion': None, 'projects': []}
    if args.rebuild_deepscan:
        # Once for the whole list (each project's own process then finds it built), not once per project.
        set_affinity(args.affinity_mask)
        ensure_deepscan(args)
    worst = 0
    excludes = load_all_excludes()
    for name in args.exclude:
        excludes.setdefault(name.lower(), {'name': name, 'reason': 'excluded on the command line (--exclude)'})
    summary['excluded'] = list(excludes.values())
    excluded = set(excludes)
    exempt = load_all_exempt()
    summary['exempt'] = list(exempt.values())
    for e in entries:
        if e['name'].lower() in exempt:
            summary['projects'].append({'name': e['name'], 'repo': e.get('repo'), 'verdict': 'SKIPPED',
                                        'reason': 'exempt: ' + exempt[e['name'].lower()]['reason']})
            continue
        if e['name'].lower() in excluded:
            summary['projects'].append({'name': e['name'], 'repo': e.get('repo'), 'verdict': 'SKIPPED',
                                        'reason': 'excluded: ' + excludes[e['name'].lower()]['reason']})
            continue
        name = re.sub(r'[^\w.-]+', '_', e['name'])
        item = {'name': e['name'], 'repo': e.get('repo'), 'head': e.get('head')}
        if e.get('skip'):
            item.update(verdict='SKIPPED', reason=e['skip'])
            summary['projects'].append(item)
            continue
        cmd = [sys.executable, os.path.abspath(__file__), '--repo', e['repo'], '--head', e.get('head') or 'WORKTREE', '--gate',
               '--out', os.path.join(out, name), '--affinity-mask', hex(args.affinity_mask)]
        if e.get('base'):
            cmd += ['--base', e['base']]
        if args.nonce:
            cmd += ['--nonce', args.nonce]
        print(f'== gate {e["name"]}', flush=True)
        r = run(cmd, timeout=4 * 3600, capture=False)
        try:
            g = json.load(open(os.path.join(out, name, 'gate.json'), encoding='utf-8'))
            item.update(verdict=g.get('verdict'), counts=g.get('counts'), gaps=len(g.get('gaps') or []), exit=r.returncode,
                        head=g.get('head'), toolsVersion=g.get('toolsVersion'))
            summary['toolsVersion'] = summary['toolsVersion'] or g.get('toolsVersion')
        except (OSError, ValueError):
            item.update(verdict='ERROR', exit=r.returncode)
        worst = max(worst, {0: 0, 3: 3, 1: 4}.get(r.returncode, 4))
        summary['projects'].append(item)
    summary['finishedUtc'] = utc_now()
    write_json(os.path.join(out, 'gate-summary.json'), summary)
    with open(os.path.join(out, 'gate-summary.txt'), 'w', encoding='utf-8') as f:
        for x in summary['excluded']:
            f.write(f'EXCLUDED {x["name"]}: {x["reason"]}\n')
        for p in summary['projects']:
            c = p.get('counts') or {}
            f.write(f'{p["verdict"]:8} {p["name"]}: {c.get("issues", "-")} issues, {p.get("gaps", "-")} gaps {p.get("reason", "")}\n')
    print(open(os.path.join(out, 'gate-summary.txt'), encoding='utf-8').read())
    return {0: 0, 3: 3, 4: 1}[worst]


def execute(args, out, work, record):
    repo = os.path.abspath(args.repo)
    pinned = set_affinity(args.affinity_mask)
    log(f'affinity {hex(args.affinity_mask)} (cores {cpuset(args.affinity_mask)}) {"set" if pinned else "NOT set"}')
    if not pinned:
        gap('could not pin the process affinity; child processes may use every core')
    if git(repo, 'rev-parse', '--git-dir').returncode:
        raise SystemExit(f'{repo} is not a git repository')
    for flag in ('skip_tests', 'skip_vuln', 'skip_csproj_build', 'skip_optional'):
        if args.gate and getattr(args, flag):
            gap(f'--{flag.replace("_", "-")} in gate mode: that check did not run')

    pack_info = ensure_analyzers(args)
    deepscan = ensure_deepscan(args)
    pack_info = probe_dropped_packs(pack_info, deepscan, args)
    record['toolsVersion'] = tools_version()
    off_rules, missing_reasons = off_rules_without_reason()
    missing_reasons += [f'{o["tool"]}:{o["key"]}' for o in other_off_rules() if not o['reason']]
    if missing_reasons:
        gap(f'deep.globalconfig turns off {len(missing_reasons)} rules without a "# reason:" line: {", ".join(missing_reasons[:20])}')

    head_tree = os.path.join(work, 'trees', 'head')
    head_label, head_sha = get_tree(repo, args.head, head_tree)
    record['head'] = head_label
    if args.head.upper() == 'WORKTREE':
        record['headTree'] = head_label
        record['contentSha256'] = content_sha256(head_tree, LISTED.get(head_tree, []))
    else:
        record['headTree'] = git(repo, 'rev-parse', head_sha + '^{tree}').stdout.strip()
    base_tree = base_sha = None
    if args.base:
        base_tree = os.path.join(work, 'trees', 'base')
        base_sha, _ = get_tree(repo, args.base, base_tree)
    elif not args.gate:
        gap('no --base: NEW/FIXED and diff scope not computed')

    targets, manifests = discover_targets(head_tree, args.manifest, repo)
    not_covered = []
    all_projects = sorted(p for p in walk_files(head_tree, '*.csproj'))
    project_kinds, mods, pbs = detect_kinds(head_tree, targets, all_projects)
    for m in mods:
        r = rel(m, head_tree)
        targets.append({'name': 'mod' if r == '.' else f'mod:{r}', 'kind': 'se-mod', 'manifest': {}, 'runtimes': ['net48'],
                        'sources': sorted(walk_files(os.path.join(m, 'Data', 'Scripts'), '*.cs')),
                        'note': "SE mod scripts compiled as the game does (C# 6, game assemblies), and checked against the game's ModAPI whitelist by the game's own checker"})
    for p in pbs:
        targets.append({'name': f'pb:{os.path.splitext(os.path.basename(p))[0]}', 'kind': 'pb-script', 'manifest': {}, 'project': p,
                        'runtimes': ['net48'], 'note': "programmable block script compiled as the game does (C# 6, game assemblies), and checked against the game's Ingame whitelist by the game's own checker"})
    # Plugin kinds, mods and PB scripts get DeepScan; every .sln/.slnx/.csproj also gets the real csproj build.
    generic = not targets
    if not project_kinds:
        project_kinds = ['non-dotnet']
    record['projectKind'] = project_kinds[0]
    record['projectKinds'] = project_kinds
    for t in targets:
        if t.get('project'):
            t['parsed'] = parse_csproj(t['project'], head_tree)
    log(f'project kinds: {", ".join(project_kinds)}; targets: '
        + (', '.join(f'{t["name"]}[{"/".join(t["runtimes"])}]' for t in targets) or 'none (csproj build only)'))
    refs = Refs()
    kinds = {t['kind'] for t in targets}
    has_local_refs = bool(args.ds_dir) or bool(os.environ.get('DS64')) or bool(os.environ.get('MagnetarBin'))
    if kinds or has_local_refs or any(GAME_REF.search(read_text(p)) for p in all_projects):
        resolve_refs(args, refs, kinds or {'magnetar', 'torch'})

    analyzer_dirs = [p['dir'] for name, p in sorted(pack_info['packs'].items()) if name not in pack_info.get('dropped', {})]
    deeprules = os.path.join(CUSTOM_ANALYZERS, 'DeepRules.dll')
    if os.path.isdir(CUSTOM_ANALYZERS) and glob.glob(os.path.join(CUSTOM_ANALYZERS, '*.dll')):
        analyzer_dirs.append(CUSTOM_ANALYZERS)
    if not os.path.isfile(deeprules) and (targets or all_projects):
        gap('DeepRules.dll not present in tools/bin/analyzers: custom rules did not run')

    head_runs, base_runs = [], []
    target_runtimes = {t['name']: t['runtimes'] for t in targets}
    for t in targets:
        for rt in t['runtimes']:
            d = run_deepscan(deepscan, t, rt, head_tree, refs, analyzer_dirs, work, 'head', emit=args.emit)
            if d:
                head_runs.append(d)
            else:
                gap(f'{t["name"]}/{rt} was not compiled')
    if targets and not head_runs:
        raise SystemExit('every DeepScan run failed; see work/deepscan/*.log')
    if base_tree and not generic:
        base_targets, _ = discover_targets(base_tree, args.manifest, repo)
        for t in targets:
            if t['kind'] in ('se-mod', 'pb-script'):
                bt = dict(t)
                if t['kind'] == 'se-mod':
                    bt['sources'] = [os.path.join(base_tree, rel(f, head_tree)) for f in t['sources'] if os.path.isfile(os.path.join(base_tree, rel(f, head_tree)))]
                    bt = bt if bt['sources'] else None
                else:
                    bp = os.path.join(base_tree, rel(t['project'], head_tree))
                    bt = dict(t, project=bp) if os.path.isfile(bp) else None
            else:
                bt = next((b for b in base_targets if b['kind'] == t['kind'] and rel_manifest(b) == rel_manifest(t)), None)
            if not bt:
                log(f'base has no {t["name"]} target: everything there counts as NEW')
                continue
            bt['name'] = t['name']
            if bt.get('project'):
                bt['parsed'] = parse_csproj(bt['project'], base_tree)
            for rt in t['runtimes']:
                d = run_deepscan(deepscan, bt, rt, base_tree, refs, analyzer_dirs, work, 'base')
                if d:
                    base_runs.append(d)

    loaded, failed = {}, {}
    enabled_rules = set()
    for d in head_runs + base_runs:
        for a in d['analyzers']['loaded']:
            loaded[a['assembly']] = {'assembly': a['assembly'], 'pack': a['pack'], 'analyzers': a['analyzers'], 'rules': a['rules']}
        for f in d['analyzers']['failed']:
            failed[f['path']] = f['error']
        enabled_rules |= set(d['analyzers'].get('enabledRuleIds') or [])
        for g in d.get('gaps', []):
            gap(f'{d["_target"]}/{d["runtime"]}: {g}')
    if not head_runs and all_projects:
        enabled_rules = rule_probe(deepscan, analyzer_dirs, work)

    head_diags = merge_runs(head_runs)
    compiled = {f'{d["_target"]}/{d["runtime"]}': set(d.get('sources', [])) for d in head_runs}
    deep_compiled = set().union(*compiled.values()) if compiled else set()
    all_cs = {rel(f, head_tree) for f in walk_files(head_tree, '*.cs')}
    all_compiled = deep_compiled | all_cs
    mark_runtime_specific(head_diags, target_runtimes, compiled)

    # Suggestions of net10-only APIs on code that must also compile for net48 are not actionable.
    dual = {t['name'] for t in targets if 'net48' in t['runtimes'] and 'net10' in t['runtimes']}
    kept, dropped_api = [], 0
    for d in head_diags:
        if d['id'] in NET10_API_RULES and d.get('onlyOn') and all(o.endswith('/net10') and o.split('/')[0] in dual for o in d['onlyOn']) \
                and set(d['targets']) <= dual:
            dropped_api += 1
            continue
        kept.append(d)
    head_diags = kept
    # MA0182 on the types the compiler itself needs (POLYFILLS) is not a finding: they are used, by the compiler.
    polyfills = []
    head_diags = drop_compiler_polyfills(head_diags, head_tree, polyfills)

    arch, guarded =architecture(head_tree, head_runs, targets) if not generic else ([], 0)
    head_diags += arch
    whitelist, whitelist_diags, whitelist_compiles = whitelist_check(refs, targets, head_tree, work, head_diags)
    head_diags += whitelist_diags

    added, deleted, renames = ({}, {}, {})
    fixed = []
    if base_tree:
        added, deleted, renames = changed_lines(repo, args.base, args.head, head_tree)
        base_diags = drop_compiler_polyfills(merge_runs(base_runs), base_tree, []) if base_runs else []
        if base_runs:
            base_arch, _ = architecture(base_tree, base_runs, targets)
            base_diags += base_arch
        fixed = classify_base(head_diags, base_diags, renames)
    members, calls, entries, by_file, changed, affected, reached = compute_scope(head_runs, base_runs, added, deleted, renames)

    for d in head_diags:
        d['scope'] = diag_scope(d, added, changed, affected, members, by_file) if base_tree else 'no-base'
    bmembers, _, _, bby_file = member_index(base_runs) if base_runs else ({}, [], {}, {})
    for d in fixed:
        f = d.get('file')
        if f in deleted and set(range(d['line'], (d.get('endLine') or d['line']) + 1)) & deleted[f]:
            d['scope'] = 'in-diff-line'
        elif any(m['id'] in changed for m in members_at(bby_file, f, d.get('line') or 0)):
            d['scope'] = 'in-changed-member'
        elif any(m['id'] in affected for m in members_at(bby_file, f, d.get('line') or 0)):
            d['scope'] = 'in-affected-member'
        else:
            d['scope'] = 'elsewhere'

    # ClrHeapAllocationAnalyzer: only per-tick members and their direct callees.
    tick = {m for m, info in members.items() if info.get('perTick')}
    tick |= {c['to'] for c in calls if c['from'] in tick and c['kind'] in ('call', 'get', 'set', 'new', 'delegate', 'creates')}
    kept, dropped_heap = [], 0
    for d in head_diags + fixed:
        if d.get('tool') == 'ClrHeapAllocationAnalyzer':
            ids = [d.get('member')] + [m['id'] for m in members_at(by_file, d.get('file'), d.get('line') or 0)]
            if not any(i in tick for i in ids if i):
                dropped_heap += 1
                continue
        kept.append(d)
    diagnostics = kept

    ships_legacy = any('net48' in t['runtimes'] for t in targets)
    tests, coverage = run_tests(args, refs, head_tree, work, ships_legacy, changed, members)
    vulns = vulnerabilities(args, refs, head_tree, targets, manifests, work, all_projects)
    # Every project in the repo is built: plugin projects, tests, tools, samples (generic .NET is first class).
    cb, build_diags, build_suppressed = csproj_build(args, refs, head_tree, pack_info, work, all_projects)
    build_diags = drop_compiler_polyfills(build_diags, head_tree, polyfills)
    for d in build_diags:
        d['scope'] = diag_scope(d, added, changed, affected, members, by_file) if base_tree else 'no-base'
        d['status'] = None
        d['runtimes'] = [d['runtime']]
        d['seenOn'] = [f'csproj-build/{d["runtime"]}']
    # Files DeepScan compiled keep DeepScan's analyzer results (the csproj build's copies are only compared, and its
    # compiler warnings counted); every other file takes the csproj build's results, limited to the tools' rule set.
    in_rule_set = lambda d: (d.get('id') or '').startswith(('CS', 'DR')) or d.get('id') in enabled_rules or d.get('id') == 'RS0030'
    covered_build = [d for d in build_diags if d.get('file') in deep_compiled]
    uncovered_build = [d for d in build_diags if d.get('file') not in deep_compiled and in_rule_set(d) and not d['id'].startswith('HAA')]
    # ClrHeapAllocationAnalyzer: no member map outside DeepScan's files, so no per-tick members there: all dropped.
    dropped_heap += sum(1 for d in build_diags if d.get('file') not in deep_compiled and d['id'].startswith('HAA'))
    outside_rule_set = sum(1 for d in build_diags if d.get('file') not in deep_compiled and not in_rule_set(d))
    comparison = compare_builds(diagnostics, covered_build) if head_runs and cb.get('builds') else None
    diagnostics = diagnostics + uncovered_build
    review_only = [d for d in diagnostics if 'ReviewOnly' in (d.get('tags') or [])]
    diagnostics = [d for d in diagnostics if 'ReviewOnly' not in (d.get('tags') or [])]
    covered_build = [d for d in covered_build if 'ReviewOnly' not in (d.get('tags') or [])]
    not_covered += cb.get('notCovered', [])
    lang_counts, lang_files = language_inventory(head_tree)
    dotnet_checked = bool(head_runs) or any(b.get('status') == 'built' for b in cb.get('builds', []))
    languages, lang_diags, lang_gaps = run_language_checks(head_tree, lang_counts, lang_files, os.path.join(work, 'lint'), dotnet_checked)
    for g in lang_gaps:
        gap(g)
    for d in lang_diags:
        d.update(scope=diag_scope(d, added, changed, affected, members, by_file) if base_tree else 'no-base', status=None,
                 runtimes=[d['runtime']], seenOn=[d['tool']], helpUri=None, lineText=None, endLine=d.get('line'))
    diagnostics += lang_diags
    for lang, info in languages.items():
        if not info['checked']:
            gap(f'{lang}: {info["files"]} files not checked ({info["status"]})')
    emitted = [os.path.join(work, 'emit', f'{fs_name(t["name"])}-{rt}') for t in targets for rt in t['runtimes']] if args.emit else []
    optional = optional_tools(args, refs, head_tree, work, [e for e in emitted if os.path.isdir(e)]) if not args.skip_optional else {'all': 'not run: --skip-optional'}
    secrets_status, secrets = check_secrets(head_tree, os.path.join(work, 'lint'), repo, args.base, args.head)
    if secrets_status['status'] != 'ran':
        gap(f'secrets scan did not run: {secrets_status["status"]}')
    for d in secrets:
        d.update(scope='no-base' if not base_tree else diag_scope(d, added, changed, affected, members, by_file), status=None,
                 runtimes=['any'], seenOn=['gitleaks'], category='secrets', helpUri=None, lineText=None, endLine=d.get('line'))
    diagnostics += secrets
    release = release_checks(head_tree, manifests, targets, all_compiled)
    suppression_sites = find_suppressions(head_tree, all_compiled | {rel(p, head_tree) for p in all_projects}) + find_inline_suppressions(head_tree, lang_files)
    suppressed_diags = [{'id': d['id'], 'file': d.get('file'), 'line': d.get('line'), 'tool': d.get('tool'), 'suppression': d.get('suppression')}
                        for d in diagnostics if d.get('suppressed')] + \
                       [{'id': d['id'], 'file': d.get('file'), 'line': d.get('line'), 'tool': 'csproj-build', 'suppression': d.get('suppression')}
                        for d in build_suppressed]

    order = {s: i for i, s in enumerate(SCOPES)}
    st = {'NEW': 0, 'EXISTING': 1, 'FIXED': 2, None: 1}
    diagnostics.sort(key=lambda d: (order.get(d['scope'], 9), st.get(d.get('status'), 1), d['id'], d.get('file') or '', d.get('line') or 0))
    by_scope_status = collections.defaultdict(collections.Counter)
    for d in diagnostics:
        by_scope_status[d['scope']][d.get('status') or 'n/a'] += 1
    by_rule = collections.Counter(d['id'] for d in diagnostics if d.get('status') != 'FIXED')
    changed_list = []
    for mid, info in sorted(changed.items(), key=lambda kv: (members.get(kv[0], {}).get('file', ''), members.get(kv[0], {}).get('start', 0))):
        m = members.get(mid, {})
        changed_list.append({'id': mid, 'file': m.get('file'), 'start': m.get('start'), 'end': m.get('end'), 'kind': m.get('kind'),
                             'reason': info['reason'], 'entryPointsReached': reached.get(mid, []), 'isEntryPoint': mid in entries,
                             'coverage': coverage.get(mid)})
    affected_list = [{'id': mid, **info, 'file': members.get(mid, {}).get('file'), 'start': members.get(mid, {}).get('start'),
                      'isEntryPoint': mid in entries} for mid, info in sorted(affected.items(), key=lambda kv: kv[1]['distance'])]
    entry_list = [{'member': mid, 'reasons': sorted({e['reason'] for e in es}), 'detail': es[0]['detail'], 'file': es[0]['file'], 'line': es[0]['line']}
                  for mid, es in sorted(entries.items())]

    target_project = {t['name']: rel(t['project'], head_tree) if t.get('project') else t['manifest'].get('rel') for t in targets}
    compiles = [{'id': f'{d["_target"]}/{d["runtime"]}', 'project': target_project.get(d['_target']), 'tfm': d['runtime'], 'profile': d['_kind'],
                 'nullable': str(d['nullable']).lower(), 'langVersion': d['langVersion'], 'defines': d['defines'], 'unsafe': d.get('allowUnsafe'),
                 'fileCount': len(d['sources'])} for d in head_runs]
    compiles += whitelist_compiles + cb.get('compiles', [])
    deepscan_ids = {c['id'] for c in compiles}
    for d in diagnostics + covered_build + review_only:
        if not d.get('compiles') and d.get('seenOn'):
            d['compiles'] = [x for x in d['seenOn'] if x in deepscan_ids] or None
    compiled_runtimes = sorted({f'{d["_target"]}/{d["runtime"]}' for d in head_runs} |
                               {f'csproj:{b["project"]}/{b["framework"]}' for b in cb.get('builds', []) if b.get('status') == 'built'})
    report = dict(record)
    report.update({
        'base': base_sha,
        'headSha': head_sha,
        'generated': datetime.datetime.now().astimezone().isoformat(timespec='seconds'),
        'targets': [{'name': t['name'], 'kind': t['kind'], 'runtimes': t['runtimes'], 'manifest': t['manifest'].get('rel'),
                     'project': rel(t['project'], head_tree) if t.get('project') else None, 'why': t.get('why'), 'note': t.get('note')} for t in targets],
        'runtimes': compiled_runtimes,
        'compiles': compiles,
        'refs': refs.as_json(),
        'deepscan': [{'target': d['_target'], 'runtime': d['runtime'], 'profile': d['profile'], 'langVersion': d['langVersion'], 'defines': d['defines'],
                      'nullable': d['nullable'], 'warningLevel': d['warningLevel'], 'sources': len(d['sources']), 'references': len(d['references']),
                      'skippedReferences': d['skippedReferences'], 'compileErrors': d['compileErrors'], 'diagnostics': len(d['diagnostics']),
                      'timing': d['timing']} for d in head_runs],
        'analyzers': {'loaded': sorted(loaded.values(), key=lambda a: a['assembly']), 'failed': [{'path': k, 'error': v} for k, v in failed.items()],
                      'dropped': pack_info.get('dropped', {}), 'packs': {k: v['version'] for k, v in pack_info['packs'].items()},
                      'deepRules': os.path.isfile(deeprules), 'enabledRules': len(enabled_rules),
                      'offRules': off_rules},
        'counts': {'byScopeStatus': {k: dict(v) for k, v in by_scope_status.items()}, 'byRule': dict(by_rule.most_common()),
                   'total': len(diagnostics), 'runtimeSpecific': sum(1 for d in diagnostics if d.get('runtimeSpecific'))},
        'dropped': {'ClrHeapAllocationAnalyzer': dropped_heap, 'net10OnlyApiSuggestions': dropped_api, 'compilerPolyfills': len(polyfills)},
        'compilerPolyfills': polyfills,
        'architectureGuarded': guarded,
        'whitelist': whitelist,
        'diagnostics': diagnostics,
        'reviewOnly': review_only,
        'csprojBuildDiagnostics': covered_build,
        'csprojBuildOutsideRuleSet': outside_rule_set,
        'languages': languages,
        'secrets': secrets_status,
        'changedMembers': changed_list,
        'affectedMembers': affected_list,
        'entryPoints': entry_list,
        'callGraph': {'members': len(members), 'edges': len(calls)},
        'tests': tests,
        'coverage': coverage,
        'vulnerabilities': vulns,
        'csprojBuild': cb,
        'buildComparison': comparison,
        'release': release,
        'optional': optional,
        'suppressions': {'sites': suppression_sites, 'suppressedDiagnostics': suppressed_diags},
        'notCovered': not_covered,
        'gaps': GAPS,
    })
    verdict, issues, gate_counts = gate_verdict(report, True, enabled_rules, suppression_sites)
    report['finishedUtc'] = utc_now()
    report['verdict'] = verdict if args.gate else None
    report['gate'] = {'verdict': verdict, 'counts': gate_counts}
    if args.gate:
        exit_code = 0 if verdict == 'PASS' else 3
    else:
        exit_code = 2 if GAPS else 0
    report['exitCode'] = exit_code
    write_json(os.path.join(out, 'tools-report.json'), report)
    with open(os.path.join(out, 'tools-report.md'), 'w', encoding='utf-8') as f:
        f.write(md_report(report))
    root_uri = 'file:///' + repo.replace('\\', '/').lstrip('/') + '/'
    write_json(os.path.join(out, 'tools-report.sarif'), sarif_report(diagnostics + covered_build, root_uri,
                                                                    {'head': head_label, 'base': base_sha, 'toolsVersion': record['toolsVersion']}))
    gate_record = {k: report[k] for k in ('schema', 'kind', 'gateMode', 'nonce', 'repo', 'head', 'headTree', 'toolsVersion', 'options',
                                          'startedUtc', 'finishedUtc', 'fresh', 'projectKind', 'projectKinds', 'runtimes')}
    gate_record['languages'] = {k: {'files': v['files'], 'checked': v['checked'], 'by': v['by']} for k, v in languages.items()}
    gate_record['kind'] = 'gate'
    if 'contentSha256' in report:
        gate_record['contentSha256'] = report['contentSha256']
    gate_record.update({'verdict': verdict, 'counts': gate_counts, 'issues': issues[:200], 'gaps': GAPS, 'notCovered': not_covered,
                        'suppressions': suppression_sites, 'suppressedDiagnostics': suppressed_diags[:200]})
    gate_record['compiles'] = compiles  # additive optional field (schema 1): every compile and the settings it used
    if args.gate:
        write_json(os.path.join(out, 'gate.json'), gate_record)
        with open(os.path.join(out, 'gate.txt'), 'w', encoding='utf-8') as f:
            f.write(gate_text(gate_record))
    log(f'report: {os.path.join(out, "tools-report.md")} ({len(diagnostics)} diagnostics, {len(GAPS)} gaps, gate {verdict})')
    return exit_code


def rule_probe(deepscan, analyzer_dirs, work):
    """The rule ids deep.globalconfig turns on, from a DeepScan run over an empty source (for repos built only by csproj)."""
    d = os.path.join(work, 'rule-probe')
    os.makedirs(d, exist_ok=True)
    src = os.path.join(d, 'Empty.cs')
    with open(src, 'w', encoding='utf-8') as f:
        f.write('namespace Probe { internal static class Empty { } }\n')
    out = os.path.join(d, 'probe.json')
    run(['dotnet', deepscan, '--sources', src, '--runtime', 'net10', '--profile', 'plain', '--no-graph', '--globalconfig', GLOBALCONFIG,
         '--analyzers', *analyzer_dirs, '--out', out], timeout=900)
    try:
        return set(json.load(open(out, encoding='utf-8'))['analyzers']['enabledRuleIds'])
    except (OSError, ValueError, KeyError):
        gap('rule probe failed: the csproj build cannot be limited to the tools\' rule set')
        return set()


def rel_manifest(t):
    m = t.get('manifest') or {}
    return m.get('rel')


if __name__ == '__main__':
    sys.exit(main())
