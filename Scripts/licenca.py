#!/usr/bin/env python3
"""
Emite uma chave Premium do LibertyX Partition ("LXP1-…"), assinada com a chave privada que
fica fora do repositório. É o que o bot de venda chama depois do pagamento — e serve para
emitir à mão (cortesia, troca, suporte).

  python3 Scripts/licenca.py "nota (comprador, pedido…)"      → imprime a chave

Cada chave emitida vai para o registro (CSV ao lado da chave privada), com número e nota:
é o que permite achar a licença de alguém depois. Formato igual ao do app
(Core/Licensing/License.cs): 12 bytes de dados + 64 de assinatura ECDSA P-256, em base32 Crockford.
"""
import base64, csv, datetime, os, secrets, struct, sys
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.asymmetric.utils import decode_dss_signature

CHAVES = os.environ.get("LXP_CHAVES", "/DATA/Claudinho/chaves")
PRIVADA = os.path.join(CHAVES, "libertyx-partition-licenca.pem")
REGISTRO = os.path.join(CHAVES, "libertyx-partition-licencas.csv")
ALFABETO = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
EPOCA = datetime.date(2026, 1, 1)


def b32(b):
    out, buf, bits = [], 0, 0
    for x in b:
        buf = (buf << 8) | x; bits += 8
        while bits >= 5:
            out.append(ALFABETO[(buf >> (bits - 5)) & 31]); bits -= 5
    if bits:
        out.append(ALFABETO[(buf << (5 - bits)) & 31])
    return "".join(out)


def emitir(nota="", hoje=None):
    hoje = hoje or datetime.date.today()
    chave = serialization.load_pem_private_key(open(PRIVADA, "rb").read(), None)
    ident = secrets.token_bytes(8)
    dados = bytes([1, 1]) + struct.pack(">H", (hoje - EPOCA).days) + ident
    r, s = decode_dss_signature(chave.sign(dados, ec.ECDSA(hashes.SHA256())))
    corpo = b32(dados + r.to_bytes(32, "big") + s.to_bytes(32, "big"))
    texto = "LXP1-" + "-".join(corpo[i:i + 6] for i in range(0, len(corpo), 6))
    novo = not os.path.exists(REGISTRO)
    with open(REGISTRO, "a", newline="") as f:
        w = csv.writer(f)
        if novo:
            w.writerow(["licenca", "data", "nota", "chave"])
        w.writerow([ident.hex().upper(), hoje.isoformat(), nota, texto])
    os.chmod(REGISTRO, 0o600)
    return texto


if __name__ == "__main__":
    print(emitir(" ".join(sys.argv[1:])))
