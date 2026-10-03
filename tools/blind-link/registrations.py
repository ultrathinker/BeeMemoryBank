"""Which registrations of the full application's AddStorage / AddCore / AddSync does a blind node keep?

A report, not a generator: it reads the three DependencyInjection.cs files of the libraries and, for every registration
statement, says whether the types it names are inside the linked set (libs/BeeMemoryBank.Blind/linked-lib-files.props).
A registration whose implementation is not linked cannot exist in the blind assembly; one whose types are all linked is a
candidate for BlindComposition.cs. The composition itself is written (and reviewed) by hand.

  python tools/blind-link/registrations.py
"""
import io
import os
import re

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
LIBS = os.path.join(REPO, 'libs')
PROPS = os.path.join(LIBS, 'BeeMemoryBank.Blind', 'linked-lib-files.props')
DI_FILES = [
    'BeeMemoryBank.Storage/DependencyInjection.cs',
    'BeeMemoryBank.Core/DependencyInjection.cs',
    'BeeMemoryBank.Sync/DependencyInjection.cs',
    'BeeMemoryBank.Sync/Recovery/RecoveryServiceCollectionExtensions.cs',
]
TYPE_RE = re.compile(r'\b(?:class|record|struct|interface|enum|delegate)\s+(?:class\s+|struct\s+)?([A-Z][A-Za-z0-9_]*)')


def linked_files():
    s = io.open(PROPS, encoding='utf-8').read()
    out = []
    for m in re.finditer(r'Include="([^"]+)"', s):
        out.append(os.path.normpath(os.path.join(os.path.dirname(PROPS), m.group(1).replace('\\', os.sep))))
    return out


def main():
    declared = set()
    for f in linked_files():
        declared |= set(TYPE_RE.findall(io.open(f, encoding='utf-8', errors='replace').read()))
    for rel in DI_FILES:
        src = re.sub(r'//[^\n]*', '', io.open(os.path.join(LIBS, rel.replace('/', os.sep)), encoding='utf-8').read())
        print('== ' + rel)
        for stmt in re.findall(r'services\s*\.\s*(?:Try)?Add\w*(?:<[^;]*?>)?\s*\([^;]*?\)\s*;', src, re.S):
            one = ' '.join(stmt.split())
            generic = re.search(r'Add\w*<(.*)>\s*\(', one)
            names = re.findall(r'\b[A-Z][A-Za-z0-9_]*\b', generic.group(1)) if generic else re.findall(r'new (\w+)', one)
            names = [n for n in names if n not in ('Core', 'Interfaces', 'Storage', 'Sqlite', 'Sync', 'BeeMemoryBank', 'Search', 'Blind')]
            missing = [n for n in names if n not in declared]
            print(('KEEP  ' if not missing else 'DROP  ') + one[:150] + (('   <- not linked: ' + ', '.join(missing)) if missing else ''))


if __name__ == '__main__':
    main()
