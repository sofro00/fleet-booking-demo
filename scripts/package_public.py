"""Create a source-only ZIP from the reviewed manifest; never include the working tree wholesale."""
from pathlib import Path
import zipfile

from verify_public import ROOT, MANIFEST, load_manifest, sha256, verify


def main():
    manifest = verify()
    output = ROOT / 'artifacts' / 'FleetBookingDemo-source.zip'
    output.parent.mkdir(exist_ok=True)
    with zipfile.ZipFile(output, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
        for name, expected in sorted(manifest.items()):
            path = ROOT / name
            data = path.read_bytes()
            if sha256(data) != expected:
                raise ValueError('A file changed during packaging.')
            # ZipInfo keeps local absolute paths, usernames and timestamps out of metadata.
            entry = zipfile.ZipInfo('FleetBookingDemo/' + name, date_time=(2026, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(entry, data)
        entry = zipfile.ZipInfo('FleetBookingDemo/' + MANIFEST, date_time=(2026, 1, 1, 0, 0, 0))
        archive.writestr(entry, (ROOT / MANIFEST).read_bytes())
    with zipfile.ZipFile(output) as archive:
        expected_names = {'FleetBookingDemo/' + name for name in manifest} | {'FleetBookingDemo/' + MANIFEST}
        if len(archive.namelist()) != len(expected_names) or set(archive.namelist()) != expected_names:
            raise ValueError('Unexpected ZIP contents.')
        for name, expected in manifest.items():
            if sha256(archive.read('FleetBookingDemo/' + name)) != expected:
                raise ValueError('ZIP checksum mismatch.')
    print(f'Created artifacts/{output.name}: {len(manifest) + 1} entries; no Git history or local output included.')


if __name__ == '__main__':
    main()
