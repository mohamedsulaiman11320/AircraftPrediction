"""Model artifact storage with integrity protection.

joblib files are pickles: loading one executes code.  Therefore an artifact is only loaded when

1. its path resolves (after following symlinks) inside ``AFP_MODEL_DIR``, and
2. the SHA-256 of the exact bytes read matches the hash recorded in ``model_version``.

The bytes are hashed and then deserialised from memory, so the file cannot be swapped between
the check and the load.  Saved files are made read-only.

Durable copy in MinIO (optional, ``AFP_ARTIFACT_*``): every saved artifact and its metadata are also uploaded to
the environment's own bucket and read back to confirm the bytes.  The local directory is then a cache: when a
file is missing locally it is fetched from the bucket, the SHA-256 recorded in ``model_version`` is checked, and
only then is it cached and loaded.  Objects are never overwritten with different content.
"""

from __future__ import annotations

import hashlib
import hmac
import importlib.metadata
import io
import json
import os
import platform
import re
import stat
import urllib.parse
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Protocol

import joblib

from .util import to_jsonable

TRACKED_DISTRIBUTIONS = ("numpy", "pandas", "scikit-learn", "scipy", "joblib", "psycopg")
ARTIFACT_FILE = "model.joblib"
METADATA_FILE = "metadata.json"
_SAFE_COMPONENT = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")


class ArtifactError(Exception):
    """Artifact missing, outside the model directory, or failing the integrity check."""


@dataclass(frozen=True)
class SavedArtifact:
    artifact_path: str  # stored in model_version.artifact_path (relative to AFP_MODEL_DIR, forward slashes)
    absolute_path: Path
    sha256: str
    metadata_path: Path
    artifact_uri: str | None = None  # s3://bucket/key of the durable copy; None when no remote is configured


class RemoteObjectStore(Protocol):
    """The minimal object-store surface the artifact store needs (MinIO in production, a dict in tests)."""

    bucket: str

    def put(self, key: str, data: bytes, content_type: str) -> None: ...
    def get(self, key: str) -> bytes | None: ...  # None when the object does not exist


class MinioObjectStore:
    """MinIO/S3 bucket accessed with the environment's own least-privilege user (no delete permission)."""

    def __init__(self, endpoint_url: str, bucket: str, access_key: str, secret_key: str) -> None:
        from minio import Minio

        parsed = urllib.parse.urlparse(endpoint_url)
        self.bucket = bucket
        self._client = Minio(parsed.netloc or parsed.path, access_key=access_key, secret_key=secret_key,
                             secure=parsed.scheme == "https")

    def put(self, key: str, data: bytes, content_type: str) -> None:
        self._client.put_object(self.bucket, key, io.BytesIO(data), len(data), content_type=content_type)

    def get(self, key: str) -> bytes | None:
        from minio.error import S3Error

        try:
            response = self._client.get_object(self.bucket, key)
        except S3Error as exc:
            if exc.code in ("NoSuchKey", "NoSuchObject"):
                return None
            raise
        try:
            return response.read()
        finally:
            response.close()
            response.release_conn()


def dependency_versions() -> dict[str, str | None]:
    """Python and key package versions (installed distribution metadata; None if not installed)."""
    versions: dict[str, str | None] = {"python": platform.python_version()}
    for dist in TRACKED_DISTRIBUTIONS:
        try:
            versions[dist] = importlib.metadata.version(dist)
        except importlib.metadata.PackageNotFoundError:
            versions[dist] = None
    return versions


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: str | os.PathLike[str]) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for block in iter(lambda: fh.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def _make_read_only(path: Path) -> None:
    os.chmod(path, stat.S_IREAD | stat.S_IRGRP | stat.S_IROTH)


def put_immutable(remote: RemoteObjectStore, key: str, data: bytes, content_type: str) -> bool:
    """Upload unless an identical object exists; refuse to overwrite different content; read back to confirm.

    Returns True when the object was written, False when an identical copy was already there.
    """
    existing = remote.get(key)
    if existing is not None:
        if sha256_bytes(existing) != sha256_bytes(data):
            raise ArtifactError(f"object {key!r} already exists with different content; artifacts are immutable")
        return False
    remote.put(key, data, content_type)
    echoed = remote.get(key)
    if echoed is None or sha256_bytes(echoed) != sha256_bytes(data):
        raise ArtifactError(f"uploaded object {key!r} could not be read back intact")
    return True


class ArtifactStore:
    def __init__(self, model_dir: str | os.PathLike[str], remote: RemoteObjectStore | None = None,
                 remote_prefix: str = "models") -> None:
        self.model_dir = Path(model_dir)
        self.remote = remote
        self.remote_prefix = remote_prefix.strip("/")

    def remote_key(self, artifact_path: str) -> str:
        rel = artifact_path.replace("\\", "/").lstrip("/")
        if Path(rel).is_absolute() or ".." in rel.split("/"):
            raise ArtifactError(f"artifact path {artifact_path!r} cannot be mapped to an object key")
        return f"{self.remote_prefix}/{rel}" if self.remote_prefix else rel

    def remote_uri(self, artifact_path: str) -> str | None:
        return f"s3://{self.remote.bucket}/{self.remote_key(artifact_path)}" if self.remote else None

    def _upload_verified(self, key: str, data: bytes, content_type: str) -> None:
        assert self.remote is not None
        put_immutable(self.remote, key, data, content_type)

    def mirror(self, artifact_path: str, expected_sha256: str) -> str:
        """Upload an existing, integrity-checked local artifact (and its metadata) to the remote; return its URI."""
        if self.remote is None:
            raise ArtifactError("no remote artifact store is configured (AFP_ARTIFACT_BUCKET)")
        data = self.verify(artifact_path, expected_sha256)
        self._upload_verified(self.remote_key(artifact_path), data, "application/octet-stream")
        meta_path = self.resolve(artifact_path).parent / METADATA_FILE
        if meta_path.is_file():
            meta_rel = artifact_path.rsplit("/", 1)[0] + "/" + METADATA_FILE
            self._upload_verified(self.remote_key(meta_rel), meta_path.read_bytes(), "application/json")
        return self.remote_uri(artifact_path)  # type: ignore[return-value]

    @property
    def root(self) -> Path:
        return self.model_dir.resolve()

    def save(self, model_name: str, version: str, bundle: dict[str, Any], metadata: dict[str, Any]) -> SavedArtifact:
        for part in (model_name, version):
            if not _SAFE_COMPONENT.match(part):
                raise ArtifactError(f"unsafe artifact path component {part!r}")
        self.model_dir.mkdir(parents=True, exist_ok=True)
        target_dir = self.root / model_name / version
        if target_dir.exists():
            raise ArtifactError(f"artifact directory already exists: {target_dir}")
        target_dir.mkdir(parents=True)
        buf = io.BytesIO()
        joblib.dump(bundle, buf, compress=3)
        data = buf.getvalue()
        digest = sha256_bytes(data)
        model_path = target_dir / ARTIFACT_FILE
        tmp = target_dir / (ARTIFACT_FILE + ".tmp")
        with open(tmp, "wb") as fh:
            fh.write(data)
            fh.flush()
            os.fsync(fh.fileno())
        os.replace(tmp, model_path)
        if sha256_file(model_path) != digest:  # defensive: verify what landed on disk
            raise ArtifactError("artifact hash changed while writing")
        meta = dict(metadata)
        meta.update({"artifact_file": ARTIFACT_FILE, "artifact_sha256": digest, "model_name": model_name,
                     "version": version})
        meta_path = target_dir / METADATA_FILE
        meta_path.write_text(json.dumps(to_jsonable(meta), indent=2, sort_keys=True), encoding="utf-8")
        _make_read_only(model_path)
        _make_read_only(meta_path)
        rel = f"{model_name}/{version}/{ARTIFACT_FILE}"
        uri = None
        if self.remote is not None:
            # A configured remote is a requirement, not a best effort: if the durable copy cannot be written,
            # the training job fails instead of registering a model that exists on one disk only.
            uri = self.mirror(rel, digest)
        return SavedArtifact(rel, model_path, digest, meta_path, uri)

    def resolve(self, artifact_path: str) -> Path:
        """Resolve a stored path (relative to the model dir, or absolute) and ensure it stays inside."""
        if not artifact_path:
            raise ArtifactError("model_version.artifact_path is empty")
        raw = Path(artifact_path)
        candidate = raw if raw.is_absolute() else self.root / raw
        resolved = candidate.resolve()
        root = self.root
        if not resolved.is_relative_to(root):
            raise ArtifactError(f"artifact path {artifact_path!r} resolves outside AFP_MODEL_DIR ({root}); refusing to load")
        return resolved

    def verify(self, artifact_path: str, expected_sha256: str) -> bytes:
        """Return the artifact bytes if path and SHA-256 are valid, else raise :class:`ArtifactError`."""
        path = self.resolve(artifact_path)
        if not expected_sha256 or not re.fullmatch(r"[0-9a-fA-F]{64}", expected_sha256):
            raise ArtifactError("model_version.artifact_sha256 is missing or malformed")
        if not path.is_file():
            if self.remote is None:
                raise ArtifactError(f"artifact file not found: {path}")
            self._fetch_into_cache(artifact_path, path, expected_sha256)
        data = path.read_bytes()
        actual = sha256_bytes(data)
        if not hmac.compare_digest(actual.lower(), expected_sha256.lower()):
            raise ArtifactError(
                f"artifact SHA-256 mismatch for {path} (expected {expected_sha256}, found {actual}); "
                "the file may have been replaced - refusing to load")
        return data

    def _fetch_into_cache(self, artifact_path: str, path: Path, expected_sha256: str) -> None:
        """Download a missing artifact; it is cached only if its SHA-256 matches model_version."""
        assert self.remote is not None
        data = self.remote.get(self.remote_key(artifact_path))
        if data is None:
            raise ArtifactError(f"artifact not found locally ({path}) or in bucket {self.remote.bucket!r}")
        if not hmac.compare_digest(sha256_bytes(data).lower(), expected_sha256.lower()):
            raise ArtifactError(f"downloaded artifact for {artifact_path!r} fails its SHA-256 check; refusing to cache it")
        path.parent.mkdir(parents=True, exist_ok=True)
        tmp = path.with_name(path.name + ".tmp")
        tmp.write_bytes(data)
        os.replace(tmp, path)
        _make_read_only(path)

    def load_verified(self, artifact_path: str, expected_sha256: str) -> dict[str, Any]:
        data = self.verify(artifact_path, expected_sha256)
        bundle = joblib.load(io.BytesIO(data))
        if not isinstance(bundle, dict) or "pipeline" not in bundle:
            raise ArtifactError("artifact does not contain an afp_ml model bundle")
        return bundle


def make_artifact_store(settings: Any) -> ArtifactStore:
    """The environment's artifact store: local directory plus, when AFP_ARTIFACT_BUCKET is set, its MinIO bucket."""
    remote = None
    if settings.artifact_bucket:
        remote = MinioObjectStore(settings.artifact_endpoint, settings.artifact_bucket,
                                  settings.artifact_access_key, settings.artifact_secret_key)
    return ArtifactStore(settings.model_dir, remote, settings.artifact_prefix)
