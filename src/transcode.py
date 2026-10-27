# -*- coding: utf-8 -*-
# ============================================================
# 本地预转码工具: 任意音频 -> 16kHz 单声道 wav (asr_server 只接受 wav)
# 用法:
#   转单个文件:  .venv\Scripts\python.exe transcode.py <文件路径>
#   转整个目录:  .venv\Scripts\python.exe transcode.py <目录路径>
#   指定输出目录: .venv\Scripts\python.exe transcode.py <路径> <输出目录>
# 输出文件名 = 原文件名 + ".wav", 已存在则覆盖
# ============================================================

import subprocess
import sys
from pathlib import Path

import imageio_ffmpeg

AUDIO_EXTS = {".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma", ".wav"}


def convert(src: Path, out_dir: Path) -> Path:
    dst = out_dir / (src.stem + ".wav")
    cmd = [
        imageio_ffmpeg.get_ffmpeg_exe(), "-y", "-i", str(src),
        "-ar", "16000", "-ac", "1", "-vn", str(dst),
    ]
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0:
        raise RuntimeError(r.stderr.strip().splitlines()[-1] if r.stderr else "ffmpeg 失败")
    return dst


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    target = Path(sys.argv[1])
    out_dir = Path(sys.argv[2]) if len(sys.argv) > 2 else target.parent
    out_dir.mkdir(parents=True, exist_ok=True)

    files = (
        [p for p in target.iterdir() if p.suffix.lower() in AUDIO_EXTS]
        if target.is_dir()
        else [target]
    )
    if not files:
        print(f"没有找到可转码的音频: {target}")
        sys.exit(1)

    ok = 0
    for f in files:
        try:
            dst = convert(f, out_dir)
            print(f"[OK] {f.name} -> {dst}")
            ok += 1
        except Exception as e:
            print(f"[FAIL] {f.name}: {e}")
    print(f"完成 {ok}/{len(files)}")


if __name__ == "__main__":
    main()
