# NotoSansKR TTF(약 6MB/개, 한자 포함 전체 CJK) → 한글/라틴 서브셋 woff2 변환.
# 요구: pip install fonttools brotli
# 실행: python scripts/subset-fonts.py  (Twms2.Server 디렉토리 기준 상대경로)
# 원본 TTF 는 용량 문제로 저장소에서 제거됨 — 재생성하려면
# https://fonts.google.com/noto/specimen/Noto+Sans+KR 에서 받아 files/ 에 놓고 실행.
#
# 유지 범위 — 한글 음절 전체(U+AC00-D7A3)를 통째로 유지하므로 어떤 한글 텍스트도 깨지지 않는다.
# 빠지는 것은 한자·가나·키릴 등 비한글 스크립트뿐이며, 해당 글자는 시스템 폰트(맑은 고딕)로 폴백.
#
# Material Symbols(아이콘 폰트)는 서브셋하지 않는다: 리가처 기반이라 a-z 글리프를 유지하는 한
# GSUB 클로저가 모든 아이콘을 끌고 와 실효가 없고, 코드포인트 방식은 마크업 전면 수정이 필요.
import subprocess
import sys
from pathlib import Path

FILES_DIR = Path(__file__).resolve().parent.parent / "wwwroot" / "lib" / "fonts" / "files"

UNICODES = ",".join([
    "U+0000-00FF",   # Basic Latin + Latin-1
    "U+1100-11FF",   # Hangul Jamo
    "U+2000-206F",   # General Punctuation (– — ‘ ’ “ ” … ‰ 등)
    "U+20A0-20CF",   # Currency (₩)
    "U+2190-21FF",   # Arrows
    "U+2460-24FF",   # Enclosed Alphanumerics (①②)
    "U+2500-25FF",   # Box Drawing + Geometric Shapes (■ ● ▲)
    "U+3000-303F",   # CJK Symbols and Punctuation (、。「」·)
    "U+3130-318F",   # Hangul Compatibility Jamo (ㄱ-ㅣ)
    "U+AC00-D7A3",   # Hangul Syllables 전체 (11,172자)
    "U+FF00-FFEF",   # Halfwidth/Fullwidth Forms (％ ｜)
])

WEIGHTS = ["Light", "Regular", "Medium", "Bold"]

for w in WEIGHTS:
    src = FILES_DIR / f"NotoSansKR-{w}.ttf"
    dst = FILES_DIR / f"NotoSansKR-{w}-subset.woff2"
    if not src.exists():
        print(f"skip: {src} 없음", file=sys.stderr)
        continue
    subprocess.run([
        sys.executable, "-m", "fontTools.subset", str(src),
        f"--unicodes={UNICODES}",
        "--flavor=woff2",
        "--layout-features=*",
        f"--output-file={dst}",
    ], check=True)
    print(f"{src.name} {src.stat().st_size:>9,}B -> {dst.name} {dst.stat().st_size:>9,}B")
