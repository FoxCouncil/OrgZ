#!/usr/bin/env python3
# Independent hash58 oracle: implements the algorithm from the documented spec, generating the
# AES S-box from GF(2^8) first principles (not copied from OrgZ's tables) and using Python's stdlib
# HMAC-SHA1. If this agrees with OrgZ's ITunesDbHash58, the port's y-derivation, zeroing and HMAC
# construction are cross-validated by a fully independent implementation.
import hashlib, hmac, math, sys

def gf_inv(a):
    if a == 0:
        return 0
    for b in range(1, 256):
        p, x, y = 0, a, b
        for _ in range(8):
            if y & 1:
                p ^= x
            hi = x & 0x80
            x = (x << 1) & 0xFF
            if hi:
                x ^= 0x1B
            y >>= 1
        if p == 1:
            return b
    return 0

def sbox_val(x):
    b = gf_inv(x)
    s = b
    for _ in range(4):
        b = ((b << 1) | (b >> 7)) & 0xFF
        s ^= b
    return s ^ 0x63

SBOX = [sbox_val(x) for x in range(256)]
INV = [0] * 256
for i, v in enumerate(SBOX):
    INV[v] = i

# hash58's fixed 18-byte prefix (documented libgpod/freemyipod constant).
FIXED = bytes([0x67, 0x23, 0xFE, 0x30, 0x45, 0x33, 0xF8, 0x90, 0x99,
               0x21, 0x07, 0xC1, 0xD0, 0x12, 0xB2, 0xA1, 0x07, 0x81])

def lcm(a, b):
    return 1 if (a == 0 or b == 0) else a * b // math.gcd(a, b)

def derive_y(fwid):
    y = bytearray(16)
    for i in range(4):
        l = lcm(fwid[2 * i], fwid[2 * i + 1])
        hi, lo = (l >> 8) & 0xFF, l & 0xFF
        y[4 * i]     = SBOX[hi]
        y[4 * i + 1] = INV[hi]
        y[4 * i + 2] = SBOX[lo]
        y[4 * i + 3] = INV[lo]
    return bytes(y)

def hash58(db, guid_hex):
    db = bytearray(db)
    fwid = bytes.fromhex(guid_hex)
    db[0x30] = 1
    db[0x31] = 0
    for i in range(0x18, 0x20):
        db[i] = 0
    for i in range(0x32, 0x46):
        db[i] = 0
    for i in range(0x58, 0x6C):
        db[i] = 0
    key = hashlib.sha1(FIXED + derive_y(fwid)).digest()
    return hmac.new(key, bytes(db), hashlib.sha1).digest()

# sanity: our generated S-box must be the real AES S-box
assert SBOX[0] == 0x63 and SBOX[1] == 0x7C and SBOX[16] == 0xCA, "S-box generation wrong"

db = open(sys.argv[1], "rb").read()
print("independent_hash58=" + hash58(db, sys.argv[2]).hex())
