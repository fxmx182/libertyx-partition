# Grava um cabeçalho de volume HFS+ mínimo (o que a busca de partições lê): python3 hfshdr.py <dispositivo> <bytes>
import struct, sys
dev, size = sys.argv[1], int(sys.argv[2])
h = bytearray(512)
h[0:2] = b"H+"
h[2:4] = struct.pack(">H", 4)
h[40:44] = struct.pack(">I", 4096)
h[44:48] = struct.pack(">I", size // 4096)
with open(dev, "r+b") as f:
    f.seek(1024); f.write(h)
