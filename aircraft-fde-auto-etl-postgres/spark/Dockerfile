FROM spark:4.2.0-scala2.13-java17-python3-ubuntu

USER root

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && python3 -m pip install --no-cache-dir minio==7.2.18 "psycopg[binary]==3.3.6"

WORKDIR /opt/aircraft

# Committer bindings for the S3A magic committer (PathOutputCommitProtocol /
# BindingParquetOutputCommitter). Not bundled in the Spark image. Installed as a plain jar rather
# than via --packages: spark-hadoop-cloud drags in the whole hadoop-cloud-storage tree
# (aliyun/azure/opentracing), which is unnecessary here and made job start-up fail on resolution.
ARG SPARK_HADOOP_CLOUD_VERSION=4.2.0
ARG SCALA_BINARY_VERSION=2.13
RUN curl -fsSL -o /opt/spark/jars/spark-hadoop-cloud_${SCALA_BINARY_VERSION}-${SPARK_HADOOP_CLOUD_VERSION}.jar \
    "https://repo1.maven.org/maven2/org/apache/spark/spark-hadoop-cloud_${SCALA_BINARY_VERSION}/${SPARK_HADOOP_CLOUD_VERSION}/spark-hadoop-cloud_${SCALA_BINARY_VERSION}-${SPARK_HADOOP_CLOUD_VERSION}.jar"

COPY run-spark-job.sh /usr/local/bin/run-spark-job

RUN sed -i 's/\r$//' /usr/local/bin/run-spark-job \
    && chmod +x /usr/local/bin/run-spark-job

CMD ["bash", "-lc", "tail -f /dev/null"]
