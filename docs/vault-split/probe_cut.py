"""Removal probe for S1 (capability inventory). Reads a list of repo-relative files (V.txt: one per line, # comments) that are candidates for
the vault, removes them from the generated linked sets of the blind host (props), builds the blind host once, prints the compile errors grouped
by file (with the missing names), then restores both props byte for byte. Usage: python probe_cut.py V.txt"""
import collections, io, os, re, subprocess, sys

REPO = r'D:\review\bmb-vault-split\repo'
LIB = os.path.join(REPO, 'libs', 'BeeMemoryBank.Blind', 'linked-lib-files.props')
API = os.path.join(REPO, 'server', 'BeeMemoryBank.BlindNode', 'linked-api-files.props')
PROJ = os.path.join(REPO, 'server', 'BeeMemoryBank.BlindNode', 'BeeMemoryBank.BlindNode.csproj')


def read_list(path):
    out = set()
    for line in io.open(path, encoding='utf-8').read().splitlines():
        line = line.split('#', 1)[0].strip().replace('\\', '/')
        if line:
            out.add(line)
    return out


def drop(text, base, remove, found):
    out = []
    for line in text.split('\r\n'):
        m = re.search(r'Include="([^"]+)"', line)
        if m:
            full = os.path.normpath(os.path.join(base, m.group(1).replace(chr(92), os.sep)))
            rel = os.path.relpath(full, REPO).replace(os.sep, '/')
            if rel in remove:
                found.add(rel)
                continue
        out.append(line)
    return '\r\n'.join(out)


def main():
    remove = read_list(sys.argv[1])
    stubs = os.path.abspath(sys.argv[2]) if len(sys.argv) > 2 else None
    saved = {p: io.open(p, encoding='utf-8', newline='').read() for p in (LIB, API)}
    found = set()
    try:
        lib_text = drop(saved[LIB], os.path.dirname(LIB), remove, found)
        if stubs:   # a probe-only file of stand-in types, so that member-level errors show up
            stub_line = '    <Compile Include="' + stubs + '" Link="Probe' + chr(92) + 'stubs.cs" />' + chr(13) + chr(10) + '  </ItemGroup>'
            lib_text = lib_text.replace('</ItemGroup>', stub_line, 1)
        lib_text = lib_text.replace('</ItemGroup>', '    <Compile Remove="BlindComposition.cs" />' + chr(13) + chr(10) + '  </ItemGroup>', 1)
        io.open(LIB, 'w', encoding='utf-8', newline='').write(lib_text)
        api_text = drop(saved[API], os.path.dirname(API), remove, found)
        api_text = api_text.replace('</ItemGroup>', '    <Compile Remove="Program.cs;Startup' + chr(92) + '*.cs" />' + chr(13) + chr(10) + '  </ItemGroup>', 1)
        io.open(API, 'w', encoding='utf-8', newline='').write(api_text)
        missing = sorted(remove - found)
        if missing:
            print('NOT IN THE LINKED SETS (ignored):', missing)
        p = subprocess.run(['dotnet', 'build', PROJ, '-c', 'Debug', '-nologo', '-v', 'q', '-clp:NoSummary'], capture_output=True, text=True,
                           encoding='utf-8', errors='replace')
        errs = sorted(set(l.strip() for l in (p.stdout + p.stderr).splitlines() if ' error ' in l and 'CS5001' not in l))
        by_file = collections.defaultdict(list)
        for e in errs:
            m = re.match(r'(.+?)\((\d+),\d+\): error (\w+): (.*?) \[', e)
            if m:
                rel = os.path.relpath(m.group(1), REPO).replace(os.sep, '/') if os.path.isabs(m.group(1)) else m.group(1)
                by_file[rel].append((m.group(3), m.group(4)))
        print('removed %d files; distinct errors: %d in %d files' % (len(found), len(errs), len(by_file)))
        for f, es in sorted(by_file.items()):
            names = []
            for _c, msg in es:
                names += re.findall(r"'([A-Za-z_][A-Za-z_0-9.<>]*)'", msg)
            print('%-78s %3d  %s' % (f, len(es), ', '.join(sorted(set(names))[:7])))
    finally:
        for path, text in saved.items():
            io.open(path, 'w', encoding='utf-8', newline='').write(text)
        print('props restored')


main()
