"""
Provision MinIO storage for model artifacts: one bucket and one least-privilege user per environment.

  operational  bucket `models`         user `afp-ml-models`     -> written to the root .env
  development  bucket `synthetic-dev`  user `afp-ml-synthetic`  -> written to .env.synthetic

Each user may read, write and list ITS OWN bucket only, and may not delete: model artifacts are immutable
evidence (SHA-256 recorded in model_version). The development bucket also holds the archived synthetic raw files,
so synthetic data and synthetic models never share a bucket with operational ones. Each env file also forbids the
other environment's bucket (AFP_ARTIFACT_FORBIDDEN_BUCKETS), so a missing key can never silently fall back to it.

Idempotent: existing buckets/policies are kept; a user whose secret is already in its env file is left alone,
otherwise a new secret is generated and stored. Secrets are never printed. MinIO root credentials are read from
aircraft-fde-auto-etl-postgres/.env (where the Docker stack takes them from).

Usage:  ml\\.venv\\Scripts\\python -I tools/minio/provision_model_storage.py
"""
from __future__ import annotations

import io
import json
import pathlib
import secrets
import sys
import urllib.parse

from minio import Minio
from minio.credentials import StaticProvider
from minio.minioadmin import MinioAdmin

ROOT = pathlib.Path(__file__).resolve().parents[2]
ETL_ENV = ROOT / "aircraft-fde-auto-etl-postgres" / ".env"
PREFIX = "models"

ENVIRONMENTS = [
    {"name": "operational", "env_file": ROOT / ".env", "bucket": "models", "user": "afp-ml-models",
     "forbidden": "synthetic-dev"},
    {"name": "development", "env_file": ROOT / ".env.synthetic", "bucket": "synthetic-dev", "user": "afp-ml-synthetic",
     "forbidden": "models"},
]


def read_env(path: pathlib.Path) -> dict[str, str]:
    values: dict[str, str] = {}
    if not path.exists():
        return values
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        values[key.strip()] = value.strip().strip('"').strip("'")
    return values


def set_env_keys(path: pathlib.Path, updates: dict[str, str], comment: str) -> None:
    """Replace KEY=... lines in place, append missing keys under one comment; everything else is untouched."""
    lines = path.read_text(encoding="utf-8").splitlines() if path.exists() else []
    remaining = dict(updates)
    for i, raw in enumerate(lines):
        key = raw.split("=", 1)[0].strip() if "=" in raw and not raw.lstrip().startswith("#") else None
        if key in remaining:
            lines[i] = f"{key}={remaining.pop(key)}"
    if remaining:
        lines += ["", f"# {comment}"] + [f"{k}={v}" for k, v in remaining.items()]
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def bucket_policy(bucket: str) -> dict:
    return {
        "Version": "2012-10-17",
        "Statement": [
            {"Effect": "Allow", "Action": ["s3:GetBucketLocation", "s3:ListBucket"], "Resource": [f"arn:aws:s3:::{bucket}"]},
            # No s3:DeleteObject: artifacts are immutable once written.
            {"Effect": "Allow", "Action": ["s3:GetObject", "s3:PutObject"], "Resource": [f"arn:aws:s3:::{bucket}/*"]},
        ],
    }


def main() -> int:
    etl = read_env(ETL_ENV)
    root_user, root_password = etl.get("MINIO_ROOT_USER"), etl.get("MINIO_ROOT_PASSWORD")
    if not root_user or not root_password:
        print(f"MINIO_ROOT_USER/MINIO_ROOT_PASSWORD not found in {ETL_ENV}", file=sys.stderr)
        return 2
    endpoint_url = read_env(ROOT / ".env").get("MINIO_ENDPOINT", "http://localhost:9000")
    parsed = urllib.parse.urlparse(endpoint_url)
    host, secure = parsed.netloc or parsed.path, parsed.scheme == "https"

    client = Minio(host, access_key=root_user, secret_key=root_password, secure=secure)
    admin = MinioAdmin(endpoint=host, credentials=StaticProvider(root_user, root_password), secure=secure)

    for env in ENVIRONMENTS:
        bucket, user, env_file = env["bucket"], env["user"], env["env_file"]
        if not env_file.exists():
            print(f"[{env['name']}] {env_file.name} does not exist - skipped", file=sys.stderr)
            continue
        if client.bucket_exists(bucket):
            print(f"[{env['name']}] bucket {bucket}: exists")
        else:
            client.make_bucket(bucket)
            print(f"[{env['name']}] bucket {bucket}: created")

        policy_name = f"afp-{bucket}-rw-nodelete"
        admin.policy_add(policy_name, policy=bucket_policy(bucket))

        current = read_env(env_file)
        have_secret = current.get("AFP_ARTIFACT_ACCESS_KEY") == user and current.get("AFP_ARTIFACT_SECRET_KEY")
        secret = current["AFP_ARTIFACT_SECRET_KEY"] if have_secret else secrets.token_urlsafe(32)
        # user_add creates the user, or resets the secret of an existing one, to the value we store.
        admin.user_add(user, secret)
        try:
            admin.attach_policy([policy_name], user=user)
        except Exception as exc:  # already attached is not an error
            if "already" not in str(exc).lower():
                raise
        set_env_keys(env_file, {
            "AFP_ARTIFACT_ENDPOINT": endpoint_url,
            "AFP_ARTIFACT_BUCKET": bucket,
            "AFP_ARTIFACT_PREFIX": PREFIX,
            "AFP_ARTIFACT_ACCESS_KEY": user,
            "AFP_ARTIFACT_SECRET_KEY": secret,
            "AFP_ARTIFACT_FORBIDDEN_BUCKETS": env["forbidden"],
        }, f"Model artifacts in MinIO ({env['name']}): written by tools/minio/provision_model_storage.py")
        print(f"[{env['name']}] user {user}: policy {policy_name} attached; "
              f"{'existing' if have_secret else 'new'} secret stored in {env_file.name} (not shown)")

        # Prove least privilege with the user's own credentials: own bucket writable, other bucket refused,
        # and delete refused even in its own bucket.
        own = Minio(host, access_key=user, secret_key=secret, secure=secure)
        probe = f"{PREFIX}/_provisioning_probe.txt"
        own.put_object(bucket, probe, io.BytesIO(b"probe"), 5)
        checks = {"own bucket write": True}
        try:
            own.remove_object(bucket, probe)
            checks["delete refused"] = False
        except Exception:
            checks["delete refused"] = True
        try:
            own.put_object(env["forbidden"], f"{PREFIX}/_cross_probe.txt", io.BytesIO(b"x"), 1)
            checks["other bucket refused"] = False
        except Exception:
            checks["other bucket refused"] = True
        client.remove_object(bucket, probe)          # root cleans up its own probe
        print(f"[{env['name']}] least-privilege checks: {json.dumps(checks)}")
        if not all(checks.values()):
            print(f"[{env['name']}] LEAST-PRIVILEGE CHECK FAILED", file=sys.stderr)
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
