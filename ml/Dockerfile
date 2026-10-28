# syntax=docker/dockerfile:1
# ML worker: executes pipeline_run jobs queued by the API (python -m afp_ml worker). Database roles come from the
# environment (ML_READER_* / ETL_WRITER_* + DB_HOST/DB_PORT/DB_NAME); model artifacts live in the mounted volume
# at ~/.local/share/AircraftFailurePrediction/models (AFP_MODEL_DIR default).
FROM python:3.13-slim

RUN apt-get update \
    && apt-get install -y --no-install-recommends gcc libpq-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
COPY requirements.txt pyproject.toml README.md ./
RUN pip install --no-cache-dir -r requirements.txt
COPY afp_ml/ afp_ml/
RUN pip install --no-cache-dir -e .

CMD ["python", "-m", "afp_ml", "worker"]
