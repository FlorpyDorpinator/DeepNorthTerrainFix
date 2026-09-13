#!/usr/bin/env python3
"""
Surgical repair for a Valheim 1.0 chunked world save (.chunks/.chunk/.db2/.fwl2/.ok).

Never edits the input directory. Writes a complete copy to --out.

Operations (all optional, combinable):
  --dedupe-tc                 Merge duplicate _TerrainCompiler ZDOs that share a zone (keeps the richest,
                              unions the others' modified vertices into it, deletes the rest).
  --reset-tc ZX,ZY[;ZX,ZY]    Wipe the terrain-modification block (TCData) of the given zone(s) but keep
                              every other ZDO (buildings, chests, portals...). Terrain returns to worldgen.
  --restore-tc-from DIR       Take TCData for --zones from another save of the same world (e.g. a backup
                              taken before the corruption) and put it into this save's compiler for that zone.
  --zones ZX,ZY[;ZX,ZY]       Zones for --restore-tc-from.
  --merge                     With --restore-tc-from: union backup data with current data instead of replacing.

Example:
  python fix_save.py --in TheCapitolCorrupt --out TheCapitolCorrupt_repaired --dedupe-tc \
      --restore-tc-from "C:/path/to/backup/TheCapitolCorrupt" --zones "9,132;9,133"
"""
import argparse, os, shutil, glob, sys, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import vhchunk as v

def parse_zones(s):
    out = []
    for part in (s or '').split(';'):
        part = part.strip()
        if not part: continue
        x, y = part.split(',')
        out.append((int(x), int(y)))
    return out

def find_chunk_for_zone(save_dir, zone):
    """Return path of the .chunk file that holds `zone` in save_dir (any chunk size), or None."""
    for size in (1, 2, 3, 0, 4):
        prefix = v.zone_chunk_filename_prefix(zone[0], zone[1], size)
        hits = glob.glob(os.path.join(save_dir, prefix + '*.chunk'))
        if hits:
            # highest version wins if several exist
            hits.sort(key=lambda p: int(os.path.basename(p).rsplit('_', 1)[1].split('.')[0]))
            return hits[-1]
    return None

def tc_richness(z):
    tc = v.parse_tcdata(z.bytes_[v.H_TCDATA])
    return (tc['ops'], sum(tc['modH']) + sum(tc['modP']), len(z.bytes_[v.H_TCDATA]), -z.idx), tc

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--in', dest='src', required=True)
    ap.add_argument('--out', dest='dst', required=True)
    ap.add_argument('--dedupe-tc', action='store_true')
    ap.add_argument('--reset-tc', default='')
    ap.add_argument('--restore-tc-from', default='')
    ap.add_argument('--zones', default='')
    ap.add_argument('--merge', action='store_true')
    a = ap.parse_args()

    src = os.path.abspath(a.src); dst = os.path.abspath(a.dst)
    if os.path.exists(dst) and os.listdir(dst):
        print('ERROR: output dir exists and is not empty:', dst); sys.exit(2)
    os.makedirs(dst, exist_ok=True)

    idx_files = glob.glob(os.path.join(src, '_main.*.chunks'))
    if len(idx_files) != 1:
        print('ERROR: expected exactly one _main.N.chunks in', src); sys.exit(2)
    idx_path = idx_files[0]
    ver, total, entries = v.read_chunks_index(idx_path)
    print(f'index: version {ver}, {total} ZDOs in {len(entries)} chunks')

    # Which chunk files do we need to touch?
    touched = {}  # chunk path -> (ver, zdos)
    def load(path):
        if path not in touched:
            cver, zdos, _ = v.load_chunk(path)
            touched[path] = [cver, zdos]
        return touched[path]

    reset_zones = parse_zones(a.reset_tc)
    restore_zones = parse_zones(a.zones)

    # 1. dedupe across all chunks
    removed_total = 0
    if a.dedupe_tc:
        for path in sorted(glob.glob(os.path.join(src, '*.chunk'))):
            cur = load(path)
            zdos = cur[1]
            by_zone = collections.defaultdict(list)
            for z in zdos:
                if z.prefab == v.H_TC and v.H_TCDATA in z.bytes_:
                    by_zone[z.zone()].append(z)
            dups = {zn: lst for zn, lst in by_zone.items() if len(lst) > 1}
            if not dups:
                del touched[path]
                continue
            keep_ids = set()
            for zn, lst in dups.items():
                ranked = sorted(((tc_richness(z), z) for z in lst), key=lambda t: t[0], reverse=True)
                (_, best_tc), best = ranked[0]
                merged = best_tc
                for (_, tc), other in ranked[1:]:
                    merged = v.merge_tcdata(merged, tc)
                best.bytes_[v.H_TCDATA] = v.build_tcdata(merged)
                keep_ids.add(id(best))
                print(f'  {os.path.basename(path)} zone {zn}: {len(lst)} compilers -> 1 (kept idx {best.idx}, ops {best_tc["ops"]}, merged ops {merged["ops"]})')
            before = len(cur[1])
            cur[1] = [z for z in cur[1] if not (z.prefab == v.H_TC and z.zone() in dups and id(z) not in keep_ids)]
            removed_total += before - len(cur[1])
        print(f'dedupe: removed {removed_total} duplicate terrain compilers')

    # 2. reset TC for zones
    for zn in reset_zones:
        path = find_chunk_for_zone(src, zn)
        if not path: print('  reset: no chunk for zone', zn); continue
        cur = load(path)
        hit = [z for z in cur[1] if z.prefab == v.H_TC and z.zone() == zn]
        if not hit: print('  reset: zone', zn, 'has no terrain compiler (nothing to reset)'); continue
        for z in hit:
            old = v.parse_tcdata(z.bytes_[v.H_TCDATA])
            z.bytes_[v.H_TCDATA] = v.build_tcdata(v.empty_tcdata(old['nh']))
            print(f'  reset: zone {zn} compiler idx {z.idx}: ops {old["ops"]}, heights {sum(old["modH"])}, paint {sum(old["modP"])} -> cleared')

    # 3. restore TC from backup save
    if a.restore_tc_from:
        bsrc = os.path.abspath(a.restore_tc_from)
        for zn in restore_zones:
            bpath = find_chunk_for_zone(bsrc, zn)
            if not bpath: print('  restore: backup has no chunk for zone', zn); continue
            _, bz, _ = v.load_chunk(bpath)
            btc = [z for z in bz if z.prefab == v.H_TC and z.zone() == zn and v.H_TCDATA in z.bytes_]
            if not btc: print('  restore: backup has no terrain compiler in zone', zn); continue
            btc.sort(key=lambda z: tc_richness(z)[0], reverse=True)
            bdata = v.parse_tcdata(btc[0].bytes_[v.H_TCDATA])
            path = find_chunk_for_zone(src, zn)
            if not path: print('  restore: current save has no chunk for zone', zn); continue
            cur = load(path)
            hit = [z for z in cur[1] if z.prefab == v.H_TC and z.zone() == zn]
            if not hit:
                # clone backup compiler ZDO into current chunk
                nz = btc[0]; nz.idx = -1
                cur[1].append(nz); hit = [nz]
                print(f'  restore: zone {zn} had no compiler, added one from backup')
            target = hit[0]
            cdata = v.parse_tcdata(target.bytes_[v.H_TCDATA])
            newdata = v.merge_tcdata(bdata, cdata) if a.merge else bdata
            target.bytes_[v.H_TCDATA] = v.build_tcdata(newdata)
            print(f'  restore: zone {zn}: current ops {cdata["ops"]} (h{sum(cdata["modH"])}/p{sum(cdata["modP"])}) '
                  f'<- backup ops {bdata["ops"]} (h{sum(bdata["modH"])}/p{sum(bdata["modP"])}) '
                  f'{"merged" if a.merge else "replaced"} -> ops {newdata["ops"]} (h{sum(newdata["modH"])}/p{sum(newdata["modP"])})')

    # 4. write everything
    for name in os.listdir(src):
        sp = os.path.join(src, name); dp = os.path.join(dst, name)
        if os.path.isdir(sp): continue
        if sp in touched:
            cver, zdos = touched[sp]
            v.dump_chunk(dp, cver, zdos)
            # verify
            v2, z2, _ = v.load_chunk(dp)
            assert len(z2) == len(zdos)
            # update index entry
            base = name.split('.')[0]  # e.g. 30_20__1_75
            hi, lo, rest = base.split('_', 2)
            chunk = (int(hi, 16) << 8) | int(lo, 16)
            size, version = rest.lstrip('_').split('_')
            for e in entries:
                if e[0] == chunk and e[1] == int(size) and e[2] == int(version):
                    e[3] = len(zdos)
            print(f'wrote {name}: {len(zdos)} ZDOs')
        elif name.endswith('.chunks'):
            continue
        else:
            shutil.copy2(sp, dp)
    v.write_chunks_index(os.path.join(dst, os.path.basename(idx_path)), ver, entries)
    print('index rewritten; total ZDOs now', sum(e[3] for e in entries))
    print('done ->', dst)

if __name__ == '__main__':
    main()
