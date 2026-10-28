import csv
import importlib.util
import subprocess
import sys
from pathlib import Path

import pytest

from afp_ml import cli

GENERATOR = Path(__file__).resolve().parents[2] / "tools" / "data" / "generate_synthetic_telemetry.py"


@pytest.fixture
def clean_env(monkeypatch, tmp_path):
    for name in ("AFP_ML_READER_DSN", "AFP_ML_WRITER_DSN"):
        monkeypatch.delenv(name, raising=False)
    monkeypatch.setenv("AFP_ML_ENV_FILE", str(tmp_path / "missing.env"))  # ensure no real .env is picked up
    empty = tmp_path / "empty.env"
    empty.write_text("", encoding="utf-8")
    return empty


@pytest.mark.parametrize("argv", [["audit"], ["train"], ["score"], ["worker", "--once"], ["register", "--latest"]])
def test_commands_fail_clearly_without_writer_dsn(argv, clean_env, capsys):
    code = cli.run(["--env-file", str(clean_env), *argv])
    assert code == cli.EXIT_CONFIG
    assert "AFP_ML_WRITER_DSN is not set" in capsys.readouterr().err


@pytest.mark.parametrize("argv", [["status"], ["audit", "--no-record"]])
def test_read_only_commands_fail_clearly_without_reader_dsn(argv, clean_env, capsys):
    assert cli.run(["--env-file", str(clean_env), *argv]) == cli.EXIT_CONFIG
    assert "AFP_ML_READER_DSN is not set" in capsys.readouterr().err


@pytest.mark.parametrize("cmd", [[], ["audit"], ["train"], ["register"], ["score"], ["status"], ["worker"]])
def test_help(cmd, capsys):
    with pytest.raises(SystemExit) as exc:
        cli.run([*cmd, "--help"])
    assert exc.value.code == 0
    assert "usage" in capsys.readouterr().out


def test_register_requires_selector(capsys):
    with pytest.raises(SystemExit) as exc:
        cli.run(["register"])
    assert exc.value.code == 2


def _load_generator():
    spec = importlib.util.spec_from_file_location("gen", GENERATOR)
    mod = importlib.util.module_from_spec(spec)
    sys.modules["gen"] = mod
    spec.loader.exec_module(mod)
    return mod


def test_generator_output(tmp_path):
    out = tmp_path / "t.csv"
    gen = _load_generator()
    assert gen.main(["--out", str(out), "--days", "2", "--seed", "3", "--end", "2026-10-01"]) == 0
    with open(out, newline="", encoding="utf-8") as fh:
        rows = list(csv.reader(fh))
    assert rows[0] == gen.HEADER
    data = rows[1:]
    grid = 12 * 2 * 2 * 144
    assert len(data) == grid + 3 + 1  # grid + duplicates + unknown aircraft
    assert {r[7] for r in data} == {"SYNTHETIC_DEMO"}
    regs = {r[0] for r in data}
    assert {f"DEMO-A{i:02d}" for i in range(1, 13)} <= regs
    assert {r[4] for r in data} >= {"C", "F", ""} and {r[6] for r in data} >= {"kPa", "psi"}
    assert sum(1 for r in data if not r[2].endswith("Z")) == 3
    assert max(r[2] for r in data if r[2].endswith("Z")) < "2026-10-01T00:00:00Z"
    keys = [(r[0], r[1], r[2]) for r in data]
    assert len(keys) - len(set(keys)) == 3
    assert any(r[3] == "-300.00" or r[3] == "-520.00" for r in data)
    # No maintenance / label columns are generated.
    assert not any("maint" in h or "fail" in h for h in rows[0])


def test_generator_is_deterministic(tmp_path):
    gen = _load_generator()
    a, b = tmp_path / "a.csv", tmp_path / "b.csv"
    gen.main(["--out", str(a), "--days", "1", "--seed", "9", "--end", "2026-10-01"])
    gen.main(["--out", str(b), "--days", "1", "--seed", "9", "--end", "2026-10-01"])
    assert a.read_bytes() == b.read_bytes()
