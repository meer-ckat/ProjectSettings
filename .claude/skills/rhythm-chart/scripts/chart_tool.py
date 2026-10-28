#!/usr/bin/env python3
"""채보 JSON 도구.

  stats  <chart.json> [--compare other.json]   난이도·위험 패턴 리포트
  thin   <src.json> <out.json> --profile easy|normal|hard   기존 채보에서 쉬운 난이도 파생 (hard = 원본 다듬기)
  foot   <src.json> <out.json> --profile pump_easy|pump_normal|pump_hard   손 채보에서 펌프(발판 4칸) 채보 만들기

채보 형식(게임 NoteManager가 읽는 것):
  { "bpm": 122.0, "gridOffset": 0.35, "noteList": [ {"interval": 1.0, "input": 1, "message": ""} ] }
  interval = 앞 노트와의 간격(박), 0이면 동시치기. input 0/1/2 = 레인 A/B/C = 키보드 A/S/D, 3 = 펌프(F키).
  펌프 모드는 <곡>_pump_<easy|normal|hard>.json 발판 전용 채보를 쓴다(input 0~3 = 발판 4칸 = A/S/D/F 키).
  게임은 음악 0초부터 interval을 누적한다. gridOffset은 에디터 그리드(마디 시작)용.
"""
import argparse
import json
import sys
from collections import Counter

LANES = 'ASDF'

# 판정: perfect 0.1s, great 0.2s, good 0.3s. Good/Miss면 콤보가 끊긴다.
# 같은 레인 노트가 0.2s 안쪽이면 늦게 친 입력이 다음 노트를 가져가 Miss가 연쇄된다 → JACK_RISK
JACK_RISK = 0.2

PROFILES = {
    # grid: 허용 박자 칸(2=8분, 4=16분, 12=16분+셋잇단). min_gap: 이벤트 사이 최소 간격(박과 초 둘 다 만족)
    # same_lane: 같은 레인 최소 간격. max_in_2s: 2초 창 최대 노트 수
    # chord_space_beats: 동시치기는 앞뒤 노트와 이만큼 떨어져 있을 때만 남긴다 (빠른 줄기 속 동시치기가 제일 잘 끊김)
    'easy': dict(grid=2, min_gap_beats=0.5, min_gap_s=0.30, chords=False,
                 same_lane_beats=1.0, same_lane_s=0.45, max_in_2s=6),
    'normal': dict(grid=4, min_gap_beats=0.25, min_gap_s=0.18, chords=True, max_chords_per_bar=1,
                   chord_space_beats=0.5, chord_min_strength=3, triple_min_strength=99,  # 세 키 동시치기 없음
                   # 2초 8개 = 초당 4개. 10이면 빠른 곡(138BPM 이상)에서 8분 줄기가 통째로 남아 어려움과 같아진다
                   same_lane_beats=0.5, same_lane_s=0.25, max_in_2s=8),
    # 어려움 '다듬기': 리듬(셋잇단 포함)은 거의 그대로, 32분 같은 초고속·빠른 줄기 속 동시치기·0.2초 미만 같은 레인 연타만 고친다
    # chord_min_strength 3 = 동시치기는 정박에만(뒷박 동시치기 줄기가 A+D 연타가 됨), triple_min_strength 4 = 세 키는 마디 첫 박에만
    'hard': dict(grid=12, min_gap_beats=0.25, min_gap_s=0.11, chords=True, max_chords_per_bar=4,
                 chord_space_beats=0.5, chord_min_strength=3, triple_min_strength=4,
                 same_lane_beats=0.25, same_lane_s=JACK_RISK, max_in_2s=16),
    # 펌프(발판) 리듬 고르기. 발은 손보다 느리다: 쉬움·보통 4분음표(2초 2개·4개), 어려움 8분까지(2초 5개). 동시치기 = 점프(양발)
    # 레인은 foot()이 발 교대에 맞춰 다시 정하므로 same_lane 규칙은 끈다
    # alt_s: 스텝 간격이 1박 또는 이 초보다 짧으면 왼발·오른발을 번갈아 (빠른 곡은 1박이 0.35초라 박 기준만으론 부족)
    # 2026-10 발판으로 밟아 보고 너무 어렵다고 해서 한 단계씩 낮춤: 어려움 = 옛 보통 수준
    'pump_easy': dict(grid=1, min_gap_beats=1.0, min_gap_s=0.55, chords=False,
                      same_lane_beats=0, same_lane_s=0, max_in_2s=2, alt_s=0.8),
    'pump_normal': dict(grid=1, min_gap_beats=1.0, min_gap_s=0.40, chords=True, max_chords_per_bar=1,
                        chord_space_beats=2.0, chord_min_strength=4, triple_min_strength=99,  # 점프는 마디 첫 박, 앞뒤 2박 비었을 때만
                        same_lane_beats=0, same_lane_s=0, max_in_2s=4, alt_s=0.8),
    'pump_hard': dict(grid=2, min_gap_beats=0.5, min_gap_s=0.30, chords=True, max_chords_per_bar=1,
                      chord_space_beats=1.0, chord_min_strength=4, triple_min_strength=99,  # 점프는 마디 첫 박에만
                      same_lane_beats=0, same_lane_s=0, max_in_2s=5, alt_s=0.8),
}


def load(path):
    d = json.load(open(path, encoding='utf-8-sig'))
    t = 0.0
    # 같은 박 노트를 한 이벤트로: {'b': 박, 'lanes': [...], 'msgs': [...]}
    # 메시지는 목록: 동시치기 노트마다 다른 메시지가 달려 있을 수 있다 (게임은 노트 순서대로 다 보낸다)
    events = []
    for n in d['noteList']:
        t += n['interval']
        msgs = [n['message']] if n.get('message') else []
        if events and abs(events[-1]['b'] - t) < 1e-4:
            events[-1]['lanes'].append(n['input'])
            events[-1]['msgs'] += msgs
        else:
            events.append({'b': t, 'lanes': [n['input']], 'msgs': msgs})
    return d, events


def save(path, d, events):
    out, prev = [], 0.0
    for e in sorted(events, key=lambda e: e['b']):
        b = round(e['b'], 4)
        lanes = sorted(set(e['lanes']))
        if len(e['msgs']) > len(lanes):  # 노트 하나에 메시지 하나뿐. 조용히 버리지 않는다
            raise ValueError(f"{b}박: 노트 {len(lanes)}개에 메시지 {len(e['msgs'])}개 {e['msgs']}")
        for k, lane in enumerate(lanes):
            out.append({'interval': round(b - prev, 4) if k == 0 else 0.0, 'input': lane,
                        'message': e['msgs'][k] if k < len(e['msgs']) else ''})
        prev = b
    d2 = {'bpm': d['bpm'], 'gridOffset': d.get('gridOffset', 0.0), 'noteList': out}
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(d2, f, ensure_ascii=False, indent=4)


def strength(g):
    """그리드 박 g의 박자 세기. 마디 첫 박 4, 정박 3, 8분 뒷박 2, 16분 1, 그 밖 0"""
    def on(x):
        return abs(x - round(x)) < 0.02
    if on(g / 4):
        return 4
    if on(g):
        return 3
    if on(g * 2):
        return 2
    if on(g * 4):
        return 1
    return 0


def stats(path):
    d, ev = load(path)
    beat = 60.0 / d['bpm']
    notes = [(e['b'], l) for e in ev for l in e['lanes']]
    times = [e['b'] * beat for e in ev]
    length = times[-1] if times else 0
    best, j = 0, 0
    for i in range(len(ev)):
        while times[i] - times[j] > 2.0:
            j += 1
        best = max(best, sum(len(ev[k]['lanes']) for k in range(j, i + 1)))
    last, jacks = {}, []
    for b, l in notes:
        if l in last and b > last[l]:
            jacks.append(((b - last[l]) * beat, l, b * beat))
        last[l] = b
    jacks.sort()
    risky = [x for x in jacks if x[0] < JACK_RISK]
    off = d.get('gridOffset', 0.0) / beat
    offgrid = sum(1 for e in ev if strength(e['b'] - off) == 0)
    gaps = Counter(round(ev[i]['b'] - ev[i - 1]['b'], 3) for i in range(1, len(ev)))
    lanes = Counter(LANES[l] for _, l in notes)
    msgs = [(round(e['b'] * beat, 2), m) for e in ev for m in e['msgs']]
    return {
        'file': path, 'bpm': d['bpm'], 'notes': len(notes), 'length_s': round(length, 1),
        'avg_nps': round(len(notes) / length, 2) if length else 0, 'max_in_2s': best,
        'lanes': dict(lanes), 'chords': sum(1 for e in ev if len(e['lanes']) > 1),
        'min_gap_beats': min(gaps) if gaps else None,
        'gaps_beats': dict(sorted(gaps.items())),
        'same_lane_under_200ms': len(risky),
        'shortest_same_lane': [(round(g * 1000), LANES[l], round(t, 2)) for g, l, t in jacks[:5]],
        'off_grid_events': offgrid, 'messages': msgs,
    }


def thin(src, dst, prof_name, loose=False):
    p = PROFILES[prof_name]
    d, ev = load(src)
    beat = 60.0 / d['bpm']
    off = d.get('gridOffset', 0.0) / beat
    min_gap = max(p['min_gap_beats'], p['min_gap_s'] / beat)
    same_min = max(p['same_lane_beats'], p['same_lane_s'] / beat)
    messages = [(e['b'], m) for e in ev for m in e['msgs']]  # 곡 순서 그대로

    def st(e):
        return strength(e['b'] - off)

    # 1. 박자 칸 밖(예: 쉬움에서 16분) 버리기
    def on_grid(e):
        x = (e['b'] - off) * p['grid']
        return abs(x - round(x)) < 0.02
    ev = [dict(e) for e in ev if loose or on_grid(e)]  # loose: 격자 무시 (박에 안 맞춰 찍힌 채보)

    # 2. 동시치기: 쉬움은 하나만, 그 밖은 마디당 개수 제한 + 빠른 줄기 속이면 하나로. 남길 레인은 약지(A)보다 S/D 우선
    bar_chords = Counter()
    space = p.get('chord_space_beats', 0.0)
    for i, e in enumerate(ev):
        lanes = sorted(set(e['lanes']))
        bar = int((e['b'] - off) // 4)
        prev = ev[i - 1]['lanes'] if i > 0 else []
        nxt = ev[i + 1]['lanes'] if i + 1 < len(ev) else []
        crowded = (i > 0 and e['b'] - ev[i - 1]['b'] < space - 1e-4) or \
                  (i + 1 < len(ev) and ev[i + 1]['b'] - e['b'] < space - 1e-4)
        if len(lanes) > 2 and st(e) < p.get('triple_min_strength', 0):
            lanes = [1, 2]  # 세 키 → S+D (약지 A를 뺀다)
        if len(lanes) > 1 and (not p['chords'] or crowded or st(e) < p.get('chord_min_strength', 0)
                               or bar_chords[bar] >= p.get('max_chords_per_bar', 0)):
            # 하나로: 앞뒤 노트와 안 겹치는 레인 우선(원래 동시치기에 없던 레인이어도), 그다음 A가 아닌 쪽
            free = [l for l in (1, 2, 0) if l not in prev and l not in nxt]
            lanes = [free[0] if free else sorted(lanes, key=lambda l: (l == 0, -l))[0]]
        if len(lanes) > 1:
            bar_chords[bar] += 1
        e['lanes'] = lanes

    # 3. 최소 간격: 너무 붙으면 박자가 센 쪽을 남긴다
    kept = []
    for e in ev:
        if kept and e['b'] - kept[-1]['b'] < min_gap - 1e-4:
            if st(e) > st(kept[-1]) and (len(kept) < 2 or e['b'] - kept[-2]['b'] >= min_gap - 1e-4):
                kept[-1] = e
            continue
        kept.append(e)
    ev = kept

    # 4. 밀도 상한: 2초 창이 넘치면 그 창에서 가장 약한 박부터 뺀다
    changed = True
    while changed:
        changed = False
        j = 0
        for i in range(len(ev)):
            while (ev[i]['b'] - ev[j]['b']) * beat > 2.0:
                j += 1
            window = ev[j:i + 1]
            if sum(len(e['lanes']) for e in window) > p['max_in_2s']:
                weakest = min(window[1:] or window, key=lambda e: (st(e), -e['b']))
                ev.remove(weakest)
                changed = True
                break

    # 5. 같은 레인 연타 풀기: 리듬은 두고 레인만 옮긴다. 다음 노트와 또 겹치지 않는 쪽, S → D → A 순
    for i in range(1, len(ev)):
        prev, cur = ev[i - 1], ev[i]
        if cur['b'] - prev['b'] >= same_min - 1e-4:
            continue
        nxt = ev[i + 1]['lanes'] if i + 1 < len(ev) else []
        new = []
        for lane in cur['lanes']:
            if lane in prev['lanes']:
                options = [l for l in (1, 2, 0) if l not in prev['lanes'] and l not in new and l not in cur['lanes']]
                options.sort(key=lambda l: l in nxt)
                lane = options[0] if options else lane
            new.append(lane)
        cur['lanes'] = new

    # 6. 메시지 옮기기: 지워진 노트의 메시지는 그 박 이전의 마지막 남은 노트로 (배너 시점이 늦어지지 않게)
    #    단 순서가 뒤집히면 안 된다(drum이 chorus보다 먼저 뜨는 식): 앞 메시지가 들어간 노트보다 앞으로는 안 간다.
    #    노트 하나엔 메시지 하나라서 자리가 차 있으면 뒤 노트로 민다. 끝까지 자리가 없으면 멈춘다(조용히 버리지 않음)
    for e in ev:
        e['msgs'] = []
    floor = 0
    for b, msg in messages:
        target = floor
        for i in range(floor, len(ev)):
            if ev[i]['b'] > b + 1e-4:
                break
            target = i
        while target < len(ev) and len(ev[target]['msgs']) >= len(set(ev[target]['lanes'])):
            target += 1
        if target >= len(ev):
            raise SystemExit(f"메시지 '{msg}'({b:.2f}박)를 둘 노트가 없음. 노트를 덜 줄이는 프로필로 다시")
        ev[target]['msgs'].append(msg)
        floor = target

    save(dst, d, ev)


def foot(src, dst, prof_name):
    """펌프(발판 4칸) 채보: thin으로 발에 맞는 리듬만 고른 뒤, 레인을 발 교대에 맞춰 다시 정한다.
    발판 0 1 | 2 3 = 왼발 쪽 | 오른발 쪽 (키 A S | D F). 0·3이 바깥, 1·2가 안쪽
    - 1박보다 붙은 스텝은 왼발·오른발을 번갈아 (같은 발로 빠르게 두 번 = 발 연타가 안 생기게)
    - 1박 이상 떨어지면 원래 손 레인 방향(A=왼쪽, D=오른쪽)을 따라간다
    - 바깥/안쪽은 원래 레인으로 고른다 (멜로디 높낮이가 발판 좌우로 이어지게)
    - 동시치기는 점프: 마디 첫 박이면 바깥 두 칸(0+3), 아니면 안쪽 두 칸(1+2)"""
    # 노트 절반 이상이 8분 격자 밖이면(셔플·MIDI 그대로 찍은 채보) 격자로 거르면 거의 다 사라진다 → 간격만으로 고른다
    d0, ev0 = load(src)
    off0 = d0.get('gridOffset', 0.0) * d0['bpm'] / 60.0
    on8 = sum(1 for e in ev0 if abs((e['b'] - off0) * 2 - round((e['b'] - off0) * 2)) < 0.02)
    loose = on8 < len(ev0) / 2
    if loose:
        print(f"{src}: 8분 격자에 맞는 노트 {on8}/{len(ev0)} → 격자 무시하고 간격으로만 고름", file=sys.stderr)
    thin(src, dst, prof_name, loose)
    d, ev = load(dst)
    beat = 60.0 / d['bpm']
    off = d.get('gridOffset', 0.0) / beat
    alt = max(1.0, PROFILES[prof_name]['alt_s'] / beat)
    last_foot, last_b, last_lanes = None, None, []
    for e in ev:
        lanes = sorted(set(e['lanes']))
        fast = last_b is not None and e['b'] - last_b < alt - 1e-4
        if len(lanes) > 1:
            e['lanes'] = [0, 3] if strength(e['b'] - off) == 4 else [1, 2]
            last_foot = None  # 점프 뒤엔 어느 발이든
        else:
            src_lane = lanes[0]
            if fast and last_foot is not None:
                foot_side = 'R' if last_foot == 'L' else 'L'
            elif src_lane == 0:
                foot_side = 'L'
            elif src_lane == 2:
                foot_side = 'R'
            else:
                foot_side = 'R' if last_foot == 'L' else 'L'
            if foot_side == 'L':
                e['lanes'] = [0 if src_lane == 0 else 1]
            else:
                e['lanes'] = [3 if src_lane == 2 else 2]
            if fast and e['lanes'][0] in last_lanes:  # 점프 바로 뒤 같은 칸 → 같은 발의 옆 칸 (0↔1, 2↔3)
                e['lanes'] = [e['lanes'][0] ^ 1]
            last_foot = foot_side
        last_b, last_lanes = e['b'], e['lanes']
    save(dst, d, ev)


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest='cmd', required=True)
    s = sub.add_parser('stats')
    s.add_argument('chart')
    s.add_argument('--compare')
    t = sub.add_parser('thin')
    t.add_argument('src')
    t.add_argument('dst')
    t.add_argument('--profile', choices=list(PROFILES), required=True)
    f = sub.add_parser('foot')
    f.add_argument('src')
    f.add_argument('dst')
    f.add_argument('--profile', choices=[k for k in PROFILES if k.startswith('pump_')], required=True)
    a = ap.parse_args()
    if a.cmd == 'stats':
        for path in [a.chart] + ([a.compare] if a.compare else []):
            print(json.dumps(stats(path), ensure_ascii=False, indent=1))
    elif a.cmd == 'foot':
        foot(a.src, a.dst, a.profile)
        print(json.dumps(stats(a.dst), ensure_ascii=False, indent=1))
    else:
        thin(a.src, a.dst, a.profile)
        print(json.dumps(stats(a.dst), ensure_ascii=False, indent=1))


if __name__ == '__main__':
    sys.exit(main())
