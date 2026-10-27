# -*- coding: utf-8 -*-
# 转写验证脚本: 用 data/ai_news_audio 里的音频测试 8088 服务
# 用法: .venv\Scripts\python.exe test_asr.py <音频路径, 缺省取双主播样段>
import base64
import json
import sys
import urllib.request
from pathlib import Path

def main():
    path = sys.argv[1] if len(sys.argv) > 1 else str(
        Path(__file__).parent.parent / "data" / "ai_news_audio" / "seg_01_F.mp3"
    )
    print("INPUT:", path, flush=True)
    audio = base64.b64encode(Path(path).read_bytes()).decode("ascii")
    req = urllib.request.Request(
        "http://127.0.0.1:8088/transcribe_b64",
        data=json.dumps({"audio_b64": audio, "format": Path(path).suffix.lstrip(".") or "mp3"}).encode(),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=600) as r:
        res = json.loads(r.read().decode())
    print(f"时长: {res.get('duration_sec')}s  耗时: {res.get('elapsed_sec')}s  说话人: {res.get('speakers')}")
    print("-" * 60)
    for s in res.get("sentences", []):
        print(f"[spk{s.get('spk')}] {s.get('text')}")
    if not res.get("sentences"):
        print(res.get("text"))

if __name__ == "__main__":
    main()
