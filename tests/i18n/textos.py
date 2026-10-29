"""Textos da interface do LibertyX Partition: acha cada L.T("…") do C# e {l:T '…'} do AXAML e monta a
chave como o L.T monta em tempo de execução (L.cs). Usado por conferir.py."""
import os, re, html


def scan(src):
    out = []
    i, n = 0, len(src)
    def skip_str(i):
        # devolve (end, kind, holes, nested) para literal que começa em i
        j = i
        dollars = 0
        verb = False
        while src[j] in '$@':
            if src[j] == '$': dollars += 1
            else: verb = True
            j += 1
        if src.startswith('"""', j):
            q = 0
            while src[j] == '"': q += 1; j += 1
            close = '"' * q
            holes = []
            if dollars:
                k = j
                while True:
                    e = src.find(close, k)
                    seg = src[k:e]
                    # holes em raw interpolada com dollars chaves
                    br = '{' * dollars
                    p = 0
                    while True:
                        b = seg.find(br, p)
                        if b < 0: break
                        hs = k + b + dollars
                        he = find_hole_end(hs, '}' * dollars)
                        holes.append((hs, he))
                        p = he + dollars - k
                    break
            e = src.find(close, j)
            return e + q, ('raw$' if dollars else 'raw'), holes, j, e
        assert src[j] == '"', (i, src[i:i+20])
        j += 1
        body_start = j
        holes = []
        while True:
            c = src[j]
            if verb:
                if c == '"':
                    if src[j+1] == '"': j += 2; continue
                    break
            else:
                if c == '\\': j += 2; continue
                if c == '"': break
            if dollars and c == '{':
                if src[j+1] == '{': j += 2; continue
                hs = j + 1
                he = find_hole_end(hs, '}')
                holes.append((hs, he))
                j = he + 1
                continue
            if dollars and c == '}' and src[j+1] == '}': j += 2; continue
            j += 1
        kind = ('$' if dollars else '') + ('@' if verb else '')
        return j + 1, kind or 'str', holes, body_start, j

    def find_hole_end(k, close):
        depth = 0
        while True:
            c = src[k]
            if c in '"$@' and (c == '"' or (k + 1 < n and src[k+1] in '"$@')):
                if c == '"' or src[k+1] in '"@$':
                    e, *_ = skip_str(k)
                    k = e
                    continue
            if c == "'":
                k = skip_char(k); continue
            if c in '([{': depth += 1
            elif c in ')]': depth -= 1
            elif c == '}':
                if depth == 0: return k
                depth -= 1
            k += 1

    def skip_char(k):
        k += 1
        if src[k] == '\\': k += 2
        else: k += 1
        while src[k] != "'": k += 1
        return k + 1

    while i < n:
        c = src[i]
        if src.startswith('//', i):
            i = src.find('\n', i); i = n if i < 0 else i; continue
        if src.startswith('/*', i):
            i = src.find('*/', i) + 2; continue
        if c == "'":
            i = skip_char(i); continue
        if c == '"' or (c in '$@' and i + 1 < n and src[i+1] in '"$@' and (c != '@' or src[i+1] in '"$')):
            # cuidado com identificadores @xxx
            k = i
            while k < n and src[k] in '$@': k += 1
            if k < n and src[k] == '"':
                e, kind, holes, bs, be = skip_str(i)
                out.append(dict(start=i, end=e, kind=kind, holes=holes, bs=bs, be=be))
                i = e; continue
        i += 1
    return out


def lits(src, base=0):
    """Literais, inclusive os que estão dentro dos buracos de uma string interpolada."""
    for L in scan(src):
        L = dict(L)
        for k in ('start', 'end', 'bs', 'be'): L[k] += base
        L['holes'] = [(a + base, b + base) for a, b in L['holes']]
        yield L
        for a, b in L['holes']:
            yield from lits(src[a - base:b - base], a)


def wrapped(src):
    for L in lits(src):
        if src[max(0, L['start'] - 4):L['start']] == 'L.T(' and src[L['end']:L['end'] + 1] == ')':
            yield L


ESC = {'n': '\n', 't': '\t', 'r': '\r', '0': '\0', '"': '"', "'": "'", '\\': '\\', 'a': '\a', 'b': '\b', 'f': '\f', 'v': '\v', 'e': '\x1b'}

def unescape(s, verbatim):
    if verbatim: return s.replace('""', '"')
    out, i = [], 0
    while i < len(s):
        c = s[i]
        if c == '\\':
            n = s[i + 1]
            if n == 'u': out.append(chr(int(s[i + 2:i + 6], 16))); i += 6; continue
            if n == 'x':
                m = re.match(r'[0-9a-fA-F]{1,4}', s[i + 2:]); out.append(chr(int(m.group(0), 16))); i += 2 + len(m.group(0)); continue
            out.append(ESC[n]); i += 2; continue
        out.append(c); i += 1
    return ''.join(out)

def split_hole(h):
    """'expr,align:fmt' no nível de cima (fora de parênteses/strings)."""
    depth, k, align, fmt = 0, 0, None, None
    i = 0
    cut_a = cut_f = None
    while i < len(h):
        c = h[i]
        if c in '"\'' or (c in '$@' and i + 1 < len(h) and h[i + 1] == '"'):
            # pula string/char aninhado
            sub = scan(h[i:])
            if sub and sub[0]['start'] == 0: i += sub[0]['end']; continue
            if c == "'":
                j = h.index("'", i + 2 if h[i + 1] == '\\' else i + 1); i = j + 1; continue
        if c in '([{': depth += 1
        elif c in ')]}': depth -= 1
        elif depth == 0 and c == ',' and cut_a is None and cut_f is None: cut_a = i
        elif depth == 0 and c == ':' and cut_f is None:
            # ':' de '::' (global::) não conta
            if not (h[i + 1:i + 2] == ':' or h[i - 1:i] == ':'): cut_f = i
        i += 1
    if cut_a is not None:
        align = h[cut_a + 1:cut_f].strip() if cut_f else h[cut_a + 1:].strip()
    if cut_f is not None: fmt = h[cut_f + 1:]
    return align, fmt

    return align, fmt

def key_of(src, L):
    verb = '@' in L['kind']
    if not L['holes']:
        body = src[L['bs']:L['be']]
        if L['kind'].startswith('$'):
            # interpolada sem buracos é constante: vai para T(string) já sem {{ }}
            return unescape(body, verb).replace('{{', '{').replace('}}', '}')
        return unescape(body, verb)
    out, k = [], L['bs']
    for i, (a, b) in enumerate(L['holes']):
        out.append(unescape(src[k:a - 1], verb))
        align, fmt = split_hole(src[a:b])
        if fmt and not verb: fmt = unescape(fmt, False)
        out.append('{' + str(i) + (',' + align if align else '') + (':' + fmt if fmt else '') + '}')
        k = b + 1
    out.append(unescape(src[k:L['be']], verb))
    return ''.join(out)


def keys(root):
    """{chave: ['arquivo:linha', …]} de todo o código em root."""
    out = {}
    for dp, _, fs in os.walk(root):
        if '/obj' in dp or '/bin' in dp: continue
        for f in sorted(fs):
            p = os.path.join(dp, f)
            rel = os.path.relpath(p, root)
            if f.endswith('.cs'):
                src = open(p, encoding='utf-8').read()
                for L in wrapped(src):
                    out.setdefault(key_of(src, L), []).append(f'{rel}:{src.count(chr(10), 0, L["start"]) + 1}')
            elif f.endswith('.axaml'):
                src = open(p, encoding='utf-8').read()
                for m in re.finditer(r"\{l:T '([^']*)'\}", src):
                    out.setdefault(html.unescape(m.group(1)), []).append(f'{rel}:{src.count(chr(10), 0, m.start()) + 1}')
    return out
