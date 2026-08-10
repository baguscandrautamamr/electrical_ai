#!/usr/bin/env python3
"""Cari deklarasi lokal `var x =` yang bertabrakan dalam satu lingkup C#.

CS0128 dan CS0136 tidak bisa dilihat pemeriksa kurung, tidak terasa saat
menulis, dan biayanya satu putaran CI Windows penuh — 10 menit untuk satu
nama yang sudah dipakai 30 baris di bawahnya. Ini bukan compiler: ia hanya
melacak `var` (dan `using var`), yang justru bentuk yang paling sering
ditulis.

C# melarang sebuah lokal menutupi lokal lain di lingkup yang melingkupinya,
jadi tabrakan dicari di seluruh tumpukan lingkup, bukan hanya yang terdalam.
"""
import re, sys, pathlib

DECL = re.compile(r'\b(?:using\s+)?var\s+([A-Za-z_]\w*)\s*=(?!=)')
# `for (var i = ...)` dan sejenisnya membuka lingkupnya sendiri sebelum `{`.
HEAD = re.compile(r'\b(?:for|foreach|while|if|using|switch|catch|fixed|lock)\s*\(')

def strip_noise(line: str) -> str:
    """Buang komentar baris dan isi string agar tidak ikut terbaca."""
    line = re.sub(r'"(?:[^"\\]|\\.)*"', '""', line)
    line = re.sub(r"'(?:[^'\\]|\\.)*'", "''", line)
    return line.split('//', 1)[0]

def check(path: pathlib.Path) -> list[str]:
    problems, scopes, in_block_comment = [], [set()], False

    for number, raw in enumerate(path.read_text(encoding='utf-8', errors='replace').splitlines(), 1):
        line = raw
        if in_block_comment:
            if '*/' not in line:
                continue
            line, in_block_comment = line.split('*/', 1)[1], False
        while '/*' in line:
            before, _, after = line.partition('/*')
            if '*/' in after:
                line = before + after.split('*/', 1)[1]
            else:
                line, in_block_comment = before, True
                break

        line = strip_noise(line)

        # `for (var i = 0; ...)` dan `foreach (var row in ...)` mendeklarasikan
        # ke dalam lingkup loopnya, bukan ke lingkup yang memuatnya — dua loop
        # bersebelahan boleh sama-sama memakai `i`. Dilewati, bukan dilacak:
        # melacaknya benar butuh parser sungguhan, dan tabrakan yang mahal
        # bukan di sini.
        head = HEAD.search(line)
        header_paren = head.end() - 1 if head else None

        for match in DECL.finditer(line):
            if header_paren is not None and match.start() > header_paren:
                continue

            name = match.group(1)
            for depth, scope in enumerate(scopes):
                if name in scope:
                    problems.append(
                        f'{path}:{number}: local "{name}" sudah dideklarasikan '
                        f'di lingkup yang melingkupinya (level {depth})')
                    break
            scopes[-1].add(name)

        # Sebuah blok yang dibuka di baris ini adalah lingkup baru; yang
        # ditutup mengakhiri satu.
        for _ in range(line.count('{')):
            scopes.append(set())
        for _ in range(line.count('}')):
            if len(scopes) > 1:
                scopes.pop()

    return problems

root = pathlib.Path(sys.argv[1])
found = []
for path in sorted(root.rglob('*.cs')):
    if any(part in ('obj', 'bin') for part in path.parts):
        continue
    found += check(path)

print('\n'.join(found) if found else 'tidak ada tabrakan nama lokal')
sys.exit(1 if found else 0)
