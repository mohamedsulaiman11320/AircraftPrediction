"""
Read-back check for the committed Silver/Gold lake layers.

Reads only batches that carry a `_SUCCESS` marker, so uncommitted part files can never be mistaken
for completed output, and reports the row counts actually readable from MinIO.

Run: run-spark-job /opt/aircraft/jobs/_lake_readback.py
"""
import sys

from pyspark.sql import SparkSession

LAYERS = {"silver": "s3a://silver/telemetry_clean", "gold": "s3a://gold/aircraft_features"}


def committed_batches(spark, base: str) -> list:
    """Batch paths under `base` that have a _SUCCESS marker, i.e. a completed commit."""
    jvm = spark.sparkContext._jvm
    hadoop_conf = spark.sparkContext._jsc.hadoopConfiguration()
    path = jvm.org.apache.hadoop.fs.Path(base)
    fs = path.getFileSystem(hadoop_conf)
    if not fs.exists(path):
        return []
    found = []
    for status in fs.listStatus(path):
        batch = status.getPath()
        if fs.exists(jvm.org.apache.hadoop.fs.Path(batch, "_SUCCESS")):
            found.append(batch.toString())
    return sorted(found)


def main() -> int:
    spark = SparkSession.builder.appName("lake-readback").getOrCreate()
    failures = 0
    for layer, base in LAYERS.items():
        batches = committed_batches(spark, base)
        print(f"READBACK {layer}: {len(batches)} committed batch(es)")
        if not batches:
            print(f"READBACK {layer} = NO COMMITTED OUTPUT")
            failures += 1
            continue
        try:
            total = spark.read.parquet(*batches).count()
            print(f"READBACK {layer} = {total} row(s) readable from {len(batches)} committed batch(es)")
        except Exception as exc:  # noqa: BLE001
            print(f"READBACK {layer} = FAILED: {type(exc).__name__}: {str(exc)[:200]}")
            failures += 1
    print("READBACK RESULT = " + ("PASS" if failures == 0 else "FAIL"))
    spark.stop()
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
