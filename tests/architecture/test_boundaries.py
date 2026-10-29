"""Synthetic graphs verify the guard rejects regressions, not just today's source layout."""
import importlib.util
import os
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('architecture', Path(__file__).resolve().parents[2] / 'scripts/check_architecture.py')
architecture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(architecture)


class Boundaries(unittest.TestCase):
    def setUp(self):
        root = Path(__file__).resolve().parents[2]
        scratch = Path(os.environ.get('VN_PRIVATE_ROOT', root.parent / 'VndbCli.private')) / 'test-scratch'
        scratch.mkdir(parents=True, exist_ok=True)
        self.directory = tempfile.TemporaryDirectory(prefix='vn-architecture-', dir=scratch)
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        for name in architecture.ALLOWED:
            self.project(name)

    def project(self, name, refs=(), packages=()):
        folder = self.root / 'src' / f'Vndb.{name}'
        folder.mkdir(parents=True, exist_ok=True)
        nodes = ''.join(f'<ProjectReference Include="../Vndb.{ref}/Vndb.{ref}.csproj"/>' for ref in refs)
        nodes += ''.join(f'<PackageReference Include="{package}" Version="1.0"/>' for package in packages)
        (folder / f'Vndb.{name}.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>{nodes}</ItemGroup></Project>', encoding='utf-8')

    def test_target_graph_is_accepted(self):
        self.project('Local', ['Core', 'Contracts'])
        self.project('Contracts', ['Core'])
        self.assertEqual([], architecture.check(self.root))

    def test_reverse_and_entry_point_dependencies_are_rejected(self):
        self.project('Core', ['Contracts'])
        self.project('Desktop', ['Cli'])
        findings = architecture.check(self.root)
        self.assertTrue(any('Core -> Contracts' in item for item in findings))
        self.assertTrue(any('Desktop -> Cli' in item for item in findings))

    def test_client_cannot_acquire_sqlite_through_an_adapter(self):
        self.project('Cli', ['Local'])
        self.project('Local', packages=['Microsoft.Data.Sqlite'])
        self.assertIn('Cli: transitive SQLite package dependency', architecture.check(self.root))

    def test_web_cannot_load_local_execution(self):
        self.project('Web', ['Local'])
        self.assertIn('Web -> Local: forbidden project dependency', architecture.check(self.root))

    def test_core_cannot_acquire_platform_implementation(self):
        self.project('Core', packages=['Avalonia.Desktop'])
        (self.root / 'src/Vndb.Core/Native.cs').write_text('[LibraryImport("synthetic")]', encoding='utf-8')
        self.assertEqual(2, len(architecture.check(self.root)))

    def test_former_transition_edges_are_always_rejected(self):
        self.project('Local', ['Client'])
        self.project('Admin', ['Cli'])
        self.assertIn('Local -> Client: forbidden project dependency', architecture.check(self.root))
        self.assertIn('Admin -> Cli: forbidden project dependency', architecture.check(self.root))

    def test_core_cannot_hide_a_host_in_a_framework_reference(self):
        path = self.root / 'src/Vndb.Core/Vndb.Core.csproj'
        path.write_text('<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App"/></ItemGroup></Project>', encoding='utf-8')
        self.assertIn('Core: platform or host framework dependency', architecture.check(self.root))

    def test_runtime_manifests_reject_transitive_database_and_execution_assets(self):
        path = self.root / 'synthetic.deps.json'
        for project, packages, expected in [
            ('Cli', ['Vndb.Core', 'SQLitePCLRaw.core'], 'runtime SQLite'),
            ('Desktop', ['Microsoft.Data.Sqlite'], 'runtime SQLite'),
            ('Core', ['Avalonia.Base'], 'runtime platform or host'),
            ('Web', ['Vndb.Local'], 'runtime client or local-execution'),
            ('Admin', ['VndbCli'], 'runtime client or local-execution')]:
            with self.subTest(project=project):
                path.write_text(json.dumps({'libraries': {p + '/1.0': {} for p in packages}}), encoding='utf-8')
                self.assertTrue(any(expected in row for row in architecture.check_runtime(project, path)))
        path.write_text('{"libraries": {"Vndb.Core/1.0": {}}}', encoding='utf-8')
        self.assertEqual([], architecture.check_runtime('Core', path))
        path.write_text('{"libraries": {}}', encoding='utf-8')
        self.assertIn('Core: missing or invalid runtime dependencies', architecture.check_runtime('Core', path))


if __name__ == '__main__':
    unittest.main()
