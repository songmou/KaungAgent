"""Private, authenticated stream-in / ZIP-out engine; the .NET host owns the queue."""
from __future__ import annotations

import asyncio
import hmac
import importlib.util
import json
import os
from pathlib import Path
import shutil
import signal
import sys
import tempfile
import time
from uuid import UUID

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import FileResponse
from starlette.background import BackgroundTask

app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)
running: dict[str, asyncio.subprocess.Process] = {}
cancelled: dict[str, float] = {}
capacity = asyncio.Lock()
MAX_BYTES = int(os.environ.get("ENGINE_MAX_FILE_BYTES", 1073741824))


class BodyLimitMiddleware:
    def __init__(self, app):
        self.app = app

    async def __call__(self, scope, receive, send):
        if scope["type"] != "http":
            return await self.app(scope, receive, send)
        count = 0

        async def bounded_receive():
            nonlocal count
            message = await receive()
            count += len(message.get("body", b""))
            if count > MAX_BYTES + 1048576:
                raise HTTPException(413, "请求体超过引擎限制。")
            return message

        await self.app(scope, bounded_receive, send)


app.add_middleware(BodyLimitMiddleware)


def authorize(request: Request) -> None:
    key = os.environ.get("ENGINE_KEY", "")
    if len(key) < 32:
        raise HTTPException(503, "引擎凭据未配置。")
    if not hmac.compare_digest(request.headers.get("x-engine-key", ""), key):
        raise HTTPException(401, "无效的引擎凭据。")


def model_root() -> Path:
    return Path(os.environ.get("MODEL_DIR", "/models"))


def health(capability: str) -> dict:
    root = model_root()
    modules = {
        "ocr": ["rapidocr", "onnxruntime", "PIL", "pypdfium2"],
        "document": ["docling", "openpyxl", "xlrd"],
        "speech": ["faster_whisper", "av"],
    }
    if capability not in modules:
        raise HTTPException(404, "未知能力。")
    missing = [name for name in modules[capability] if importlib.util.find_spec(name) is None]
    models = {"ocr": root / "ocr" / "config.json", "document": root / "docling" / ".ready",
              "speech": root / "whisper-small" / "model.bin"}
    ready = not missing and models[capability].is_file()
    if capability == "document" and not shutil.which("libreoffice"):
        ready = False
        missing.append("LibreOffice")
    return {"ready": ready, "detail": "就绪" if ready else "缺少依赖或预下载模型：" + ", ".join(missing or [capability])}


@app.get("/v1/health/{capability}")
async def get_health(capability: str, request: Request):
    authorize(request)
    return health(capability)


def stop_process(process: asyncio.subprocess.Process) -> None:
    try:
        # Kill remaining descendants even if the Python parent already exited.
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass


@app.delete("/v1/executions/{execution_id}", status_code=202)
async def cancel(execution_id: UUID, request: Request):
    authorize(request)
    key = str(execution_id)
    cancelled[key] = time.monotonic()
    for old, timestamp in list(cancelled.items()):
        if timestamp < time.monotonic() - 600:
            del cancelled[old]
    process = running.get(key)
    if process:
        stop_process(process)
        await process.wait()
    return {"cancelRequested": True}


@app.post("/v1/process/{operation}/{execution_id}")
async def process(operation: str, execution_id: UUID, request: Request):
    authorize(request)
    if operation not in {"ocr", "document", "speech"}:
        raise HTTPException(404, "未知操作。")
    if capacity.locked():
        raise HTTPException(429, "引擎正在执行其他任务。")
    if int(request.headers.get("content-length", "0")) > MAX_BYTES + 1048576:
        raise HTTPException(413, "上传超过引擎限制。")
    await capacity.acquire()
    directory = Path(tempfile.mkdtemp(prefix="kaung-content-"))
    key = str(execution_id)
    child = None
    delivered = False
    try:
        async with request.form(max_files=1, max_fields=1, max_part_size=65536) as form:
            upload = form.get("file")
            if upload is None or not hasattr(upload, "read"):
                raise HTTPException(400, "需要一个上传文件。")
            try:
                options = json.loads(form.get("options", "{}"))
                limits = options["limits"]
                maximum = min(int(limits["maxFileBytes"]), MAX_BYTES)
                timeout = min(max(int(limits["timeoutSeconds"]), 1), 86400)
                if maximum < 1 or not 1 <= int(limits["maxPdfPages"]) <= 2000 or not 1 <= int(limits["maxAudioMinutes"]) <= 1440:
                    raise ValueError()
            except (ValueError, KeyError, TypeError):
                raise HTTPException(400, "处理参数无效。")
            # Only the suffix is used; user-supplied paths never reach the filesystem.
            suffix = Path((upload.filename or "").replace("\\", "/")).suffix.lower()
            if suffix not in {".pdf", ".doc", ".docx", ".xls", ".xlsx", ".txt", ".md", ".csv",
                              ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp",
                              ".wav", ".mp3", ".m4a", ".aac", ".ogg", ".flac"}:
                raise HTTPException(400, "文件格式不支持。")
            source = directory / ("input" + suffix)
            size = 0
            with source.open("wb") as output:
                while chunk := await upload.read(65536):
                    size += len(chunk)
                    if size > maximum:
                        raise HTTPException(413, "文件超过大小限制。")
                    output.write(chunk)
            if not size:
                raise HTTPException(400, "空文件。")
        if key in cancelled or await request.is_disconnected():
            raise HTTPException(409, "任务已取消。")
        config = directory / "options.json"
        config.write_text(json.dumps(options), encoding="utf-8")
        # No shell, URL, user-supplied executable or arbitrary path arguments.
        child = await asyncio.create_subprocess_exec(
            sys.executable, str(Path(__file__).with_name("processors.py")),
            operation, str(source), str(config), str(directory),
            stdout=asyncio.subprocess.DEVNULL, stderr=asyncio.subprocess.DEVNULL,
            start_new_session=True,
            env={**os.environ, "HF_HUB_OFFLINE": "1", "TRANSFORMERS_OFFLINE": "1"},
        )
        running[key] = child
        completion = asyncio.create_task(child.wait())
        deadline = time.monotonic() + timeout
        while child.returncode is None:
            if key in cancelled or await request.is_disconnected():
                stop_process(child)
                raise HTTPException(409, "任务已取消。")
            if time.monotonic() >= deadline:
                stop_process(child)
                raise HTTPException(504, "处理超时。")
            try:
                await asyncio.wait_for(asyncio.shield(completion), timeout=0.25)
            except asyncio.TimeoutError:
                pass
        archive = directory / "result.zip"
        if child.returncode != 0 or not archive.is_file():
            error_file = directory / "error.json"
            detail = json.loads(error_file.read_text(encoding="utf-8"))["detail"] if error_file.is_file() else "内容引擎处理失败，请检查依赖和文件。"
            raise HTTPException(422, detail)
        if archive.stat().st_size > maximum:
            raise HTTPException(413, "处理结果超过大小限制。")
        delivered = True
        return FileResponse(archive, media_type="application/zip", filename="result.zip",
                            background=BackgroundTask(shutil.rmtree, directory, ignore_errors=True))
    finally:
        if child:
            stop_process(child)
            await child.wait()
        running.pop(key, None)
        capacity.release()
        if not delivered:
            shutil.rmtree(directory, ignore_errors=True)
