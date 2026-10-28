import os
import time
from minio import Minio

endpoint = os.getenv("MINIO_ENDPOINT", "http://minio:9000").replace("http://", "").replace("https://", "")
client = Minio(
    endpoint,
    access_key=os.getenv("MINIO_ROOT_USER", "minioadmin"),
    secret_key=os.getenv("MINIO_ROOT_PASSWORD", "minioadmin"),
    secure=False,
)

for attempt in range(1, 61):
    try:
        list(client.list_buckets())
        break
    except Exception as exc:
        if attempt == 60:
            raise
        print(f"Waiting for MinIO ({attempt}/60): {exc}")
        time.sleep(2)

# models: operational model artifacts; synthetic-dev: the isolated development environment (synthetic raw
# files + development models). Users and policies: tools/minio/provision_model_storage.py.
for bucket in ("bronze", "silver", "gold", "models", "synthetic-dev"):
    if client.bucket_exists(bucket):
        print(f"Bucket already exists: {bucket}")
    else:
        client.make_bucket(bucket)
        print(f"Created bucket: {bucket}")
