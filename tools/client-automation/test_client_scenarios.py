"""Executor contract tests; fake state/clock/input only, never touches a GUI."""
from __future__ import annotations

import copy
from datetime import datetime, timedelta, timezone
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location(
    'client_scenarios_under_test', Path(__file__).with_name('client_scenarios.py'))
scenarios = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(scenarios)


class Clock:
    def __init__(self):
        self.elapsed = 0.0
        self.origin = datetime(2026, 9, 14, tzinfo=timezone.utc)

    def monotonic(self):
        return self.elapsed

    def sleep(self, seconds):
        self.elapsed += seconds

    def now(self):
        return self.origin + timedelta(seconds=self.elapsed)

    def iso(self):
        return self.now().isoformat()


def item(slot, item_type=0, stack=0, prefix=0, favorited=False):
    return {'slot': slot, 'type': item_type, 'stack': stack,
            'prefix': prefix, 'favorited': favorited}


def fixture(clock):
    session = {'serverSessionId': 'server-a', 'worldId': 7,
               'playerName': 'automation-actor', 'accountId': 13,
               'sessionGeneration': 5}
    inventory = [item(i) for i in range(59)]
    inventory[10] = item(10, 9, 18)
    chest = [item(i) for i in range(40)]
    chest[0] = item(0, 9, 20)
    state = {'schemaVersion': 1, 'utc': clock.iso(), 'observationSequence': 1,
             'serverSessionId': 'server-a', 'worldId': 7,
             'worldPath': 'isolated/test.wld',
             'health': {'status': 'ok', 'unknown': []},
             'players': [{'slot': 0, 'name': 'automation-actor', 'accountId': 13,
                          'sessionGeneration': 5, 'isLoggedIn': True,
                          'active': True, 'dead': False, 'selectedItem': 0,
                          'currentLoadoutIndex': 0, 'activeChest': -1,
                          'inventory': inventory}],
             'chests': [{'id': 11, 'x': 100, 'y': 200, 'slots': chest}]}
    return session, state


class Game:
    def __init__(self, clock, state):
        self.clock, self.state = clock, copy.deepcopy(state)
        self.actions = []
        self.reads = 0
        self.due = None
        self.delay = 0
        self.on_action = self.apply
        self.update_timestamps = True

    def read(self):
        self.reads += 1
        if self.due is not None and self.clock.monotonic() >= self.due[0]:
            _, key = self.due
            self.due = None
            self.on_action(key)
        if self.update_timestamps:
            self.state['utc'] = self.clock.iso()
        return copy.deepcopy(self.state)

    def gesture(self, key):
        self.actions.append(key)
        self.due = (self.clock.monotonic() + self.delay, key)

    def apply(self, key):
        player = self.state['players'][0]
        if key == 'quickstack-click':
            count = player['inventory'][10]['stack']
            player['inventory'][10] = item(10)
            self.state['chests'][0]['slots'][0]['stack'] += count
        elif key.startswith('f'):
            player['currentLoadoutIndex'] = int(key[1:]) - 1
        else:
            player['selectedItem'] = int(key) - 1
        self.state['observationSequence'] += 1


class ExecutorContractTests(unittest.TestCase):
    def setUp(self):
        self.clock = Clock()
        clock = self.clock

        class ControlledDateTime(datetime):
            @classmethod
            def now(cls, tz=None):
                return clock.now() if tz is not None else clock.now().replace(tzinfo=None)

        self.datetime_patch = patch.object(scenarios, 'datetime', ControlledDateTime)
        self.datetime_patch.start()
        self.addCleanup(self.datetime_patch.stop)
        self.session, self.state = fixture(self.clock)
        self.game = Game(self.clock, self.state)

    def executor(self, **kwargs):
        return scenarios.Executor(self.session, self.game.read, self.game.gesture,
                                  clock=self.clock, **kwargs)

    def test_hotbar_cycle_restores_original_selection_with_three_gestures(self):
        executor = self.executor()
        self.assertTrue(executor.run('ClientHotbarCycle'))
        self.assertEqual(['2', '3', '1'], self.game.actions)
        self.assertEqual(3, executor.actions)
        self.assertEqual(7, executor.reads)
        self.assertEqual(4, len(executor.assertions))
        self.assertEqual(0, self.game.state['players'][0]['selectedItem'])

    def test_loadout_cycle_restores_original_page_with_three_gestures(self):
        self.game.state['players'][0]['currentLoadoutIndex'] = 2
        executor = self.executor()
        self.assertTrue(executor.run('ClientLoadoutCycle'))
        self.assertEqual(['f1', 'f2', 'f3'], self.game.actions)
        self.assertEqual(2, self.game.state['players'][0]['currentLoadoutIndex'])

    def test_quickstack_requires_positive_matching_inventory_chest_delta(self):
        executor = self.executor()
        self.assertTrue(executor.run('ClientQuickStack'))
        self.assertEqual(['quickstack-click'], self.game.actions)
        self.assertEqual(1, executor.actions)
        self.assertEqual(38, self.game.state['chests'][0]['slots'][0]['stack'])

    def test_delayed_quickstack_can_use_frozen_before_within_five_second_deadline(self):
        self.game.delay = 2
        executor = self.executor()
        self.assertTrue(executor.run('ClientQuickStack'))
        self.assertGreaterEqual(self.clock.elapsed, 2)
        self.assertEqual(1, executor.actions)

    def test_stale_state_blocks_before_any_gesture(self):
        self.game.update_timestamps = False
        self.clock.sleep(2)
        executor = self.executor()
        with self.assertRaisesRegex(scenarios.Blocked, 'stale'):
            executor.run('ClientHotbarCycle')
        self.assertEqual([], self.game.actions)

    def test_future_timestamp_blocks_before_any_gesture(self):
        self.game.update_timestamps = False
        self.game.state['utc'] = (self.clock.now() + timedelta(seconds=1)).isoformat()
        with self.assertRaisesRegex(scenarios.Blocked, 'stale'):
            self.executor().run('ClientLoadoutCycle')
        self.assertEqual([], self.game.actions)

    def test_health_unknown_is_never_pass(self):
        self.game.state['health']['status'] = 'unknown'
        with self.assertRaisesRegex(scenarios.Blocked, 'health_unknown'):
            self.executor().run('ClientQuickStack')
        self.assertEqual([], self.game.actions)

    def test_world_or_server_generation_change_blocks(self):
        for field, changed in [('worldId', 8), ('serverSessionId', 'server-b')]:
            with self.subTest(field=field):
                state = copy.deepcopy(self.state)
                state[field] = changed
                with self.assertRaisesRegex(scenarios.Blocked, 'generation_changed'):
                    scenarios.state_player(state, self.session)

    def test_player_slot_reuse_generation_cannot_inherit_evidence(self):
        original = self.game.apply

        def reconnect(key):
            original(key)
            self.game.state['players'][0]['sessionGeneration'] += 1

        self.game.on_action = reconnect
        executor = self.executor()
        with self.assertRaisesRegex(scenarios.Blocked, 'authenticated_live_actor_unknown'):
            executor.run('ClientHotbarCycle')
        self.assertEqual(['2'], self.game.actions)

    def test_front_window_identity_failure_blocks_before_injection(self):
        def wrong_window():
            raise scenarios.Blocked('foreground_owner_changed')

        with self.assertRaisesRegex(scenarios.Blocked, 'foreground_owner_changed'):
            self.executor(check_identity=wrong_window).run('ClientHotbarCycle')
        self.assertEqual([], self.game.actions)

    def test_observation_sequence_regression_aborts(self):
        executor = self.executor()
        executor.read()
        self.game.state['observationSequence'] = 0
        with self.assertRaisesRegex(scenarios.Blocked, 'sequence_regressed'):
            executor.read()

    def test_repeated_sequence_cannot_satisfy_postcondition(self):
        self.game.on_action = lambda _: None
        executor = self.executor()
        executor.read()
        with self.assertRaisesRegex(TimeoutError, 'postcondition_timeout'):
            executor.wait(1, lambda _: True, 'needs-new-observation')
        self.assertGreaterEqual(self.clock.elapsed, 5)
        self.assertLess(self.game.reads, 110)
        self.assertEqual([], self.game.actions)

    def test_timeout_after_one_gesture_never_retries(self):
        self.game.on_action = lambda _: None
        executor = self.executor()
        with self.assertRaisesRegex(TimeoutError, 'postcondition_timeout'):
            executor.run('ClientHotbarCycle')
        self.assertEqual(['2'], self.game.actions)
        self.assertEqual(1, executor.actions)
        self.assertFalse(executor.assertions[-1]['passed'])

    def test_injection_exception_records_attempt_before_unknown_result(self):
        calls = []

        def uncertain_gesture(key):
            calls.append(key)
            raise RuntimeError('mouse_down_unknown')

        executor = scenarios.Executor(self.session, self.game.read, uncertain_gesture, clock=self.clock)
        with self.assertRaisesRegex(RuntimeError, 'mouse_down_unknown'):
            executor.run('ClientQuickStack')
        self.assertEqual(['quickstack-click'], calls)
        self.assertEqual(1, executor.actions)
        self.assertEqual(1, len(executor.events))

    def test_inventory_loss_does_not_pass_selection_cycle(self):
        original = self.game.apply

        def lose_item(key):
            original(key)
            self.game.state['players'][0]['inventory'][10]['stack'] -= 1

        self.game.on_action = lose_item
        executor = self.executor()
        self.assertFalse(executor.run('ClientHotbarCycle'))
        self.assertFalse(executor.assertions[-1]['passed'])

    def test_quickstack_rejects_loss_duplication_and_prefix_substitution(self):
        after = copy.deepcopy(self.state)
        after['observationSequence'] = 2
        after['players'][0]['inventory'][10] = item(10)
        for final_stack, final_prefix in [(20, 0), (39, 0), (38, 1)]:
            with self.subTest(final_stack=final_stack, final_prefix=final_prefix):
                after['chests'][0]['slots'][0] = item(0, 9, final_stack, final_prefix)
                self.assertFalse(scenarios.transfer_proven(self.state, after, self.session))

    def test_chest_replacement_rejects_matching_item_delta(self):
        self.game.apply('quickstack-click')
        self.game.state['chests'][0]['x'] += 1
        with self.assertRaisesRegex(scenarios.Blocked, 'chest_identity_changed'):
            scenarios.transfer_proven(self.state, self.game.state, self.session)

    def test_no_eligible_source_blocks_without_attempt(self):
        self.game.state['players'][0]['inventory'][10]['favorited'] = True
        with self.assertRaisesRegex(scenarios.Blocked, 'no_eligible_source_inventory'):
            self.executor().run('ClientQuickStack')
        self.assertEqual([], self.game.actions)

    def test_unknown_quickstack_receipt_prevents_second_process_attempt(self):
        with tempfile.TemporaryDirectory(prefix='client-scenario-contract-') as temporary:
            root = Path(temporary)
            session_id = 'a' * 32
            session_dir = root / '.lab/devmcp/client-sessions'
            session_dir.mkdir(parents=True)
            output = root / '.lab/result'
            output.mkdir()
            save = root / '.lab/save'
            save.mkdir()
            (save / 'input profiles.json').write_text('{}', encoding='utf-8')
            exe = root / '.lab/Terraria.exe'
            exe.write_bytes(b'fake-executable-never-launched')
            state_path = root / '.lab/state.json'
            state_path.write_text(json.dumps(self.state), encoding='utf-8')
            session = dict(self.session, sessionId=session_id, candidateId='test-only',
                           evidenceLayer='stock_gui_input_server_observed',
                           serverStatePath=str(state_path), savedirectory=str(save),
                           clientExe=str(exe), clientExeSha256=scenarios.sha(exe),
                           inputProfileSha256=scenarios.sha(save / 'input profiles.json'))
            session_path = session_dir / (session_id + '.json')
            session_path.write_text(json.dumps(session), encoding='utf-8')
            calls = []

            class UnknownNativeInput:
                def __init__(self, _):
                    calls.append('opened')

                def check(self):
                    pass

                def __call__(self, key):
                    calls.append(key)
                    raise RuntimeError('mouse_release_unknown')

                def close(self):
                    calls.append('closed')

            arguments = ['--project-root', str(root), '--session-id', session_id,
                         '--scenario', 'ClientQuickStack', '--output', str(output)]
            with patch.object(scenarios, 'NativeInput', UnknownNativeInput), patch('sys.stdout', new=io.StringIO()):
                self.assertEqual(1, scenarios.main(arguments))
                first = json.loads((output / 'client-result.json').read_text(encoding='utf-8'))
                self.assertEqual('unknown', first['status'])
                self.assertEqual(1, first['executedActions'])
                self.assertFalse(first['cleanup']['inputReleased'])
                self.assertTrue(session_path.with_suffix('.quickstack-pending.json').exists())
                self.assertEqual(1, scenarios.main(arguments))
                second = json.loads((output / 'client-result.json').read_text(encoding='utf-8'))
            self.assertEqual('blocked', second['status'])
            self.assertIn('requires_reconciliation', second['failure'])
            self.assertEqual(0, second['executedActions'])
            self.assertEqual(['opened', 'quickstack-click', 'closed'], calls)
            self.assertEqual(0, first['metrics']['automaticRetries'])
            self.assertEqual(0, first['metrics']['screenshots'])
            self.assertEqual(0, first['metrics']['modelCallsInsideExecutor'])
            self.assertIsNone(first['metrics']['actualModelTokens'])


if __name__ == '__main__':
    unittest.main(verbosity=2)
