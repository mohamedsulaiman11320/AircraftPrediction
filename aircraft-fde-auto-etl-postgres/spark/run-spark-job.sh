#!/usr/bin/env bash
set -euo pipefail

JOB_FILE="${1:-/opt/aircraft/jobs/bronze_to_silver_gold_postgres_stream.py}"

MINIO_ENDPOINT="${MINIO_ENDPOINT:-http://minio:9000}"
MINIO_ROOT_USER="${MINIO_ROOT_USER:-minioadmin}"
MINIO_ROOT_PASSWORD="${MINIO_ROOT_PASSWORD:-minioadmin}"
MINIO_REGION="${MINIO_REGION:-us-east-1}"
HADOOP_AWS_VERSION="${HADOOP_AWS_VERSION:-3.5.0}"

# Needed to pull the matching spark-hadoop-cloud artifact, which supplies the committer bindings.
SPARK_VERSION="${SPARK_VERSION:-$(ls /opt/spark/jars/spark-core_*.jar 2>/dev/null | head -1 | sed -E 's/.*spark-core_[0-9.]+-(.+)\.jar/\1/')}"
SPARK_VERSION="${SPARK_VERSION:-4.2.0}"
SCALA_BINARY_VERSION="${SCALA_BINARY_VERSION:-$(ls /opt/spark/jars/spark-core_*.jar 2>/dev/null | head -1 | sed -E 's/.*spark-core_([0-9.]+)-.+\.jar/\1/')}"
SCALA_BINARY_VERSION="${SCALA_BINARY_VERSION:-2.13}"

until curl -fsS "${MINIO_ENDPOINT}/minio/health/live" >/dev/null 2>&1; do
  sleep 2
done

# Silver/Gold Parquet commits used to fail with:
#   AccessDeniedException: Remove S3 Dir Markers on s3a://silver/... _temporary
# because this MinIO build refuses DeleteObjects on keys ending in "/", which is exactly what the
# default FileOutputCommitter does when it cleans up its `_temporary` tree. Part files were written
# but never committed, so `_SUCCESS` was missing and the Gold write for that batch was skipped.
# (First observed at micro-batch 4 on 2026-10-21 11:58.)
#
# Fix: use the S3A *magic* committer. It streams task output straight to the final keys as
# uncompleted multipart uploads and completes them on commit, so no `_temporary` tree and no
# directory-marker deletion is ever needed. The committer bindings
# (PathOutputCommitProtocol / BindingParquetOutputCommitter) live in spark-hadoop-cloud, which is
# not bundled in this image, so it is resolved alongside hadoop-aws below.
# `fs.s3a.directory.marker.retention=keep` is kept: it is the correct setting for a store that
# cannot delete markers (it alone did NOT fix the commit failure).
/opt/spark/bin/spark-submit \
  --master "local[*]" \
  --packages "org.apache.hadoop:hadoop-aws:${HADOOP_AWS_VERSION}" \
  --conf "spark.hadoop.fs.s3a.impl=org.apache.hadoop.fs.s3a.S3AFileSystem" \
  --conf "spark.hadoop.fs.s3a.endpoint=${MINIO_ENDPOINT}" \
  --conf "spark.hadoop.fs.s3a.access.key=${MINIO_ROOT_USER}" \
  --conf "spark.hadoop.fs.s3a.secret.key=${MINIO_ROOT_PASSWORD}" \
  --conf "spark.hadoop.fs.s3a.path.style.access=true" \
  --conf "spark.hadoop.fs.s3a.connection.ssl.enabled=false" \
  --conf "spark.hadoop.fs.s3a.endpoint.region=${MINIO_REGION}" \
  --conf "spark.hadoop.fs.s3a.aws.credentials.provider=org.apache.hadoop.fs.s3a.SimpleAWSCredentialsProvider" \
  --conf "spark.hadoop.fs.s3a.directory.marker.retention=keep" \
  --conf "spark.hadoop.fs.s3a.committer.name=magic" \
  --conf "spark.hadoop.fs.s3a.committer.magic.enabled=true" \
  --conf "spark.hadoop.fs.s3a.committer.generate.uuid=true" \
  --conf "spark.hadoop.mapreduce.outputcommitter.factory.scheme.s3a=org.apache.hadoop.fs.s3a.commit.S3ACommitterFactory" \
  --conf "spark.sql.sources.commitProtocolClass=org.apache.spark.internal.io.cloud.PathOutputCommitProtocol" \
  --conf "spark.sql.parquet.output.committer.class=org.apache.spark.internal.io.cloud.BindingParquetOutputCommitter" \
  --py-files /opt/aircraft/jobs/telemetry_rules.py \
  "${JOB_FILE}"
