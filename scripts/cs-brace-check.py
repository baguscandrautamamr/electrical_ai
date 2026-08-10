#!/usr/bin/env python3
"""Brace balance for C# files, ignoring strings and comments.

Not a compiler. It only answers the one question that has actually bitten us:
did an edit leave a file with a brace missing? CI on Windows answers it too,
twenty minutes later.
"""
import sys, pathlib

def balance(src: str) -> int:
    depth = 0
    i, n = 0, len(src)
    while i < n:
        c = src[i]
        if c == '/' and i + 1 < n and src[i+1] == '/':
            i = src.find('\n', i)
            if i < 0: break
            continue
        if c == '/' and i + 1 < n and src[i+1] == '*':
            j = src.find('*/', i + 2)
            i = n if j < 0 else j + 2
            continue
        if c == '@' and i + 1 < n and src[i+1] == '"':
            i += 2
            while i < n:
                if src[i] == '"':
                    if i + 1 < n and src[i+1] == '"':
                        i += 2
                        continue
                    i += 1
                    break
                i += 1
            continue
        if c == '"':
            i += 1
            while i < n and src[i] != '"':
                i += 2 if src[i] == '\\' else 1
            i += 1
            continue
        if c == "'":
            i += 1
            while i < n and src[i] != "'":
                i += 2 if src[i] == '\\' else 1
            i += 1
            continue
        if c == '{': depth += 1
        elif c == '}': depth -= 1
        i += 1
    return depth

bad = 0
for path in sorted(pathlib.Path(sys.argv[1]).rglob('*.cs')):
    if 'obj/' in str(path) or 'bin/' in str(path):
        continue
    d = balance(path.read_text(encoding='utf-8', errors='replace'))
    if d != 0:
        print(f'UNBALANCED {d:+d}: {path}')
        bad += 1
print('all balanced' if not bad else f'{bad} file(s) unbalanced')
sys.exit(1 if bad else 0)
