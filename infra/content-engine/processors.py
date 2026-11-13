"""One subprocess per invocation: bounded files, local models, no document code execution."""
from __future__ import annotations

import codecs
import csv
from datetime import date, datetime, time
import io
import json
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import sys
import warnings
import zipfile

MODEL_DIR = Path(os.environ.get("MODEL_DIR", "/models"))
MAX_CELLS = 1000000
MAX_IMAGE_PIXELS = 40000000


class ProcessingError(ValueError):
    pass


def selected_pages(spec: str, total: int, maximum: int) -> list[int]:
    if total > maximum:
        raise ProcessingError("PDF 或图片页数超过限制。")
    if not spec:
        return list(range(1, total + 1))
    if not re.fullmatch(r"[1-9]\d*(?:-[1-9]\d*)?(?:,[1-9]\d*(?:-[1-9]\d*)?)*", spec):
        raise ProcessingError("页范围格式应为 1-3,5。")
    pages = set()
    for part in spec.split(","):
        bounds = list(map(int, part.split("-")))
        start, end = bounds[0], bounds[-1]
        if start > end or end > total:
            raise ProcessingError("页范围超出文档范围。")
        pages.update(range(start, end + 1))
    return sorted(pages)


def safe_office(path: Path, kind: str) -> None:
    try:
        with zipfile.ZipFile(path) as archive:
            entries = archive.infolist()
            if len(entries) > 10000 or sum(e.file_size for e in entries) > 512 * 1024 * 1024:
                raise ProcessingError("Office 解压内容超过限制。")
            expected = "word/document.xml" if kind == "docx" else "xl/workbook.xml"
            if expected not in archive.namelist():
                raise ProcessingError("Office 文件格式不匹配。")
            if any(e.filename.lower().endswith("vbaproject.bin") for e in entries):
                raise ProcessingError("不支持含宏的 Office 文件。")
    except zipfile.BadZipFile as error:
        raise ProcessingError("Office 文件损坏或已加密。") from error


def decode_text(path: Path, encoding: str) -> tuple[str, str]:
    raw = path.read_bytes()
    choices = [encoding] if encoding != "auto" else (
        ["utf-16"] if raw.startswith((codecs.BOM_UTF16_LE, codecs.BOM_UTF16_BE)) else ["utf-8-sig", "gb18030"])
    for candidate in choices:
        if candidate not in {"utf-8", "utf-8-sig", "utf-16", "gb18030"}:
            raise ProcessingError("不支持指定的文本编码。")
        try:
            text = raw.decode(candidate, errors="strict")
            if "\x00" in text:
                raise ProcessingError("文本包含二进制内容。")
            return text, candidate
        except UnicodeError:
            pass
    raise ProcessingError("文本编码无法识别，请显式指定编码。")


def simple_document(path: Path, args: dict) -> dict:
    text, encoding = decode_text(path, args.get("encoding", "auto"))
    result = {"text": text, "encoding": encoding, "blocks": [{"type": "text", "text": text}], "tables": [], "warnings": []}
    if path.suffix == ".csv":
        try:
            dialect = csv.Sniffer().sniff(text[:65536], delimiters=",;\t|")
        except csv.Error:
            dialect = csv.excel
        rows = []
        cells = 0
        for row in csv.reader(io.StringIO(text, newline=""), dialect=dialect):
            cells += len(row)
            if cells > MAX_CELLS:
                raise ProcessingError("表格单元格数量超过限制。")
            rows.append(row)
        result["tables"] = [{"name": "CSV", "rows": rows}]
    return result


def json_value(value):
    if isinstance(value, (datetime, date, time)):
        return value.isoformat()
    if isinstance(value, bytes):
        return value.hex()
    return value


def xls_formula_tokens(path: Path, book) -> dict:
    """Read BIFF8 FORMULA records without evaluating them; preserve opaque tokens on decompile failure."""
    import olefile
    from xlrd.formula import FMLA_TYPE_CELL, decompile_formula

    formulas = {}
    with olefile.OleFileIO(path) as ole:
        if any("vba" in "/".join(name).lower() for name in ole.listdir()):
            raise ProcessingError("不支持含宏的旧版 Excel 文件。")
        stream_name = "Workbook" if ole.exists("Workbook") else "Book"
        raw = ole.openstream(stream_name).read()
    for sheet_index, start in enumerate(book._sh_abs_posn):
        position = start - book.base
        while position + 4 <= len(raw):
            code, length = struct.unpack_from("<HH", raw, position)
            data = raw[position + 4:position + 4 + length]
            position += 4 + length
            if code == 0x000A:
                break
            if code == 0x0006 and len(data) >= 22:
                row, col = struct.unpack_from("<HH", data)
                size = struct.unpack_from("<H", data, 20)[0]
                tokens = data[22:22 + size]
                try:
                    expression = decompile_formula(book, tokens, len(tokens), FMLA_TYPE_CELL, browx=row, bcolx=col)
                except Exception:
                    expression = None
                formulas[(sheet_index, row, col)] = {"formula": "=" + expression if expression else None, "formulaTokensHex": tokens.hex()}
    return formulas


def spreadsheet(path: Path, args: dict) -> dict:
    names = args.get("sheetNames", [])
    tables, sheets, text, notices = [], [], [], []
    total_cells = 0
    if path.suffix == ".xlsx":
        import openpyxl
        safe_office(path, "xlsx")
        book = openpyxl.load_workbook(path, read_only=False, data_only=False, keep_links=False)
        values = openpyxl.load_workbook(path, read_only=False, data_only=True, keep_links=False)
        try:
            if any(name not in book.sheetnames for name in names):
                raise ProcessingError("指定的工作表不存在。")
            for sheet in book:
                if names and sheet.title not in names:
                    continue
                total_cells += sheet.max_row * sheet.max_column
                if total_cells > MAX_CELLS:
                    raise ProcessingError("表格单元格数量超过限制。")
                rows, cells = [], []
                for row in sheet.iter_rows():
                    rendered = []
                    for cell in row:
                        value = json_value(values[sheet.title][cell.coordinate].value)
                        formula = str(cell.value) if cell.data_type == "f" else None
                        if formula and value is None and "部分公式没有已保存的缓存值，未执行重新计算。" not in notices:
                            notices.append("部分公式没有已保存的缓存值，未执行重新计算。")
                        if cell.value is not None:
                            cells.append({"address": cell.coordinate, "row": cell.row, "column": cell.column,
                                          "value": value, "formula": formula})
                        rendered.append(value if value is not None else formula or "")
                    rows.append(rendered)
                tables.append({"name": sheet.title, "rows": rows})
                sheets.append({"name": sheet.title, "cells": cells, "mergedRanges": [str(r) for r in sheet.merged_cells.ranges]})
        finally:
            book.close()
            values.close()
    else:
        import xlrd
        book = xlrd.open_workbook(str(path), formatting_info=True, on_demand=True)
        try:
            if any(name not in book.sheet_names() for name in names):
                raise ProcessingError("指定的工作表不存在。")
            formulas = xls_formula_tokens(path, book)
            for index in range(book.nsheets):
                sheet = book.sheet_by_index(index)
                if names and sheet.name not in names:
                    continue
                total_cells += sheet.nrows * sheet.ncols
                if total_cells > MAX_CELLS:
                    raise ProcessingError("表格单元格数量超过限制。")
                rows, cells = [], []
                for row in range(sheet.nrows):
                    rendered = []
                    for col in range(sheet.ncols):
                        cell = sheet.cell(row, col)
                        value = cell.value
                        if cell.ctype == xlrd.XL_CELL_DATE:
                            value = xlrd.xldate_as_datetime(value, book.datemode).isoformat()
                        formula = formulas.get((index, row, col), {})
                        if formula and formula.get("formula") is None and "部分旧版公式仅保留原始 BIFF token，未能还原表达式。" not in notices:
                            notices.append("部分旧版公式仅保留原始 BIFF token，未能还原表达式。")
                        if cell.ctype != xlrd.XL_CELL_EMPTY or formula:
                            cells.append({"row": row + 1, "column": col + 1, "value": value, **formula})
                        rendered.append(value)
                    rows.append(rendered)
                tables.append({"name": sheet.name, "rows": rows})
                sheets.append({"name": sheet.name, "cells": cells,
                               "mergedRanges": [{"startRow": a + 1, "endRow": b, "startColumn": c + 1, "endColumn": d}
                                                for a, b, c, d in sheet.merged_cells]})
        finally:
            book.release_resources()
    for table in tables:
        text.append("# " + table["name"] + "\n" + "\n".join("\t".join(str(v) if v is not None else "" for v in row) for row in table["rows"]))
    return {"text": "\n\n".join(text), "tables": tables, "sheets": sheets, "warnings": notices}


def ocr_engine():
    from rapidocr import RapidOCR
    config = MODEL_DIR / "ocr" / "config.json"
    if not config.is_file():
        raise ProcessingError("OCR 模型未预下载。")
    params = json.loads(config.read_text(encoding="utf-8"))
    # Prefetch records relative paths so volumes can move between hosts.
    for key in ("Det.model_path", "Cls.model_path", "Rec.model_path"):
        params[key] = str(MODEL_DIR / "ocr" / params[key])
        if not Path(params[key]).is_file():
            raise ProcessingError("OCR 模型文件缺失。")
    return RapidOCR(params=params)


def image_ocr(path: Path, args: dict, limits: dict) -> dict:
    from PIL import Image, ImageOps
    Image.MAX_IMAGE_PIXELS = MAX_IMAGE_PIXELS
    engine = ocr_engine()
    blocks, texts, notices = [], [], []

    def recognize(image, page_number: int):
        import numpy as np
        if image.width * image.height > MAX_IMAGE_PIXELS:
            raise ProcessingError("图片像素数量超过限制。")
        image = ImageOps.exif_transpose(image).convert("RGB")
        output = engine(np.asarray(image)[:, :, ::-1].copy())
        page_text = []
        for box, text, score in zip(output.boxes if output.boxes is not None else [],
                                    output.txts if output.txts is not None else [],
                                    output.scores if output.scores is not None else []):
            score = float(score)
            blocks.append({"page": page_number, "text": text, "confidence": score,
                           "box": box.tolist(), "imageWidth": image.width, "imageHeight": image.height})
            page_text.append(text)
            if score < 0.7 and "存在低置信度文字，请复核原图。" not in notices:
                notices.append("存在低置信度文字，请复核原图。")
        texts.append("\n".join(page_text))

    if path.suffix == ".pdf":
        import pypdfium2 as pdfium
        with pdfium.PdfDocument(path) as document:
            for number in selected_pages(args.get("pages", ""), len(document), limits["maxPdfPages"]):
                page = document[number - 1]
                try:
                    if page.get_width() * page.get_height() * 4 > MAX_IMAGE_PIXELS:
                        raise ProcessingError("PDF 渲染像素数量超过限制。")
                    bitmap = page.render(scale=2)
                    try:
                        recognize(bitmap.to_pil(), number)
                    finally:
                        bitmap.close()
                finally:
                    page.close()
    else:
        try:
            with warnings.catch_warnings():
                warnings.simplefilter("error", Image.DecompressionBombWarning)
                with Image.open(path) as image:
                    for number in selected_pages("", getattr(image, "n_frames", 1), limits["maxPdfPages"]):
                        image.seek(number - 1)
                        recognize(image.copy(), number)
        except (Image.DecompressionBombWarning, Image.DecompressionBombError) as error:
            raise ProcessingError("图片像素数量超过限制。") from error
    if not blocks:
        notices.append("未识别到文字。")
    return {"text": "\n\n".join(texts), "blocks": blocks, "tables": [], "warnings": notices}


def convert_doc(path: Path, directory: Path) -> Path:
    import olefile
    with olefile.OleFileIO(path) as ole:
        if not ole.exists("WordDocument"):
            raise ProcessingError("文件并非旧版 Word 文档。")
        if any("vba" in "/".join(name).lower() for name in ole.listdir()):
            raise ProcessingError("不支持含宏的旧版 Word 文件。")
    executable = shutil.which("libreoffice")
    if not executable:
        raise ProcessingError("旧版 Word 解析需要 LibreOffice。")
    profile = directory / "lo-profile"
    profile.mkdir()
    # Disable macros and external-link updates in an isolated profile.
    (profile / "user").mkdir()
    (profile / "user" / "registrymodifications.xcu").write_text(
        '<?xml version="1.0" encoding="UTF-8"?><oor:items xmlns:oor="http://openoffice.org/2001/registry">'
        '<item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop></item>'
        '<item oor:path="/org.openoffice.Office.Writer/Content/Update"><prop oor:name="Link" oor:op="fuse"><value>0</value></prop></item></oor:items>', encoding="utf-8")
    completed = subprocess.run([executable, "-env:UserInstallation=" + profile.as_uri(), "--headless", "--norestore",
                                "--convert-to", "docx", "--outdir", str(directory), str(path)],
                               capture_output=True, timeout=120, check=False)
    output = directory / (path.stem + ".docx")
    if completed.returncode or not output.is_file():
        raise ProcessingError("旧版 Word 转换失败；文件可能损坏或已加密。")
    return output


def document(path: Path, args: dict, limits: dict, directory: Path) -> dict:
    if path.suffix in {".txt", ".md", ".csv"}:
        return simple_document(path, args)
    if path.suffix in {".xls", ".xlsx"}:
        return spreadsheet(path, args)
    if path.suffix == ".doc":
        path = convert_doc(path, directory)
    if path.suffix == ".docx":
        safe_office(path, "docx")
    from docling.datamodel.base_models import InputFormat
    from docling.document_converter import DocumentConverter, PdfFormatOption
    if path.suffix == ".docx":
        pieces = [DocumentConverter(allowed_formats=[InputFormat.DOCX]).convert(path)]
    else:
        if not (MODEL_DIR / "docling" / ".ready").is_file():
            raise ProcessingError("文档模型未预下载。")
        from docling.datamodel.pipeline_options import PdfPipelineOptions, RapidOcrOptions, AcceleratorOptions, AcceleratorDevice
        ocr_config = json.loads((MODEL_DIR / "ocr" / "config.json").read_text(encoding="utf-8"))
        ocr = RapidOcrOptions(backend="onnxruntime", mode="full_page" if args.get("ocrMode") == "force" else "default",
                             det_model_path=str(MODEL_DIR / "ocr" / ocr_config["Det.model_path"]),
                             rec_model_path=str(MODEL_DIR / "ocr" / ocr_config["Rec.model_path"]),
                             cls_model_path=str(MODEL_DIR / "ocr" / ocr_config["Cls.model_path"]))
        pipeline = PdfPipelineOptions(artifacts_path=MODEL_DIR / "docling", do_ocr=args.get("ocrMode", "auto") != "off",
                                      ocr_options=ocr, enable_remote_services=False,
                                      accelerator_options=AcceleratorOptions(device=AcceleratorDevice.CPU, num_threads=4))
        converter = DocumentConverter(allowed_formats=[InputFormat.PDF], format_options={InputFormat.PDF: PdfFormatOption(pipeline_options=pipeline)})
        import pypdfium2 as pdfium
        with pdfium.PdfDocument(path) as source:
            pages = selected_pages(args.get("pages", ""), len(source), limits["maxPdfPages"])
            for number in pages:
                page = source[number - 1]
                try:
                    if page.get_width() * page.get_height() * 4 > MAX_IMAGE_PIXELS:
                        raise ProcessingError("PDF 渲染像素数量超过限制。")
                finally:
                    page.close()
        # Convert contiguous ranges together, retaining original page numbers in provenance.
        ranges = []
        for page in pages:
            if ranges and page == ranges[-1][1] + 1:
                ranges[-1][1] = page
            else:
                ranges.append([page, page])
        pieces = [converter.convert(path, page_range=(start, end), max_num_pages=limits["maxPdfPages"], raises_on_error=False) for start, end in ranges]
    blocks, tables, texts, notices = [], [], [], []
    valid_piece = False
    for converted in pieces:
        if str(converted.status.value) not in {"success", "partial_success"}:
            notices.append("部分页面解析失败，请对照原文复核。")
            continue
        valid_piece = True
        if str(converted.status.value) != "success":
            notices.append("部分内容解析失败，请对照原文复核。")
        doc = converted.document
        texts.append(doc.export_to_markdown())
        for item, _level in doc.iterate_items():
            if hasattr(item, "text"):
                blocks.append({"type": str(item.label.value), "text": item.text,
                               "provenance": [p.model_dump(mode="json") for p in getattr(item, "prov", [])]})
        for table in doc.tables:
            frame = table.export_to_dataframe(doc=doc)
            tables.append({"name": f"Table {len(tables) + 1}",
                           "rows": [list(map(str, frame.columns))] + frame.fillna("").values.tolist(),
                           "provenance": [p.model_dump(mode="json") for p in table.prov]})
    if not valid_piece:
        raise ProcessingError("文档解析失败，未生成可用内容。")
    if not any(text.strip() for text in texts):
        notices.append("未提取到文字；若为扫描文档，请开启 OCR。")
    return {"text": "\n\n".join(texts), "blocks": blocks, "tables": tables, "warnings": notices}


def subtitle_time(seconds: float, vtt: bool = False) -> str:
    milliseconds = max(0, round(seconds * 1000))
    hours, rest = divmod(milliseconds, 3600000)
    minutes, rest = divmod(rest, 60000)
    secs, ms = divmod(rest, 1000)
    return f"{hours:02}:{minutes:02}:{secs:02}{'.' if vtt else ','}{ms:03}"


def speech(path: Path, args: dict, limits: dict) -> dict:
    import av
    import numpy as np
    from faster_whisper import WhisperModel
    model_path = MODEL_DIR / "whisper-small"
    if not (model_path / "model.bin").is_file():
        raise ProcessingError("语音模型未预下载。")
    sample_limit = limits["maxAudioMinutes"] * 60 * 16000
    chunks, sample_count = [], 0
    with av.open(str(path)) as container:
        if not container.streams.audio:
            raise ProcessingError("文件中没有音轨。")
        resampler = av.AudioResampler(format="s16", layout="mono", rate=16000)
        for frame in container.decode(audio=0):
            for converted in resampler.resample(frame):
                samples = converted.to_ndarray().flatten()
                sample_count += samples.size
                if sample_count > sample_limit:
                    raise ProcessingError("音频时长超过限制。")
                chunks.append(samples.astype(np.float32) / 32768.0)
        for converted in resampler.resample(None):
            samples = converted.to_ndarray().flatten()
            sample_count += samples.size
            if sample_count > sample_limit:
                raise ProcessingError("音频时长超过限制。")
            chunks.append(samples.astype(np.float32) / 32768.0)
    if not chunks:
        raise ProcessingError("音频不包含可解码采样。")
    model = WhisperModel(str(model_path), device="cpu", compute_type="int8", cpu_threads=4, local_files_only=True)
    audio = np.concatenate(chunks)
    language = args.get("language", "auto")
    if language not in {"auto", "zh", "en"}:
        raise ProcessingError("不支持指定的语音语言。")
    segments, info = model.transcribe(audio, language=None if language == "auto" else language,
                                      word_timestamps=bool(args.get("wordTimestamps")), vad_filter=True,
                                      condition_on_previous_text=False)
    output = []
    for segment in segments:
        item = {"start": segment.start, "end": segment.end, "text": segment.text.strip()}
        if args.get("wordTimestamps"):
            item["words"] = [{"start": word.start, "end": word.end, "word": word.word, "probability": word.probability}
                             for word in segment.words or []]
        output.append(item)
    notices = [] if output else ["未检测到语音。"]
    return {"text": "\n".join(segment["text"] for segment in output), "segments": output,
            "language": info.language, "durationSeconds": len(audio) / 16000, "warnings": notices, "tables": []}


def write_result(result: dict, operation: str, directory: Path, maximum: int) -> None:
    outputs = []

    def write(name: str, mime: str, text: str):
        path = directory / name
        path.write_text(text, encoding="utf-8")
        outputs.append({"name": name, "mimeType": mime})
        if len(outputs) > 255 or sum((directory / f["name"]).stat().st_size for f in outputs) > maximum:
            raise ProcessingError("解析结果超过大小或文件数量限制。")

    write("result.txt" if operation == "speech" else "result.md", "text/plain" if operation == "speech" else "text/markdown", result["text"])
    write("result.json", "application/json", json.dumps(result, ensure_ascii=False, default=json_value, allow_nan=False))
    for index, table in enumerate(result.get("tables", []), 1):
        buffer = io.StringIO(newline="")
        writer = csv.writer(buffer)
        # Avoid spreadsheet formula execution when users open an exported CSV.
        for row in table["rows"]:
            writer.writerow(["'" + value if isinstance(value, str) and value.lstrip().startswith(("=", "+", "-", "@")) else value for value in row])
        write(f"table-{index}.csv", "text/csv", buffer.getvalue())
    if operation == "speech":
        for vtt in (False, True):
            lines = ["WEBVTT\n"] if vtt else []
            for index, segment in enumerate(result["segments"], 1):
                lines.extend([str(index), f'{subtitle_time(segment["start"], vtt)} --> {subtitle_time(segment["end"], vtt)}', segment["text"], ""])
            write("result.vtt" if vtt else "result.srt", "text/vtt" if vtt else "application/x-subrip", "\n".join(lines))
    manifest = {"summary": f"处理完成，生成 {len(outputs)} 个结果文件。", "files": outputs, "warnings": result.get("warnings", [])}
    (directory / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False), encoding="utf-8")
    with zipfile.ZipFile(directory / "result.zip", "w", zipfile.ZIP_DEFLATED) as archive:
        archive.write(directory / "manifest.json", "manifest.json")
        for item in outputs:
            archive.write(directory / item["name"], item["name"])


def main():
    operation, source, options, output = sys.argv[1:]
    path, directory = Path(source), Path(output)
    try:
        config = json.loads(Path(options).read_text(encoding="utf-8"))
        args, limits = config["arguments"], config["limits"]
        if operation == "document":
            result = document(path, args, limits, directory)
        elif operation == "ocr":
            result = image_ocr(path, args, limits)
        else:
            result = speech(path, args, limits)
        result["schemaVersion"] = 1
        result["operation"] = operation
        write_result(result, operation, directory, limits["maxFileBytes"])
    except Exception as error:
        # Public errors never expose internal paths, secrets, source contents or stack traces.
        detail = str(error) if isinstance(error, ProcessingError) else "文件损坏、已加密、格式不支持或本地处理依赖不可用。"
        (directory / "error.json").write_text(json.dumps({"detail": detail}, ensure_ascii=False), encoding="utf-8")
        raise SystemExit(1)


if __name__ == "__main__":
    main()
