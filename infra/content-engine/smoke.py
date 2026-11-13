"""Model acceptance. Exits nonzero on any missing fixture, failed processor or accuracy threshold."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time
import unicodedata
import zipfile


def normalized(text):
    return "".join(c.lower() for c in unicodedata.normalize("NFKC", text) if c.isalnum())


def distance(left, right):
    previous = list(range(len(right) + 1))
    for index, a in enumerate(left, 1):
        current = [index]
        for column, b in enumerate(right, 1):
            current.append(min(current[-1] + 1, previous[column] + 1, previous[column - 1] + (a != b)))
        previous = current
    return previous[-1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--fixtures", type=Path, default=Path(__file__).resolve().parents[2] / "tests/KaungMcp.Tests/Fixtures/Content")
    parser.add_argument("--report", type=Path)
    parser.add_argument("--skip-legacy", action="store_true", help="For local development without LibreOffice; Linux acceptance must not use this flag")
    args = parser.parse_args()
    expected = json.loads((args.fixtures / "expected.json").read_text(encoding="utf-8"))
    report = {"platform": sys.platform, "checks": [], "modelManifest": str(Path(os.environ.get("MODEL_DIR", "/models")) / "model-lock.json")}
    report["skipped"] = ["sample.doc (explicit --skip-legacy)"] if args.skip_legacy else []

    def process(name, operation="document", expect_failure=False, max_pages=200, **options):
        source = (args.fixtures / name).resolve()
        if not source.is_file():
            raise AssertionError("Missing fixture: " + name)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            config = root / "options.json"
            config.write_text(json.dumps({"arguments": options, "limits": {"maxFileBytes": 104857600,
                "maxPdfPages": max_pages, "maxAudioMinutes": 120, "timeoutSeconds": 7200}}), encoding="utf-8")
            start = time.monotonic()
            completed = subprocess.run([sys.executable, str(Path(__file__).with_name("processors.py")), operation,
                                        str(source), str(config), str(root)], timeout=7200,
                                       env={**os.environ, "HF_HUB_OFFLINE": "1", "TRANSFORMERS_OFFLINE": "1"})
            if completed.returncode:
                if expect_failure:
                    assert (root / "error.json").is_file()
                    report["checks"].append({"fixture": name, "scenario": "expected rejection", "passed": True})
                    print("PASS rejection", name, flush=True)
                    return None
                raise AssertionError(name + ": " + (root / "error.json").read_text(encoding="utf-8"))
            assert not expect_failure, "Expected processor to reject " + name
            with zipfile.ZipFile(root / "result.zip") as archive:
                result = json.loads(archive.read("result.json"))
                if operation == "speech":
                    assert "result.srt" in archive.namelist() and "result.vtt" in archive.namelist()
            report["checks"].append({"fixture": name, "seconds": round(time.monotonic() - start, 2), "passed": True})
            print("PASS", name, flush=True)
            return result

    for name in ["utf8.txt", "utf16.txt", "gb18030.txt"]:
        assert "中文 hello KAUNG2026" == process(name)["text"]
    assert process("sample.csv")["tables"][0]["rows"][1] == ["样品", "12"]
    workbook = process("sample.xlsx")
    assert len(workbook["sheets"]) == 2
    assert any(c["formula"] == "=B2*C2" for c in workbook["sheets"][0]["cells"])
    assert "A4:B4" in workbook["sheets"][0]["mergedRanges"]
    assert process("sample.xls")["sheets"][0]["cells"][-1]["formulaTokensHex"]
    for name in (["sample.docx"] if args.skip_legacy else ["sample.docx", "sample.doc"]):
        result = process(name)
        assert "KAUNG2026" in result["text"] and result["tables"]
    native = process("native.pdf", ocrMode="off")
    assert "KAUNG DOCUMENT SAMPLE" in native["text"] and native["tables"]
    ocr = process("printed-zh-en.png", "ocr")
    reference, actual = normalized(expected["ocr"]), normalized(ocr["text"])
    cer = distance(reference, actual) / len(reference)
    report["ocrCer"] = cer
    assert cer <= .05, ("OCR CER", cer, ocr["text"])
    for name in ["scanned.pdf", "mixed.pdf"]:
        result = process(name, ocrMode="auto")
        assert "KAUNG2026" in result["text"]
        assert result["blocks"]
    page = process("mixed.pdf", pages="2", ocrMode="force")
    assert "KAUNG2026" in page["text"] and "KAUNG DOCUMENT SAMPLE" not in page["text"]
    assert all(p["page_no"] == 2 for block in page["blocks"] for p in block.get("provenance", []))
    for name in ["encrypted.pdf", "corrupt.pdf"]:
        process(name, expect_failure=True)
    process("mixed.pdf", expect_failure=True, max_pages=1)
    for language, fixture, key, metric, threshold in [
        ("zh", "audio-zh.wav", "speechZh", "speechZhCer", .10),
        ("en", "audio-en.wav", "speechEn", "speechEnWer", .15)
    ]:
        result = process(fixture, "speech", language=language, wordTimestamps=True)
        if language == "zh":
            reference, actual = normalized(expected[key]), normalized(result["text"])
        else:
            reference = re.findall(r"\w+", expected[key].lower())
            actual = re.findall(r"\w+", result["text"].lower())
        error_rate = distance(reference, actual) / len(reference)
        report[metric] = error_rate
        assert error_rate <= threshold, (metric, error_rate, result["text"])
        assert result["segments"] and any(segment.get("words") for segment in result["segments"])
        assert all(0 <= segment["start"] <= segment["end"] <= result["durationSeconds"] + 1 for segment in result["segments"])
    if args.report:
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
