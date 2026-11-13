"""Fast tests need no model downloads; real OCR/ASR acceptance is in smoke.py."""
import asyncio
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
import zipfile

import processors


class ProcessorTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)

    def tearDown(self):
        self.directory.cleanup()

    def test_text_encoding_and_csv(self):
        for encoding in ("utf-8", "utf-16", "gb18030"):
            path = self.root / "sample.txt"
            path.write_bytes("中文 hello".encode(encoding))
            result = processors.simple_document(path, {"encoding": "auto"})
            self.assertEqual("中文 hello", result["text"])
        path = self.root / "sample.csv"
        path.write_text('名称,数量\n"测试,甲",12\n', encoding="utf-8")
        self.assertEqual(["测试,甲", "12"], processors.simple_document(path, {})["tables"][0]["rows"][1])

    def test_binary_text_and_bad_encoding_rejected(self):
        path = self.root / "sample.txt"
        path.write_bytes(b"abc\x00def")
        with self.assertRaises(processors.ProcessingError):
            processors.simple_document(path, {})
        path.write_bytes("中文".encode("gb18030"))
        with self.assertRaises(processors.ProcessingError):
            processors.simple_document(path, {"encoding": "utf-8"})

    def test_page_ranges_preserve_order_and_reject_overflow(self):
        self.assertEqual([1, 2, 3, 5], processors.selected_pages("5,1-3,2", 6, 200))
        for value in ("0", "3-1", "1-8", "1;2"):
            with self.assertRaises(processors.ProcessingError):
                processors.selected_pages(value, 6, 200)
        with self.assertRaises(processors.ProcessingError):
            processors.selected_pages("1", 201, 200)

    def test_xlsx_values_formulas_merges_and_sheet_selection(self):
        from openpyxl import Workbook
        workbook = Workbook()
        first = workbook.active
        first.title = "订单"
        first.append(["名称", "数量", "单价", "金额"])
        first.append(["样品", 12, 10, "=B2*C2"])
        first.merge_cells("A4:B4")
        workbook.create_sheet("其他").append(["忽略"])
        path = self.root / "sample.xlsx"
        workbook.save(path)
        result = processors.spreadsheet(path, {"sheetNames": ["订单"]})
        self.assertEqual(1, len(result["sheets"]))
        self.assertEqual(["A4:B4"], result["sheets"][0]["mergedRanges"])
        formula = next(c for c in result["sheets"][0]["cells"] if c["address"] == "D2")
        self.assertEqual("=B2*C2", formula["formula"])
        self.assertIsNone(formula["value"])
        with self.assertRaises(processors.ProcessingError):
            processors.spreadsheet(path, {"sheetNames": ["不存在"]})

    def test_xls_cached_values_and_formula_tokens(self):
        # xlwt is a test-only fixture writer, never a production dependency.
        import xlwt
        book = xlwt.Workbook()
        sheet = book.add_sheet("旧表")
        sheet.write(0, 0, "数量")
        sheet.write(1, 0, 12)
        sheet.write(1, 1, xlwt.Formula("A2*10"))
        sheet.write_merge(3, 3, 0, 1, "合并")
        path = self.root / "sample.xls"
        book.save(path)
        result = processors.spreadsheet(path, {})
        self.assertEqual(12, result["tables"][0]["rows"][1][0])
        formula = next(c for c in result["sheets"][0]["cells"] if c["row"] == 2 and c["column"] == 2)
        self.assertTrue(formula["formulaTokensHex"])
        self.assertIn("A2", formula["formula"])
        self.assertEqual(1, len(result["sheets"][0]["mergedRanges"]))

    def test_export_formats_and_result_limits(self):
        result = {"text": "hello", "tables": [{"rows": [["=1+1", "safe"]]}],
                  "segments": [{"start": 0, "end": 1.25, "text": "hello"}], "warnings": []}
        processors.write_result(result, "speech", self.root, 100000)
        with zipfile.ZipFile(self.root / "result.zip") as archive:
            self.assertIn("result.srt", archive.namelist())
            self.assertIn("00:00:01,250", archive.read("result.srt").decode())
            self.assertTrue(archive.read("table-1.csv").decode().startswith("'=1+1"))
        with self.assertRaises(processors.ProcessingError):
            processors.write_result(result, "speech", self.root, 1)

    def test_subtitle_rounding(self):
        self.assertEqual("01:00:00,000", processors.subtitle_time(3599.9999))
        self.assertEqual("00:00:01.250", processors.subtitle_time(1.25, True))

    def test_image_pixel_limits_have_an_explicit_error(self):
        from PIL import Image
        from unittest.mock import patch
        source = self.root / "oversized.png"
        Image.new("RGB", (10, 10)).save(source)
        with patch.object(processors, "MAX_IMAGE_PIXELS", 20), patch.object(Image, "MAX_IMAGE_PIXELS", 20), \
             patch.object(processors, "ocr_engine", return_value=None):
            with self.assertRaisesRegex(processors.ProcessingError, "像素"):
                processors.image_ocr(source, {}, {"maxPdfPages": 200})

    def test_private_api_authentication_and_independent_health(self):
        from fastapi.testclient import TestClient
        from unittest.mock import patch
        import app
        with patch.dict(os.environ, {"ENGINE_KEY": "x" * 32, "MODEL_DIR": str(self.root)}):
            client = TestClient(app.app)
            self.assertEqual(401, client.get("/v1/health/ocr").status_code)
            response = client.get("/v1/health/speech", headers={"X-Engine-Key": "x" * 32})
            self.assertEqual(200, response.status_code)
            self.assertFalse(response.json()["ready"])

    def test_api_cancel_terminates_child_process_group(self):
        from unittest.mock import patch
        import app

        async def check():
            key = "9efc7c75-1898-4ebe-92db-556756f65fab"
            child = await asyncio.create_subprocess_exec("/bin/sh", "-c", "sleep 60", start_new_session=True)
            app.running[key] = child
            try:
                request = type("Request", (), {"headers": {"x-engine-key": "x" * 32}})()
                from uuid import UUID
                await app.cancel(UUID(key), request)
                self.assertIsNotNone(child.returncode)
            finally:
                app.stop_process(child)
                await child.wait()
                app.running.pop(key, None)

        with patch.dict(os.environ, {"ENGINE_KEY": "x" * 32}):
            asyncio.run(check())


if __name__ == "__main__":
    unittest.main()
