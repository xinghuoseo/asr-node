# -*- coding: utf-8 -*-
# ============================================================
# FunASR 中文语音识别本地服务 (sherpa-onnx INT8 版)
# 模型: Paraformer-zh(转写) + Silero-VAD(切分) + ct-transformer(标点)
#       + pyannote-seg(分段) + CAM++(声纹) 聚类 -> 说话人分离
# 特点: 全 INT8, 纯 CPU 推理, 常驻内存约 1GB (原 PyTorch 版 2.9GB)
# 监听: 127.0.0.1:8088  (仅本机可访问, 由 GUI Agent / 远程隧道调用)
#
# API:
#   GET  /health      健康检查(状态/进程内存)
#   POST /transcribe  上传 wav 文件, 返回带说话人标签的转写结果
#   POST /transcribe_b64  JSON {"audio_b64","format","id"}, 隧道转发用
#
# 隧道协议(服务器端以后实现):
#   请求  {"type":"transcribe","id":"<uuid>","audio_b64":"...","format":"wav"}
#   响应  {"type":"result","id":"<uuid>","ok":true,"data":{...}}
#   响应  {"type":"error","id":"<uuid>","ok":false,"message":"..."}
# 注意: 只接受 wav 格式(建议 16kHz 单声道), 其他格式先由 GUI/transcode.py 预转码
# ============================================================

import base64
import logging
import os
import re
import sys
import tempfile
import time
import uuid
from pathlib import Path

import numpy as np
import soundfile as sf

from fastapi import FastAPI, HTTPException, UploadFile
from pydantic import BaseModel

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    handlers=[
        logging.StreamHandler(sys.stdout),
        logging.FileHandler(
            Path(__file__).parent / "asr_server.log", encoding="utf-8"
        ),
    ],
)
log = logging.getLogger("asr_server")

HOST = "127.0.0.1"
PORT = 8088
SAMPLE_RATE = 16000
MODELS = Path(__file__).resolve().parent / "models"

app = FastAPI(title="FunASR Local ASR (sherpa-onnx)", version="2.0")

_state = {"recognizer": None, "vad_cfg": None, "punc": None, "diar": None,
          "emb": None, "ready": False, "device": "cpu"}


def process_mem_mb() -> int:
    """当前进程物理内存占用(MB)。"""
    try:
        import psutil

        return int(psutil.Process().memory_info().rss // (1024 * 1024))
    except Exception:
        return 0


def load_models():
    import sherpa_onnx

    asr_model = os.environ.get("ASR_MODEL", "qwen3")   # qwen3 | sensevoice | paraformer
    log.info("加载 sherpa-onnx 模型中... (models/ 目录, 主模型=%s)", asr_model)
    t0 = time.time()

    if asr_model == "qwen3":
        # 注: LLM 自回归解码在 CPU 上对线程数敏感, 4 是实测最优(10 线程反而慢近一倍)
        recognizer = sherpa_onnx.OfflineRecognizer.from_qwen3_asr(
            conv_frontend=str(MODELS / "qwen3" / "conv_frontend.onnx"),
            encoder=str(MODELS / "qwen3" / "encoder.int8.onnx"),
            decoder=str(MODELS / "qwen3" / "decoder.int8.onnx"),
            tokenizer=str(MODELS / "qwen3" / "tokenizer"),
            num_threads=4,
            sample_rate=SAMPLE_RATE,
            feature_dim=128,
            decoding_method="greedy_search",
            provider="cpu",
        )
    elif asr_model == "sensevoice":
        recognizer = sherpa_onnx.OfflineRecognizer.from_sense_voice(
            model=str(MODELS / "sensevoice" / "model.int8.onnx"),
            tokens=str(MODELS / "sensevoice" / "tokens.txt"),
            num_threads=4,
            sample_rate=SAMPLE_RATE,
            feature_dim=80,
            use_itn=True,
            provider="cpu",
        )
    else:
        recognizer = sherpa_onnx.OfflineRecognizer.from_paraformer(
            paraformer=str(MODELS / "paraformer" / "model.int8.onnx"),
            tokens=str(MODELS / "paraformer" / "tokens.txt"),
            num_threads=4,
            sample_rate=SAMPLE_RATE,
            feature_dim=80,
            decoding_method="greedy_search",
            provider="cpu",
        )
    _state["model_name"] = asr_model

    silero = sherpa_onnx.SileroVadModelConfig(
        model=str(MODELS / "vad" / "silero_vad.onnx"),
        threshold=0.5,
        min_silence_duration=0.5,
        min_speech_duration=0.25,
        max_speech_duration=20,
    )
    vad_cfg = sherpa_onnx.VadModelConfig()
    vad_cfg.silero_vad = silero
    vad_cfg.sample_rate = SAMPLE_RATE

    # SenseVoice / Qwen3 自带标点, 仅 paraformer 需要标点模型 (省 280MB 内存)
    punc = None
    if asr_model == "paraformer":
        punc = sherpa_onnx.OfflinePunctuation(
            sherpa_onnx.OfflinePunctuationConfig(
                model=sherpa_onnx.OfflinePunctuationModelConfig(
                    ct_transformer=str(MODELS / "punc" / "model.onnx"),
                    num_threads=2,
                    provider="cpu",
                )
            )
        )
    _state["punc"] = punc

    diar_cfg = sherpa_onnx.OfflineSpeakerDiarizationConfig(
        segmentation=sherpa_onnx.OfflineSpeakerSegmentationModelConfig(
            pyannote=sherpa_onnx.OfflineSpeakerSegmentationPyannoteModelConfig(
                model=str(MODELS / "diar" / "pyannote_segmentation.onnx"),
            ),
            num_threads=2,
            provider="cpu",
        ),
        embedding=sherpa_onnx.SpeakerEmbeddingExtractorConfig(
            model=str(MODELS / "diar" / "campplus.onnx"),
            num_threads=2,
            provider="cpu",
        ),
        clustering=sherpa_onnx.FastClusteringConfig(num_clusters=-1, threshold=0.5),
        min_duration_on=0.3,
        min_duration_off=0.5,
    )
    diar = sherpa_onnx.OfflineSpeakerDiarization(diar_cfg)
    emb = sherpa_onnx.SpeakerEmbeddingExtractor(
        sherpa_onnx.SpeakerEmbeddingExtractorConfig(
            model=str(MODELS / "diar" / "campplus.onnx"),
            num_threads=2,
            provider="cpu",
        )
    )

    _state["recognizer"] = recognizer
    _state["vad_cfg"] = vad_cfg
    _state["punc"] = punc
    _state["diar"] = diar
    _state["emb"] = emb
    _state["ready"] = True
    log.info("模型加载完成, 耗时 %.1fs, 进程内存 %d MB", time.time() - t0, process_mem_mb())


@app.get("/health")
def health():
    return {
        "status": "ok" if _state["ready"] else "loading",
        "device": _state["device"],
        "model": _state.get("model_name"),
        "mem_mb": process_mem_mb(),
        "features": ["file_transcribe", "speaker_diarization"],
    }


def _read_wav(path: str) -> np.ndarray:
    data, sr = sf.read(path, dtype="float32", always_2d=True)
    if sr != SAMPLE_RATE:
        raise HTTPException(400, f"采样率必须为 {SAMPLE_RATE}Hz, 当前 {sr}Hz (请用 transcode.py 转码)")
    mono = data.mean(axis=1)
    return np.asarray(mono, dtype=np.float32)


def _match_spk(mid_sec: float, diar_segs) -> int | None:
    for start, end, spk in diar_segs:
        if start - 0.05 <= mid_sec <= end + 0.05:
            return spk
    return None


# 相邻重复标点清理: SenseVoice 自带标点与 VAD 段边界拼接处易出现 "。，" 等
_DUP_PUNCT = re.compile(r"\s*([，,.。.！!？?；;：:])\s*(?:[，,.。.！!？?；;：:]\s*)+")


def _clean_punct(text: str) -> str:
    return _DUP_PUNCT.sub(r"\1", text).strip()


def _fallback_spk_by_embedding(segments) -> dict:
    """diarization 不可用(音频过短等)时的兜底: 对 VAD 段提取声纹做贪心聚类。"""
    ext = _state["emb"]
    centers = []
    assign = {}
    last_spk = 0
    for idx, (start_sample, seg_samples) in enumerate(segments):
        spk = None
        if len(seg_samples) >= SAMPLE_RATE * 0.5:
            try:
                e = np.asarray(
                    ext.compute(np.asarray(seg_samples, dtype=np.float32)),
                    dtype=np.float32,
                )
                best, best_sim = None, -1.0
                for ci, c in enumerate(centers):
                    sim = float(
                        np.dot(e, c)
                        / ((np.linalg.norm(e) + 1e-9) * (np.linalg.norm(c) + 1e-9))
                    )
                    if sim > best_sim:
                        best_sim, best = sim, ci
                if best is None or best_sim < 0.5:
                    centers.append(e)
                    spk = len(centers) - 1
                else:
                    spk = best
            except Exception:
                spk = None
        if spk is None:
            spk = last_spk
        last_spk = spk
        assign[idx] = spk
    return assign


def _do_transcribe(path: str) -> dict:
    import sherpa_onnx

    t0 = time.time()
    samples = _read_wav(path)
    duration = round(len(samples) / SAMPLE_RATE, 2)

    # 1) 说话人分离(整段音频)
    diar_segs = []
    try:
        result = _state["diar"].process(samples)
        for seg in result.sort_by_start_time():
            diar_segs.append((float(seg.start), float(seg.end), int(seg.speaker)))
    except Exception as e:
        log.warning("说话人分离失败(音频过短?): %s", e)

    # 2) VAD 切分 (必须逐窗喂入, 大块数组内部不处理)
    vad = sherpa_onnx.VoiceActivityDetector(_state["vad_cfg"], buffer_size_in_seconds=600)
    window = _state["vad_cfg"].silero_vad.window_size
    segments = []
    i = 0
    n = len(samples)
    while i + window <= n:
        vad.accept_waveform(samples[i:i + window])
        i += window
        while not vad.empty():
            seg = vad.front
            segments.append((seg.start, seg.samples))
            vad.pop()
    vad.flush()
    while not vad.empty():
        seg = vad.front
        segments.append((seg.start, seg.samples))
        vad.pop()

    # 3) 批量解码全部语音段(一次交给引擎) + 标点 + 说话人匹配
    if not diar_segs and segments:
        # diarization 不可用 -> 段级声纹聚类兜底
        fallback_map = _fallback_spk_by_embedding(segments)
    else:
        fallback_map = None
    streams = []
    metas = []
    for start_sample, seg_samples in segments:
        st = _state["recognizer"].create_stream()
        st.accept_waveform(SAMPLE_RATE, seg_samples)
        streams.append(st)
        metas.append((start_sample, len(seg_samples)))
    if streams:
        _state["recognizer"].decode_streams(streams)

    sentences = []
    speakers = set()
    full_parts = []
    for seg_idx, ((start_sample, seg_len), st) in enumerate(zip(metas, streams)):
        raw = (st.result.text or "").strip()
        if not raw:
            continue
        punc = _state["punc"]
        if punc is not None:
            try:
                raw = punc.add_punctuation(raw).strip()
            except Exception:
                pass
        text = _clean_punct(raw)
        if not text:
            continue
        start_sec = start_sample / SAMPLE_RATE
        end_sec = start_sec + seg_len / SAMPLE_RATE
        mid = (start_sec + end_sec) / 2
        if fallback_map is not None:
            spk = fallback_map.get(seg_idx)
        else:
            spk = _match_spk(mid, diar_segs)
        sentences.append({
            "text": text,
            "start": int(start_sec * 1000),
            "end": int(end_sec * 1000),
            "spk": spk,
        })
        if spk is not None:
            speakers.add(spk)
        full_parts.append(text)

    return {
        "text": "".join(full_parts),
        "sentences": sentences,
        "speakers": sorted(speakers),
        "duration_sec": duration,
        "elapsed_sec": round(time.time() - t0, 2),
    }


def _ensure_wav(fmt: str):
    if (fmt or "").lower().lstrip(".") != "wav":
        raise HTTPException(
            400,
            "仅支持 wav 格式, 请先用 transcode.py 或 ffmpeg 转码: "
            "ffmpeg -i in.mp3 -ar 16000 -ac 1 out.wav",
        )


@app.post("/transcribe")
async def transcribe(file: UploadFile):
    if not _state["ready"]:
        raise HTTPException(503, "模型尚未加载完成")
    suffix = Path(file.filename or "audio.wav").suffix or ".wav"
    _ensure_wav(suffix)
    data = await file.read()
    if not data:
        raise HTTPException(400, "空文件")

    fd, path = tempfile.mkstemp(suffix=suffix)
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(data)
        result = _do_transcribe(path)
    except HTTPException:
        raise
    except Exception as e:
        log.exception("转写失败")
        raise HTTPException(500, f"转写失败: {e}")
    finally:
        try:
            os.unlink(path)
        except OSError:
            pass
    result["id"] = str(uuid.uuid4())
    log.info("转写完成: id=%s 时长=%s 耗时=%ss", result["id"], result["duration_sec"], result["elapsed_sec"])
    return result


@app.post("/shutdown")
def shutdown():
    """供 GUI 停止本服务 (仅监听 127.0.0.1, 无外部暴露风险)。"""
    os._exit(0)


class TranscribeB64Req(BaseModel):
    """隧道转发用: 音频 base64, 方便 GUI Agent 与服务器端转发。"""
    audio_b64: str
    format: str = "wav"
    id: str | None = None


def _run_b64(audio_b64: str, fmt: str, req_id: str | None) -> dict:
    _ensure_wav(fmt)
    if not _state["ready"]:
        raise HTTPException(503, "模型尚未加载完成")
    try:
        data = base64.b64decode(audio_b64)
    except Exception:
        raise HTTPException(400, "audio_b64 不是合法的 base64")
    if not data:
        raise HTTPException(400, "空音频")
    fd, path = tempfile.mkstemp(suffix="." + (fmt or "wav").lstrip("."))
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(data)
        result = _do_transcribe(path)
    except HTTPException:
        raise
    except Exception as e:
        log.exception("转写失败")
        raise HTTPException(500, f"转写失败: {e}")
    finally:
        try:
            os.unlink(path)
        except OSError:
            pass
    result["id"] = req_id or str(uuid.uuid4())
    log.info("转写完成: id=%s 时长=%s 耗时=%ss", result["id"], result["duration_sec"], result["elapsed_sec"])
    return result


@app.post("/transcribe_b64")
def transcribe_b64(req: TranscribeB64Req):
    return _run_b64(req.audio_b64, req.format, req.id)


def main():
    import uvicorn

    load_models()
    uvicorn.run(app, host=HOST, port=PORT, log_level="warning")


if __name__ == "__main__":
    main()
