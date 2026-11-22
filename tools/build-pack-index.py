"""Generate the embedded dialogue fingerprint index from the pinned pack catalog.

Only unique 64-bit fingerprints and a catalog slot are emitted, never dialogue.
Run from any directory with: python tools/build-pack-index.py
"""
import concurrent.futures
import gzip
import hashlib
import json
import re
import struct
from pathlib import Path
from urllib.request import Request, urlopen

CORE = Path(__file__).resolve().parents[1] / 'src' / 'OyunAltyazi.Core'


def main():
    source = (CORE / 'AutoPack.cs').read_text(encoding='utf-8-sig')
    root, = re.findall(r'private const string Root = "([^"]+)";', source)
    entries = re.findall(r'E\("([^"]+)", "[^"]+", (\d+), "([a-f0-9]{64})"', source)
    if not 0 < len(entries) < 255:
        raise ValueError('Unexpected catalog size')

    def fetch(entry):
        name, size, digest = entry
        request = Request(root + name + '.json', headers={'User-Agent': 'OyunAltyazi-pack-fetch'})
        with urlopen(request, timeout=65) as response:
            data = response.read(int(size) + 1)
        if len(data) != int(size) or hashlib.sha256(data).hexdigest() != digest:
            raise ValueError(f'{name}: size or SHA-256 mismatch')
        pack = json.loads(data)
        if pack['id'] != name or pack['format'] != 2:
            raise ValueError(f'{name}: pack identity or format mismatch')
        return [int(key, 16) for key, value in pack['entries'].items()
                if re.fullmatch(r'[0-9a-f]{16}', key) and isinstance(value, str) and value.strip()]

    owners = {}
    with concurrent.futures.ThreadPoolExecutor(max_workers=5) as pool:
        for slot, keys in enumerate(pool.map(fetch, entries)):
            print(entries[slot][0], len(keys), flush=True)
            for key in keys:
                owners[key] = slot if key not in owners else 255
    # Header binds the file to the catalog order and versions; records are sorted by key.
    count = sum(slot != 255 for slot in owners.values())
    content = bytearray(hashlib.sha256('\n'.join(f'{name}:{digest}' for name, _, digest in entries).encode()).digest())
    content.extend(struct.pack('<I', count))
    for key, slot in sorted(owners.items()):
        if slot != 255:
            content.extend(struct.pack('<QB', key, slot))
    output = CORE / 'PackFingerprints.bin.gz'
    output.write_bytes(gzip.compress(content, compresslevel=9, mtime=0))
    print(count, 'unique fingerprints;', output.stat().st_size, 'bytes')


if __name__ == '__main__':
    main()
