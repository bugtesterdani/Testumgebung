# -*- coding: utf-8 -*-
"""
Python-Worker der Testumgebung.

Protokoll: eine JSON-Nachricht pro Zeile über stdin/stdout (wie der .NET-Worker).
  Host -> Worker: compile, invoke, cancel, reply, exit
  Worker -> Host: hello, compiled, call, done, error

Nutzercode ist ein Funktionsrumpf. Variablen (Ports bzw. Ordnervariablen) sind lokale Namen;
geänderte Werte werden nach der Ausführung zurückgegeben. API: Pass, Fail, Error, NResult, Measured,
read, write, log, var, set_var, ask, ask_number, confirm, sleep, cancelled.
"""
import json
import hashlib
import importlib.metadata
import keyword
import queue
import sys
import threading
import time
import traceback

PY_TYPES = {"Bool", "Int", "Double", "String", "DateTime"}
_stdin = sys.stdin
_stdout = sys.stdout
_cache = {}
_call_counter = 0
_cancelled = set()
_replies = queue.Queue()
_requests = queue.Queue()
_write_lock = threading.Lock()


def send(message):
    with _write_lock:
        _stdout.write(json.dumps(message, ensure_ascii=False) + "\n")
        _stdout.flush()


def _reader():
    """Liest stdin im Hintergrund: Abbruch wirkt sofort, Antworten gehen an den wartenden Rückruf."""
    for line in _stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except ValueError:
            continue
        t = msg.get("t")
        if t == "cancel":
            _cancelled.add(msg.get("id"))
        elif t == "reply":
            _replies.put(msg)
        else:
            _requests.put(msg)
    _requests.put({"t": "exit"})
    _replies.put({"t": "closed"})


class Cancelled(Exception):
    """Kooperativer Abbruch (Werkerabbruch)."""


class HostError(Exception):
    """Fehler bei einem Rückruf an den Host."""


def _wrap(source, names):
    """Bettet den Nutzercode in eine Funktion ein. Liefert (Quelltext, Zeilenversatz)."""
    params = ", ".join(["__ctx"] + names)
    header = [
        f"def __block__({params}):",
        "    try:",
    ]
    body = source.replace("\t", "    ").splitlines() or ["pass"]
    if not any(line.strip() and not line.strip().startswith("#") for line in body):
        body.append("pass")
    lines = header + ["        " + line for line in body] + [
        "        pass",
        "    finally:",
        "        __capture__.update({k: v for k, v in locals().items() if k in __names__})",
    ]
    return "\n".join(lines) + "\n", len(header)


def _valid(name, type_):
    return name.isidentifier() and not keyword.iskeyword(name) and not name.startswith("__") and type_ in PY_TYPES


def _compile(source, variables):
    names = [v["name"] for v in variables if _valid(v["name"], v["type"])]
    key = (source, tuple(names))
    if key in _cache:
        return _cache[key]
    wrapped, offset = _wrap(source, names)
    try:
        code = compile(wrapped, "<codeblock>", "exec")
        result = (code, offset, names, None)
    except SyntaxError as e:
        line = (e.lineno or 0) - offset
        result = (None, offset, names, {
            "line": max(line, 0), "column": max((e.offset or 0) - 8, 1) if line > 0 else 0, "severity": "error",
            "message": f"{type(e).__name__}: {e.msg}", "code": "PY",
        })
    _cache[key] = result
    return result


def _convert(value, type_):
    if value is None:
        return None
    if type_ == "Double":
        return float(value)
    if type_ == "Int":
        return int(value)
    if type_ == "Bool":
        return bool(value)
    if type_ == "String":
        return str(value)
    return value  # DateTime als ISO-Text


def _out(value):
    if isinstance(value, bool) or value is None or isinstance(value, (int, float, str)):
        return value
    return str(value)


class Context:
    def __init__(self, invocation_id):
        self.id = invocation_id

    def call(self, op, args):
        global _call_counter
        if self.id in _cancelled:
            raise Cancelled()
        _call_counter += 1
        cid = f"p{_call_counter}"
        send({"t": "call", "id": self.id, "cid": cid, "op": op, "args": args})
        while True:
            msg = _replies.get()
            if msg.get("t") == "closed":
                raise HostError("Host-Verbindung getrennt")
            if msg.get("cid") == cid:
                if msg.get("error"):
                    raise HostError(msg["error"])
                return msg.get("value")


def _api(ctx):
    def outcome(rating, value=None, message=None, unit=None, low=None, high=None):
        return {"rating": rating, "value": value, "message": message, "unit": unit, "low": low, "high": high}

    def measured(value, low=None, high=None, unit=None):
        ok = (low is None or value >= low) and (high is None or value <= high)
        return outcome("Pass" if ok else "Fail", value, None if ok else f"{value} außerhalb {low}–{high}", unit, low, high)

    def sleep(seconds):
        end = time.time() + seconds
        while time.time() < end:
            if ctx.id in _cancelled:
                raise Cancelled()
            time.sleep(min(0.05, max(0.0, end - time.time())))

    def cancelled():
        return ctx.id in _cancelled

    def dev(op, resource, **kw):
        args = {"resource": resource}
        args.update({k: v for k, v in kw.items() if v is not None})
        return ctx.call(op, args)

    def data_arg(data, hex):
        if isinstance(data, (bytes, bytearray)):
            return bytes(data).hex(" "), True
        return str(data), bool(hex)

    def comm_send(resource, data, hex=False):
        d, h = data_arg(data, hex)
        dev("comm.send", resource, data=d, hex=h)

    def comm_receive(resource, timeout_ms=1000, end="\r\n", length=None, hex=False):
        return dev("comm.receive", resource, timeout=timeout_ms, end=end, length=length, hex=hex)

    def comm_query(resource, data, timeout_ms=1000, end="\r\n", length=None, hex=False):
        d, h = data_arg(data, hex)
        return dev("comm.query", resource, data=d, hex=h, timeout=timeout_ms, end=end, length=length)

    def modbus_read(resource, address, type="UInt16", low_first=False, length=None):
        return dev("modbus.read", resource, address=address, type=type, lowFirst=low_first, length=length)

    def modbus_write(resource, address, value, type="UInt16", low_first=False):
        dev("modbus.write", resource, address=address, value=_out(value), type=type, lowFirst=low_first)

    def can_send(resource, id, data=b"", extended=False, fd=False):
        d, _ = data_arg(data, True)
        dev("can.send", resource, id=int(id), data=d, extended=extended, fd=fd)

    def can_receive(resource, id, mask=None, timeout_ms=1000, extended=None, discard=False):
        f = dev("can.receive", resource, id=int(id), mask=mask, timeout=timeout_ms, extended=extended, discard=discard)
        if isinstance(f, str):
            f = json.loads(f)
        f["bytes"] = bytes.fromhex(f.get("data", ""))
        return f

    def uds(resource, request, tx=0x7E0, rx=0x7E8, timeout_ms=1000, extended=False, fd=False, timeout_pending_ms=5000,
            transport="can", tester=0x0E00, ecu=0x1000, tx_address=None, rx_address=None):
        d, _ = data_arg(request, True)
        return bytes.fromhex(dev("uds", resource, request=d, tx=int(tx), rx=int(rx), timeout=timeout_ms, extended=extended, fd=fd,
                                 timeoutPending=timeout_pending_ms, transport=transport, tester=tester, ecu=ecu,
                                 txAddress=tx_address, rxAddress=rx_address))

    def j1939_send(resource, pgn, data, source=0xF9, destination=0xFF, priority=6):
        d, _ = data_arg(data, True)
        dev("j1939.send", resource, pgn=int(pgn), data=d, source=source, destination=destination, priority=priority)

    def j1939_receive(resource, pgn, source=None, request=False, own=0xF9, timeout_ms=1000):
        m = dev("j1939.receive", resource, pgn=int(pgn), source=source, request=request, own=own, timeout=timeout_ms)
        if isinstance(m, str):
            m = json.loads(m)
        m["bytes"] = bytes.fromhex(m.get("data", ""))
        return m

    def lin_read(resource, id, length=8, classic=False, timeout_ms=100):
        return bytes.fromhex(dev("lin.read", resource, id=int(id), length=length, classic=classic, timeout=timeout_ms))

    def lin_write(resource, id, data, classic=False):
        d, _ = data_arg(data, True)
        dev("lin.write", resource, id=int(id), data=d, classic=classic)

    def opcua_read(resource, node):
        return dev("opcua.read", resource, node=node)

    def opcua_write(resource, node, value):
        dev("opcua.write", resource, node=node, value=_out(value))

    def opcua_call(resource, object, method, args=None):
        a = ";".join(str(x) for x in args) if args else ""
        out = dev("opcua.call", resource, object=object, method=method, args=a)
        return out

    return {
        "Pass": lambda value=None: outcome("Pass", value),
        "Fail": lambda message=None, value=None: outcome("Fail", value, message),
        "Error": lambda message: outcome("Error", None, message),
        "NResult": lambda value=None: outcome("NResult", value),
        "Measured": measured,
        "read": lambda resource: float(ctx.call("read", {"resource": resource})),
        "write": lambda resource, value: ctx.call("write", {"resource": resource, "value": float(value)}),
        "log": lambda message: ctx.call("log", {"message": str(message)}),
        "var": lambda path: ctx.call("get", {"path": path}),
        "set_var": lambda path, value: ctx.call("set", {"path": path, "value": _out(value)}),
        "ask": lambda text: ctx.call("ask", {"kind": "Input", "text": text, "type": "String", "required": True}),
        "ask_number": lambda text, min=None, max=None, unit=None: float(ctx.call(
            "ask", {"kind": "Input", "text": text, "type": "Double", "required": True, "min": min, "max": max, "unit": unit})),
        "confirm": lambda text: ctx.call("ask", {"kind": "Instruction", "text": text}),
        "select_test": lambda test, version=None, order=None, parameters=None, python_venv=None: ctx.call(
            "select.test", {"test": test, "version": version, "order": order, "parameters": parameters, "pythonVenv": python_venv}),
        "sleep": sleep,
        "cancelled": cancelled,
        "comm_send": comm_send,
        "comm_receive": comm_receive,
        "comm_query": comm_query,
        "configure_connection": lambda resource, address, options="": dev("comm.configure", resource, address=address, options=options),
        "http_request": lambda resource, path, method="GET", body="", headers="", timeout_ms=30000: dev("http.request", resource, path=path, method=method, body=body, headers=headers, timeout=timeout_ms),
        "ssh_execute": lambda resource, command, timeout_ms=30000: dev("ssh.exec", resource, command=command, timeout=timeout_ms),
        "sftp_upload": lambda resource, local, remote, overwrite=False, timeout_ms=30000: dev("sftp.upload", resource, local=local, remote=remote, overwrite=overwrite, timeout=timeout_ms),
        "sftp_download": lambda resource, remote, local, overwrite=False, timeout_ms=30000: dev("sftp.download", resource, local=local, remote=remote, overwrite=overwrite, timeout=timeout_ms),
        "modbus_read": modbus_read,
        "modbus_write": modbus_write,
        "can_send": can_send,
        "can_receive": can_receive,
        "uds": uds,
        "j1939_send": j1939_send,
        "j1939_receive": j1939_receive,
        "lin_read": lin_read,
        "lin_write": lin_write,
        "opcua_read": opcua_read,
        "opcua_write": opcua_write,
        "opcua_call": opcua_call,
    }


def _dbg_snapshot(frame, offset, api_names):
    """Lokale Variablen des Nutzercodes am Haltepunkt (ohne API-Namen und interne Namen)."""
    snap = {}
    for k, v in list(frame.f_locals.items()):
        if k.startswith("__") or k in api_names:
            continue
        snap[k] = _out(v)
    return snap


class _DebugStop(Exception):
    """Abbruch durch den Debugger (Stopp-Befehl am Haltepunkt)."""


def _make_tracer(ctx, offset, state, api_names):
    """settrace-Tracer: meldet Nutzercode-Zeilen (bzw. Eintritt) an den Host und wendet die Steuerung an."""

    def emit(frame, line, kind):
        reply = ctx.call("debug.at", {"line": line, "kind": kind, "vars": _dbg_snapshot(frame, offset, api_names)})
        if isinstance(reply, dict):
            state["breakpoints"] = set(reply.get("breakpoints") or [])
            state["step"] = bool(reply.get("step"))
            if reply.get("cmd") == "stop":
                raise _DebugStop()

    def localtrace(frame, event, arg):
        if event == "line":
            line = frame.f_lineno - offset
            if line >= 1 and (state["step"] or line in state["breakpoints"]):
                emit(frame, line, "line")
        return localtrace

    def globaltrace(frame, event, arg):
        if event == "call" and frame.f_code.co_filename == "<codeblock>":
            if state["entry"]:
                emit(frame, 1, "entry")
            return localtrace
        return None

    return globaltrace


def _user_line(tb, offset):
    for frame in reversed(traceback.extract_tb(tb)):
        if frame.filename == "<codeblock>":
            return frame.lineno - offset
    return None


def _invoke(msg):
    inv = msg["id"]
    variables = msg.get("vars", [])
    code, offset, names, error = _compile(msg.get("source", ""), variables)
    if code is None:
        send({"t": "error", "id": inv, "message": f"Build fehlgeschlagen: Zeile {error['line']}: {error['message']}"})
        return
    types = {v["name"]: v["type"] for v in variables}
    values = msg.get("values", {}) or {}
    ctx = Context(inv)
    capture = {}
    api = _api(ctx)
    namespace = dict(api)
    namespace["__capture__"] = capture
    namespace["__names__"] = set(names)

    debug = msg.get("debug") or {}
    tracer = None
    if debug.get("enabled"):
        state = {
            "breakpoints": set(debug.get("breakpoints") or []),
            "step": bool(debug.get("step")),
            "entry": bool(debug.get("entry")),
        }
        tracer = _make_tracer(ctx, offset, state, set(api.keys()))

    try:
        exec(code, namespace)
        args = [_convert(values.get(n), types[n]) for n in names]
        if tracer is not None:
            sys.settrace(tracer)
        try:
            result = namespace["__block__"](ctx, *args)
        finally:
            if tracer is not None:
                sys.settrace(None)
    except (Cancelled, _DebugStop):
        send({"t": "done", "id": inv, "rating": "NResult", "cancelled": True, "values": {}})
        return
    except Exception as e:  # noqa: BLE001 – Fehler des Nutzercodes werden zu ERROR
        line = _user_line(e.__traceback__, offset)
        where = f" (Zeile {line})" if line else ""
        send({"t": "error", "id": inv, "message": f"{type(e).__name__}: {e}{where}", "details": traceback.format_exc()})
        return
    finally:
        _cancelled.discard(inv)

    if result is None:
        result = {"rating": "NResult"}
    if not isinstance(result, dict) or "rating" not in result:
        result = {"rating": "NResult", "value": _out(result)}
    send({
        "t": "done", "id": inv,
        "rating": result.get("rating", "NResult"),
        "message": result.get("message"),
        "value": _out(result.get("value")),
        "unit": result.get("unit"),
        "low": result.get("low"),
        "high": result.get("high"),
        "values": {n: _out(capture.get(n)) for n in names if n in capture},
        "cancelled": False,
    })


def main():
    packages = sorted((d.metadata.get("Name", "").lower(), d.version) for d in importlib.metadata.distributions())
    package_hash = hashlib.sha256(json.dumps(packages, separators=(",", ":")).encode()).hexdigest()
    with open(sys.executable, "rb") as interpreter:
        executable_hash = hashlib.sha256(interpreter.read()).hexdigest()
    send({"t": "hello", "runtime": f"Python {sys.version.split()[0]} ({sys.implementation.cache_tag}); interpreter={executable_hash}; packages={package_hash}"})
    threading.Thread(target=_reader, daemon=True).start()
    while True:
        msg = _requests.get()
        t = msg.get("t")
        if t == "compile":
            code, _, _, error = _compile(msg.get("source", ""), msg.get("vars", []))
            send({"t": "compiled", "id": msg.get("id"), "ok": code is not None, "diagnostics": [error] if error else []})
        elif t == "invoke":
            _invoke(msg)
        elif t == "exit":
            break


if __name__ == "__main__":
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stdout = sys.stderr  # print() im Nutzercode darf das Protokoll nicht stören
    main()
