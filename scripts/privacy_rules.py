"""Shared filename and content rules; findings never contain matched values."""
from pathlib import Path
import re

BLOCKED_PARTS = {'artifacts', 'tmp', 'bin', 'obj', 'testresults', 'secrets', 'backups', 'data', '__pycache__', '.vndb',
    'client-state', 'query-cache', 'cover-cache', 'pending-sync', 'archive-runs', 'nas-audit', 'test-scratch', 'build-logs', 'servers', 'vndblocal'}
BLOCKED_NAMES = {'client.json', 'device.json', 'desktop-settings.json', 'launch-profiles.json', 'credential.bin', 'active-session.json', 'session.lock'}
BLOCKED_PARTS.update({'play-activity', 'local-checks', 'presence-receipts', 'presence-sent', 'organization-state', 'legacy-records'})
BLOCKED_NAMES.update({'local-library.json', 'credential.json', 'presence-pending.json', 'local-presence-seed.json', 'launch-selection-needed.json', 'legacy-migration.json', 'local-data-migration.json', 'local-data-verification.json', 'local-mapping-corrections.json'})
BLOCKED_PARTS.add('registry-repairs')
BLOCKED_NAMES.update({'shelf-preferences.json', 'launch-history.json'})
BLOCKED_NAMES.update({'search-history-lookup.json', 'search-history-catalog.json'})
BLOCKED_NAMES.add('catalog-producers.json')
BLOCKED_PARTS.add('personal-edits')
BLOCKED_PARTS.add('local-registrations')
BLOCKED_PARTS.update({'presence-operations', 'presence-conflicts', 'presence-recovery', 'presence-recovered', 'presence-rejected'})
BLOCKED_PARTS.update({'metadata-operations', 'query-migrations'})
BLOCKED_PARTS.add('item-plays')
BLOCKED_PARTS.update({'local-metadata', 'item-details'})
BLOCKED_NAMES.add('local-library.v2.json')
BLOCKED_NAMES.update({'observation-settings.json', 'local-observations.json'})
BLOCKED_PARTS.add('local-resource-intents')
BLOCKED_NAMES.add('query-scope.json')
BLOCKED_NAMES.add('ui-preferences.json')
BLOCKED_NAMES.add('startup-diagnostics.json')
BLOCKED_NAMES.update({'personal-organization.json', 'local-resources.json'})
BLOCKED_PARTS.update({'personal-organization-receipts', 'personal-organization-conflicts'})
BLOCKED_SUFFIXES = {'.db', '.sqlite', '.sqlite3', '.xls', '.xlsx', '.binlog', '.trx', '.pem', '.key', '.pfx', '.p12', '.pyc', '.zip', '.7z', '.gz', '.tar', '.reg'}
PATTERNS = {
    'private-key': re.compile(rb'-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'),
    'access-token': re.compile(rb'(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|sk-(?:proj-)?[A-Za-z0-9_-]{30,})'),
    'private-address': re.compile(rb'\b192\.168\.\d{1,3}\.\d{1,3}\b|\bDESKTOP-[A-Z0-9]{5,}\b|[A-Za-z]:[\\/]Users[\\/](?!Public\b|Default\b)[^\\/\s"\']+'),
}
ASSIGNMENT = re.compile(rb'''(?i)(?:["'])(?:password|passwd|api[_-]?key|access[_-]?token|client[_-]?secret|write[_-]?key|Library:WriteKey|Library__WriteKey)["']\s*[:,=]\s*["']([^"'\r\n]{6,160})["']''')
BARE_ASSIGNMENT = re.compile(rb'''(?i)\b(?:password|passwd|api[_-]?key|access[_-]?token|client[_-]?secret|write[_-]?key)\s*=\s*["']([^"'\r\n]{6,160})["']''')

def name_issues(name):
    issues = []
    path = Path(name)
    if any(p.lower() in BLOCKED_PARTS or 'private' in p.lower() for p in path.parts) or path.name.lower() in BLOCKED_NAMES or path.suffix.lower() in BLOCKED_SUFFIXES or path.suffix.lower().startswith(('.db-', '.sqlite')) or path.name.lower().startswith('known_hosts') or path.name.lower().startswith('.env') and path.name != '.env.example':
        issues.append((0, 'private-or-generated-file'))
    return issues

def content_issues(name, data, *, source=False):
    issues = []
    for rule, pattern in PATTERNS.items():
        for match in pattern.finditer(data): issues.append((data[:match.start()].count(b'\n') + 1, rule))
    for match in [*ASSIGNMENT.finditer(data), *BARE_ASSIGNMENT.finditer(data)]:
        value = match.group(1)
        # Explicit synthetic constants only; real-looking values in tests still fail.
        synthetic = source and name.startswith('tests/') and (b'test' in value.lower() or value in [b'private-key', b'm1-key', b'correct-key', b'configured-key'])
        placeholder = value.startswith((b'${', b'<', b'YOUR_', b'CHANGE_'))
        if not synthetic and not placeholder: issues.append((data[:match.start()].count(b'\n') + 1, 'literal-credential'))
    return issues
