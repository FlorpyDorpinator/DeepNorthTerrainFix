"""
Valheim 1.0 chunked-save (.chunk) reader/writer and TerrainComp (TCData) codec.

Format (from decompiled assembly_valheim, ZDOMan.SaveChunk / ZDO.Save / TerrainComp.Save):
  .chunk  : int16 worldVersion (41), int32 count, then `count` ZDO records (no per-ZDO UID on disk)
  ZDO     : uint16 flags, position (Vector2s if SmallPosition flag else Vector3), int32 prefabHash,
            small-rotation (if Rotation flag), then optional typed key/value blocks in fixed order
            (connection, floats, vec3, quats, ints, longs, strings, byte[]).
  TCData  : gzip( int32 ver(1), int32 ops, Vector3 lastOp, float lastRadius,
                  int32 n=4225 x [bool mod, (float level, float smooth)],
                  int32 n=4225 x [bool mod, (float r,g,b,a)] )
Zone id of a ZDO: (floor((x+32)/64), floor((z+32)/64)).
Chunk file for a zone: chunk = ((zx+256)/8) | ((zy+256)/8)<<8, masked by size filter
  size1 mask 0xFEFE, size2 0xFCFC, size3 0xF8F8 -> filename "{hi:02x}_{lo:02x}__{size}_{version}.chunk"
"""
import struct, gzip, math, collections

def stable_hash(s):
    a = 5381; b = 5381; i = 0; n = len(s)
    while i < n:
        a = (((a << 5) + a) ^ ord(s[i])) & 0xFFFFFFFF
        if i + 1 < n:
            b = (((b << 5) + b) ^ ord(s[i+1])) & 0xFFFFFFFF
        i += 2
    v = (a + b * 1566083941) & 0xFFFFFFFF
    if v >= 0x80000000: v -= 0x100000000
    return v

class R:
    def __init__(self, data, pos=0):
        self.d = data; self.p = pos
    def u8(self):  v = self.d[self.p]; self.p += 1; return v
    def bool(self): return self.u8() != 0
    def i16(self): v = struct.unpack_from('<h', self.d, self.p)[0]; self.p += 2; return v
    def u16(self): v = struct.unpack_from('<H', self.d, self.p)[0]; self.p += 2; return v
    def i32(self): v = struct.unpack_from('<i', self.d, self.p)[0]; self.p += 4; return v
    def u32(self): v = struct.unpack_from('<I', self.d, self.p)[0]; self.p += 4; return v
    def i64(self): v = struct.unpack_from('<q', self.d, self.p)[0]; self.p += 8; return v
    def f32(self): v = struct.unpack_from('<f', self.d, self.p)[0]; self.p += 4; return v
    def vec3(self): v = struct.unpack_from('<fff', self.d, self.p); self.p += 12; return v
    def quat(self): v = struct.unpack_from('<ffff', self.d, self.p); self.p += 16; return v
    def vec2s(self): v = struct.unpack_from('<hh', self.d, self.p); self.p += 4; return v
    def numitems(self):
        n = self.u8()
        if n & 128:
            n = ((n & 127) << 8) | self.u8()
        return n
    def str7(self):
        n = 0; shift = 0
        while True:
            b = self.u8(); n |= (b & 0x7F) << shift; shift += 7
            if not (b & 0x80): break
        s = self.d[self.p:self.p+n].decode('utf-8', 'replace'); self.p += n; return s
    def bytearr(self):
        n = self.i32(); v = self.d[self.p:self.p+n]; self.p += n; return bytes(v)
    def small_rot(self):
        n = self.u16()
        if n & 0x8000:
            return (0.0, (n & 0x7FFF) * 0.5, 0.0)
        n = (n << 16) | self.u16()
        return ((n & 1023) * 0.5, ((n >> 10) & 1023) * 0.5, ((n >> 20) & 1023) * 0.5)

F_CONN=1; F_FLOAT=2; F_VEC3=4; F_QUAT=8; F_INT=16; F_LONG=32; F_STR=64; F_BYTES=128
F_PERSIST=256; F_DISTANT=512; F_ROT=4096; F_SMALLPOS=8192

class ZDO:
    def __init__(self):
        self.conn=None; self.floats={}; self.vec3s={}; self.quats={}; self.ints={}; self.longs={}; self.strs={}; self.bytes_={}
    @property
    def persistent(self): return bool(self.flags & F_PERSIST)
    @property
    def distant(self): return bool(self.flags & F_DISTANT)
    @property
    def type(self): return (self.flags >> 10) & 3
    def zone(self):
        return (math.floor((self.pos[0]+32)/64), math.floor((self.pos[2]+32)/64))

def read_zdo(r, idx):
    z = ZDO(); z.idx = idx; z.start = r.p
    fl = r.u16(); z.flags = fl
    if fl & F_SMALLPOS:
        x,y = r.vec2s(); z.pos = (float(x), 0.0, float(y))
    else:
        z.pos = r.vec3()
    z.prefab = r.i32()
    z.rot = r.small_rot() if (fl & F_ROT) else (0.0,0.0,0.0)
    if fl & 0xFF:
        if fl & F_CONN:
            z.conn = (r.u8(), r.i32())
        if fl & F_FLOAT:
            for _ in range(r.numitems()): k=r.i32(); z.floats[k]=r.f32()
        if fl & F_VEC3:
            for _ in range(r.numitems()): k=r.i32(); z.vec3s[k]=r.vec3()
        if fl & F_QUAT:
            for _ in range(r.numitems()): k=r.i32(); z.quats[k]=r.quat()
        if fl & F_INT:
            for _ in range(r.numitems()): k=r.i32(); z.ints[k]=r.i32()
        if fl & F_LONG:
            for _ in range(r.numitems()): k=r.i32(); z.longs[k]=r.i64()
        if fl & F_STR:
            for _ in range(r.numitems()): k=r.i32(); z.strs[k]=r.str7()
        if fl & F_BYTES:
            for _ in range(r.numitems()): k=r.i32(); z.bytes_[k]=r.bytearr()
    z.end = r.p
    return z

def load_chunk(path):
    data = open(path,'rb').read()
    r = R(data)
    ver = r.i16(); n = r.i32()
    zdos = [read_zdo(r, i) for i in range(n)]
    assert r.p == len(data), "trailing bytes: %d" % (len(data)-r.p)
    return ver, zdos, data

def pack_numitems(n):
    if n < 128: return bytes([n])
    return bytes([(n >> 8) | 128, n & 0xFF])

def pack_str7(s):
    b = s.encode('utf-8'); n = len(b); out = bytearray()
    while True:
        c = n & 0x7F; n >>= 7
        if n: out.append(c | 0x80)
        else: out.append(c); break
    return bytes(out) + b

def pack_small_rot(v):
    x,y,z = [int(c*2) for c in v]
    if (x <= 1 or x >= 719) and (z <= 1 or z >= 719):
        return struct.pack('<H', (y | 0x8000) & 0xFFFF)
    n = x | (y << 10) | (z << 20)
    return struct.pack('<HH', (n >> 16) & 0xFFFF, n & 0xFFFF)

def write_zdo(z):
    w = bytearray(); fl = z.flags
    w += struct.pack('<H', fl)
    if fl & F_SMALLPOS: w += struct.pack('<hh', int(z.pos[0]), int(z.pos[2]))
    else: w += struct.pack('<fff', *z.pos)
    w += struct.pack('<i', z.prefab)
    if fl & F_ROT: w += pack_small_rot(z.rot)
    if fl & 0xFF:
        if fl & F_CONN: w += struct.pack('<Bi', *z.conn)
        def items(d, enc):
            w.extend(pack_numitems(len(d)))
            for k,v in d.items():
                w.extend(struct.pack('<i', k)); w.extend(enc(v))
        if fl & F_FLOAT: items(z.floats, lambda v: struct.pack('<f', v))
        if fl & F_VEC3:  items(z.vec3s, lambda v: struct.pack('<fff', *v))
        if fl & F_QUAT:  items(z.quats, lambda v: struct.pack('<ffff', *v))
        if fl & F_INT:   items(z.ints, lambda v: struct.pack('<i', v))
        if fl & F_LONG:  items(z.longs, lambda v: struct.pack('<q', v))
        if fl & F_STR:   items(z.strs, pack_str7)
        if fl & F_BYTES: items(z.bytes_, lambda b: struct.pack('<i', len(b)) + b)
    return bytes(w)

def dump_chunk(path, ver, zdos):
    w = bytearray(struct.pack('<hi', ver, len(zdos)))
    for z in zdos: w += write_zdo(z)
    open(path,'wb').write(w)

def parse_tcdata(raw):
    dec = gzip.decompress(raw)
    r = R(dec)
    ver = r.i32(); ops = r.i32(); lastop = r.vec3(); lastrad = r.f32()
    nh = r.i32()
    modH = [False]*nh; lvl=[0.0]*nh; smo=[0.0]*nh
    for i in range(nh):
        if r.bool():
            modH[i]=True; lvl[i]=r.f32(); smo[i]=r.f32()
    npnt = r.i32()
    modP=[False]*npnt; paint=[None]*npnt
    for i in range(npnt):
        if r.bool():
            modP[i]=True; paint[i]=(r.f32(),r.f32(),r.f32(),r.f32())
    assert r.p == len(dec), "tc trailing %d" % (len(dec)-r.p)
    return dict(ver=ver, ops=ops, lastop=lastop, lastrad=lastrad, nh=nh, modH=modH, lvl=lvl, smo=smo, npnt=npnt, modP=modP, paint=paint, rawlen=len(raw), declen=len(dec))

def empty_tcdata(n=4225):
    return dict(ver=1, ops=0, lastop=(0.0,0.0,0.0), lastrad=0.0, nh=n, modH=[False]*n, lvl=[0.0]*n, smo=[0.0]*n, npnt=n, modP=[False]*n, paint=[None]*n)

def build_tcdata(tc, level=6):
    w = bytearray()
    w += struct.pack('<ii', tc['ver'], tc['ops'])
    w += struct.pack('<fff', *tc['lastop']); w += struct.pack('<f', tc['lastrad'])
    w += struct.pack('<i', tc['nh'])
    for i in range(tc['nh']):
        if tc['modH'][i]: w += b'\x01' + struct.pack('<ff', tc['lvl'][i], tc['smo'][i])
        else: w += b'\x00'
    w += struct.pack('<i', tc['npnt'])
    for i in range(tc['npnt']):
        if tc['modP'][i]: w += b'\x01' + struct.pack('<ffff', *tc['paint'][i])
        else: w += b'\x00'
    return gzip.compress(bytes(w), compresslevel=level)

def merge_tcdata(primary, secondary):
    """Union: keep primary's values, fill in vertices only secondary modified."""
    out = dict(primary)
    out['modH']=list(primary['modH']); out['lvl']=list(primary['lvl']); out['smo']=list(primary['smo'])
    out['modP']=list(primary['modP']); out['paint']=list(primary['paint'])
    for i in range(min(primary['nh'], secondary['nh'])):
        if secondary['modH'][i] and not out['modH'][i]:
            out['modH'][i]=True; out['lvl'][i]=secondary['lvl'][i]; out['smo'][i]=secondary['smo'][i]
    for i in range(min(primary['npnt'], secondary['npnt'])):
        if secondary['modP'][i] and not out['modP'][i]:
            out['modP'][i]=True; out['paint'][i]=secondary['paint'][i]
    out['ops'] = primary['ops'] + secondary['ops']
    return out

def zone_chunk_filename_prefix(zx, zy, size=1):
    x = (zx + 256) // 8; y = (zy + 256) // 8
    chunk = (x | (y << 8)) & [0xFFFF, 0xFEFE, 0xFCFC, 0xF8F8, 0xF0F0][size]
    return "%02x_%02x__%d_" % (chunk >> 8, chunk & 255, size)

def read_chunks_index(path):
    d = open(path,'rb').read(); r = R(d)
    ver = r.u16(); total = r.i32(); n = r.i32()
    entries = []
    for _ in range(n):
        entries.append([r.u16(), r.u8(), r.u32(), r.i32()])  # chunk, size, version, numZDOs
    return ver, total, entries

def write_chunks_index(path, ver, entries):
    w = bytearray(struct.pack('<Hii', ver, sum(e[3] for e in entries), len(entries)))
    for c,s,v,n in entries: w += struct.pack('<HBIi', c, s, v, n)
    open(path,'wb').write(w)

H_TC = stable_hash("_TerrainCompiler")
H_TCDATA = stable_hash("TCData")
