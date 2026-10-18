#!/usr/bin/env python3
"""
As artes do instalador (Inno Setup): a faixa da esquerda das telas de boas-vindas e de
conclusão, e o selo do canto das outras telas. Mesma linguagem do app: preto quente, brilho
dourado vindo do alto, o símbolo em ouro e os sulcos de um prato de disco no rodapé.

Gera installer/wizard.bmp (164x314 e 2x) e installer/small.bmp (55x55 e 2x).
Uso: toolchain/icon-venv/bin/python Scripts/instalador.py
"""
import math, os
from PIL import Image, ImageDraw, ImageFilter, ImageFont

AQUI = os.path.dirname(os.path.abspath(__file__))
RAIZ = os.path.join(AQUI, "..")
ASSETS = os.path.join(RAIZ, "src/LibertyX.Partition/Assets")
SAIDA = os.path.join(RAIZ, "installer")
OURO = (251, 197, 1)
FUNDO = (12, 11, 10)


def fonte(peso, tam):
    return ImageFont.truetype(os.path.join(ASSETS, f"Fonts/manrope_{peso}.ttf"), tam)


def faixa(esc):
    w, h = 164 * esc, 314 * esc
    im = Image.new("RGB", (w, h), FUNDO)
    # a luz de cima: um brilho dourado fraco no alto à esquerda
    luz = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(luz)
    r = int(w * 1.05)
    d.ellipse((-r // 2, -r // 2 - 20 * esc, r // 2 + 10 * esc, r // 2 - 20 * esc), fill=60)
    luz = luz.filter(ImageFilter.GaussianBlur(40 * esc))
    im.paste(Image.new("RGB", (w, h), OURO), (0, 0), luz)

    # os sulcos do prato: arcos concêntricos finos saindo do canto de baixo, o de dentro mais aceso
    sulcos = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(sulcos)
    cx, cy = int(w * 1.0), int(h * 1.02)
    for i, raio in enumerate(range(int(40 * esc), int(260 * esc), int(9 * esc))):
        a = max(14, 120 - i * 5)
        d.ellipse((cx - raio, cy - raio, cx + raio, cy + raio), outline=OURO + (a,), width=max(1, esc))
    # a fatia: um arco mais grosso, o "uso" do disco
    raio = int(150 * esc)
    d.arc((cx - raio, cy - raio, cx + raio, cy + raio), 196, 244, fill=OURO + (235,), width=4 * esc)
    im.paste(sulcos, (0, 0), sulcos)

    # o símbolo e o nome
    logo = Image.open(os.path.join(ASSETS, "logo-mark.png")).convert("RGBA")
    lado = 58 * esc
    logo = logo.resize((lado, lado), Image.LANCZOS)
    x0, y0 = 18 * esc, 30 * esc
    # o brilho num quadro maior que o símbolo, senão o desfoque bate na borda e desenha um quadrado
    m = 30 * esc
    forma = Image.new("L", (lado + 2 * m, lado + 2 * m), 0)
    forma.paste(logo.split()[3], (m, m))
    brilho = forma.filter(ImageFilter.GaussianBlur(10 * esc)).point(lambda v: int(v * 0.5))
    im.paste(Image.new("RGB", forma.size, OURO), (x0 - m, y0 - m), brilho)
    im.paste(logo, (x0, y0), logo)
    d = ImageDraw.Draw(im)
    y = y0 + lado + 16 * esc
    t = "LIBERTYX"
    f = fonte("bold", 9 * esc)
    xx = x0 + 1 * esc
    for ch in t:                                   # caixa alta espaçada, como no app
        d.text((xx, y), ch, font=f, fill=OURO)
        xx += f.getlength(ch) + 2.6 * esc
    d.text((x0, y + 11 * esc), "Partition", font=fonte("extrabold", 24 * esc), fill=(246, 242, 232))
    d.text((x0 + 1 * esc, y + 44 * esc), "Discos e recuperação\nde dados", font=fonte("medium", 10 * esc),
           fill=(170, 164, 152), spacing=3 * esc)
    return im


def selo(esc):
    lado = 55 * esc
    ic = Image.open(os.path.join(AQUI, "icone-512.png")).convert("RGBA").resize((lado, lado), Image.LANCZOS)
    fundo = Image.new("RGB", (lado, lado), (255, 255, 255))   # o cabeçalho do assistente é claro
    fundo.paste(ic, (0, 0), ic)
    return fundo


os.makedirs(SAIDA, exist_ok=True)
for esc, suf in ((1, ""), (2, "-2x")):
    faixa(esc).save(os.path.join(SAIDA, f"wizard{suf}.bmp"))
    selo(esc).save(os.path.join(SAIDA, f"small{suf}.bmp"))
print("ok")
