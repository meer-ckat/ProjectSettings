#!/usr/bin/env python3
"""음원 분석 도구 (numpy 필요). 입력 wav는 export_wav.cs.txt로 Unity에서 뽑는다.

  sections <song.wav> --bpm 122 --offset 0.35         마디별 음량 → 조용/큰 구간 나누기
  draft    <song.wav> <out.json> --bpm 122 --offset 0.35 [--grid 4]
           소리가 튀는 지점(온셋)을 그리드에 붙여 '어려움' 초안 채보를 만든다.
           쉬운 난이도는 chart_tool.py thin으로 이 초안에서 파생한다.
"""
import argparse
import json
import wave

import numpy as np


def read_wav(path):
    w = wave.open(path)
    rate, ch, width = w.getframerate(), w.getnchannels(), w.getsampwidth()
    raw = w.readframes(w.getnframes())
    x = np.frombuffer(raw, dtype={1: np.int8, 2: np.int16, 4: np.int32}[width]).astype(np.float32)
    x = x.reshape(-1, ch).mean(axis=1) / float(2 ** (8 * width - 1))
    return x, rate


def sections(path, bpm, offset):
    x, rate = read_wav(path)
    bar = 4 * 60.0 / bpm
    rms = []
    b = 0
    while True:
        s0, s1 = int((offset + b * bar) * rate), int((offset + (b + 1) * bar) * rate)
        if s1 > len(x):
            break
        rms.append(float(np.sqrt(np.mean(x[s0:s1] ** 2))))
        b += 1
    rms = np.array(rms)
    # 곡 전체 최대의 55% 미만 = 조용한 구간. 연속 구간으로 묶어서 출력
    level = np.where(rms < rms.max() * 0.55, '조용', np.where(rms > rms.max() * 0.85, '큼', '중간'))
    start = 0
    for i in range(1, len(level) + 1):
        if i == len(level) or level[i] != level[start]:
            print(f"마디 {start:3}~{i - 1:3}  {offset + start * bar:6.1f}s~{offset + i * bar:6.1f}s  "
                  f"{level[start]}  평균음량 {rms[start:i].mean():.3f}")
            start = i


def draft(path, out, bpm, offset, grid):
    x, rate = read_wav(path)
    n_fft, hop = 2048, 512
    win = np.hanning(n_fft)
    frames = 1 + (len(x) - n_fft) // hop
    idx = np.arange(n_fft)[None, :] + hop * np.arange(frames)[:, None]
    mag = np.log1p(np.abs(np.fft.rfft(x[idx] * win, axis=1)))
    freqs = np.fft.rfftfreq(n_fft, 1.0 / rate)
    flux = np.maximum(np.diff(mag, axis=0), 0)  # 스펙트럼 증가량 = 소리가 새로 나는 정도
    env = flux.sum(axis=1)
    times = (np.arange(1, frames) * hop + n_fft / 2) / rate

    # 봉우리: 주변 ±3프레임 최대 + 1초 이동 평균보다 확실히 큼
    k = max(1, int(rate / hop))
    mean = np.convolve(env, np.ones(k) / k, mode='same')
    std = np.sqrt(np.convolve((env - mean) ** 2, np.ones(k) / k, mode='same'))
    peaks = [i for i in range(3, len(env) - 3)
             if env[i] == env[i - 3:i + 4].max() and env[i] > mean[i] + 0.8 * std[i]]

    beat = 60.0 / bpm
    step = 1.0 / grid
    cells = {}  # 그리드 칸 → (세기, 소리 높이)
    for i in peaks:
        g = (times[i] - offset) / beat
        q = round(g / step) * step
        if abs(g - q) * beat > 0.06 or q < 0:  # 칸에서 60ms 넘게 벗어난 소리는 버림
            continue
        centroid = float((flux[i] * freqs).sum() / max(flux[i].sum(), 1e-9))
        s = float(env[i])
        if q not in cells or s > cells[q][0]:
            cells[q] = (s, centroid)

    if not cells:
        raise SystemExit('온셋을 못 찾음. bpm/offset 확인')
    keys = sorted(cells)
    cents = np.array([cells[q][1] for q in keys])
    lo, hi = np.percentile(cents, [33, 67])
    # 레인 = 소리 높이: 낮음 A, 중간 S, 높음 D. 같은 레인 연타는 chart_tool thin/검수 단계에서 푼다
    notes, prev_b = [], 0.0
    for q in keys:
        b = q + offset / beat  # 게임 박(음악 0초 기준)
        c = cells[q][1]
        lane = 0 if c < lo else 2 if c > hi else 1
        notes.append({'interval': round(b - prev_b, 4), 'input': lane, 'message': ''})
        prev_b = round(b, 4)
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump({'bpm': bpm, 'gridOffset': offset, 'noteList': notes}, f, ensure_ascii=False, indent=4)
    print(f"초안 {len(notes)}개 → {out}")


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest='cmd', required=True)
    s = sub.add_parser('sections')
    s.add_argument('wav')
    d = sub.add_parser('draft')
    d.add_argument('wav')
    d.add_argument('out')
    d.add_argument('--grid', type=int, default=4, help='4=16분, 2=8분')
    for p in (s, d):
        p.add_argument('--bpm', type=float, required=True)
        p.add_argument('--offset', type=float, required=True, help='첫 마디 시작(초). 노트 에디터 오프셋 값')
    a = ap.parse_args()
    if a.cmd == 'sections':
        sections(a.wav, a.bpm, a.offset)
    else:
        draft(a.wav, a.out, a.bpm, a.offset, a.grid)


if __name__ == '__main__':
    main()
