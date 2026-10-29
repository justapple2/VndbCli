"""Check source-project boundaries, including transitive client database dependencies."""
import argparse
import json
from pathlib import Path
import xml.etree.ElementTree as ET

ALLOWED = {
    'Core': set(), 'Contracts': {'Core'}, 'Client': {'Core', 'Contracts'},
    'Local': {'Core', 'Contracts'}, 'Library': {'Core', 'Contracts'},
    'Cli': {'Core', 'Contracts', 'Client', 'Local'},
    'Desktop': {'Core', 'Contracts', 'Client', 'Local'},
    'Web': {'Core', 'Contracts', 'Library'}, 'Admin': {'Core', 'Contracts', 'Library'},
}
CORE_FORBIDDEN = ('Avalonia', 'Microsoft.AspNetCore', 'Microsoft.Data.Sqlite', 'Microsoft.Win32',
                  'LibraryImport(', 'DllImport(')


def check(root):
    findings, graph, packages = [], {}, {}
    for path in sorted((root / 'src').glob('*/*.csproj')):
        name = path.stem.removeprefix('Vndb.')
        if name not in ALLOWED:
            findings.append(f'{path.stem}: unreviewed source project')
            continue
        xml = ET.parse(path).getroot()
        refs = set()
        for node in xml.iter('ProjectReference'):
            target = (path.parent / node.attrib['Include'].replace('\\', '/')).resolve()
            dependency = target.stem.removeprefix('Vndb.')
            if target != (root / 'src' / f'Vndb.{dependency}' / f'Vndb.{dependency}.csproj').resolve() or not target.is_file():
                findings.append(f'{name}: project reference outside reviewed source graph')
            refs.add(dependency)
            if dependency not in ALLOWED[name]:
                findings.append(f'{name} -> {dependency}: forbidden project dependency')
        graph[name] = refs
        packages[name] = {node.attrib.get('Include', node.attrib.get('Update', '')) for node in xml.iter('PackageReference')}
    for missing in ALLOWED.keys() - graph.keys():
        findings.append(f'{missing}: missing source project')
    for name in ('Core', 'Contracts', 'Client', 'Local', 'Cli', 'Desktop'):
        pending, seen = [name], set()
        while pending:
            node = pending.pop()
            if node in seen:
                continue
            seen.add(node)
            if node in {'Library', 'Web', 'Admin'}:
                findings.append(f'{name}: transitive server dependency {node}')
            if any('sqlite' in package.lower() for package in packages.get(node, ())):
                findings.append(f'{name}: transitive SQLite package dependency')
            pending.extend(graph.get(node, ()))
    for name in ('Core', 'Contracts'):
        if name not in graph:
            continue
        xml = ET.parse(root / 'src' / f'Vndb.{name}' / f'Vndb.{name}.csproj').getroot()
        if xml.attrib.get('Sdk') != 'Microsoft.NET.Sdk' or next(xml.iter('FrameworkReference'), None) is not None:
            findings.append(f'{name}: platform or host framework dependency')
        for package in packages.get(name, ()):
            if package.startswith(('Avalonia', 'Microsoft.AspNetCore', 'Microsoft.Win32', 'System.Security.Cryptography.ProtectedData')):
                findings.append(f'{name}: platform or host package dependency')
    for path in (root / 'src' / 'Vndb.Core').rglob('*.cs'):
        if not {'obj', 'bin'} & set(path.parts) and any(token in path.read_text(encoding='utf-8-sig') for token in CORE_FORBIDDEN):
            findings.append(f'Core/{path.name}: platform or host implementation')
    return sorted(set(findings))


ASSEMBLIES = {name: {'Cli': 'vndb', 'Admin': 'vndb-admin'}.get(name, f'Vndb.{name}') for name in ALLOWED}


def check_runtime(project, path):
    """Inspect generated or published deps, including transitive package assets."""
    if project not in ALLOWED:
        return [f'{project}: unreviewed runtime project']
    try:
        libraries = json.loads(path.read_text(encoding='utf-8-sig'))['libraries']
        if not isinstance(libraries, dict) or not libraries:
            raise ValueError('Missing libraries')
    except (OSError, ValueError, KeyError, TypeError):
        return [f'{project}: missing or invalid runtime dependencies']
    findings = []
    for entry in libraries:
        dependency = entry.split('/', 1)[0].lower()
        module = dependency.removeprefix('vndb.')
        if project in {'Core', 'Contracts', 'Client', 'Local', 'Cli', 'Desktop'} and 'sqlite' in dependency:
            findings.append(f'{project}: runtime SQLite dependency')
        if project in {'Core', 'Contracts'} and dependency.startswith(('avalonia', 'microsoft.aspnetcore', 'microsoft.windowsdesktop', 'microsoft.win32', 'system.security.cryptography.protecteddata')):
            findings.append(f'{project}: runtime platform or host dependency')
        if project in {'Web', 'Admin'} and (module in {'local', 'client', 'desktop', 'cli'} or dependency == 'vndbcli' or dependency.startswith(('avalonia', 'system.security.cryptography.protecteddata'))):
            findings.append(f'{project}: runtime client or local-execution dependency')
    return sorted(set(findings))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build-root', type=Path, help='Also check all nine Release/net10.0 build dependency manifests.')
    parser.add_argument('--deps', action='append', default=[], metavar='PROJECT=FILE', help='Check a specific generated or published dependency manifest.')
    args = parser.parse_args()
    findings = check(Path(__file__).resolve().parent.parent)
    manifests = []
    if args.build_root:
        manifests.extend((name, args.build_root / f'Vndb.{name}/bin/Release/net10.0/{assembly}.deps.json') for name, assembly in ASSEMBLIES.items())
    for value in args.deps:
        project, separator, file = value.partition('=')
        if not separator:
            parser.error('--deps requires PROJECT=FILE')
        manifests.append((project, Path(file)))
    for project, path in manifests:
        findings.extend(check_runtime(project, path))
    for finding in findings:
        print(finding)
    print(f'Architecture R6: 9 source projects, {len(manifests)} runtime manifests, findings={len(findings)}; no transition exemptions.')
    return bool(findings)


if __name__ == '__main__':
    raise SystemExit(main())
