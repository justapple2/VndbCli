"""Seal fresh publish generations and verify exact contents before packaging. No user state is adopted."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import uuid

ROOT = Path(__file__).absolute().parent.parent
MANIFEST = 'release-manifest.json'
KINDS = {'cli': 'Cli', 'desktop': 'Desktop', 'runtime': None}


def ordinary(path):
    path = Path(os.path.abspath(path))
    for node in [path, *path.parents]:
        if node.is_symlink() or node.exists() and (node.lstat().st_file_attributes if os.name == 'nt' else 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise ValueError('Reparse entry refused')
    return path


def files(root):
    root = ordinary(root)
    result = {}
    def walk(folder):
        for item in sorted(folder.iterdir()):
            ordinary(item)
            if item.is_dir():
                walk(item)
            elif item.is_file():
                result[item.relative_to(root).as_posix()] = item
            else:
                raise ValueError('Non-ordinary release file')
    walk(root)
    return result


def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def state_free(name, path):
    from release_privacy import state_free as validate
    validate(name, path)


def source():
    commit = subprocess.check_output(['git', '-C', str(ROOT), 'rev-parse', 'HEAD'], text=True).strip()
    dirty = bool(subprocess.check_output(['git', '-C', str(ROOT), 'status', '--porcelain']))
    names = subprocess.check_output(['git', '-C', str(ROOT), 'ls-files', '--cached', '--others', '--exclude-standard', '-z']).decode().split('\0')
    sha = hashlib.sha256()
    for name in sorted(set(n for n in names if n)):
        path = ordinary(ROOT / name)
        if path.is_file():
            sha.update(name.encode() + b'\0' + digest(path).encode() + b'\n')
    return {'commit': commit, 'dirty': dirty, 'tree_sha256': sha.hexdigest()}


def write_new(path, value):
    with ordinary(path).open('x', encoding='utf-8') as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)
        stream.flush()
        os.fsync(stream.fileno())


def begin(target, kind, rid):
    target = ordinary(target)
    if target.exists():
        raise ValueError('Publish target already exists; choose a new generation')
    target.parent.mkdir(parents=True, exist_ok=True)
    # Fixed per-target reservation coordinates concurrent publishers, and remains as failure evidence.
    reservation = target.with_name('.' + target.name + '.publish-intent.json')
    stage = target.with_name('.' + target.name + '.staging-' + uuid.uuid4().hex)
    intent = {'format': 'vn-release-intent', 'target': str(target), 'stage': str(stage), 'kind': kind, 'rid': rid,
              'version': '0.5.0', 'api': 1, 'schema': 14, 'source': source(), 'build_mode': 'cross-published-runtime' if kind == 'runtime' else 'self-contained-client'}
    write_new(reservation, intent)
    stage.mkdir()
    return intent


def intent_for(stage):
    stage = ordinary(stage)
    name = stage.name.split('.staging-')[0].removeprefix('.')
    intent = json.loads(ordinary(stage.parent / ('.' + name + '.publish-intent.json')).read_text(encoding='utf-8'))
    if ordinary(intent['stage']) != stage or ordinary(intent['target']).parent != stage.parent:
        raise ValueError('Publish intent mismatch')
    return intent


def capture(stage, deps=()):
    stage = ordinary(stage)
    intent = intent_for(stage)
    content = files(stage)
    def inventory(suffix, prefix=''):
        names = ordinary(str(stage) + suffix).read_text(encoding='utf-8-sig').splitlines()
        result = set()
        for name in names:
            name = name.replace('\\', '/')
            if not name or Path(name).is_absolute() or '..' in Path(name).parts:
                raise ValueError('Invalid SDK publish member')
            result.add(prefix + name)
        return result
    expected = {'Dockerfile'} if intent['kind'] == 'runtime' else set()
    if intent['kind'] == 'runtime':
        for folder in ['web', 'cli', 'admin']:
            expected.update(inventory('.' + folder + '.msbuild-files', folder + '/'))
    else:
        expected.update(inventory('.msbuild-files'))
    if set(content) != expected:
        raise ValueError('Publish directory differs from actual SDK publish inventory')
    if not content or MANIFEST in content:
        raise ValueError('Empty or already sealed publish directory')
    for name, path in content.items():
        state_free(name, path)
    from check_architecture import check_runtime
    if intent['kind'] == 'runtime':
        deps = [(name, stage / folder / (assembly + '.deps.json')) for name, folder, assembly in [('Web', 'web', 'Vndb.Web'), ('Cli', 'cli', 'vndb'), ('Admin', 'admin', 'vndb-admin')]]
        if 'Dockerfile' not in content:
            raise ValueError('Incomplete runtime')
    else:
        if len(deps) != 1 or deps[0][0] != KINDS[intent['kind']]:
            raise ValueError('Exact RID dependency manifest required')
    evidence = {}
    for project, path in deps:
        path = ordinary(path)
        if check_runtime(project, path):
            raise ValueError('Runtime dependency boundary failed: ' + project)
        evidence[project] = digest(path)
    capture_path = stage.with_name(stage.name + '.publish-files.json')
    write_new(capture_path, {'files': {name: digest(path) for name, path in content.items()}, 'dependencies': evidence})


def finalize(stage):
    stage = ordinary(stage)
    intent = intent_for(stage)
    captured = json.loads(ordinary(stage.with_name(stage.name + '.publish-files.json')).read_text(encoding='utf-8'))
    actual = files(stage)
    if {name: digest(path) for name, path in actual.items()} != captured['files'] or source() != intent['source']:
        raise ValueError('Publish output or source changed after capture')
    names = ['LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md',
             *('licenses/' + name for name in files(ROOT / 'licenses'))]
    if intent['kind'] != 'runtime':
        names.extend(['README.md', *('docs/' + p.name for p in sorted((ROOT / 'docs').glob('*.md'))),
                      *('assets/screenshots/' + name for name in files(ROOT / 'assets/screenshots'))])
    for name in names:
        origin = ordinary(ROOT / name)
        state_free(name, origin)
        destination = ordinary(stage / name)
        destination.parent.mkdir(parents=True, exist_ok=True)
        with destination.open('xb') as output:
            output.write(origin.read_bytes())
    content = files(stage)
    for name, path in content.items():
        state_free(name, path)
    sealed = {k: v for k, v in intent.items() if k not in {'stage', 'target'}}
    sealed['format'] = 'vn-release-v1'
    sealed['files'] = {name: digest(path) for name, path in content.items()}
    sealed['dependency_manifests'] = captured['dependencies']
    write_new(stage / MANIFEST, sealed)
    target = ordinary(intent['target'])
    if target.exists():
        raise ValueError('Publish target appeared; staging retained')
    stage.rename(target)
    verify(target, intent['kind'])
    return {'target': str(target), 'files': len(content), 'source': sealed['source']}


def verify(root, expected_kind=None):
    root = ordinary(root)
    manifest = json.loads(ordinary(root / MANIFEST).read_text(encoding='utf-8'))
    if manifest.get('format') != 'vn-release-v1' or expected_kind and manifest.get('kind') != expected_kind or manifest.get('version') != '0.5.0' or manifest.get('schema') != 14 or manifest.get('api') != 1:
        raise ValueError('Unknown or mismatched sealed release')
    content = files(root)
    content.pop(MANIFEST, None)
    for name, path in content.items():
        state_free(name, path)
    if {name: digest(path) for name, path in content.items()} != manifest['files']:
        raise ValueError('Sealed file set or bytes changed')
    if manifest['kind'] == 'runtime':
        from check_architecture import check_runtime
        for project, folder, assembly in [('Web', 'web', 'Vndb.Web'), ('Cli', 'cli', 'vndb'), ('Admin', 'admin', 'vndb-admin')]:
            path = root / folder / (assembly + '.deps.json')
            if check_runtime(project, path) or digest(path) != manifest['dependency_manifests'][project]:
                raise ValueError('Sealed runtime dependencies changed')
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['begin', 'capture', 'finalize', 'verify'])
    parser.add_argument('path', type=Path)
    parser.add_argument('--kind', choices=KINDS)
    parser.add_argument('--rid')
    parser.add_argument('--deps', action='append', default=[])
    args = parser.parse_args()
    if args.action == 'begin':
        if not args.kind or not args.rid:
            parser.error('begin requires kind and RID')
        result = begin(args.path, args.kind, args.rid)
    elif args.action == 'capture':
        capture(args.path, [(v.split('=', 1)[0], Path(v.split('=', 1)[1])) for v in args.deps]); result = {'captured': True}
    elif args.action == 'finalize':
        result = finalize(args.path)
    else:
        result = {'verified': True, 'kind': verify(args.path, args.kind)['kind']}
    print(json.dumps(result))


if __name__ == '__main__':
    main()
