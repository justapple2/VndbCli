"""Explicit publish member policy. Unknown/bounded-out content fails closed."""
import gzip
from pathlib import Path
import subprocess
from privacy_rules import name_issues, content_issues

TEXT_LIMIT = 32 * 1024 * 1024
TEXT_SUFFIXES = {'.json', '.txt', '.md', '.css', '.js', '.html', '.svg', '.xml', '.config', '.yaml', '.yml', '.sh', '.ps1', '.map'}
BINARY_HEADERS = {'.dll': (b'MZ',), '.exe': (b'MZ',), '.pdb': (b'BSJB', b'Microsoft C/C++'), '.so': (b'\x7fELF',),
    '.dylib': (b'\xcf\xfa\xed\xfe', b'\xfe\xed\xfa\xcf', b'\xca\xfe\xba\xbe'),
    '.png': (b'\x89PNG\r\n\x1a\n',), '.ico': (b'\x00\x00\x01\x00',), '.jpg': (b'\xff\xd8\xff',), '.jpeg': (b'\xff\xd8\xff',),
    '.gif': (b'GIF87a', b'GIF89a'), '.webp': (b'RIFF',), '.woff': (b'wOFF',), '.woff2': (b'wOF2',), '.ttf': (b'\x00\x01\x00\x00',), '.otf': (b'OTTO',)}


def state_free(name, path):
    relative = Path(name)
    suffix = relative.suffix.lower()
    # Only known framework binaries are allowed to contain this reserved word in their filename.
    ordinary_name = str(relative.with_name('framework' + suffix)) if relative.name.startswith('System.Private.') and suffix in {'.dll', '.pdb'} else name
    compressed = 'wwwroot' in relative.parts and suffix in {'.gz', '.br'} and relative.with_suffix('').suffix.lower() in TEXT_SUFFIXES
    checked_name = str(Path(ordinary_name).with_suffix('')) if compressed else ordinary_name
    if name_issues(checked_name) or any(p.lower() in {'logs', 'audit', 'locks', 'covers', 'cache', 'pending', 'receipts'} for p in relative.parts) or relative.name.lower() in {'service-instance-id', 'library_write_key'}:
        raise ValueError('Private/generated release member: ' + name)
    path = Path(path)
    if suffix in BINARY_HEADERS:
        with path.open('rb') as stream:
            if not stream.read(32).startswith(BINARY_HEADERS[suffix]):
                raise ValueError('Unrecognized release binary: ' + name)
        return  # Explicit binary exception, not a claim to scan embedded runtime data.
    if suffix == '' and relative.name in {'vndb', 'vndb-admin', 'Vndb.Web', 'Vndb.Desktop', 'createdump'}:
        with path.open('rb') as stream:
            if not stream.read(4).startswith((b'\x7fELF', b'MZ')): raise ValueError('Unrecognized apphost: ' + name)
        return
    if not compressed and suffix not in TEXT_SUFFIXES and relative.name not in {'Dockerfile', 'LICENSE', 'NOTICE'}:
        raise ValueError('Unclassified release member: ' + name)
    if path.stat().st_size > TEXT_LIMIT: raise ValueError('Release text exceeds explicit 32MiB scan bound: ' + name)
    if compressed:
        if suffix == '.gz':
            with gzip.open(path, 'rb') as stream: data = stream.read(TEXT_LIMIT + 1)
        else:
            # Node is already a development prerequisite. No optional codec silently bypasses scanning.
            script = "const z=require('node:zlib');let b=[];process.stdin.on('data',c=>b.push(c));process.stdin.on('end',()=>{try{process.stdout.write(z.brotliDecompressSync(Buffer.concat(b),{maxOutputLength:33554433}));}catch{process.exit(2);}});"
            try: result = subprocess.run(['node', '-e', script], input=path.read_bytes(), capture_output=True, timeout=30, check=False)
            except (OSError, subprocess.TimeoutExpired) as error: raise ValueError('Brotli scanning requires Node.js: ' + name) from error
            if result.returncode: raise ValueError('Invalid or oversized Brotli member: ' + name)
            data = result.stdout
    else:
        data = path.read_bytes()
    if len(data) > TEXT_LIMIT: raise ValueError('Expanded release text exceeds 32MiB scan bound: ' + name)
    try:
        text = data.decode('utf-16') if data.startswith((b'\xff\xfe', b'\xfe\xff')) else data.decode('utf-8-sig')
        if '\0' in text: raise UnicodeError()
    except UnicodeError as error: raise ValueError('Unrecognized release text encoding: ' + name) from error
    if content_issues(checked_name, text.encode('utf-8')):
        raise ValueError('Release text failed shared privacy rules: ' + name)
