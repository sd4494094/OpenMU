#!/usr/bin/env python3
"""Regression tests for the real Startup process, using only in-memory demo data.

Run on Linux after building Startup:
python3 tests/Startup/test_process_shutdown.py --startup-dir /absolute/build/output --dotnet /path/to/dotnet
The demo uses its default game ports; stop other dev demo instances first.
No production database or configuration is loaded.
"""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time
import unittest

parser = argparse.ArgumentParser()
parser.add_argument("--startup-dir", required=True, type=Path)
parser.add_argument("--dotnet", default="dotnet")
parser.add_argument("--results", type=Path)
options, remaining = parser.parse_known_args()
startup = options.startup_dir.resolve()

class ProcessShutdownTest(unittest.TestCase):
    def check_signal(self, stop_signal):
        env = {key: value for key, value in os.environ.items()
               if not key.startswith(("DB_", "OPENMU_ACTOR_", "AdminMcp"))}
        env.update(ASPNETCORE_URLS="http://127.0.0.1:0",
                   ASPNETCORE_ENVIRONMENT="Production", RESOLVE_IP="loopback")
        with tempfile.TemporaryDirectory(prefix="openmu-shutdown-") as temporary:
            log_path = Path(temporary) / "process.log"
            with log_path.open("w") as log:
                process = subprocess.Popen(
                    [options.dotnet, str(startup / "MUnique.OpenMU.Startup.dll"),
                     "-demo", "-resolveIP:loopback"],
                    cwd=startup, env=env, stdin=subprocess.DEVNULL,
                    stdout=log, stderr=subprocess.STDOUT,
                    preexec_fn=lambda: signal.signal(signal.SIGINT, signal.SIG_DFL))
                try:
                    deadline = time.monotonic() + 180
                    while time.monotonic() < deadline and process.poll() is None:
                        if "Host started, elapsed time:" in log_path.read_text(errors="replace"):
                            break
                        time.sleep(0.1)
                    else:
                        self.fail("Demo startup failed or timed out:\n" + log_path.read_text(errors="replace")[-8000:])
                    # Let automatic server startup complete before exercising normal runtime shutdown.
                    time.sleep(2)
                    started = time.monotonic()
                    process.send_signal(stop_signal)
                    try:
                        exit_code = process.wait(timeout=15)
                    except subprocess.TimeoutExpired:
                        self.fail("Shutdown did not finish in 15 seconds:\n" + log_path.read_text(errors="replace")[-8000:])
                    self.assertEqual(exit_code, 0, log_path.read_text(errors="replace")[-8000:])
                    result = dict(signal=stop_signal.name, exit_code=exit_code,
                                  seconds=round(time.monotonic() - started, 3))
                    print(json.dumps(result), flush=True)
                    if options.results:
                        options.results.mkdir(parents=True, exist_ok=True)
                        (options.results / (stop_signal.name + ".json")).write_text(json.dumps(result, indent=2))
                finally:
                    if process.poll() is None:
                        process.kill()
                        process.wait()
                    if options.results:
                        options.results.mkdir(parents=True, exist_ok=True)
                        (options.results / (stop_signal.name + ".log")).write_bytes(log_path.read_bytes())

    @unittest.skipUnless(os.name == "posix", "Unix process signal regression")
    def test_sigterm_with_closed_stdin(self):
        self.check_signal(signal.SIGTERM)

    @unittest.skipUnless(os.name == "posix", "Unix process signal regression")
    def test_sigint_with_closed_stdin(self):
        self.check_signal(signal.SIGINT)

if __name__ == "__main__":
    unittest.main(argv=[__file__] + remaining)
