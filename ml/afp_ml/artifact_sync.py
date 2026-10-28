"""Backfill: give every existing model version a durable copy in the environment's MinIO bucket."""

from __future__ import annotations

from typing import Any

from .artifacts import ArtifactError, ArtifactStore


def sync_artifacts(repo: Any, store: ArtifactStore) -> dict[str, Any]:
    """Upload each model whose ``artifact_uri`` is empty, after verifying its local file against the recorded SHA-256.

    Never changes ``artifact_path`` or ``artifact_sha256``; a model whose file is missing or fails its hash is reported,
    not uploaded. Idempotent: models that already have a URI are skipped.
    """
    if store.remote is None:
        raise ArtifactError("AFP_ARTIFACT_BUCKET is not configured for this environment; nothing to sync to.")
    uploaded, skipped, failed = [], [], []
    for m in repo.list_model_versions():
        label = f"{m['model_name']} {m['version']}"
        if m.get("artifact_uri"):
            skipped.append(label)
            continue
        try:
            uri = store.mirror(m["artifact_path"], m["artifact_sha256"])
        except ArtifactError as exc:
            failed.append({"model": label, "error": str(exc)})
            continue
        repo.set_artifact_uri(str(m["id"]), uri)
        uploaded.append({"model": label, "artifact_uri": uri})
    return {"bucket": store.remote.bucket, "uploaded": uploaded, "already_synced": skipped, "failed": failed}
