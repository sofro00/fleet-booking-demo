"""Offline publication checks. Reports locations only; never prints matching values."""
from __future__ import annotations

import argparse
import contextlib
import hashlib
import io
import ipaddress
import json
import os
from pathlib import Path, PurePosixPath
import re
import sys
import tempfile

ROOT = Path(__file__).resolve().parent.parent
MANIFEST = 'PUBLIC_FILES.txt'
LOCAL_OUTPUTS = {'.git', '.local', '.vs', '.vscode', 'bin', 'obj', 'TestResults', 'artifacts', '__pycache__'}
TEXT_EXTENSIONS = {'.cs', '.csproj', '.xaml', '.sln', '.slnx', '.json', '.md', '.ps1', '.py', '.cmd', '.txt', '.yml', '.yaml'}
REVIEWED_IMAGES = {'docs/screenshots/vehicles.png'}
EMAIL = re.compile(r'[A-Za-z0-9._%+\-]+@([A-Za-z0-9.\-]+\.[A-Za-z]{2,})')
IP = re.compile(r'(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])')
ORIGINAL_BRAND = re.compile(r'\b' + 'b' + r'ds(?:vozila|\.me|_|vehicle|backuptest)|sport\s*vision', re.I)
TOKEN_PATTERNS = [
    re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----'),
    re.compile(r'\bgh[pousr]_[A-Za-z0-9]{30,}\b'),
    re.compile(r'\bgithub_pat_[A-Za-z0-9_]{30,}\b'),
    re.compile(r'\bAKIA[A-Z0-9]{16}\b'),
    re.compile(r'\bxox[baprs]-[A-Za-z0-9-]{20,}\b'),
    re.compile(r'\beyJ[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{12,}\b'),
]
SECRET_ASSIGNMENT = re.compile(r'(?i)(?:["\[]?(?:password|secret|token|apikey|api_key)["\]]?\s*[:=]\s*["\'])([^"\'\r\n]{8,})')


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def load_manifest(root: Path = ROOT) -> dict[str, str]:
    result = {}
    for line in (root / MANIFEST).read_text(encoding='utf-8').splitlines():
        if not line or line.startswith('#'):
            continue
        digest, name = line.split('  ', 1)
        rel = PurePosixPath(name)
        if (not re.fullmatch(r'[a-f0-9]{64}', digest) or rel.is_absolute() or
                '..' in rel.parts or '\\' in name or ':' in name or name in result or
                name == MANIFEST or LOCAL_OUTPUTS.intersection(rel.parts)):
            raise ValueError('Invalid or unsafe manifest entry.')
        result[name] = digest
    if not result:
        raise ValueError('The publication manifest is empty.')
    return result


def source_files(root: Path):
    for current, directories, files in os.walk(root, followlinks=False):
        for name in list(directories):
            path = Path(current) / name
            if path.is_symlink() or getattr(path, 'is_junction', lambda: False)():
                raise ValueError('Linked directory is not allowed in the demo tree.')
            if name in LOCAL_OUTPUTS:
                directories.remove(name)
        for name in files:
            path = Path(current) / name
            if path.is_symlink():
                raise ValueError('Linked file is not allowed in the demo tree.')
            yield path


def scan_text(name: str, text: str) -> list[tuple[int, str]]:
    findings = []
    for number, line in enumerate(text.splitlines(), 1):
        if ORIGINAL_BRAND.search(line):
            findings.append((number, 'original-brand'))
        for match in EMAIL.finditer(line):
            domain = match.group(1).lower()
            if domain not in {'example.com', 'example.org', 'example.net'} and not domain.endswith('.invalid'):
                findings.append((number, 'non-example-email'))
        for match in IP.finditer(line):
            try:
                address = ipaddress.ip_address(match.group())
            except ValueError:
                continue
            private_networks = ('10.' + '0.0.0/8', '172.' + '16.0.0/12', '192.' + '168.0.0/16', '169.' + '254.0.0/16')
            if any(address in ipaddress.ip_network(network) for network in private_networks):
                findings.append((number, 'private-network-address'))
        if any(pattern.search(line) for pattern in TOKEN_PATTERNS):
            findings.append((number, 'secret-format'))
        # Test fixtures intentionally use local, fictional credentials.
        if not name.startswith('tests/') and name != 'scripts/verify_public.py' and SECRET_ASSIGNMENT.search(line):
            findings.append((number, 'literal-secret-assignment'))
    return findings


def verify(root: Path = ROOT) -> dict[str, str]:
    manifest = load_manifest(root)
    found = {path.relative_to(root).as_posix(): path for path in source_files(root)}
    issues: list[tuple[str, int, str]] = []
    for name in sorted(found.keys() - manifest.keys() - {MANIFEST}):
        issues.append((name, 0, 'unlisted-file'))
    for name, digest in manifest.items():
        if name not in found:
            issues.append((name, 0, 'missing-file'))
            continue
        path = found[name]
        data = path.read_bytes()
        if sha256(data) != digest:
            issues.append((name, 0, 'checksum-changed-review-required'))
        if name in REVIEWED_IMAGES:
            if not data.startswith(b'\x89PNG\r\n\x1a\n'):
                issues.append((name, 0, 'invalid-reviewed-image'))
            continue
        if path.suffix not in TEXT_EXTENSIONS and name not in {'.gitignore', '.gitattributes'}:
            issues.append((name, 0, 'unreviewed-file-format'))
            continue
        text = data.decode('utf-8-sig')
        issues.extend((name, number, rule) for number, rule in scan_text(name, text))
        if path.name == 'appsettings.json':
            config = json.loads(text)
            if 'Client' in name:
                if config != {'ApiBaseUrl': 'https://localhost:58443/'}:
                    issues.append((name, 0, 'unexpected-client-config'))
            else:
                expected = r'Server=.\SQLEXPRESS;Database=FleetBookingDemo;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=10'
                if (config.get('ConnectionStrings') != {'FleetBooking': expected} or
                        set(config.get('Jwt', {})) != {'Issuer', 'Audience'} or
                        config.get('Backup') != {'Enabled': False} or
                        config.get('Demo') != {'Seed': True} or config.get('Database') != {'AutoMigrate': True}):
                    issues.append((name, 0, 'unexpected-api-config'))
    if issues:
        for name, number, rule in sorted(set(issues)):
            print(f'{name}:{number}: {rule}')
        raise ValueError(f'Publication check failed: {len(set(issues))} finding(s). Values are redacted.')
    print(f'Publication check passed: {len(manifest)} files; checksums, paths, text and demo configuration checked.')
    return manifest


def self_test():
    assert scan_text('src/example.cs', 'ana@' + 'company.example')
    assert scan_text('src/example.cs', '10.' + '42.1.2')
    assert scan_text('src/example.cs', 'ghp_' + 'x' * 36)
    assert scan_text('src/example.cs', '-----BEGIN ' + 'PRIVATE KEY-----')
    assert not scan_text('README.md', 'admin@example.com https://localhost:58443/')
    assert not scan_text('tests/example.cs', 'ana@test.invalid 127.0.0.1')
    local = ROOT / '.local'
    local.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='publication-check-', dir=local) as directory:
        root = Path(directory)
        data = b'A synthetic demo.\n'
        (root / 'README.md').write_bytes(data)
        (root / MANIFEST).write_text(sha256(data) + '  README.md\n', encoding='utf-8')
        def expect_failure():
            try:
                with contextlib.redirect_stdout(io.StringIO()):
                    verify(root)
            except ValueError:
                return
            raise AssertionError('Unsafe package candidate was accepted.')
        with contextlib.redirect_stdout(io.StringIO()):
            verify(root)
        (root / 'README.md').write_bytes(b'Changed without review.\n')
        expect_failure()
        (root / 'README.md').write_bytes(data)
        (root / 'extra.csv').write_bytes(b'Unreviewed data.\n')
        expect_failure()
        (root / 'extra.csv').unlink()
        (root / MANIFEST).write_text(sha256(data) + '  ../README.md\n', encoding='utf-8')
        expect_failure()
    print('Publication scanner self-tests passed.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    try:
        if args.self_test:
            self_test()
        else:
            verify()
    except (OSError, ValueError, UnicodeError) as error:
        # Error details can contain file data. Print only a fixed diagnostic.
        print('Publication check failed. Inspect the reported paths locally.', file=sys.stderr)
        sys.exit(1)
