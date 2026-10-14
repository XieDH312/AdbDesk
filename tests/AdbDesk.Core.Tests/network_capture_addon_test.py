import contextlib
import importlib.util
import io
import json
import pathlib
import sys
import types
import unittest
import tempfile
import gzip
from unittest.mock import patch

sys.modules['mitmproxy'] = types.SimpleNamespace(http=types.SimpleNamespace(HTTPFlow=object))
addon_path = pathlib.Path(__file__).parents[2] / 'src/AdbDesk.App/Assets/network_capture.py'
spec = importlib.util.spec_from_file_location('network_capture', addon_path)
addon = importlib.util.module_from_spec(spec)
if addon_path.exists():
    spec.loader.exec_module(addon)

class CaptureTests(unittest.TestCase):
    def test_unicode_bounds_match_dotnet_utf16_protocol_limits(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict('os.environ', {'ADBDESK_CAPTURE_DIRECTORY': directory}), contextlib.redirect_stdout(io.StringIO()):
            flow = self.make_flow()
            flow.request.pretty_url = 'https://example.test/' + '\U0001f600' * 9000
            flow.request.headers['X-Emoji'] = '\U0001f600' * 9000
            capture = addon.Capture()
            capture.requestheaders(flow)
            capture.request(flow)
            event = json.loads(next(pathlib.Path(directory).glob('*.json')).read_text())
            self.assertLessEqual(len(event['Url'].encode('utf-16-le')) // 2, 16384)
            self.assertLessEqual(len(event['RequestHeaders'].encode('utf-16-le')) // 2, 16448)
            self.assertFalse(event['RequestTargetComplete'])
            self.assertFalse(event['RequestHeadersComplete'])

    def test_early_response_retains_upload_until_request_finishes(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict('os.environ', {'ADBDESK_CAPTURE_DIRECTORY': directory}), contextlib.redirect_stdout(io.StringIO()):
            capture = addon.Capture()
            flow = self.make_flow()
            capture.requestheaders(flow)
            flow.request.stream(b'first')
            capture.responseheaders(flow)
            flow.response.status_code = 413
            flow.response.stream(b'rejected')
            capture.response(flow)
            self.assertEqual(flow.request.stream(b'last'), b'last')
            capture.request(flow)
            event = json.loads(next(pathlib.Path(directory).glob('*.json')).read_text())
            self.assertEqual(event['RequestCapture']['TotalBytes'], 9, 'response completion must not discard an active upload')
            self.assertEqual(event['RequestCapture']['SavedBytes'], 9)
            self.assertTrue(event['RequestCapture']['Complete'])
            self.assertTrue(event['ResponseCapture']['Complete'])
            self.assertTrue(event['Finished'])
            self.assertNotIn(flow.id, capture.active)
            body = pathlib.Path(directory) / event['RequestCapture']['FileName']
            self.assertEqual(body.read_bytes(), b'firstlast')
            self.assertEqual(flow.request.stream(b'late'), b'late')
            self.assertEqual(body.read_bytes(), b'firstlast', 'released streams must not modify finalized files')

    def test_oversized_request_target_is_explicitly_incomplete(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict('os.environ', {'ADBDESK_CAPTURE_DIRECTORY': directory}), contextlib.redirect_stdout(io.StringIO()):
            capture = addon.Capture()
            flow = self.make_flow()
            flow.request.pretty_url = 'https://example.test/' + 'x' * 17000
            capture.requestheaders(flow)
            capture.request(flow)
            event = json.loads(next(pathlib.Path(directory).glob('*.json')).read_text())
            self.assertFalse(event.get('RequestTargetComplete', True), 'truncated URL must never be exportable as an exact request')
            self.assertLessEqual(len(event['Url']), 16384)

    def test_progress_metadata_is_throttled_but_final_saved_bytes_are_exact(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict('os.environ', {'ADBDESK_CAPTURE_DIRECTORY': directory}), contextlib.redirect_stdout(io.StringIO()):
            capture = addon.Capture()
            flow = self.make_flow()
            capture.requestheaders(flow)
            with patch.object(addon.os, 'replace', wraps=addon.os.replace) as replacements:
                for _ in range(100):
                    self.assertEqual(flow.request.stream(b'a'), b'a')
                capture.request(flow)
                self.assertLessEqual(replacements.call_count, 5, 'small chunks must not cause a metadata rewrite each time')
            saved = json.loads(next(pathlib.Path(directory).glob('*.json')).read_text())
            self.assertEqual(saved['RequestCapture']['SavedBytes'], 100)
            self.assertTrue(saved['RequestCapture']['Complete'])

    def test_repeated_concurrency_diagnostics_are_bounded(self):
        capture = addon.Capture()
        with contextlib.redirect_stdout(io.StringIO()) as output:
            for i in range(1000):
                flow = types.SimpleNamespace(id=str(i), request=types.SimpleNamespace(), metadata={})
                capture.requestheaders(flow)
        self.assertLessEqual(len(output.getvalue().splitlines()), 2)

    def test_metadata_preview_limit_keeps_valid_persisted_entry(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict('os.environ', {'ADBDESK_CAPTURE_DIRECTORY': directory}), contextlib.redirect_stdout(io.StringIO()):
            capture = addon.Capture()
            flow = self.make_flow()
            flow.request.headers['content-type'] = 'text/plain; charset=latin-1'
            flow.request.headers['content-encoding'] = 'identity'
            flow.response.headers['content-type'] = 'text/plain; charset=latin-1'
            flow.request.headers['X-Large'] = '\u4e00' * 15000
            flow.response.headers['X-Large'] = '\u4e00' * 15000
            capture.requestheaders(flow)
            flow.request.stream(b'\xff' * 65000)
            capture.request(flow)
            capture.responseheaders(flow)
            flow.response.stream(b'\xff' * 65000)
            capture.response(flow)
            files = list(pathlib.Path(directory).glob('*.json'))
            self.assertEqual(len(files), 1)
            event = json.loads(files[0].read_text())
            self.assertTrue(event['Finished'])
            self.assertLessEqual(files[0].stat().st_size, 1_100_000)

    def test_metadata_failure_retains_previous_snapshot_and_forwarding(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict('os.environ', {'ADBDESK_CAPTURE_DIRECTORY': directory}), contextlib.redirect_stdout(io.StringIO()) as output:
            capture = addon.Capture()
            flow = self.make_flow()
            capture.requestheaders(flow)
            files = list(pathlib.Path(directory).glob('*.json'))
            self.assertEqual(len(files), 1)
            with patch.object(addon.os, 'replace', side_effect=OSError('disk metadata full')):
                self.assertEqual(flow.request.stream(b'bytes'), b'bytes')
                capture.request(flow)
            self.assertFalse(json.loads(files[0].read_text())['Finished'])
            self.assertIn('disk metadata full', output.getvalue())
            self.assertEqual(list(pathlib.Path(directory).glob('*.tmp')), [])

    def test_session_and_count_caps_bound_saved_files(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict('os.environ', {'ADBDESK_CAPTURE_DIRECTORY': directory}), contextlib.redirect_stdout(io.StringIO()):
            with patch.object(addon, 'SESSION_LIMIT', 3_000_000), patch.object(addon, 'ENTRY_LIMIT', 1):
                capture = addon.Capture()
                flow = self.make_flow()
                capture.requestheaders(flow)
                chunk = b'x' * 2_000_000
                self.assertIs(flow.request.stream(chunk), chunk)
                capture.request(flow)
                capture.responseheaders(flow)
                capture.response(flow)
                other = self.make_flow()
                other.id = 'second'
                capture.requestheaders(other)
                self.assertIs(other.request.stream(chunk), chunk)
                capture.request(other)
                files = list(pathlib.Path(directory).glob('*.json'))
                self.assertEqual(len(files), 1)
                saved = json.loads(files[0].read_text())
                self.assertFalse(saved['RequestCapture']['Complete'])
                self.assertLessEqual(sum(p.stat().st_size for p in pathlib.Path(directory).iterdir()), 3_000_000)
                warning = pathlib.Path(directory) / 'storage-warning.txt'
                self.assertTrue(warning.exists(), 'capture losses must remain visible after restarting the workbench')
                self.assertLessEqual(warning.stat().st_size, 4096)
                self.assertIn('limit', warning.read_text())

    def test_header_limit_is_explicit_and_cannot_claim_completeness(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict('os.environ', {'ADBDESK_CAPTURE_DIRECTORY': directory}), contextlib.redirect_stdout(io.StringIO()):
            flow = self.make_flow()
            flow.request.headers['too-large'] = 'x' * 20000
            capture = addon.Capture()
            capture.requestheaders(flow)
            capture.request(flow)
            saved = json.loads(next(pathlib.Path(directory).glob('*.json')).read_text())
            self.assertFalse(saved['RequestHeadersComplete'])

    def make_flow(self):
        class Headers(dict):
            def items(self, multi=False):
                return list(super().items()) + [('X-Duplicate', 'one'), ('X-Duplicate', 'two')]
        return types.SimpleNamespace(id='../unsafe-flow', metadata={}, error=None,
            request=types.SimpleNamespace(method='POST', pretty_url='https://example.test/?a=1&a=2',
                headers=Headers({'content-type': 'application/octet-stream', 'content-encoding': 'gzip'}),
                http_version='HTTP/2.0', timestamp_start=100, raw_content=None),
            response=types.SimpleNamespace(status_code=200, headers=Headers(), timestamp_end=101, raw_content=None))

    def test_persistence_saves_raw_encoded_bytes_and_repeated_headers(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict('os.environ', {'ADBDESK_CAPTURE_DIRECTORY': directory}), contextlib.redirect_stdout(io.StringIO()):
            capture = addon.Capture()
            flow = self.make_flow()
            raw = gzip.compress(b'hello\x00world')
            capture.requestheaders(flow)
            self.assertIs(flow.request.stream(raw), raw)
            capture.request(flow)
            capture.responseheaders(flow)
            flow.response.stream(b'ok')
            capture.response(flow)
            files = list(pathlib.Path(directory).glob('*.json'))
            self.assertEqual(len(files), 1, 'completed flow must persist independently of stdout/UI')
            event = json.loads(files[0].read_text())
            body = event['RequestCapture']
            self.assertTrue(body['Complete'])
            self.assertEqual(body['TotalBytes'], len(raw))
            self.assertEqual((pathlib.Path(directory) / body['FileName']).read_bytes(), raw)
            self.assertEqual([h['Value'] for h in event['RequestHeaderItems'] if h['Name'] == 'X-Duplicate'], ['one', 'two'])
            self.assertTrue(event['RequestHeadersComplete'])
            self.assertTrue(event['Finished'])

    def test_body_cap_keeps_forwarding_and_persists_incomplete_marker(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict('os.environ', {'ADBDESK_CAPTURE_DIRECTORY': directory}), contextlib.redirect_stdout(io.StringIO()) as output:
            capture = addon.Capture()
            flow = self.make_flow()
            capture.requestheaders(flow)
            chunk = b'x' * (4 * 1024 * 1024 + 7)
            self.assertIs(flow.request.stream(chunk), chunk)
            capture.request(flow)
            files = list(pathlib.Path(directory).glob('*.json'))
            self.assertEqual(len(files), 1, 'cap must retain a readable metadata record')
            body = json.loads(files[0].read_text())['RequestCapture']
            self.assertFalse(body['Complete'])
            self.assertEqual(body['SavedBytes'], 4 * 1024 * 1024)
            self.assertEqual(body['TotalBytes'], len(chunk))
            self.assertTrue(body['Error'])
            self.assertIn('diagnostic', output.getvalue())

    def test_storage_failure_keeps_forwarding_and_reports_error(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict('os.environ', {'ADBDESK_CAPTURE_DIRECTORY': directory}), contextlib.redirect_stdout(io.StringIO()) as output:
            capture = addon.Capture()
            flow = self.make_flow()
            with patch('builtins.open', side_effect=OSError('disk full')):
                capture.requestheaders(flow)
                raw = b'unchanged'
                self.assertIs(flow.request.stream(raw), raw)
                capture.request(flow)
            events = [json.loads(line.removeprefix(addon.PREFIX)) for line in output.getvalue().splitlines()]
            final = [e for e in events if e['type'] == 'entry'][-1]
            self.assertIsNotNone(final.get('RequestCapture'), 'storage failure must have explicit metadata')
            self.assertFalse(final['RequestCapture']['Complete'])
            self.assertIn('disk full', final['RequestCapture']['Error'])
            self.assertTrue(any(e['type'] == 'diagnostic' for e in events))

    def test_interrupted_flow_has_saved_incomplete_snapshot(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict('os.environ', {'ADBDESK_CAPTURE_DIRECTORY': directory}), contextlib.redirect_stdout(io.StringIO()):
            capture = addon.Capture()
            flow = self.make_flow()
            flow.response = None
            capture.requestheaders(flow)
            flow.request.stream(b'partial')
            capture.request(flow)
            files = list(pathlib.Path(directory).glob('*.json'))
            self.assertEqual(len(files), 1)
            saved = json.loads(files[0].read_text())
            self.assertFalse(saved['Finished'])
            self.assertIsNone(saved['ResponseCapture'])

    def test_stream_preserves_all_bytes_and_bounds_preview(self):
        self.assertTrue(hasattr(addon, 'BodyPreview'), 'capture BodyPreview is not implemented')
        preview = addon.BodyPreview()
        chunk = b'a' * 100000
        self.assertIs(preview.feed(chunk), chunk)
        self.assertEqual(preview.feed(b''), b'')
        text = preview.render({'content-type': 'text/plain'})
        self.assertIn('truncated', text)
        self.assertLess(len(text), 66000)
        self.assertEqual(preview.total, 100000)

    def test_flow_events_include_headers_status_and_duration(self):
        self.assertTrue(hasattr(addon, 'Capture'), 'capture addon is not implemented')
        headers = type('Headers', (dict,), {'items': lambda self, multi=False: super(type(self), self).items()})
        request = types.SimpleNamespace(method='POST', pretty_url='https://example.test/', headers=headers({'content-type':'application/json'}), timestamp_start=100, raw_content=None)
        response = types.SimpleNamespace(status_code=201, headers=headers({'content-type':'text/plain'}), timestamp_end=100.25, raw_content=None)
        flow = types.SimpleNamespace(id='a', request=request, response=response, metadata={}, error=None)
        capture = addon.Capture()
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            capture.requestheaders(flow)
            request.stream(b'{"a":1}')
            capture.request(flow)
            capture.responseheaders(flow)
            response.stream(b'done')
            capture.response(flow)
        events = [json.loads(line.removeprefix('ADBDESK_CAPTURE ')) for line in output.getvalue().splitlines()]
        final = events[-1]
        self.assertEqual(final['StatusCode'], 201)
        self.assertEqual(final['DurationMs'], 250)
        self.assertEqual(final['RequestBody'], '{"a":1}')
        self.assertEqual(final['ResponseBody'], 'done')

    def test_binary_and_encoded_bodies_are_explicit(self):
        self.assertTrue(hasattr(addon, 'BodyPreview'), 'capture BodyPreview is not implemented')
        preview = addon.BodyPreview()
        preview.feed(b'\x00\xff\x80')
        self.assertIn('base64', preview.render({'content-type':'application/octet-stream'}))
        self.assertIn('gzip', preview.render({'content-type':'text/plain', 'content-encoding':'gzip'}))

    def test_concurrent_streams_have_a_global_preview_budget(self):
        capture = addon.Capture()
        flows = []
        for number in range(140):
            request = types.SimpleNamespace()
            flow = types.SimpleNamespace(id=str(number), request=request, metadata={})
            capture.requestheaders(flow)
            flows.append(flow)
        self.assertIs(flows[-1].request.stream, True, 'excess flows must stream without allocating previews')
        self.assertTrue(callable(flows[0].request.stream))

if __name__ == '__main__':
    unittest.main()
