"""Inspect repository/staged blobs without printing secret values. This is a guard, not a credential vault."""
import argparse
import os
from pathlib import Path
import re
import subprocess
import sys
import stat

ROOT = Path(__file__).resolve().parent.parent
from privacy_rules import BLOCKED_PARTS, BLOCKED_NAMES, BLOCKED_SUFFIXES, name_issues, content_issues


def git(*arguments):
    return subprocess.check_output(['git', '-C', str(ROOT), *arguments])

def check(name, data):
    return name_issues(name) + ([(0, 'oversized-source-file')] if len(data) > 2 * 1024 * 1024 else []) + content_issues(name, data, source=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--staged', action='store_true', help='Read the index, not potentially different working files.')
    parser.add_argument('--all-files', action='store_true', help='Also inspect ignored files; skip Git metadata and refuse to follow reparse entries.')
    args = parser.parse_args()
    reparse = []
    if args.staged and args.all_files: parser.error('Choose staged or all-files')
    if args.all_files:
        blobs = []
        def walk(directory):
            for entry in os.scandir(directory):
                if directory == ROOT and entry.name == '.git': continue
                path = Path(entry.path); name = path.relative_to(ROOT).as_posix()
                if getattr(entry.stat(follow_symlinks=False), 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT or entry.is_symlink():
                    reparse.append((name, 0, 'reparse-entry')); continue
                if entry.is_dir(follow_symlinks=False): walk(path)
                else: blobs.append((name, path.read_bytes()))
        walk(ROOT)
    elif args.staged:
        names = [p.decode('utf-8') for p in git('diff', '--cached', '--name-only', '--diff-filter=ACMR', '-z').split(b'\0') if p]
        blobs = [(name, git('show', ':' + name)) for name in names]
    else:
        names = [p.decode('utf-8') for p in git('ls-files', '-z', '--cached', '--others', '--exclude-standard').split(b'\0') if p]
        blobs = [(name, (ROOT / name).read_bytes()) for name in sorted(set(names)) if (ROOT / name).is_file()]
    failed = reparse
    for name, data in blobs:
        for line, rule in check(name, data): failed.append((name, line, rule))
    for name, line, rule in failed: print(f'{name}:{line}: {rule}')
    print(f'Checked {len(blobs)} files; findings={len(failed)} (matched values suppressed).')
    return bool(failed)

if __name__ == '__main__':
    sys.exit(main())
