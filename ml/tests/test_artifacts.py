import os
import stat

import pytest

from afp_ml.artifacts import ArtifactError, ArtifactStore, sha256_file


def _bundle():
    return {"kind": "AnomalyDetection", "pipeline": {"dummy": [1, 2, 3]}, "feature_names": ["a"]}


def test_save_hash_load_roundtrip(tmp_path):
    store = ArtifactStore(tmp_path / "models")
    saved = store.save("m", "20260101000000", _bundle(), {"note": "x"})
    assert saved.artifact_path == "m/20260101000000/model.joblib"
    assert sha256_file(saved.absolute_path) == saved.sha256
    assert not os.stat(saved.absolute_path).st_mode & stat.S_IWRITE  # read-only
    assert saved.metadata_path.is_file()
    loaded = store.load_verified(saved.artifact_path, saved.sha256)
    assert loaded["pipeline"] == {"dummy": [1, 2, 3]}


def test_tampered_artifact_is_refused(tmp_path):
    store = ArtifactStore(tmp_path / "models")
    saved = store.save("m", "v1", _bundle(), {})
    os.chmod(saved.absolute_path, stat.S_IWRITE | stat.S_IREAD)
    with open(saved.absolute_path, "ab") as fh:
        fh.write(b"evil")
    with pytest.raises(ArtifactError, match="SHA-256 mismatch"):
        store.load_verified(saved.artifact_path, saved.sha256)


def test_path_outside_model_dir_is_refused(tmp_path):
    store = ArtifactStore(tmp_path / "models")
    saved = store.save("m", "v1", _bundle(), {})
    outside = tmp_path / "elsewhere.joblib"
    outside.write_bytes(saved.absolute_path.read_bytes())
    with pytest.raises(ArtifactError, match="outside AFP_MODEL_DIR"):
        store.load_verified(str(outside), saved.sha256)
    with pytest.raises(ArtifactError, match="outside AFP_MODEL_DIR"):
        store.load_verified("../elsewhere.joblib", saved.sha256)


def test_missing_file_and_bad_hash(tmp_path):
    store = ArtifactStore(tmp_path / "models")
    with pytest.raises(ArtifactError, match="not found"):
        store.verify("m/v/model.joblib", "0" * 64)
    saved = store.save("m", "v1", _bundle(), {})
    with pytest.raises(ArtifactError, match="malformed"):
        store.verify(saved.artifact_path, "abc")


def test_unsafe_names_and_existing_version_rejected(tmp_path):
    store = ArtifactStore(tmp_path / "models")
    with pytest.raises(ArtifactError):
        store.save("../x", "v1", _bundle(), {})
    store.save("m", "v1", _bundle(), {})
    with pytest.raises(ArtifactError, match="already exists"):
        store.save("m", "v1", _bundle(), {})


# ---- durable copy in an object store (MinIO in production) -----------------------------------------

class FakeBucket:
    """In-memory stand-in for a MinIO bucket; like the real policy, it offers no delete."""

    def __init__(self, bucket="models", fail_put=False):
        self.bucket, self.objects, self.fail_put = bucket, {}, fail_put

    def put(self, key, data, content_type):
        if self.fail_put:
            raise OSError("MinIO unavailable")
        self.objects[key] = bytes(data)

    def get(self, key):
        return self.objects.get(key)


def test_save_uploads_model_and_metadata_and_records_the_uri(tmp_path):
    bucket = FakeBucket("synthetic-dev")
    store = ArtifactStore(tmp_path / "models", bucket, "models")
    saved = store.save("afp-failure-prediction", "20261027000000", _bundle(), {"note": "x"})
    assert saved.artifact_uri == "s3://synthetic-dev/models/afp-failure-prediction/20261027000000/model.joblib"
    key = "models/afp-failure-prediction/20261027000000/model.joblib"
    assert sha256_file(saved.absolute_path) == __import__("hashlib").sha256(bucket.objects[key]).hexdigest()
    assert "models/afp-failure-prediction/20261027000000/metadata.json" in bucket.objects


def test_a_failed_upload_fails_the_save_instead_of_staying_local_only(tmp_path):
    store = ArtifactStore(tmp_path / "models", FakeBucket(fail_put=True))
    with pytest.raises(OSError):
        store.save("m", "v1", _bundle(), {})


def test_missing_local_file_is_fetched_from_the_bucket_after_its_hash_is_checked(tmp_path):
    bucket = FakeBucket()
    saved = ArtifactStore(tmp_path / "a", bucket).save("m", "v1", _bundle(), {})
    other_host = ArtifactStore(tmp_path / "b", bucket)          # empty local cache
    loaded = other_host.load_verified(saved.artifact_path, saved.sha256)
    assert loaded["pipeline"] == {"dummy": [1, 2, 3]}
    assert (tmp_path / "b" / "m" / "v1" / "model.joblib").is_file()   # now cached


def test_a_tampered_object_in_the_bucket_is_never_cached_or_loaded(tmp_path):
    bucket = FakeBucket()
    saved = ArtifactStore(tmp_path / "a", bucket).save("m", "v1", _bundle(), {})
    bucket.objects["models/m/v1/model.joblib"] += b"evil"
    other_host = ArtifactStore(tmp_path / "b", bucket)
    with pytest.raises(ArtifactError, match="SHA-256"):
        other_host.load_verified(saved.artifact_path, saved.sha256)
    assert not (tmp_path / "b" / "m" / "v1" / "model.joblib").exists()


def test_objects_are_immutable_and_mirroring_is_idempotent(tmp_path):
    bucket = FakeBucket()
    store = ArtifactStore(tmp_path / "models", bucket)
    saved = store.save("m", "v1", _bundle(), {})
    assert store.mirror(saved.artifact_path, saved.sha256) == saved.artifact_uri   # identical: no-op
    bucket.objects["models/m/v1/model.joblib"] = b"different"
    with pytest.raises(ArtifactError, match="immutable"):
        store.mirror(saved.artifact_path, saved.sha256)


def test_object_keys_cannot_escape_the_prefix(tmp_path):
    store = ArtifactStore(tmp_path / "models", FakeBucket())
    with pytest.raises(ArtifactError):
        store.remote_key("../other/model.joblib")


def test_sync_backfills_only_models_without_a_uri_and_reports_broken_ones(tmp_path):
    from afp_ml.artifact_sync import sync_artifacts
    from conftest import FakeRepository

    local = ArtifactStore(tmp_path / "models")
    good = local.save("m", "v1", _bundle(), {})
    repo = FakeRepository()
    repo.models = [
        {"id": "1", "model_name": "m", "version": "v1", "artifact_path": good.artifact_path,
         "artifact_sha256": good.sha256, "artifact_uri": None, "model_type": "FailurePrediction", "status": "Trained",
         "created_at_utc": 1},
        {"id": "2", "model_name": "m", "version": "v2", "artifact_path": "m/v2/model.joblib",   # file missing
         "artifact_sha256": "a" * 64, "artifact_uri": None, "model_type": "FailurePrediction", "status": "Trained",
         "created_at_utc": 2},
    ]
    bucket = FakeBucket("models")
    result = sync_artifacts(repo, ArtifactStore(tmp_path / "models", bucket))
    assert [u["model"] for u in result["uploaded"]] == ["m v1"]
    assert result["failed"][0]["model"] == "m v2"
    assert repo.models[0]["artifact_uri"] == "s3://models/models/m/v1/model.joblib"
    assert repo.models[1]["artifact_uri"] is None
    again = sync_artifacts(repo, ArtifactStore(tmp_path / "models", bucket))
    assert again["uploaded"] == [] and again["already_synced"] == ["m v1"]
