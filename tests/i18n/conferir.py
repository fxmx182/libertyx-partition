"""Confere as traduções: todo texto marcado no código (L.T no C#, {l:T '…'} no AXAML) precisa estar em
src/LibertyX.Partition.Core/Lang/en.json e es.json, com os mesmos marcadores {0}, {1:N0}…
Texto que não muda (marca, sigla) entra com o próprio português como valor.

    python3 tests/i18n/conferir.py            # lista o que falta
    python3 tests/i18n/conferir.py --ci       # o mesmo, como avisos do GitHub Actions (não reprova)
    python3 tests/i18n/conferir.py --limpar   # também tira dos JSON as chaves que o código não usa mais
"""
import json, os, re, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import textos

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
SRC = os.path.join(ROOT, 'src')
LANG = os.path.join(SRC, 'LibertyX.Partition.Core', 'Lang')
PH = re.compile(r'(?<!\{)\{(\d+)[^{}]*\}(?!\})')

ci = '--ci' in sys.argv
code = textos.keys(SRC)
problems = 0
for lang in ('en', 'es'):
    path = os.path.join(LANG, f'{lang}.json')
    tr = json.load(open(path, encoding='utf-8'))
    for k, where in sorted(code.items(), key=lambda kv: kv[1][0]):
        if k not in tr:
            msg = f'{lang}: sem tradução: {k!r}'
        elif sorted(set(PH.findall(tr[k]))) != sorted(set(PH.findall(k))):
            msg = f'{lang}: marcadores diferentes: {k!r} → {tr[k]!r}'
        else:
            continue
        problems += 1
        file, line = where[0].rsplit(':', 1)
        print(f'::warning file=src/{file},line={line}::{msg}' if ci else f'{where[0]}  {msg}')
    unused = [k for k in tr if k not in code]
    if unused and '--limpar' in sys.argv:
        for k in unused: del tr[k]
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(tr, f, ensure_ascii=False, indent=1, sort_keys=True); f.write('\n')
        print(f'{lang}: {len(unused)} chave(s) sem uso removida(s)')
    elif unused:
        print(f'{lang}: {len(unused)} chave(s) que o código não usa mais (--limpar tira)')
print(f'{len(code)} textos; {problems} problema(s)')
