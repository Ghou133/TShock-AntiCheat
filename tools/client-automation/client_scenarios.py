"""Bounded original-input scenarios. No game memory, protocol writes or retries.

The caller provisions an audited isolated session, then this executor owns the
complete gesture/wait/assert sequence. UI fields absent from server state stay
unknown. A fresh observed anchor is mandatory for the single Quick Stack click.
"""
from __future__ import annotations
import argparse
import ctypes as C
from ctypes import wintypes as W
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import sys
import time

MAX_STATE = 81920


class Blocked(RuntimeError):
    pass


def utc():
    return datetime.now(timezone.utc).isoformat()


def read_json(path, limit=MAX_STATE):
    with open(path, 'rb') as stream:
        data = stream.read(limit + 1)
    if len(data) > limit:
        raise Blocked('state_capacity_exceeded')
    return json.loads(data.decode('utf-8-sig'))


def age(value):
    return (datetime.now(timezone.utc) - datetime.fromisoformat(value.replace('Z', '+00:00'))).total_seconds()


def sha(path):
    with open(path, 'rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def under(root, value, areas=('.lab', 'artifacts')):
    path = Path(value)
    if not path.is_absolute():
        path = root / path
    path = path.resolve(strict=True)
    rel = path.relative_to(root.resolve(strict=True))
    if rel.parts[0] not in areas:
        raise Blocked('path_outside_isolated_areas')
    return path


def state_player(snapshot, session, *, fresh=True):
    if snapshot.get('schemaVersion') != 1 or (fresh and not -.25 <= age(snapshot['utc']) <= 1.5):
        raise Blocked('state_stale_or_wrong_schema')
    if snapshot.get('health', {}).get('status') != 'ok':
        raise Blocked('state_health_unknown')
    if snapshot['serverSessionId'] != session['serverSessionId'] or snapshot['worldId'] != session['worldId']:
        raise Blocked('server_or_world_generation_changed')
    matches = [p for p in snapshot['players'] if p['name'] == session['playerName']
               and p['accountId'] == session['accountId'] and p['sessionGeneration'] == session['sessionGeneration']]
    if len(matches) != 1 or not matches[0]['isLoggedIn'] or not matches[0]['active'] or matches[0]['dead']:
        raise Blocked('authenticated_live_actor_unknown')
    return matches[0]


def compact(snapshot, session):
    p = state_player(snapshot, session)
    return {'utc': snapshot['utc'], 'observationSequence': snapshot['observationSequence'],
            'serverSessionId': snapshot['serverSessionId'], 'worldId': snapshot['worldId'],
            'accountId': p['accountId'], 'sessionGeneration': p['sessionGeneration'],
            'selectedItem': p['selectedItem'], 'currentLoadoutIndex': p['currentLoadoutIndex'],
            'activeChest': p['activeChest'], 'inventory': [i for i in p['inventory'] if i['type'] and i['stack']],
            'chests': snapshot['chests'], 'clientUi': {'status': 'unknown', 'reason': 'server_observation_only'}}


def totals(items):
    result = {}
    for i in items:
        if i['type'] and i['stack']:
            key = (i['type'], i['prefix'])
            result[key] = result.get(key, 0) + i['stack']
    return result


def transfer_proven(before, after, session):
    a, b = state_player(before, session, fresh=False), state_player(after, session)
    ca = {c['id']: c for c in before['chests']}
    cb = {c['id']: c for c in after['chests']}
    if ca.keys() != cb.keys() or any((ca[k]['x'], ca[k]['y']) != (cb[k]['x'], cb[k]['y']) for k in ca):
        raise Blocked('chest_identity_changed')
    ai, bi = totals(a['inventory']), totals(b['inventory'])
    ac = totals([i for c in ca.values() for i in c['slots']])
    bc = totals([i for c in cb.values() for i in c['slots']])
    keys = ai.keys() | bi.keys() | ac.keys() | bc.keys()
    moved = sum(ai.get(k, 0) - bi.get(k, 0) for k in keys)
    return moved > 0 and all(ai.get(k, 0) - bi.get(k, 0) == bc.get(k, 0) - ac.get(k, 0) >= 0 for k in keys)


class Executor:
    def __init__(self, session, read_state, gesture, check_identity=lambda: None, clock=time, action_guard=lambda: None):
        self.session, self.read_state, self.gesture = session, read_state, gesture
        self.check_identity, self.clock, self.action_guard = check_identity, clock, action_guard
        self.reads, self.actions, self.events, self.assertions = 0, 0, [], []
        self.first, self.last = None, None
        self.started = clock.monotonic()

    def read(self):
        self.check_identity()
        value = self.read_state()
        state_player(value, self.session)
        self.reads += 1
        if self.last is not None and value['observationSequence'] < self.last['observationSequence']:
            raise Blocked('observation_sequence_regressed')
        self.last = value
        if self.first is None:
            self.first = value
        return value

    def wait(self, sequence, predicate, name):
        deadline = min(self.started + 25, self.clock.monotonic() + 5)
        while self.clock.monotonic() < deadline:
            value = self.read()
            if value['observationSequence'] > sequence and predicate(value):
                self.assertions.append({'name': name, 'passed': True})
                return value
            self.clock.sleep(.05)
        self.assertions.append({'name': name, 'passed': False})
        raise TimeoutError('postcondition_timeout:' + name)

    def act(self, key):
        if self.actions >= 4 or self.clock.monotonic() - self.started > 25:
            raise Blocked('action_budget_exceeded')
        self.check_identity()
        self.action_guard()
        # Record the attempt BEFORE injection. An exception can mean a partial gesture.
        self.actions += 1
        self.events.append({'utc': utc(), 'action': key, 'attempt': self.actions})
        self.gesture(key)

    def run(self, scenario):
        before = self.read()
        p = state_player(before, self.session)
        if scenario in ('ClientHotbarCycle', 'ClientLoadoutCycle'):
            field = 'selectedItem' if scenario == 'ClientHotbarCycle' else 'currentLoadoutIndex'
            initial = p[field]
            if not 0 <= initial <= (8 if field == 'selectedItem' else 2):
                raise Blocked('initial_selection_outside_supported_range')
            targets = [x for x in range(3) if x != initial][:2] + [initial]
            for target in targets:
                previous = self.read()
                key = str(target + 1) if field == 'selectedItem' else 'f' + str(target + 1)
                self.act(key)
                self.wait(previous['observationSequence'], lambda v: state_player(v, self.session)[field] == target,
                          field + '=' + str(target))
            self.assertions.append({'name': 'inventory_totals_preserved',
                'passed': totals(p['inventory']) == totals(state_player(self.last, self.session)['inventory'])})
        elif scenario == 'ClientQuickStack':
            if p['activeChest'] != -1 or not before['chests']:
                raise Blocked('quickstack_requires_closed_chest_and_observed_targets')
            if not any(i['slot'] >= 10 and i['type'] and i['stack'] and not i['favorited'] for i in p['inventory']):
                raise Blocked('no_eligible_source_inventory')
            self.act('quickstack-click')
            self.wait(before['observationSequence'], lambda v: transfer_proven(before, v, self.session), 'inventory_to_chest_conservation')
        else:
            raise Blocked('unregistered_scenario')
        return all(a['passed'] for a in self.assertions)


class NativeInput:
    def __init__(self, session):
        self.s = session
        helper = Path(session['inputHelperPath'])
        if sha(helper) != session['inputHelperSha256'].upper():
            raise Blocked('input_helper_hash_changed')
        spec = importlib.util.spec_from_file_location('guarded_game_key', helper)
        self.g = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = self.g
        spec.loader.exec_module(self.g)
        # Only extra original loadout keys. No text, cheats, console or arbitrary keys.
        self.g.KEYS.update({'f1': [0x70], 'f2': [0x71], 'f3': [0x72]})
        self.process = self.g.WindowsProcess(session['clientPid'])
        self.process.__enter__()
        self.keyboard = self.g.WindowsKeyboard()
        self.mutex = self.g.GestureMutex()
        self.mutex.__enter__()  # Same shared mutex, held across the WHOLE batch.
        self.request = self.g.Request(session['clientPid'], session['clientExe'], session['savedirectory'], inspect_only=True)
        self.g.execute(self.request, self.process, self.keyboard)
        # Pin process creation time while process handle stays open (PID reuse is insufficient).
        self.creation = self.creation_time()
        if abs((self.creation - datetime.fromisoformat(session['clientStartedUtc'].replace('Z', '+00:00'))).total_seconds()) > .1:
            self.close()
            raise Blocked('client_process_generation_changed')
        self.hwnd = self.keyboard.foreground()[0]

    def creation_time(self):
        values = [W.FILETIME() for _ in range(4)]
        fn = self.process.kernel.GetProcessTimes
        fn.argtypes = [W.HANDLE] + [C.POINTER(W.FILETIME)] * 4
        if not fn(self.process.handle, *[C.byref(v) for v in values]):
            raise Blocked('client_process_time_unknown')
        ticks = (values[0].dwHighDateTime << 32) | values[0].dwLowDateTime
        return datetime.fromtimestamp(ticks / 10000000 - 11644473600, timezone.utc)

    def check(self):
        self.g.require_foreground(self.keyboard, self.process, self.s['clientPid'], self.hwnd)
        if self.creation_time() != self.creation:
            raise Blocked('client_process_generation_changed')

    def __call__(self, key):
        if key != 'quickstack-click':
            req = self.g.Request(self.s['clientPid'], self.s['clientExe'], self.s['savedirectory'], key, 100)
            self.g.send_gesture(req, self.keyboard, self.process, self.hwnd)
            return
        anchor = self.s.get('guiAnchor', {})
        if not anchor.get('sourceRef') or not 0 <= age(anchor['observedUtc']) <= 30 or anchor.get('kind') != 'quickstack-button':
            raise Blocked('fresh_observed_quickstack_anchor_required')
        user = self.keyboard.user
        rect = W.RECT()
        user.GetClientRect.argtypes = [W.HWND, C.POINTER(W.RECT)]
        user.ClientToScreen.argtypes = [W.HWND, C.POINTER(W.POINT)]
        user.SetCursorPos.argtypes = [C.c_int, C.c_int]
        if not user.GetClientRect(self.hwnd, C.byref(rect)) or [rect.right, rect.bottom] != anchor['clientSize']:
            raise Blocked('observed_window_geometry_changed')
        x, y = anchor['clientPoint']
        if not 0 <= x < rect.right or not 0 <= y < rect.bottom:
            raise Blocked('anchor_outside_client')
        point = W.POINT(x, y)
        if not user.ClientToScreen(self.hwnd, C.byref(point)):
            raise Blocked('coordinate_translation_failed')
        self.check()
        if any(self.keyboard.is_down(vk) for vk in self.g.MODIFIERS | {1, 2}):
            raise Blocked('mouse_or_modifier_already_held')
        if not user.SetCursorPos(point.x, point.y):
            raise Blocked('cursor_move_failed')
        self.check()
        down = False
        try:
            value = self.g.INPUT(type=0)
            value.mi.dwFlags = 0x0002
            if user.SendInput(1, C.byref(value), C.sizeof(value)) != 1:
                raise RuntimeError('mouse_down_unknown')
            down = True
            until = time.monotonic() + .1
            while time.monotonic() < until:
                self.check()
                time.sleep(.01)
        finally:
            if down:
                value = self.g.INPUT(type=0)
                value.mi.dwFlags = 0x0004
                if user.SendInput(1, C.byref(value), C.sizeof(value)) != 1:
                    raise RuntimeError('mouse_release_unknown')

    def close(self):
        if hasattr(self, 'mutex'):
            self.mutex.__exit__(None, None, None)
        if hasattr(self, 'process'):
            self.process.__exit__(None, None, None)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--project-root', required=True)
    ap.add_argument('--session-id', required=True)
    ap.add_argument('--scenario', required=True, choices=['ClientHotbarCycle', 'ClientLoadoutCycle', 'ClientQuickStack'])
    ap.add_argument('--output', required=True)
    args = ap.parse_args(argv)
    root = Path(args.project_root).resolve(strict=True)
    if not re.fullmatch('[a-f0-9]{32}', args.session_id):
        raise Blocked('invalid_session_id')
    session_path = under(root, '.lab/devmcp/client-sessions/' + args.session_id + '.json')
    session = read_json(session_path)
    state_path = under(root, session['serverStatePath'])
    output = under(root, args.output)
    under(root, session['savedirectory'], ('.lab',))
    result = {'schemaVersion': 1, 'scenarioId': args.scenario, 'sessionId': args.session_id,
              'candidateId': session['candidateId'], 'status': 'blocked', 'failure': None,
              'executedActions': 0, 'assertions': [], 'evidenceRefs': [str(session_path), str(state_path)],
              'before': None, 'after': None, 'cleanup': {'inputReleased': True, 'externalClientPreserved': True}}
    native, executor = None, None
    start = time.perf_counter()
    latch = session_path.with_suffix('.quickstack-pending.json')
    def arm_once():
        if args.scenario == 'ClientQuickStack':
            # Persist before sending: interruption/unknown cannot cause a blind replay.
            with latch.open('x', encoding='utf-8') as file:
                json.dump({'utc': utc(), 'output': str(output), 'scenario': args.scenario}, file)
    try:
        if session['sessionId'] != args.session_id or session.get('evidenceLayer') != 'stock_gui_input_server_observed':
            raise Blocked('session_identity_or_layer_mismatch')
        if sha(session['clientExe']) != session['clientExeSha256'].upper():
            raise Blocked('client_hash_changed')
        if sha(Path(session['savedirectory']) / 'input profiles.json') != session['inputProfileSha256'].upper():
            raise Blocked('input_bindings_changed')
        if latch.exists() and args.scenario == 'ClientQuickStack':
            raise Blocked('previous_side_effect_requires_reconciliation')
        native = NativeInput(session)
        executor = Executor(session, lambda: read_json(state_path), native, native.check, action_guard=arm_once)
        passed = executor.run(args.scenario)
        result['status'] = 'passed' if passed else 'failed'
        if passed and args.scenario == 'ClientQuickStack':
            # Successful receipt is retained separately; only a proven action clears uncertainty.
            latch.rename(output / 'quickstack-proven-receipt.json')
    except Exception as exc:
        result['status'] = 'unknown' if executor and executor.actions else 'blocked'
        result['failure'] = type(exc).__name__ + ':' + str(exc)
        if 'release' in str(exc).lower():
            result['cleanup']['inputReleased'] = False
    finally:
        if native:
            native.close()
    if executor:
        result.update(executedActions=executor.actions, assertions=executor.assertions)
        for key, value in [('before', executor.first), ('after', executor.last)]:
            if value:
                # Do not rerun freshness checks during serialization of already captured evidence.
                session_copy = dict(session)
                result[key] = value
        (output / 'client-events.json').write_text(json.dumps(executor.events, indent=2), encoding='utf-8')
        result['evidenceRefs'].append(str(output / 'client-events.json'))
    result['metrics'] = {'elapsedMs': round((time.perf_counter() - start) * 1000, 2),
                         'stateReads': executor.reads if executor else 0, 'screenshots': 0,
                         'modelCallsInsideExecutor': 0, 'actualModelTokens': None, 'automaticRetries': 0}
    (output / 'client-result.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({k: result[k] for k in ['status', 'failure', 'scenarioId', 'executedActions', 'metrics', 'assertions']}, ensure_ascii=False))
    return 0 if result['status'] == 'passed' else 1


if __name__ == '__main__':
    raise SystemExit(main())
