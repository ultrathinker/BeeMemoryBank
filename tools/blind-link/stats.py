"""How much of each library the blind assembly links (files and lines)."""
import collections
import io
import os
import re

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
PROPS = os.path.join(REPO, 'libs', 'BeeMemoryBank.Blind', 'linked-lib-files.props')


def count(path):
    return sum(1 for _ in io.open(path, encoding='utf-8', errors='replace'))


def main():
    s = io.open(PROPS, encoding='utf-8').read()
    files, lines = collections.Counter(), collections.Counter()
    for m in re.finditer(r'Include="([^"]+)"', s):
        full = os.path.normpath(os.path.join(os.path.dirname(PROPS), m.group(1).replace(chr(92), os.sep)))
        lib = re.search(r'BeeMemoryBank\.(\w+)', os.path.relpath(full, os.path.join(REPO, 'libs'))).group(1)
        files[lib] += 1
        lines[lib] += count(full)
    total_f = total_l = all_f = all_l = 0
    for lib in ('Core', 'Storage', 'Sync', 'Crypto', 'Search'):
        n = l = 0
        for r, d, fs in os.walk(os.path.join(REPO, 'libs', 'BeeMemoryBank.' + lib)):
            d[:] = [x for x in d if x not in ('bin', 'obj')]
            for f in fs:
                if f.endswith('.cs') and not f.endswith('.g.cs'):
                    n += 1
                    l += count(os.path.join(r, f))
        print('%-8s %3d of %3d files, %6d of %6d lines' % (lib, files[lib], n, lines[lib], l))
        total_f += files[lib]; total_l += lines[lib]; all_f += n; all_l += l
    print('%-8s %3d of %3d files, %6d of %6d lines (%.0f %% of the lines)' % ('total', total_f, all_f, total_l, all_l, 100.0 * total_l / all_l))


if __name__ == '__main__':
    main()
