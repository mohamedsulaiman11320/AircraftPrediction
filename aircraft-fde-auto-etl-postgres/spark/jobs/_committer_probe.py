"""
Disposable probe for the Silver/Gold Parquet commit defect.

Writes a tiny Parquet dataset to an isolated test prefix and reads it back, so the commit
path can be reproduced and a fix verified without touching real Silver/Gold data.

Run:  run-spark-job /opt/aircraft/jobs/_committer_probe.py
Prefix: s3a://silver/_committer_probe/<run-id>/   (safe to delete; never real data)
"""
import sys
import traceback
import uuid

from pyspark.sql import SparkSession


def main() -> int:
    run_id = uuid.uuid4().hex[:8]
    target = f"s3a://silver/_committer_probe/{run_id}"
    spark = SparkSession.builder.appName(f"committer-probe-{run_id}").getOrCreate()
    hadoop_conf = spark.sparkContext._jsc.hadoopConfiguration()

    print(f"PROBE target     = {target}")
    for key in [
        "fs.s3a.committer.name",
        "fs.s3a.committer.magic.enabled",
        "fs.s3a.directory.marker.retention",
        "mapreduce.outputcommitter.factory.scheme.s3a",
        "spark.sql.sources.commitProtocolClass",
        "spark.sql.parquet.output.committer.class",
    ]:
        print(f"PROBE conf       {key} = {hadoop_conf.get(key)}")
    for key in ["spark.sql.sources.commitProtocolClass", "spark.sql.parquet.output.committer.class"]:
        try:
            print(f"PROBE sparkconf  {key} = {spark.conf.get(key)}")
        except Exception:
            print(f"PROBE sparkconf  {key} = <unset>")

    df = spark.createDataFrame(
        [(1, "N120UA", 650.5), (2, "N237UA", 701.2), (3, "N512UA", 480.0)],
        ["id", "tail_number", "engine_temp_c"],
    )

    try:
        df.write.mode("overwrite").partitionBy("tail_number").parquet(target)
        print("PROBE write      = COMMITTED")
    except Exception:
        print("PROBE write      = FAILED")
        traceback.print_exc()
        spark.stop()
        return 1

    try:
        back = spark.read.parquet(target)
        count = back.count()
        print(f"PROBE readback   = {count} row(s)")
        if count != 3:
            print(f"PROBE RESULT     = FAIL (expected 3 rows, read {count})")
            spark.stop()
            return 1
    except Exception:
        print("PROBE readback   = FAILED")
        traceback.print_exc()
        spark.stop()
        return 1

    print("PROBE RESULT     = PASS")
    spark.stop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
