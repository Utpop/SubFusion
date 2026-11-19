"""核对发布产物的图标资源与版本元数据（发布前/后验收用）。

用法：python tools/verify_exe.py <exe 路径>
检查项：
  1. 有 RT_GROUP_ICON 且帧数、尺寸符合预期（缺这一项就是「白纸图标」）；
  2. 组内每一帧都能取到 RT_ICON 数据，且尺寸与组内声明一致；
  3. VS_VERSION_INFO 存在并带 FileVersion / ProductName。
"""
import struct
import sys


class PE:
    def __init__(self, path):
        self.d = open(path, "rb").read()
        e = struct.unpack_from("<I", self.d, 0x3C)[0]
        coff = e + 4
        nsec = struct.unpack_from("<H", self.d, coff + 2)[0]
        optsz = struct.unpack_from("<H", self.d, coff + 16)[0]
        opt = coff + 20
        magic = struct.unpack_from("<H", self.d, opt)[0]
        dd = opt + (112 if magic == 0x20B else 96)
        self.res_rva, _ = struct.unpack_from("<II", self.d, dd + 16)
        sec = opt + optsz
        self.secs = []
        for i in range(nsec):
            o = sec + i * 40
            vsz, va = struct.unpack_from("<II", self.d, o + 8)
            rawsz, raw = struct.unpack_from("<II", self.d, o + 16)
            self.secs.append((va, max(vsz, rawsz), raw))
        self.base = self.off(self.res_rva)

    def off(self, rva):
        for va, sz, raw in self.secs:
            if va <= rva < va + sz:
                return raw + (rva - va)
        raise ValueError("bad rva %#x" % rva)

    def entries(self):
        def walk(off, level, acc):
            named, ids = struct.unpack_from("<HH", self.d, off + 12)
            for i in range(named + ids):
                e = off + 16 + i * 8
                name_id, off2 = struct.unpack_from("<II", self.d, e)
                if off2 & 0x80000000:
                    yield from walk(self.base + (off2 & 0x7FFFFFFF), level + 1, acc + [name_id])
                else:
                    d = self.base + off2
                    rva, size = struct.unpack_from("<II", self.d, d)
                    yield acc + [name_id], rva, size
        yield from walk(self.base, 0, [])


def main(path):
    pe = PE(path)
    groups, icons, version = {}, {}, None
    for acc, rva, size in pe.entries():
        blob = pe.d[pe.off(rva):pe.off(rva) + size]
        typ, res_id = acc[0], acc[1]
        if typ == 14:
            groups[res_id] = blob
        elif typ == 3:
            icons[res_id] = blob
        elif typ == 16:
            version = blob

    print("RT_GROUP_ICON:", len(groups))
    if not groups:
        print("FAIL: exe 里没有任何图标分组（资源管理器会显示白纸图标）")
        return 1

    ok = True
    for gid, blob in groups.items():
        _, typ, count = struct.unpack_from("<HHH", blob, 0)
        sizes = []
        for i in range(count):
            w, h, c, r, planes, bpp, nbytes, nid = struct.unpack_from("<BBBBHHIH", blob, 6 + i * 14)
            w = w or 256
            sizes.append(w)
            if nid not in icons:
                print("FAIL: 组 %d 引用了不存在的图标 %d" % (gid, nid))
                ok = False
            elif len(icons[nid]) != nbytes:
                print("WARN: 组 %d 第 %d 帧声明 %d 字节，实际 %d" % (gid, i, nbytes, len(icons[nid])))
        print("  group %d: %d 帧, 尺寸 %s" % (gid, count, sizes))
        expected = {16, 20, 24, 32, 40, 48, 64, 96, 128, 256}
        missing = expected - set(sizes)
        if missing:
            print("  WARN: 缺少尺寸", sorted(missing))

    print("RT_ICON 帧数:", len(icons))
    print("VS_VERSION_INFO:", "有" if version else "无")
    if version:
        text = version.decode("utf-16-le", "ignore")
        for key in ("FileVersion", "ProductName", "ProductVersion"):
            idx = text.find(key)
            print("  %s: %s" % (key, "找到" if idx >= 0 else "缺失"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
