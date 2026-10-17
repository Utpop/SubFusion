# -*- coding: utf-8 -*-
"""字幕视频批量合并工具 —— 核心逻辑

职责：工具定位、媒体探测、字幕与视频自动匹配、文本编码检测与转换、
ffmpeg 命令构建、任务执行与进度解析。本模块不依赖任何 GUI 组件。
"""

import codecs
import difflib
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor, as_completed

APP_NAME = "字幕视频批量合并工具"

VIDEO_EXTS = {
    ".mp4", ".mkv", ".mov", ".m4v", ".avi", ".wmv", ".flv", ".f4v",
    ".webm", ".ts", ".m2ts", ".mpg", ".mpeg", ".vob",
}

TEXT_SUB_EXTS = {".srt", ".ass", ".ssa", ".vtt"}
BITMAP_SUB_EXTS = {".sup", ".sub"}
SUB_EXTS = TEXT_SUB_EXTS | BITMAP_SUB_EXTS

CREATE_NO_WINDOW = 0x08000000

CONFIG_PATH = os.path.join(os.path.expanduser("~"), ".subtitle_merger.json")

# --------------------------------------------------------------------------
# 语言识别
# --------------------------------------------------------------------------
LANG_HINTS = {
    "zh": "chi", "chs": "chi", "cht": "chi", "zh-cn": "chi", "zh-hans": "chi",
    "zh-hant": "chi", "zh-tw": "chi", "zh-hk": "chi", "cn": "chi", "sc": "chi",
    "tc": "chi", "gb": "chi", "big5": "chi", "chi": "chi", "zho": "chi",
    "简体": "chi", "繁體": "chi", "繁体": "chi", "中文": "chi", "中字": "chi",
    "chs-eng": "chi", "zh-eng": "chi",

    "en": "eng", "eng": "eng", "english": "eng", "en-us": "eng", "en-gb": "eng",
    "英文": "eng", "英语": "eng", "英文字幕": "eng",

    "jp": "jpn", "ja": "jpn", "jpn": "jpn", "japanese": "jpn", "日文": "jpn",
    "日语": "jpn", "日語": "jpn",

    "ko": "kor", "kr": "kor", "kor": "kor", "korean": "kor", "韩文": "kor",
    "韓文": "kor", "韩语": "kor",

    "fr": "fre", "fre": "fre", "fra": "fre", "french": "fre", "法文": "fre",
    "de": "ger", "ger": "ger", "deu": "ger", "german": "ger", "德文": "ger",
    "es": "spa", "spa": "spa", "spanish": "spa", "西文": "spa",
    "ru": "rus", "rus": "rus", "russian": "rus", "俄文": "rus",
    "pt": "por", "por": "por", "pt-br": "por",
    "it": "ita", "ita": "ita", "italian": "ita",
    "ar": "ara", "ara": "ara", "arabic": "ara",
    "th": "tha", "tha": "tha", "泰文": "tha",
    "vi": "vie", "vie": "vie",
    "id": "ind", "ind": "ind",
    "tr": "tur", "tur": "tur",
}

LANG_NAMES = {
    "chi": "中文", "eng": "英语", "jpn": "日语", "kor": "韩语", "fre": "法语",
    "ger": "德语", "spa": "西班牙语", "rus": "俄语", "por": "葡萄牙语",
    "ita": "意大利语", "ara": "阿拉伯语", "tha": "泰语", "vie": "越南语",
    "ind": "印尼语", "tur": "土耳其语", "und": "未指定",
}

LANG_CHOICES = [
    ("chi", "中文"), ("eng", "英语"), ("jpn", "日语"), ("kor", "韩语"),
    ("und", "未指定"), ("fre", "法语"), ("ger", "德语"), ("spa", "西班牙语"),
    ("rus", "俄语"), ("por", "葡萄牙语"), ("ita", "意大利语"), ("ara", "阿拉伯语"),
    ("tha", "泰语"), ("vie", "越南语"), ("ind", "印尼语"), ("tur", "土耳其语"),
]


# --------------------------------------------------------------------------
# 工具定位
# --------------------------------------------------------------------------
def load_config():
    try:
        with open(CONFIG_PATH, "r", encoding="utf-8") as fh:
            return json.load(fh)
    except Exception:
        return {}


def save_config(cfg):
    try:
        with open(CONFIG_PATH, "w", encoding="utf-8") as fh:
            json.dump(cfg, fh, ensure_ascii=False, indent=2)
    except Exception:
        pass


def bundled_dir():
    """PyInstaller 打包后资源所在目录（单文件模式是临时解压目录）。"""
    base = getattr(sys, "_MEIPASS", None)
    if base:
        return base
    if getattr(sys, "frozen", False):
        return os.path.dirname(os.path.abspath(sys.executable))
    return None


def _locate(name, cfg_key):
    cfg = load_config()
    candidates = []
    if cfg.get(cfg_key):
        candidates.append(cfg[cfg_key])
    env = os.environ.get(("FFMPEG_PATH" if name.startswith("ffmpeg") else "FFPROBE_PATH"))
    if env:
        candidates.append(env)

    # 打包进 exe 的内置版本（保证开箱即用）
    base = bundled_dir()
    if base:
        candidates.append(os.path.join(base, name + (".exe" if os.name == "nt" else "")))
        candidates.append(os.path.join(base, "bin",
                                       name + (".exe" if os.name == "nt" else "")))

    here = os.path.dirname(os.path.abspath(__file__))
    for sub in (here, os.path.join(here, "bin"), os.path.join(here, "ffmpeg", "bin")):
        candidates.append(os.path.join(sub, name + (".exe" if os.name == "nt" else "")))

    which = shutil.which(name)
    if which:
        candidates.append(which)

    if os.name == "nt":
        for root in ("C:/ffmpeg/bin", "D:/ffmpeg/bin",
                     "C:/Program Files/ffmpeg/bin"):
            candidates.append(os.path.join(root, name + ".exe"))

    for path in candidates:
        if path and os.path.isfile(path):
            return path
    return None


def find_ffmpeg():
    return _locate("ffmpeg", "ffmpeg")


def find_ffprobe():
    return _locate("ffprobe", "ffprobe")


def ffmpeg_capabilities(ffmpeg):
    """返回 {'libass': bool, 'x264': bool, 'x265': bool, 'nvenc': bool}"""
    caps = {"libass": False, "x264": False, "x265": False, "nvenc": False}
    if not ffmpeg:
        return caps
    try:
        proc = subprocess.run([ffmpeg, "-hide_banner", "-filters"],
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                              timeout=30,
                              creationflags=CREATE_NO_WINDOW if os.name == "nt" else 0)
        text = proc.stdout.decode("utf-8", "replace")
        caps["libass"] = " subtitles " in text or "\tsubtitles " in text
    except Exception:
        pass
    try:
        proc = subprocess.run([ffmpeg, "-hide_banner", "-encoders"],
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                              timeout=30,
                              creationflags=CREATE_NO_WINDOW if os.name == "nt" else 0)
        text = proc.stdout.decode("utf-8", "replace")
        caps["x264"] = "libx264" in text
        caps["x265"] = "libx265" in text
        caps["nvenc"] = "h264_nvenc" in text
    except Exception:
        pass
    return caps


def ffmpeg_version(ffmpeg):
    if not ffmpeg:
        return ""
    try:
        proc = subprocess.run([ffmpeg, "-version"], stdout=subprocess.PIPE,
                              stderr=subprocess.STDOUT, timeout=20,
                              creationflags=CREATE_NO_WINDOW if os.name == "nt" else 0)
        first = proc.stdout.decode("utf-8", "replace").splitlines()
        return first[0].strip() if first else ""
    except Exception:
        return ""


# --------------------------------------------------------------------------
# 文件收集
# --------------------------------------------------------------------------
def collect_files(paths, recursive=True):
    """返回 (videos, subtitles, skipped)"""
    videos, subs, skipped = [], [], []
    seen_v, seen_s = set(), set()

    def add(path):
        ext = os.path.splitext(path)[1].lower()
        real = os.path.abspath(path)
        if ext in VIDEO_EXTS:
            if real not in seen_v:
                seen_v.add(real)
                videos.append(real)
            return True
        if ext in SUB_EXTS:
            if real not in seen_s:
                seen_s.add(real)
                subs.append(real)
            return True
        skipped.append(real)
        return False

    for path in paths:
        if os.path.isfile(path):
            add(path)
        elif os.path.isdir(path):
            if recursive:
                for root, dirs, names in os.walk(path):
                    dirs[:] = [d for d in dirs if not d.startswith(".")]
                    for name in sorted(names):
                        add(os.path.join(root, name))
            else:
                for name in sorted(os.listdir(path)):
                    full = os.path.join(path, name)
                    if os.path.isfile(full):
                        add(full)

    videos.sort(key=lambda p: p.lower())
    subs.sort(key=lambda p: p.lower())
    return videos, subs, skipped


# --------------------------------------------------------------------------
# 文件名匹配
# --------------------------------------------------------------------------
NOISE_RE = re.compile(
    # 两侧用「非字母数字」断言而不是 \b：中文紧邻时也能命中（如「影片1080p」），
    # 同时不会破坏包含这些片段的正常词（如 nosub 里的 sub、english 里的 eng）。
    r"(?<![a-z0-9])"
    r"(480p|720p|1080p|1440p|2160p|4320p|4k|8k|bluray|blu-?ray|bdrip|brrip|"
    r"webrip|web-?dl|webdl|hdrip|hdtv|hddvd|dvdrip|dvdscr|remux|x264|x265|"
    r"h264|h265|hevc|xvid|divx|aac|ac3|eac3|dts|dd5|ddp5|truehd|atmos|"
    r"10bit|8bit|hdr|hdr10|sdr|dolby|vision|dv|amzn|nf|dsnp|hmax|max|"
    r"proper|repack|internal|extended|unrated|imax|directors|cut|"
    r"mp4|mkv|avi|mov|webm|ts)"
    r"(?![a-z0-9])"
)

CJK_RE = re.compile(r"[\u4e00-\u9fff\u3040-\u30ff\uac00-\ud7af]")


def strip_lang_tokens(tokens):
    """从 token 列表中移除语言标记，返回 (剩余 tokens, 检测到的语言代码)"""
    lang = None
    rest = []
    for tok in tokens:
        key = tok.lower()
        if key in LANG_HINTS:
            if lang is None:
                lang = LANG_HINTS[key]
            continue
        rest.append(tok)
    return rest, lang


def tokenize(name):
    """把文件名拆成规范化 token 列表"""
    base = os.path.splitext(os.path.basename(name))[0]
    # 去掉括号类内容
    base = re.sub(r"\[[^\]]*\]|\([^)]*\)|\{[^}]*\}", " ", base)
    base = base.lower()
    base = NOISE_RE.sub(" ", base)
    # 保留中中日韩文字、字母、数字
    base = re.sub(r"[^\u4e00-\u9fff\u3040-\u30ff\uac00-\ud7af0-9a-z]+", " ", base)
    tokens = [t for t in base.split() if t]
    return tokens


def normalize_name(name):
    tokens, _lang = strip_lang_tokens(tokenize(name))
    return " ".join(tokens)


def detect_lang_from_name(path):
    _, lang = strip_lang_tokens(tokenize(path))
    if lang:
        return lang
    # 次级判断：文件名里出现中文字符 -> chi，日文假名 -> jpn，韩文 -> kor
    base = os.path.basename(path)
    if re.search(r"[\u3040-\u30ff]", base):
        return "jpn"
    if re.search(r"[\uac00-\ud7af]", base):
        return "kor"
    if re.search(r"[\u4e00-\u9fff]", base):
        return "chi"
    return "und"


def similarity(a, b):
    if not a or not b:
        return 0.0
    if a == b:
        return 1.0
    if a in b or b in a:
        # 包含关系：按长度比例给分，避免「一个短词匹配上超长名字」
        short, long_name = (a, b) if len(a) <= len(b) else (b, a)
        if len(short) < 3:
            return 0.0
        ratio = len(short) / float(len(long_name))
        return 0.72 + 0.22 * min(1.0, ratio)
    return difflib.SequenceMatcher(None, a, b).ratio()


def match_subtitles(videos, subtitles, threshold=0.72, one_sub_per_video=False):
    """把字幕匹配到视频。

    返回 (mapping: {video: [sub_path...]}, unmatched_subs: [sub_path...])
    """
    mapping = {v: [] for v in videos}
    norm_videos = [(v, normalize_name(v)) for v in videos]

    for sub in subtitles:
        sub_norm = normalize_name(sub)
        best_video, best_score = None, 0.0
        for video, v_norm in norm_videos:
            score = similarity(v_norm, sub_norm)
            if score > best_score:
                best_score, best_video = score, video
        if best_video is not None and best_score >= threshold:
            if one_sub_per_video and mapping[best_video]:
                continue
            mapping[best_video].append((sub, best_score))

    for video in mapping:
        mapping[video].sort(key=lambda item: item[0].lower())
        mapping[video] = [item[0] for item in mapping[video]]

    matched = set()
    for subs in mapping.values():
        matched.update(subs)
    unmatched = [s for s in subtitles if s not in matched]
    return mapping, unmatched


# --------------------------------------------------------------------------
# 文本编码检测与转换
# --------------------------------------------------------------------------
CANDIDATE_ENCODINGS = ("utf-8", "gb18030", "big5", "shift_jis", "euc-jp",
                       "euc-kr", "cp1252")

_UTF8_CACHE = {}

COMMON_SIMPLIFIED = (
    "的一是不了在人有我他这个们中来上大为和国地到以说时要就出会可也你对生能而子那得于着下"
    "自之年过发后作里用道行所然家种事成方多经么去法学如都同现当没动面起看定天分还进好小"
    "部其些主样理心她本前开但因只从想实日军者意无力它与长把机十民第公此已工使情明性知全"
    "三又关点正业外将两高间由问很最重并物手应战向头文体政美相见被利什二等产或新己制身果"
    "加西斯月话合回特代内信表化老给世位次度门任常先海通教儿原东声提立及比员解水名真论处"
    "走义各入几口认条平系气题活尔更别打女变四神总何电数安少报才结反受目太量再感建务做接"
    "必场件计管期市直德资命山金指克许统区保至队形社便空决治展马科司五基眼书非则听白却界"
    "达光放强即像难且权思王象完设式色路记南品住告类求据程北边死张该交规万取拉格望觉术领"
    "共确传师观清今切院让识候带导争运笑飞风步改收根干造言联持组每济车亲极林服快办议往元"
    "英士证近失转夫令准布始怎呢存未远叫台单影具罗字爱击流备兵连调深商算质团集百需价花党"
    "华城石级整府离况亚请技际约示复病息究线似官火断精满支视消越器容照须九增研写称企八功"
    "吗包片史委乎查轻易早曾除农找装广显吧阿李标谈吃图念六引历首医局突专费号尽另周较注语"
    "仅考落青随选列武红响虽推势参希古众构房半节土投某案黑维革划敌致陈律足态护七兴派孩验"
    "责营星够章音跟志底站严例留调"  # noqa: E501
)

COMMON_TRADITIONAL = (
    "的一是不了在人有我他這個們中來上大為和國地到以說時要就出會可也你對生能而子那得於著下"
    "自之年過發後作裡用道行所然家種事成方多經麼去法學如都同現當沒動面起看定天分還進好小"
    "部其些主樣理心她本前開但因只從想實日軍者意無力它與長把機十民第公此已工使情明性知全"
    "三又關點正業外將兩高間由問很最重並物手應戰向頭文體政美相見被利什二等產或新己制身果"
    "加西斯月話合回特代內信表化老給世位次度門任常先海通教兒原東聲提立及比員解水名真論處"
    "走義各入幾口認條平係氣題活爾更別打女變四神總何電數安少報才結反受目太量再感建務做接"
    "必場件計管期市直德資命山金指克許統區保至隊形社便空決治展馬科司五基眼書非則聽白卻界"
    "達光放強即像難且權思王象完設式色路記南品住告類求據程北邊死張該交規萬取拉格望覺術領"
    "共確傳師觀清今切院讓識候帶導爭運笑飛風步改收根幹造言聯持組每濟車親極林服快辦議往元"
    "英士證近失轉夫令準布始怎呢存未遠叫臺單影具羅字愛擊流備兵連調深商算質團集百需價花黨"
    "華城石級整府離況亞請技際約示復病息究線似官火斷精滿支視消越器容照須九增研寫稱企八功"
)


def _score_text(text, encoding):
    """给解码结果打分，越高越可能是正确编码。"""
    if not text:
        return -1.0
    sample = text[:4000]
    total = len(sample)
    if total == 0:
        return -1.0

    bad = 0
    cjk = 0
    kana = 0
    hangul = 0
    for ch in sample:
        code = ord(ch)
        if code < 0x20 and ch not in "\r\n\t":
            bad += 1
        elif 0x7F <= code <= 0x9F:
            bad += 1
        elif code in (0xFFFD,):
            bad += 10
        elif 0x4E00 <= code <= 0x9FFF:
            cjk += 1
        elif 0x3040 <= code <= 0x30FF:
            kana += 1
        elif 0xAC00 <= code <= 0xD7AF:
            hangul += 1

    score = -bad * 5.0
    if encoding == "utf-8":
        score += 30.0  # 能严格解码 utf-8 就很可能是 utf-8
    elif encoding != "utf-8":
        score += cjk * 0.35 + kana * 0.4 + hangul * 0.4

    if cjk:
        simp_hits = sum(1 for ch in COMMON_SIMPLIFIED if ch in sample)
        trad_hits = sum(1 for ch in COMMON_TRADITIONAL if ch in sample)
        if encoding in ("gb18030",):
            score += simp_hits * 0.6 - trad_hits * 0.25
        elif encoding in ("big5",):
            score += trad_hits * 0.6 - simp_hits * 0.25
        elif encoding in ("shift_jis", "euc-jp"):
            score += kana * 0.5
        elif encoding in ("euc-kr",):
            score += hangul * 0.5
    return score


def _looks_like_utf16(raw):
    """无 BOM 的 UTF-16 启发式判断。

    字幕里 ASCII（时间码、序号）占多数，其 UTF-16 表示总有一个字节为 0，
    且固定落在偶/奇位置，据此可区分 UTF-16-BE / LE。
    """
    sample = raw[:4000]
    if len(sample) < 8:
        return None
    pairs = len(sample) // 2
    if pairs < 4:
        return None
    zeros_even = sum(1 for i in range(0, pairs * 2 - 1, 2) if sample[i] == 0)
    zeros_odd = sum(1 for i in range(1, pairs * 2, 2) if sample[i] == 0)
    ratio_even = zeros_even / float(pairs)
    ratio_odd = zeros_odd / float(pairs)
    if ratio_even > 0.55 and ratio_odd < 0.15:
        return "utf-16-be"
    if ratio_odd > 0.55 and ratio_even < 0.15:
        return "utf-16-le"
    return None


def detect_encoding(path, sample_size=262144):
    """检测文本字幕编码，返回 (encoding, has_bom)。"""
    try:
        with open(path, "rb") as fh:
            raw = fh.read(sample_size)
    except OSError:
        return "utf-8", False

    for bom, enc in ((codecs.BOM_UTF8, "utf-8-sig"),
                     (codecs.BOM_UTF32_LE, "utf-32"),
                     (codecs.BOM_UTF32_BE, "utf-32"),
                     (codecs.BOM_UTF16_LE, "utf-16"),
                     (codecs.BOM_UTF16_BE, "utf-16")):
        if raw.startswith(bom):
            return enc, True

    # 无 BOM 的 UTF-16（不少老字幕工具会这么存）
    if raw.count(b"\x00") > max(2, len(raw) // 8):
        guess = _looks_like_utf16(raw)
        if guess:
            return guess, False

    try:
        raw.decode("utf-8")
        return "utf-8", False
    except UnicodeDecodeError:
        pass

    best, best_score = "utf-8", -1e9
    for enc in CANDIDATE_ENCODINGS:
        if enc == "utf-8":
            continue
        try:
            text = raw.decode(enc, errors="strict")
        except (UnicodeDecodeError, LookupError):
            continue
        score = _score_text(text, enc)
        if score > best_score:
            best_score, best = score, enc
    return best, False


def is_text_subtitle(path):
    return os.path.splitext(path)[1].lower() in TEXT_SUB_EXTS


def ensure_utf8(path, temp_dir, use_cache=True):
    """把文本字幕转成 UTF-8（无 BOM）。已经是 UTF-8 则返回原路径。

    返回 (新路径, 是否创建了临时文件, 原编码)
    同一字幕在多个任务中被重复引用时，缓存可避免重复检测与写入。
    """
    if not is_text_subtitle(path):
        return path, False, ""

    key = (os.path.abspath(path), os.path.abspath(temp_dir))
    if use_cache and key in _UTF8_CACHE:
        return _UTF8_CACHE[key]

    result = _convert_to_utf8(path, temp_dir)
    if use_cache and result[1]:
        _UTF8_CACHE[key] = result
    return result


def clear_utf8_cache():
    """清空转码缓存（每轮批量处理结束后调用）。"""
    _UTF8_CACHE.clear()


def _convert_to_utf8(path, temp_dir):
    encoding, has_bom = detect_encoding(path)
    if encoding == "utf-8" and not has_bom:
        return path, False, encoding

    try:
        with open(path, "rb") as fh:
            raw = fh.read()
    except OSError:
        return path, False, encoding

    try:
        text = raw.decode(encoding, errors="replace")
    except LookupError:
        text = raw.decode("utf-8", errors="replace")

    if has_bom and text.startswith("\ufeff"):
        text = text[1:]

    try:
        os.makedirs(temp_dir, exist_ok=True)
    except OSError:
        return path, False, encoding

    name = os.path.basename(path)
    stem, ext = os.path.splitext(name)
    out_path = os.path.join(temp_dir, "%s_utf8%s" % (stem, ext))
    try:
        with open(out_path, "w", encoding="utf-8", newline="") as fh:
            fh.write(text)
        return out_path, True, encoding
    except OSError:
        return path, False, encoding


# --------------------------------------------------------------------------
# 媒体探测
# --------------------------------------------------------------------------
def prepare_hard_subs(tracks, work_dir, convert=True):
    """硬字幕专用的字幕文件预处理：复制到工作目录并使用 ASCII 文件名。

    libass 对带冒号/反斜杠的 Windows 路径转义非常挑剔，这里统一规避。
    返回 (可用的 SubTrack 列表, 失败列表[(文件名, 原因)])。
    """
    prepared = []
    failed = []
    try:
        os.makedirs(work_dir, exist_ok=True)
    except OSError as exc:
        return [], [(t.name, "无法创建工作目录：%s" % exc) for t in tracks]

    for index, track in enumerate(tracks):
        ext = os.path.splitext(track.path)[1].lower() or ".srt"
        target = os.path.join(work_dir, "hardsub_%d%s" % (index, ext))
        try:
            if convert and is_text_subtitle(track.path):
                encoding, _has_bom = detect_encoding(track.path)
                with open(track.path, "rb") as fh:
                    raw = fh.read()
                try:
                    text = raw.decode(encoding, errors="replace")
                except LookupError:
                    text = raw.decode("utf-8", errors="replace")
                if text.startswith("\ufeff"):
                    text = text[1:]
                with open(target, "w", encoding="utf-8", newline="") as fh:
                    fh.write(text)
            else:
                shutil.copyfile(track.path, target)
        except OSError as exc:
            failed.append((track.name, str(exc)))
            continue
        clone = track.clone()
        clone.path = target
        prepared.append(clone)
    return prepared, failed


def probe_many(ffprobe, paths, workers=4, on_result=None, cancel_event=None):
    """并发探测多个文件的流信息，返回 {path: info}。

    on_result(path, info) 会在有结果时立即回调（工作线程中调用，UI 需自行投递）。
    """
    results = {}
    if not ffprobe or not paths:
        return results

    workers = max(1, min(8, int(workers)))

    def work(path):
        return path, probe_streams(ffprobe, path)

    try:
        with ThreadPoolExecutor(max_workers=workers) as pool:
            futures = {pool.submit(work, p): p for p in paths}
            for future in as_completed(futures):
                if cancel_event is not None and cancel_event.is_set():
                    for pending in futures:
                        pending.cancel()
                    break
                try:
                    path, info = future.result()
                except Exception:
                    continue
                results[path] = info
                if on_result:
                    try:
                        on_result(path, info)
                    except Exception:
                        pass
    except Exception:
        pass
    return results


def probe_subtitle(ffprobe, path, timeout=20):
    """检查字幕文件能否被 ffmpeg 识别。

    空文件、乱码文件、损坏的图形字幕都会在这里被挡掉，
    避免它们把一个本来正常的视频任务整体拖失败。
    返回 (是否可用, 说明)
    """
    if not ffprobe:
        return True, ""
    if not os.path.isfile(path):
        return False, "文件不存在"
    try:
        if os.path.getsize(path) == 0:
            return False, "文件为空"
    except OSError:
        return False, "无法读取"

    cmd = [ffprobe, "-v", "error", "-select_streams", "s",
           "-show_entries", "stream=index,codec_name", "-of", "csv=p=0", path]
    try:
        proc = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                              timeout=timeout,
                              creationflags=CREATE_NO_WINDOW if os.name == "nt" else 0)
    except Exception as exc:
        return True, "校验失败（按可用处理）：%s" % exc

    out = proc.stdout.decode("utf-8", "replace").strip()
    if proc.returncode != 0 or not out:
        err = proc.stderr.decode("utf-8", "replace").strip().splitlines()
        return False, (err[-1][:80] if err else "没有可识别的字幕流")
    return True, out


def probe_streams(ffprobe, path, timeout=30):
    """返回 {'duration': float|None, 'vstreams': int, 'astreams': int,
             'sstreams': int, 'vcodec': str, 'acodec': str, 'width': int,
             'height': int}"""
    info = {"duration": None, "vstreams": 0, "astreams": 0, "sstreams": 0,
            "vcodec": "", "acodec": "", "width": 0, "height": 0, "size": 0}
    if not ffprobe:
        return info
    cmd = [ffprobe, "-v", "error", "-show_entries",
           "stream=index,codec_type,codec_name,width,height",
           "-show_entries", "format=duration,size",
           "-of", "json", path]
    try:
        proc = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                              timeout=timeout,
                              creationflags=CREATE_NO_WINDOW if os.name == "nt" else 0)
        data = json.loads(proc.stdout.decode("utf-8", "replace") or "{}")
    except Exception:
        return info

    for stream in data.get("streams", []):
        kind = stream.get("codec_type")
        if kind == "video":
            info["vstreams"] += 1
            if info["vstreams"] == 1:
                info["vcodec"] = stream.get("codec_name", "") or ""
                info["width"] = stream.get("width") or 0
                info["height"] = stream.get("height") or 0
        elif kind == "audio":
            info["astreams"] += 1
            if info["astreams"] == 1:
                info["acodec"] = stream.get("codec_name", "") or ""
        elif kind == "subtitle":
            info["sstreams"] += 1

    try:
        fmt = data.get("format", {})
        duration = fmt.get("duration")
        if duration not in (None, "", "N/A"):
            info["duration"] = float(duration)
        if fmt.get("size"):
            info["size"] = int(fmt["size"])
    except (TypeError, ValueError):
        pass
    return info


# --------------------------------------------------------------------------
# 任务数据结构
# --------------------------------------------------------------------------
class SubTrack(object):
    __slots__ = ("path", "lang", "title", "default", "forced", "kind",
                 "encoding", "enabled")

    def __init__(self, path, lang="und", title="", default=False, forced=False,
                 kind="text", encoding="", enabled=True):
        self.path = path
        self.lang = lang
        self.title = title
        self.default = default
        self.forced = forced
        self.kind = kind
        self.encoding = encoding
        self.enabled = enabled

    @property
    def name(self):
        return os.path.basename(self.path)

    def label(self):
        lang = LANG_NAMES.get(self.lang, self.lang or "")
        text = self.name
        if lang:
            text = "%s（%s）" % (text, lang)
        if self.default:
            text += " [默认]"
        if self.forced:
            text += " [强制]"
        return text

    def clone(self):
        return SubTrack(self.path, self.lang, self.title, self.default,
                        self.forced, self.kind, self.encoding, self.enabled)


class Job(object):
    STATE_PENDING = "待处理"
    STATE_RUNNING = "处理中"
    STATE_DONE = "完成"
    STATE_FAILED = "失败"
    STATE_SKIPPED = "跳过"
    STATE_CANCELLED = "已取消"

    def __init__(self, video, subs, output, duration=None, info=None):
        self.video = video
        self.subs = subs or []
        self.output = output
        self.duration = duration
        self.info = info or {}
        self.state = Job.STATE_PENDING
        self.progress = 0.0
        self.speed = ""
        self.eta = ""
        self.message = ""
        self.started_at = None
        self.finished_at = None
        self.size = None
        self.verify_ok = None       # None=未校验 / True=通过 / False=有异常
        self.verify_issues = []

    @property
    def name(self):
        return os.path.basename(self.video)


# --------------------------------------------------------------------------
# ffmpeg 命令构建
# --------------------------------------------------------------------------
class Options(object):
    """一次批量处理的全部选项。"""

    def __init__(self):
        self.container = "mkv"          # mkv / mp4 / same
        self.mode = "soft"              # soft / hard
        self.vcodec = "copy"            # copy / libx264 / libx265 / h264_nvenc
        self.acodec = "copy"            # copy / aac
        self.crf = 20
        self.preset = "medium"
        self.keep_existing_subs = True
        self.keep_metadata = True
        self.convert_encoding = True
        self.output_dir = ""
        self.suffix = ""
        self.overwrite = True
        self.workers = 1
        self.hard_multi = True          # 硬字幕时是否叠加多个字幕轨
        self.verify = True              # 处理完成后校验输出


def build_output_path(video, options, output_dir):
    stem, ext = os.path.splitext(os.path.basename(video))
    if options.container == "same":
        new_ext = ext.lower()
    else:
        new_ext = "." + options.container
    name = stem + (options.suffix or "") + new_ext
    out_dir = output_dir or os.path.dirname(video)
    path = os.path.join(out_dir, name)
    # 绝不允许覆盖源文件
    if os.path.abspath(path) == os.path.abspath(video):
        path = os.path.join(out_dir, stem + (options.suffix or "_sub") + new_ext)
    return path


def build_command(job, options, ffmpeg):
    """返回 (cmd_list, notes_list, plan)。

    notes 用于提示用户被跳过的轨；plan 描述输出应有的结构，供 verify_output 校验。
    """
    notes = []
    cmd = [ffmpeg]
    if options.overwrite:
        cmd.append("-y")
    cmd += ["-hide_banner", "-loglevel", "error", "-nostdin",
            "-progress", "pipe:1"]

    cmd += ["-i", job.video]

    usable_subs = []
    for track in job.subs:
        if not track.enabled:
            continue
        if options.container == "mp4" and track.kind == "bitmap":
            notes.append("MP4 不支持图形字幕（%s），已跳过" % track.name)
            continue
        if options.mode == "hard" and track.kind == "bitmap":
            notes.append("硬字幕暂不支持图形字幕（%s），已跳过" % track.name)
            continue
        usable_subs.append(track)

    if not usable_subs:
        notes.append("没有可用的字幕轨")

    for track in usable_subs:
        cmd += ["-i", track.path]

    if options.mode == "hard":
        tracks_to_burn = usable_subs if options.hard_multi else usable_subs[:1]
    else:
        tracks_to_burn = usable_subs

    existing_subs = 0
    if options.mode == "soft" and options.keep_existing_subs:
        existing_subs = job.info.get("sstreams", 0) or 0

    plan = {
        "mode": options.mode,
        "container": options.container,
        "expected_video": 1,
        "expected_audio": job.info.get("astreams", 0) or 0,
        "expected_subs": 0 if options.mode == "hard" else (len(usable_subs) + existing_subs),
        "burned_subs": len(tracks_to_burn) if options.mode == "hard" else 0,
        "vcodec": options.vcodec,
        "acodec": options.acodec,
    }

    if options.mode == "hard":
        _build_hard(cmd, job, options, usable_subs)
    else:
        _build_soft(cmd, job, options, usable_subs)

    if not options.keep_metadata:
        cmd += ["-map_metadata", "-1"]

    cmd.append(job.output)
    return cmd, notes, plan


def check_integrity(ffmpeg, path, timeout=60):
    """快速完整性检查：解码开头与结尾各一小段。

    只读头部是发现不了「文件被截断」的（流结构看起来完全正常），
    从尾部 seek 读取一小段才能暴露数据不全。开销很小。
    """
    issues = []
    if not ffmpeg or not os.path.isfile(path):
        return issues

    probes = ((["-t", "1"], "开头"), (["-sseof", "-2", "-t", "2"], "结尾"))
    for extra, label in probes:
        cmd = ([ffmpeg, "-hide_banner", "-loglevel", "error", "-nostdin"]
               + extra + ["-i", path, "-f", "null", "-"])
        try:
            proc = subprocess.run(cmd, stdout=subprocess.PIPE,
                                  stderr=subprocess.PIPE, timeout=timeout,
                                  creationflags=CREATE_NO_WINDOW if os.name == "nt" else 0)
        except Exception:
            continue          # 超时/无法启动时不误报
        err_text = proc.stderr.decode("utf-8", "replace").strip()
        bad = proc.returncode != 0
        if not bad and err_text:
            # ffmpeg 读截断文件时不一定返回非 0，但会打出这类提示
            lowered = err_text.lower()
            bad = any(k in lowered for k in ("prematurely", "invalid data",
                                             "truncat", "corrupt",
                                             "error while decoding"))
        if bad:
            err_lines = err_text.splitlines()
            keywords = ("prematurely", "invalid", "truncat", "corrupt", "error")
            reason = ""
            for line in err_lines:
                low = line.lower()
                if any(k in low for k in keywords):
                    reason = line[:70]
                    break
            if not reason:
                reason = err_lines[-1][:70] if err_lines else "读取失败"
            issues.append("输出文件%s数据不完整或无法解码（%s）" % (label, reason))
    return issues


def verify_output(ffprobe, output, plan, source_info=None, ffmpeg=None,
                  deep=True, timeout=30):
    """校验输出文件是否符合预期。

    返回 (ok, issues, detail)。ffprobe 不可用时返回通过，避免误报。
    deep=True 时额外做一次开头/结尾的快速解码检查（能发现截断文件）。
    """
    issues = []
    detail = {}
    if not ffprobe:
        return True, issues, detail

    if not os.path.isfile(output):
        return False, ["输出文件不存在"], detail

    try:
        size = os.path.getsize(output)
    except OSError:
        size = 0
    detail["size"] = size
    if size <= 0:
        return False, ["输出文件为空（0 字节）"], detail

    info = probe_streams(ffprobe, output, timeout=timeout)
    detail.update(info)

    if not info.get("vstreams") and plan.get("expected_video", 1):
        issues.append("输出中没有视频流")

    expected_subs = plan.get("expected_subs")
    actual_subs = info.get("sstreams", 0)
    if expected_subs is not None and actual_subs != expected_subs:
        issues.append("字幕轨数量不符（期望 %d 条，实际 %d 条）"
                      % (expected_subs, actual_subs))

    expected_audio = plan.get("expected_audio") or 0
    if expected_audio and not info.get("astreams"):
        issues.append("源文件有 %d 条音轨，输出中没有音轨" % expected_audio)

    vcodec = plan.get("vcodec")
    if vcodec and vcodec != "copy" and info.get("vcodec"):
        wanted = {"libx264": "h264", "libx265": "hevc",
                  "h264_nvenc": "h264", "hevc_nvenc": "hevc"}.get(vcodec)
        if wanted and info["vcodec"] != wanted:
            issues.append("视频编码为 %s，预期 %s" % (info["vcodec"], wanted))

    if source_info:
        src_dur = source_info.get("duration")
        out_dur = info.get("duration")
        if src_dur and out_dur:
            tolerance = max(2.0, src_dur * 0.05)
            if abs(out_dur - src_dur) > tolerance:
                issues.append("时长偏差较大（源 %.1f 秒，输出 %.1f 秒）"
                              % (src_dur, out_dur))

        # 纯封装（不重编码）时体积不该明显缩水，缩太多说明数据不完整
        if plan.get("vcodec") == "copy" and plan.get("acodec") == "copy":
            src_size = source_info.get("size") or 0
            if src_size > 0 and size < src_size * 0.5:
                issues.append("输出体积（%s）明显小于源文件（%s），可能数据不完整"
                              % (format_size(size), format_size(src_size)))

    if deep and ffmpeg:
        issues.extend(check_integrity(ffmpeg, output))

    detail["issues"] = issues
    return (not issues), issues, detail


def _build_hard(cmd, job, options, usable_subs):
    """烧录字幕：视频必须重编码。"""
    if not usable_subs:
        # 没有字幕就原样转封装
        cmd += ["-map", "0:v:0", "-map", "0:a?", "-map", "0:s?"]
        _append_av_codec(cmd, options, force_video_encode=False)
        return

    tracks = usable_subs if options.hard_multi else usable_subs[:1]

    chains = []
    prev = "[0:v:0]"
    for index, track in enumerate(tracks):
        out_tag = "[vout]" if index == len(tracks) - 1 else "[vs%d]" % index
        # 字幕文件已复制到工作目录并使用 ASCII 文件名，避免 Windows 路径转义问题
        chains.append("%ssubtitles=filename='%s'%s"
                      % (prev, os.path.basename(track.path), out_tag))
        prev = out_tag

    cmd += ["-filter_complex", ";".join(chains)]
    cmd += ["-map", "[vout]", "-map", "0:a?"]
    _append_av_codec(cmd, options, force_video_encode=True)


def _build_soft(cmd, job, options, usable_subs):
    cmd += ["-map", "0:v:0", "-map", "0:a?"]
    existing = 0
    if options.keep_existing_subs:
        existing = job.info.get("sstreams", 0)
        cmd += ["-map", "0:s?"]

    for i, _track in enumerate(usable_subs):
        cmd += ["-map", "%d:0" % (i + 1)]

    _append_av_codec(cmd, options, force_video_encode=False)

    if options.container == "mp4":
        scodec = "mov_text"
    else:
        scodec = "copy"
    cmd += ["-c:s", scodec]

    for i, track in enumerate(usable_subs):
        index = existing + i
        if track.lang:
            cmd += ["-metadata:s:s:%d" % index, "language=%s" % track.lang]
        title = track.title or LANG_NAMES.get(track.lang, "")
        if title:
            cmd += ["-metadata:s:s:%d" % index, "title=%s" % title]
        disposition = "default" if track.default else "0"
        if track.forced:
            disposition = "forced"
        cmd += ["-disposition:s:%d" % index, disposition]


def _append_av_codec(cmd, options, force_video_encode=False):
    vcodec = options.vcodec
    if force_video_encode and vcodec == "copy":
        vcodec = "libx264"
    if vcodec == "copy":
        cmd += ["-c:v", "copy"]
    else:
        cmd += ["-c:v", vcodec]
        if vcodec in ("libx264", "libx265", "h264_nvenc", "hevc_nvenc"):
            cmd += ["-preset", options.preset]
            if vcodec in ("libx264", "libx265"):
                cmd += ["-crf", str(options.crf)]
            else:
                cmd += ["-rc", "vbr", "-cq", str(options.crf)]
        cmd += ["-pix_fmt", "yuv420p"]

    if options.acodec == "copy":
        cmd += ["-c:a", "copy"]
    else:
        cmd += ["-c:a", options.acodec]


# --------------------------------------------------------------------------
# 执行与进度
# --------------------------------------------------------------------------
def format_duration(seconds):
    if not seconds or seconds <= 0:
        return "--:--"
    seconds = float(seconds)
    hours = int(seconds // 3600)
    minutes = int((seconds % 3600) // 60)
    secs = int(seconds % 60)
    if hours:
        return "%d:%02d:%02d" % (hours, minutes, secs)
    return "%02d:%02d" % (minutes, secs)


def format_size(num):
    if not num:
        return "-"
    num = float(num)
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if num < 1024 or unit == "TB":
            if unit == "B":
                return "%d B" % int(num)
            return "%.2f %s" % (num, unit)
        num /= 1024.0
    return "-"


def _popen_kwargs():
    kwargs = {"stdout": subprocess.PIPE, "stderr": subprocess.PIPE,
              "stdin": subprocess.DEVNULL,
              "text": True, "encoding": "utf-8", "errors": "replace",
              "bufsize": 1}
    if os.name == "nt":
        kwargs["creationflags"] = CREATE_NO_WINDOW
    return kwargs


class ProgressState(object):
    """解析 ffmpeg `-progress pipe:1` 的输出。"""

    def __init__(self):
        self.position = 0.0
        self.speed = 0.0

    def feed(self, line):
        line = (line or "").strip()
        if not line or "=" not in line:
            return
        key, _, value = line.partition("=")
        value = value.strip()
        if key == "out_time_us":
            try:
                self.position = int(value) / 1000000.0
            except ValueError:
                pass
        elif key == "out_time_ms":
            try:
                self.position = int(value) / 1000.0
            except ValueError:
                pass
        elif key == "out_time":
            try:
                parts = value.split(":")
                if len(parts) == 3:
                    self.position = (int(parts[0]) * 3600 + int(parts[1]) * 60
                                     + float(parts[2]))
            except ValueError:
                pass
        elif key == "speed":
            try:
                self.speed = float(value.rstrip("x"))
            except ValueError:
                self.speed = 0.0

    def percent(self, duration):
        if duration and duration > 0:
            return min(100.0, self.position / duration * 100.0)
        return 0.0

    def eta(self, duration):
        if self.speed > 0 and duration and duration > self.position:
            return (duration - self.position) / self.speed
        return None


def run_ffmpeg(cmd, duration, on_progress=None, cancel_event=None, log_lines=None,
               max_log=40, cwd=None):
    """执行 ffmpeg，解析进度。

    on_progress(percent, speed, eta_seconds)
    返回 (returncode, error_text)
    """
    kwargs = _popen_kwargs()
    if cwd:
        kwargs["cwd"] = cwd
    try:
        proc = subprocess.Popen(cmd, **kwargs)
    except Exception as exc:
        return -1, str(exc)

    stderr_lines = []

    def read_stderr():
        try:
            for line in proc.stderr:
                stderr_lines.append(line.rstrip("\n"))
                if len(stderr_lines) > max_log:
                    stderr_lines.pop(0)
        except Exception:
            pass

    reader = threading.Thread(target=read_stderr, daemon=True)
    reader.start()

    state = ProgressState()
    last_emit = 0.0

    try:
        for line in proc.stdout:
            if cancel_event is not None and cancel_event.is_set():
                try:
                    proc.terminate()
                except Exception:
                    pass
                break
            state.feed(line)

            now = time.time()
            if on_progress and (now - last_emit) > 0.25:
                last_emit = now
                on_progress(state.percent(duration), state.speed,
                            state.eta(duration))

        proc.wait()
    except Exception:
        try:
            proc.kill()
        except Exception:
            pass
    finally:
        try:
            proc.stdout.close()
        except Exception:
            pass
        reader.join(timeout=5)

    if log_lines is not None:
        log_lines.extend(stderr_lines)

    error_text = "\n".join(stderr_lines[-8:]).strip()
    return proc.returncode, error_text
