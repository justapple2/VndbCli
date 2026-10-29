"""Package a published runtime or Git-visible sources, excluding private/generated material."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import uuid
import release_guard as guard

ROOT = Path(__file__).resolve().parent.parent
sys.dont_write_bytecode = True

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('kind', choices=['source', 'runtime', 'cli', 'desktop'])
    parser.add_argument('output', type=Path)
    parser.add_argument('--runtime-root', '--release-root', dest='runtime_root', type=Path)
    args = parser.parse_args()
    args.output = guard.ordinary(args.output)
    receipt_path = args.output.with_suffix(args.output.suffix + '.json')
    if args.output.exists() or receipt_path.exists(): raise ValueError('Archive/receipt already exists; choose a new output path')
    release = None
    if args.kind == 'source':
        from check_repository import check
        names = subprocess.check_output(['git', '-C', str(ROOT), 'ls-files', '--cached', '--others', '--exclude-standard', '-z']).decode().split('\0')
        files = sorted({guard.ordinary(ROOT / name) for name in names if name and (ROOT / name).is_file()})
        base = ROOT
        if any(check(file.relative_to(ROOT).as_posix(), file.read_bytes()) for file in files): raise ValueError('Repository content check failed')
    else:
        if args.runtime_root is None: raise ValueError('--runtime-root is required')
        base = guard.ordinary(args.runtime_root)
        private = Path(os.environ.get('VN_PRIVATE_ROOT', str(ROOT.parent / (ROOT.name + '.private')))).resolve()
        if not (base.is_relative_to(ROOT / 'artifacts') or base.is_relative_to(private / 'releases')): raise ValueError('Runtime must be inside configured private releases/ or ignored artifacts/')
        release = guard.verify(base, args.kind)
        files = sorted(guard.files(base).values())
    if args.output.is_relative_to(base): raise ValueError('Archive cannot be inside packaged content')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    manifest = {p.relative_to(base).as_posix(): guard.digest(p) for p in files}
    temporary = args.output.with_name('.' + args.output.name + '.partial-' + uuid.uuid4().hex)
    try:
        with tarfile.open(temporary, 'x:gz') as archive:
            for file in files:
                guard.ordinary(file)
                archive.add(file, arcname=file.relative_to(base).as_posix(), recursive=False)
        if manifest != {p.relative_to(base).as_posix(): guard.digest(p) for p in files}:
            raise ValueError('Content changed during packaging')
        if release: guard.verify(base, args.kind)
        # No overwrite, including another publisher that finishes while tar is running.
        if os.name == 'nt': os.rename(temporary, args.output)  # Windows rename fails if the destination exists.
        else: os.link(temporary, args.output)
    finally:
        if temporary.exists(): temporary.unlink()
    receipt = {'archive': args.output.name, 'files': len(files), 'sha256': guard.digest(args.output), 'manifest': manifest,
               'release': {k: release[k] for k in ['version', 'api', 'schema', 'rid', 'source', 'build_mode']} if release else {'build_mode': 'source', 'source': guard.source()}}
    guard.write_new(receipt_path, receipt)
    print(json.dumps({k:v for k,v in receipt.items() if k != 'manifest'}))

if __name__ == '__main__': main()
