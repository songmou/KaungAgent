ARG PYTHON_IMAGE=python:3.11-slim-bookworm
FROM ${PYTHON_IMAGE} AS system
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 MODEL_DIR=/models \
    HF_HOME=/models/huggingface OMP_NUM_THREADS=4 DOCLING_NUM_THREADS=4
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    sed -i 's|http://deb.debian.org|https://deb.debian.org|g' /etc/apt/sources.list.d/debian.sources \
    && apt-get update -o Acquire::Retries=3 && apt-get install -y -o Acquire::Retries=3 --no-install-recommends \
    libreoffice-writer fonts-noto-cjk libgl1 libglib2.0-0 \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 10001 engine && useradd --uid 10001 --gid engine --create-home engine \
    && mkdir /models && chown engine:engine /models
FROM system AS runtime
WORKDIR /app
COPY requirements.lock ./
RUN pip install --no-cache-dir --extra-index-url https://download.pytorch.org/whl/cpu -r requirements.lock
COPY app.py processors.py prefetch.py model-sources.json ./
USER engine
EXPOSE 8092
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8092", "--workers", "1", "--no-access-log"]
