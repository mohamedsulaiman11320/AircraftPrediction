import os
from pathlib import Path

import pytest

from afp_ml.config import ConfigError, Settings, load_env_file, load_env_files, parse_env_file


def test_parse_env_file(tmp_path):
    p = tmp_path / ".env"
    p.write_text("# comment\n\nexport A=1\nB = 'two words'\nC=\"x # y\"\nD=plain # trailing\nE=\n", encoding="utf-8")
    assert parse_env_file(p) == {"A": "1", "B": "two words", "C": "x # y", "D": "plain", "E": ""}


def test_env_file_does_not_override_existing(tmp_path):
    p = tmp_path / ".env"
    p.write_text("AFP_X=file\nAFP_Y=file\n", encoding="utf-8")
    env = {"AFP_X": "real"}
    load_env_file(p, env)
    assert env == {"AFP_X": "real", "AFP_Y": "file"}


def test_defaults_and_model_dir():
    s = Settings.from_env({"LOCALAPPDATA": r"C:\Users\u\AppData\Local"})
    assert s.prediction_horizon_days == 30
    assert s.min_positive_labels == 30 and s.min_aircraft_with_positives == 5
    assert s.if_contamination == 0.01
    assert s.model_dir == Path(r"C:\Users\u\AppData\Local") / "AircraftFailurePrediction" / "models"
    assert s.reader_dsn is None and s.writer_dsn is None


def test_missing_dsn_messages():
    s = Settings.from_env({})
    with pytest.raises(ConfigError, match="AFP_ML_WRITER_DSN is not set"):
        s.require_writer_dsn()
    with pytest.raises(ConfigError, match="AFP_ML_READER_DSN is not set"):
        s.require_reader_dsn()


def test_invalid_values_rejected():
    with pytest.raises(ConfigError):
        Settings.from_env({"AFP_IF_CONTAMINATION": "0.9"})
    with pytest.raises(ConfigError):
        Settings.from_env({"AFP_PREDICTION_HORIZON_DAYS": "abc"})


def test_public_dict_hides_dsn():
    s = Settings.from_env({"AFP_ML_WRITER_DSN": "postgresql://u:secret@h/aircraft_predict_db",
                           "ML_READER_USER": "r", "ML_READER_PASSWORD": "pw-reader-secret"})
    d = s.public_dict()
    assert "secret" not in str(d) and d["writer_dsn_configured"] is True and d["reader_dsn_configured"] is True
    assert d["db_name"] == "aircraft_predict_db"


def test_public_dict_hides_the_artifact_secret():
    s = Settings.from_env({"DB_NAME": "aircraft_predict_db", "AFP_ARTIFACT_ENDPOINT": "http://localhost:9000",
                           "AFP_ARTIFACT_BUCKET": "models", "AFP_ARTIFACT_ACCESS_KEY": "afp-ml-models",
                           "AFP_ARTIFACT_SECRET_KEY": "s3cr3t-value-xyz"})
    d = s.public_dict()
    assert "s3cr3t-value-xyz" not in str(d) and "artifact_secret_key" not in d
    assert d["artifact_credentials_configured"] is True and d["artifact_bucket"] == "models"
    assert "s3cr3t-value-xyz" not in repr(s)


def test_artifact_bucket_guards():
    base = {"DB_NAME": "aircraft_predict_db_synth", "AFP_ARTIFACT_ENDPOINT": "http://localhost:9000",
            "AFP_ARTIFACT_ACCESS_KEY": "afp-ml-synthetic", "AFP_ARTIFACT_SECRET_KEY": "x"}
    # The development config inheriting the operational bucket must fail, not write there.
    with pytest.raises(ConfigError):
        Settings.from_env({**base, "AFP_ARTIFACT_BUCKET": "models", "AFP_ARTIFACT_FORBIDDEN_BUCKETS": "models"})
    with pytest.raises(ConfigError):   # a bucket without credentials is a misconfiguration, not "local only"
        Settings.from_env({"DB_NAME": "aircraft_predict_db_synth", "AFP_ARTIFACT_BUCKET": "synthetic-dev"})
    assert Settings.from_env({**base, "AFP_ARTIFACT_BUCKET": "synthetic-dev",
                              "AFP_ARTIFACT_FORBIDDEN_BUCKETS": "models"}).artifact_bucket == "synthetic-dev"
    assert Settings.from_env({"DB_NAME": "aircraft_predict_db_synth"}).artifact_bucket is None


def test_dsn_composed_from_db_and_role_variables():
    s = Settings.from_env({"DB_HOST": "dbhost", "DB_PORT": "6543", "DB_NAME": "aircraft_predict_db",
                           "ML_READER_USER": "reader", "ML_READER_PASSWORD": "p@ss:w/rd",
                           "ETL_WRITER_USER": "etl user", "ETL_WRITER_PASSWORD": "x#y%z"})
    assert s.reader_dsn == "postgresql://reader:p%40ss%3Aw%2Frd@dbhost:6543/aircraft_predict_db"
    assert s.writer_dsn == "postgresql://etl%20user:x%23y%25z@dbhost:6543/aircraft_predict_db"


def test_dsn_defaults_host_port_name():
    s = Settings.from_env({"ML_READER_USER": "r", "ML_READER_PASSWORD": "p"})
    assert s.reader_dsn == "postgresql://r:p@localhost:5432/aircraft_predict_db"
    assert s.writer_dsn is None and s.db_name == "aircraft_predict_db"


def test_explicit_dsn_has_precedence():
    s = Settings.from_env({"AFP_ML_READER_DSN": "postgresql://a:b@h/aircraft_predict_db",
                           "ML_READER_USER": "r", "ML_READER_PASSWORD": "p", "DB_HOST": "other"})
    assert s.reader_dsn == "postgresql://a:b@h/aircraft_predict_db"


@pytest.mark.parametrize("env, match", [
    ({"AFP_ML_WRITER_DSN": "postgresql://u:p@h/aircraft_app"}, "protected database 'aircraft_app'"),
    ({"AFP_ML_READER_DSN": "host=h dbname=AIRCRAFT_APP user=u"}, "protected database"),
    ({"DB_NAME": "aircraft_app"}, "protected database"),
    ({"DB_NAME": "aircraft_app", "ML_READER_USER": "r"}, "protected database"),
    ({"AFP_ML_WRITER_DSN": "postgresql://u:p@h/some_other_db"}, "DB_NAME is 'aircraft_predict_db'"),
    ({"AFP_ML_WRITER_DSN": "postgresql://u:p@h/"}, "does not name a database"),
    ({"DB_FORBIDDEN_NAMES": "Legacy_DB, prod", "DB_NAME": "prod", "ML_READER_USER": "r"}, "protected database 'prod'"),
    ({"DB_FORBIDDEN_NAMES": "legacy_db", "DB_NAME": "legacy_db"}, "protected database"),
])
def test_database_guard_rejects(env, match):
    with pytest.raises(ConfigError, match=match) as exc:
        Settings.from_env(env)
    assert "p@h" not in str(exc.value) and ":p@" not in str(exc.value)  # never echo the DSN / password


def test_database_guard_accepts_configured_name():
    s = Settings.from_env({"DB_NAME": "afp_test", "DB_FORBIDDEN_NAMES": "aircraft_prod",
                           "AFP_ML_WRITER_DSN": "postgresql://u:p@h/afp_test"})
    assert s.db_name == "afp_test" and s.db_forbidden_names == ("aircraft_prod",)


def test_new_label_settings():
    s = Settings.from_env({})
    assert s.label_definition_confirmed is False and s.failure_codes == ()
    s = Settings.from_env({"ML_LABEL_DEFINITION_CONFIRMED": "true", "ML_FAILURE_CODES": " F01, ,F02 "})
    assert s.label_definition_confirmed is True and s.failure_codes == ("F01", "F02")


def test_root_env_does_not_override_ml_env_or_real_env(tmp_path):
    ml_env = tmp_path / "ml.env"
    ml_env.write_text("DB_NAME=from_ml\nDB_HOST=ml_host\n", encoding="utf-8")
    root_env = tmp_path / "root.env"
    root_env.write_text("DB_NAME=from_root\nDB_HOST=root_host\nDB_PORT=7777\nML_READER_USER=root_user\n",
                        encoding="utf-8")
    env = {"DB_HOST": "real_host"}
    loaded = load_env_files([ml_env, None, root_env, ml_env], env)
    assert loaded == [ml_env, root_env]
    assert env == {"DB_HOST": "real_host", "DB_NAME": "from_ml", "DB_PORT": "7777", "ML_READER_USER": "root_user"}


def test_cli_loads_root_env_after_ml_env(monkeypatch, tmp_path):
    from afp_ml import cli

    ml_env = tmp_path / "ml.env"
    ml_env.write_text("DB_NAME=aircraft_predict_db\n", encoding="utf-8")
    root_env = tmp_path / "root.env"
    root_env.write_text("DB_NAME=aircraft_app\nAFP_TEST_ROOT_ONLY=1\n", encoding="utf-8")
    monkeypatch.setattr(cli, "root_env_file", lambda: root_env)
    fake_environ = {k: v for k, v in os.environ.items() if k != "AFP_TEST_ROOT_ONLY"}
    monkeypatch.setattr(os, "environ", fake_environ)  # restored after the test
    # status needs a reader DSN; none is configured, so this stops with a config error before any connection.
    assert cli.run(["--env-file", str(ml_env), "status"]) == cli.EXIT_CONFIG
    assert fake_environ["DB_NAME"] == "aircraft_predict_db" and fake_environ["AFP_TEST_ROOT_ONLY"] == "1"


class _FakeCursor:
    def __init__(self, dbname):
        self.dbname = dbname

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False

    def execute(self, sql, params=None):
        assert sql == "SELECT current_database()"

    def fetchone(self):
        return (self.dbname,)


class _FakeConn:
    def __init__(self, dbname):
        self.dbname = dbname

    def cursor(self):
        return _FakeCursor(self.dbname)


def test_connection_database_verification():
    from afp_ml.db import DatabaseError, verify_database

    assert verify_database(_FakeConn("aircraft_predict_db"), "aircraft_predict_db") == "aircraft_predict_db"
    with pytest.raises(DatabaseError, match="DB_NAME is 'aircraft_predict_db'"):
        verify_database(_FakeConn("other_db"), "aircraft_predict_db")
    with pytest.raises(DatabaseError, match="protected database"):
        verify_database(_FakeConn("aircraft_app"), "aircraft_app")
