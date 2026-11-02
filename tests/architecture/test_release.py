import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import shutil
import unittest
import gzip
from unittest.mock import patch

ROOT = Path(__file__).absolute().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
import release_guard as release
import release_privacy
import package_release


class ReleaseTests(unittest.TestCase):
    def test_new_local_index_and_lazy_details_are_blocked_from_release(self):
        from privacy_rules import name_issues
        for name in ['web/local-library.v2.json', 'web/local-metadata/synthetic.json', 'cli/item-details/synthetic.json',
                     'desktop/observation-settings.json', 'desktop/local-observations.json', 'desktop/local-resource-intents/synthetic.json',
                     'desktop/search-history-lookup.json', 'desktop/search-history-catalog.json', 'desktop/catalog-producers.json']:
            with self.subTest(name=name):
                self.assertTrue(name_issues(name))

    def setUp(self):
        scratch = ROOT.parent / 'VndbCli.private' / 'test-scratch'
        scratch.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=scratch)
        self.root = Path(self.temp.name)
        self.source = patch.object(release, 'source', return_value={'commit': 'synthetic', 'dirty': False, 'tree_sha256': 'test'})
        self.source.start()

    def tearDown(self):
        self.source.stop()
        self.temp.cleanup()

    def stage(self, target='output'):
        intent = release.begin(self.root / target, 'runtime', 'linux-x64')
        stage = Path(intent['stage'])
        for folder, name in [('web', 'Vndb.Web'), ('cli', 'vndb'), ('admin', 'vndb-admin')]:
            (stage / folder).mkdir()
            (stage / folder / (name + '.deps.json')).write_text('{"libraries":{"Vndb.Core/0.5.0":{}}}')
            Path(str(stage) + '.' + folder + '.msbuild-files').write_text(name + '.deps.json\n', encoding='utf-8')
        (stage / 'Dockerfile').write_text('FROM runtime')
        return stage

    def test_existing_empty_target_and_interrupted_reservation_refuse_overwrite(self):
        target = self.root / 'old'
        target.mkdir()
        with self.assertRaises(ValueError): release.begin(target, 'cli', 'win-x64')
        release.begin(self.root / 'next', 'cli', 'win-x64')
        with self.assertRaises(FileExistsError): release.begin(self.root / 'next', 'cli', 'win-x64')

    def test_exact_publish_capture_and_sealed_file_set(self):
        stage = self.stage()
        release.capture(stage)
        release.finalize(stage)
        final = self.root / 'output'
        manifest = release.verify(final, 'runtime')
        self.assertEqual('linux-x64', manifest['rid'])
        self.assertTrue({'LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md', 'licenses/dependencies.json'} <= manifest['files'].keys())
        hashes = release.digest(final / release.MANIFEST)
        (final / 'extra.txt').write_text('extra')
        with self.assertRaises(ValueError): release.verify(final)
        self.assertEqual(hashes, release.digest(final / release.MANIFEST))

    def test_mutation_between_capture_and_seal_is_not_released(self):
        stage = self.stage()
        release.capture(stage)
        (stage / 'Dockerfile').write_text('changed')
        with self.assertRaises(ValueError): release.finalize(stage)
        self.assertFalse((self.root / 'output').exists())

    def test_client_documentation_includes_licenses_and_referenced_demo_images(self):
        stage = Path(release.begin(self.root / 'client', 'cli', 'win-x64')['stage'])
        deps = stage / 'vndb.deps.json'
        deps.write_text('{"libraries":{"Vndb.Core/0.5.0":{}}}')
        Path(str(stage) + '.msbuild-files').write_text('vndb.deps.json\n', encoding='utf-8')
        release.capture(stage, [('Cli', deps)])
        release.finalize(stage)
        manifest = release.verify(self.root / 'client', 'cli')
        self.assertTrue({'LICENSE', 'NOTICE', 'licenses/dependencies.json', 'README.md',
                         'assets/screenshots/desktop-cards-light.png', 'assets/screenshots/desktop-list-dark.png',
                         'assets/screenshots/public-catalog.png'} <= manifest['files'].keys())
        self.assertNotIn('AGENTS.md', manifest['files'])
        self.assertNotIn('docs/WORKLOG.md', manifest['files'])

    def test_private_state_and_dependency_boundaries_rejected(self):
        stage = self.stage()
        (stage / 'library.db').write_bytes(b'synthetic')
        with self.assertRaises(ValueError): release.capture(stage)
        (stage / 'library.db').unlink()
        (stage / 'cli' / 'vndb.deps.json').write_text('{"libraries":{"Microsoft.Data.Sqlite/10.0":{}}}')
        with self.assertRaises(ValueError): release.capture(stage)

    def test_linked_directory_is_rejected_before_enumeration(self):
        stage = self.stage()
        outside = self.root / 'outside'
        outside.mkdir()
        if os.name == 'nt':
            subprocess.run(['cmd', '/c', 'mklink', '/J', str(stage / 'linked'), str(outside)], check=True, capture_output=True)
        else:
            (stage / 'linked').symlink_to(outside, target_is_directory=True)
        try:
            with self.assertRaises(ValueError): release.files(stage)
        finally:
            if os.name == 'nt': os.rmdir(stage / 'linked')
            else: (stage / 'linked').unlink()

    def test_target_appearing_at_finalize_is_preserved(self):
        stage = self.stage()
        release.capture(stage)
        target = self.root / 'output'
        target.mkdir()
        (target / 'previous.txt').write_text('untouched')
        with self.assertRaises(ValueError): release.finalize(stage)
        self.assertEqual('untouched', (target / 'previous.txt').read_text())

    def test_packaging_requires_sealed_bytes_and_preserves_existing_archives(self):
        stage = self.stage(); release.capture(stage); release.finalize(stage)
        published = self.root / 'releases' / 'generation'
        shutil.copytree(self.root / 'output', published)
        output = self.root / 'package.tar.gz'
        arguments = ['package_release.py', 'runtime', str(output), '--runtime-root', str(published)]
        with patch.dict(os.environ, {'VN_PRIVATE_ROOT': str(self.root)}), patch.object(sys, 'argv', arguments):
            package_release.main()
            original = release.digest(output)
            with self.assertRaises(ValueError): package_release.main()
            self.assertEqual(original, release.digest(output))
            (published / 'personal.json').write_text('{}')
            with patch.object(sys, 'argv', [*arguments[:2], str(self.root / 'dirty.tar.gz'), *arguments[3:]]):
                with self.assertRaises(ValueError): package_release.main()
            self.assertFalse((self.root / 'dirty.tar.gz').exists())

    def test_sdk_declared_private_names_and_large_credentials_cannot_be_captured(self):
        cases = {'library.db-wal': b'synthetic', 'library.sqlite-shm': b'synthetic', '.env': b'synthetic', 'KNOWN_HOSTS': b'synthetic',
                 'settings.txt': b'{"password":"synthetic-test-secret"}',
                 'large.json': b' ' * (2 * 1024 * 1024 + 1) + b'{"password":"synthetic-test-secret"}'}
        for index, (name, data) in enumerate(cases.items()):
            with self.subTest(name=name):
                stage = self.stage('bad-' + str(index)); (stage / 'web' / name).write_bytes(data)
                with Path(str(stage) + '.web.msbuild-files').open('a', encoding='utf-8') as stream: stream.write(name + '\n')
                with self.assertRaises(ValueError): release.capture(stage)
                self.assertFalse((self.root / ('bad-' + str(index))).exists())
                self.assertTrue((stage / 'web' / name).exists())

    def test_verify_and_packager_recheck_even_a_matching_poisoned_manifest(self):
        stage = self.stage(); release.capture(stage); release.finalize(stage)
        root = self.root / 'releases' / 'poisoned'; shutil.copytree(self.root / 'output', root)
        old = self.root / 'older.tar.gz'; old.write_bytes(b'synthetic old archive'); previous = release.digest(old)
        poison = root / 'web' / 'settings.txt'; poison.write_bytes(b'{"password":"synthetic-test-secret"}')
        manifest = json.loads((root / release.MANIFEST).read_text()); manifest['files']['web/settings.txt'] = release.digest(poison)
        (root / release.MANIFEST).write_text(json.dumps(manifest), encoding='utf-8')
        with self.assertRaises(ValueError): release.verify(root)
        output = self.root / 'new.tar.gz'
        with patch.dict(os.environ, {'VN_PRIVATE_ROOT': str(self.root)}), patch.object(sys, 'argv', ['package_release.py', 'runtime', str(output), '--runtime-root', str(root)]):
            with self.assertRaises(ValueError): package_release.main()
        self.assertFalse(output.exists()); self.assertEqual(previous, release.digest(old))

    def test_static_compression_scans_expanded_text_and_rejects_unclassified_members(self):
        safe = self.root / 'safe.gz'; safe.write_bytes(gzip.compress(b'body{color:var(--ink)}'))
        release.state_free('web/wwwroot/app.css.gz', safe)
        safe.write_bytes(gzip.compress(b'{"password":"synthetic-test-secret"}'))
        with self.assertRaises(ValueError): release.state_free('web/wwwroot/app.json.gz', safe)
        for name in ['web/settings.gz', 'web/unclassified.bin', 'web/fake.dll']:
            with self.subTest(name=name), self.assertRaises(ValueError): release.state_free(name, safe)

    def test_scan_bound_and_utf16_credentials_fail_closed(self):
        data = self.root / 'text'; data.write_bytes(b' ' * (release_privacy.TEXT_LIMIT + 1))
        with self.assertRaises(ValueError): release.state_free('web/large.txt', data)
        data.write_bytes('{"password":"synthetic-test-secret"}'.encode('utf-16'))
        with self.assertRaises(ValueError): release.state_free('web/encoded.txt', data)
        data.write_bytes(gzip.compress(b' ' * (release_privacy.TEXT_LIMIT + 1)))
        with self.assertRaises(ValueError): release.state_free('web/wwwroot/large.json.gz', data)

    def test_brotli_is_scanned_and_missing_codec_is_not_a_pass(self):
        script = "process.stdout.write(require('node:zlib').brotliCompressSync(Buffer.from('{\\\"password\\\":\\\"synthetic-test-secret\\\"}')));"
        result = subprocess.run(['node', '-e', script], capture_output=True, check=True)
        data = self.root / 'compressed'; data.write_bytes(result.stdout)
        with self.assertRaises(ValueError): release.state_free('web/wwwroot/app.json.br', data)
        with patch.object(release_privacy.subprocess, 'run', side_effect=FileNotFoundError()), self.assertRaises(ValueError): release.state_free('web/wwwroot/app.json.br', data)


if __name__ == '__main__': unittest.main()
