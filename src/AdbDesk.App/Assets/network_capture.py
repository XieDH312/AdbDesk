"""AdbDesk mitmproxy addon. Body streams always pass through unchanged."""
import base64
import datetime
import json
import os
import pathlib
import time
import uuid
import zlib
from mitmproxy import http

PREFIX = 'ADBDESK_CAPTURE '
LIMIT = 64 * 1024
HEADER_LIMIT = 16 * 1024
BODY_LIMIT = 4 * 1024 * 1024
SESSION_LIMIT = 256 * 1024 * 1024
METADATA_LIMIT = 1_100_000
ENTRY_LIMIT = 5000


class BodyPreview:
    def __init__(self, store=None, filename=None, changed=None):
        self.data = bytearray()
        self.total = 0
        self.store = store
        self.filename = filename
        self.changed = changed
        self.saved = 0
        self.finished = False
        self.closed = False
        self.error = ''

    def feed(self, chunk):
        if self.closed:
            return chunk
        self.total += len(chunk)
        remaining = LIMIT - len(self.data)
        if remaining > 0:
            self.data.extend(chunk[:remaining])
        if self.store and chunk:
            if not self.error:
                allowed = min(len(chunk), BODY_LIMIT - self.saved, self.store.available())
                try:
                    if allowed:
                        with open(self.store.directory / self.filename, 'ab') as stream:
                            stream.write(chunk[:allowed])
                        self.saved += allowed
                        self.store.used += allowed
                    if allowed < len(chunk):
                        self.error = 'Body limit (4 MiB) or session limit (256 MiB) reached; forwarding continues.'
                except OSError as error:
                    self.error = 'Body persistence failed: ' + str(error)[:512]
                    # Reconcile partially written files after disk-full errors.
                    try:
                        actual = (self.store.directory / self.filename).stat().st_size
                        self.store.used += max(0, actual - self.saved)
                        self.saved = actual
                    except OSError:
                        pass
                if self.error:
                    self.store.diagnostic(self.error)
            if self.changed:
                self.changed()
        return chunk

    def capture(self):
        error = self.error or ('' if self.finished else 'Body stream has not completed.')
        if not self.store:
            error = error or 'Body storage is unavailable.'
        return {'FileName': self.filename if self.saved else None, 'TotalBytes': self.total,
                'SavedBytes': self.saved, 'Complete': bool(self.store and self.finished and not error and self.total == self.saved), 'Error': error}

    def render(self, headers):
        data = bytes(self.data)
        encoding = headers.get('content-encoding', '').lower()
        truncated = self.total > len(data)
        note = ''
        if data and encoding in ('gzip', 'deflate'):
            try:
                decoder = zlib.decompressobj(31 if encoding == 'gzip' else 15)
                data = decoder.decompress(data, LIMIT + 1)
                truncated = truncated or len(data) > LIMIT or bool(decoder.unconsumed_tail)
                data = data[:LIMIT]
                note = '[decoded ' + encoding + ']\n'
            except zlib.error:
                note = '[encoded ' + encoding + '; base64]\n'
        elif encoding and encoding != 'identity':
            note = '[encoded ' + encoding + '; base64]\n'
        content_type = headers.get('content-type', '').lower()
        textual = any(part in content_type for part in ('text/', 'json', 'xml', 'javascript', 'x-www-form-urlencoded', 'graphql'))
        if 'base64' in note or (data and (not textual or b'\x00' in data)):
            text = note + ('' if 'base64' in note else '[binary; base64]\n') + base64.b64encode(data[:48000]).decode('ascii')
            truncated = truncated or len(data) > 48000
        else:
            charset = 'utf-8'
            if 'charset=' in content_type:
                charset = content_type.split('charset=', 1)[1].split(';')[0].strip(' "\'')
            try:
                text = note + data.decode(charset, errors='replace')
            except LookupError:
                text = note + data.decode('utf-8', errors='replace')
        if truncated:
            text += '\n[truncated preview; ' + str(self.total) + ' wire bytes]'
        return text


def emit(event):
    try:
        print(PREFIX + json.dumps(event, ensure_ascii=True, separators=(',', ':')), flush=True)
    except (OSError, ValueError):
        pass  # A closed UI/engine pipe cannot interrupt the forwarded stream.


class CaptureStore:
    def __init__(self, directory):
        self.directory = pathlib.Path(directory)
        self.used = 0
        self.reserved = 0
        self.entries = {}
        self.count = 0
        self.last_error = ''
        self.directory.mkdir(parents=True, exist_ok=True)
        self.used = sum(p.stat().st_size for p in self.directory.iterdir() if p.is_file())
        self.warning_size = 0
        warning = self.directory / 'storage-warning.txt'
        if warning.exists():
            self.warning_size = warning.stat().st_size
        self.reserved = max(0, 4096 - self.warning_size)

    def diagnostic(self, message):
        if message != self.last_error:
            emit({'type': 'diagnostic', 'Error': message[:4096]})
            self.last_error = message
            warning = self.directory / 'storage-warning.txt'
            try:
                with open(warning, 'wb') as stream:
                    stream.write(message.encode('utf-8')[:4096])
            except OSError:
                pass  # Best effort on full/unwritable disks; stdout still reports the loss.
            try:
                actual = warning.stat().st_size
                difference = actual - self.warning_size
                self.used += difference
                self.reserved -= difference
                self.warning_size = actual
            except OSError:
                pass

    def available(self):
        return max(0, SESSION_LIMIT - self.used - self.reserved)

    def admit(self):
        # Reserve both current metadata and an atomic replacement before bodies consume disk budget.
        if self.count >= ENTRY_LIMIT or self.available() < 2 * METADATA_LIMIT:
            self.diagnostic('Session storage/count limit reached; subsequent flows are not saved; forwarding continues.')
            return None
        key = uuid.uuid4().hex
        self.entries[key] = 0
        self.count += 1
        self.reserved += 2 * METADATA_LIMIT
        return key

    def save(self, key, event):
        temporary = self.directory / (key + '.tmp')
        try:
            data = json.dumps(event, ensure_ascii=True, separators=(',', ':')).encode('utf-8')
            if len(data) > METADATA_LIMIT:
                raise OSError('Metadata limit exceeded')
            with open(temporary, 'wb') as stream:
                stream.write(data)
                stream.flush()
            os.replace(temporary, self.directory / (key + '.json'))
            difference = len(data) - self.entries[key]
            self.used += difference
            self.reserved -= difference
            self.entries[key] = len(data)
            return True
        except OSError as error:
            self.diagnostic('Metadata persistence failed: ' + str(error)[:512])
            try:
                temporary.unlink(missing_ok=True)
            except OSError:
                pass
            return False

    def release(self, key):
        self.reserved -= 2 * METADATA_LIMIT - self.entries.pop(key)


def text_units(text):
    return len(text.encode('utf-16-le', errors='surrogatepass')) // 2


def bounded_text(text, limit):
    return text.encode('utf-16-le', errors='surrogatepass')[:limit * 2].decode('utf-16-le', errors='ignore')


def header_text(headers):
    result = ''
    for key, value in headers.items(multi=True):
        line = str(key) + ': ' + str(value) + '\n'
        if text_units(result) + text_units(line) > HEADER_LIMIT:
            return result + bounded_text(line, max(0, HEADER_LIMIT - text_units(result))) + '\n[headers truncated]'
        result += line
    return result


def header_items(headers):
    result, size = [], 0
    for key, value in headers.items(multi=True):
        key, value = str(key), str(value)
        size += text_units(key) + text_units(value)
        if size > HEADER_LIMIT or len(result) >= 256:
            return result, False
        result.append({'Name': key, 'Value': value})
    return result, True


class Capture:
    def __init__(self):
        self.active = set()
        self.store = None
        self.last_concurrency_notice = float('-inf')
        directory = os.environ.get('ADBDESK_CAPTURE_DIRECTORY')
        if directory:
            try:
                self.store = CaptureStore(directory)
            except OSError as error:
                emit({'type': 'diagnostic', 'Error': 'Capture storage unavailable: ' + str(error)[:512]})

    def running(self):
        emit({'type': 'ready'})

    def requestheaders(self, flow: http.HTTPFlow):
        if len(self.active) >= 128:
            flow.request.stream = True
            now = time.monotonic()
            if now - self.last_concurrency_notice >= 3:
                self.last_concurrency_notice = now
                message = 'Concurrent capture limit reached; this flow is not saved; forwarding continues.'
                if self.store:
                    self.store.diagnostic(message)
                else:
                    emit({'type': 'diagnostic', 'Error': message})
            return
        self.active.add(flow.id)
        key = self.store.admit() if self.store else None
        flow.metadata['adbdesk_key'] = key
        preview = BodyPreview(self.store if key else None, key + '-request.body' if key else None,
                              lambda: self.progress_snapshot(flow))
        flow.metadata['adbdesk_request'] = preview
        flow.request.stream = preview.feed
        if key:
            self.snapshot(flow, publish=False)

    def responseheaders(self, flow: http.HTTPFlow):
        if flow.id not in self.active:
            flow.response.stream = True
            return
        key = flow.metadata.get('adbdesk_key')
        preview = BodyPreview(self.store if key else None, key + '-response.body' if key else None,
                              lambda: self.progress_snapshot(flow))
        flow.metadata['adbdesk_response'] = preview
        flow.response.stream = preview.feed
        if key:
            self.snapshot(flow, publish=False)

    def request(self, flow: http.HTTPFlow):
        if flow.id not in self.active:
            return
        if preview := flow.metadata.get('adbdesk_request'):
            preview.finished = True
        finished = bool(flow.metadata.get('adbdesk_response_finished'))
        self.snapshot(flow, finished=finished)
        if finished:
            self.release(flow)

    def response(self, flow: http.HTTPFlow):
        if flow.id not in self.active:
            return
        if preview := flow.metadata.get('adbdesk_response'):
            preview.finished = True
        flow.metadata['adbdesk_response_finished'] = True
        request = flow.metadata.get('adbdesk_request')
        finished = bool(request and request.finished)
        self.snapshot(flow, finished=finished)
        if finished:
            self.release(flow)

    def error(self, flow: http.HTTPFlow):
        if flow.id not in self.active:
            return
        emit({'type': 'diagnostic', 'Error': ('HTTP/TLS flow failed: ' + str(flow.error))[:4096]})
        self.snapshot(flow, finished=True)
        self.release(flow)

    def tls_failed_client(self, data):
        emit({'type': 'diagnostic', 'Error': 'Client TLS handshake failed; verify app trust configuration, certificate pinning and TLS compatibility. No trust was established.'})

    def tls_failed_server(self, data):
        emit({'type': 'diagnostic', 'Error': 'Upstream TLS handshake failed; check server certificate and TLS compatibility.'})

    def release(self, flow):
        self.active.discard(flow.id)
        for name in ('adbdesk_request', 'adbdesk_response'):
            preview = flow.metadata.pop(name, None)
            if preview:
                preview.closed = True
                preview.changed = None
        flow.metadata.pop('adbdesk_response_finished', None)
        flow.metadata.pop('adbdesk_next_snapshot', None)
        key = flow.metadata.pop('adbdesk_key', None)
        if self.store and key:
            self.store.release(key)

    def progress_snapshot(self, flow):
        now = time.monotonic()
        if now >= flow.metadata.get('adbdesk_next_snapshot', 0):
            flow.metadata['adbdesk_next_snapshot'] = now + 0.25
            self.snapshot(flow, publish=False)

    def snapshot(self, flow, finished=False, publish=True):
        request = flow.request
        response = flow.response
        start = request.timestamp_start
        end = response.timestamp_end if response else None
        req = flow.metadata.get('adbdesk_request')
        res = flow.metadata.get('adbdesk_response')
        request_items, request_complete = header_items(request.headers)
        response_items, response_complete = header_items(response.headers) if response else (None, False)
        event = {'type': 'entry', 'Id': str(flow.id),
              'Time': datetime.datetime.fromtimestamp(start, datetime.timezone.utc).isoformat(),
              'Method': bounded_text(request.method, 128), 'Url': bounded_text(request.pretty_url, 16384),
              'RequestTargetComplete': text_units(request.method) <= 128 and text_units(request.pretty_url) <= 16384,
              'StatusCode': response.status_code if response else None,
              'DurationMs': round(max(0, end - start) * 1000, 2) if end is not None else None,
              'RequestHeaders': header_text(request.headers),
              'RequestBody': req.render(request.headers) if req else '[body preview unavailable; concurrent capture limit]',
              'ResponseHeaders': header_text(response.headers) if response else '',
              'ResponseBody': res.render(response.headers) if res and response else ('[body preview unavailable; concurrent capture limit]' if response else ''),
              'Error': bounded_text(str(flow.error), 4096) if flow.error else '',
              'RequestHeaderItems': request_items, 'ResponseHeaderItems': response_items,
              'RequestHeadersComplete': request_complete, 'ResponseHeadersComplete': response_complete,
              'RequestCapture': req.capture() if req else None, 'ResponseCapture': res.capture() if res else None,
              'HttpVersion': getattr(request, 'http_version', '')[:32],
              'ResponseHttpVersion': getattr(response, 'http_version', '')[:32] if response else '', 'Finished': finished}
        # Headers and escaped previews share the wire/metadata budget. Shorten only
        # display previews; the independent raw files remain byte-for-byte intact.
        if len(json.dumps(event, ensure_ascii=True, separators=(',', ':'))) > METADATA_LIMIT:
            for field in ('RequestBody', 'ResponseBody'):
                event[field] = event[field][:32768] + '\n[preview shortened for metadata limit]'
        key = flow.metadata.get('adbdesk_key')
        if key and self.store:
            self.store.save(key, event)
        if publish:
            emit(event)


addons = [Capture()]
