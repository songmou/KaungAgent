"""Regenerate the checked-in synthetic fixtures; optional --legacy requires LibreOffice."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


def main():
    from PIL import Image, ImageDraw, ImageFont
    from reportlab.pdfgen import canvas
    from reportlab.lib import pdfencrypt
    from reportlab.lib.utils import ImageReader
    from docx import Document
    from openpyxl import Workbook
    import xlwt
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=Path(__file__).resolve().parents[2] / "tests/KaungMcp.Tests/Fixtures/Content")
    parser.add_argument("--legacy", action="store_true")
    args = parser.parse_args()
    root = args.output.resolve()
    root.mkdir(parents=True, exist_ok=True)
    font = os.environ.get("CONTENT_TEST_FONT") or next(str(p) for p in [
        Path("/System/Library/Fonts/PingFang.ttc"),
        Path("/System/Library/Fonts/STHeiti Light.ttc"),
        Path("/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc")
    ] if p.exists())
    ocr_text = "Kaung 内容处理测试\n订单编号 KAUNG2026\n数量 12 金额 1280.50\n本地识别 中文 English"
    image = Image.new("RGB", (1600, 1000), "white")
    draw = ImageDraw.Draw(image)
    draw.multiline_text((90, 120), ocr_text, font=ImageFont.truetype(font, 60), fill="black", spacing=40)
    image.save(root / "printed-zh-en.png")
    for kind in ("native", "scanned", "mixed"):
        pdf = canvas.Canvas(str(root / (kind + ".pdf")), pagesize=(800, 600), invariant=1)
        if kind in {"native", "mixed"}:
            pdf.setFont("Helvetica", 22)
            pdf.drawString(60, 530, "KAUNG DOCUMENT SAMPLE")
            pdf.setFont("Helvetica", 16)
            pdf.drawString(60, 475, "Order KAUNG2026")
            for y, row in [(420, ["Product", "Quantity", "Price"]), (380, ["Sample", "12", "120.00"])]:
                for x, value in zip([60, 300, 500], row):
                    pdf.drawString(x, y, value)
            for y in [450, 400, 360]:
                pdf.line(50, y, 650, y)
            for x in [50, 280, 480, 650]:
                pdf.line(x, 360, x, 450)
            if kind == "mixed":
                pdf.showPage()
        if kind in {"scanned", "mixed"}:
            pdf.drawImage(ImageReader(image), 0, 50, width=800, height=500)
        pdf.save()
    protected = canvas.Canvas(str(root / "encrypted.pdf"), invariant=1,
                              encrypt=pdfencrypt.StandardEncryption("fixture-password"))
    protected.drawString(60, 700, "Protected test document")
    protected.save()
    (root / "corrupt.pdf").write_bytes(b"%PDF-1.7\ninvalid document\n")
    doc = Document()
    doc.add_heading("Kaung 文档样例", 0)
    doc.add_paragraph("中文段落。Order KAUNG2026。")
    doc.add_paragraph("列表项目", style="List Bullet")
    table = doc.add_table(rows=2, cols=2)
    table.style = "Table Grid"
    table.cell(0, 0).text = "名称"
    table.cell(0, 1).text = "数量"
    table.cell(1, 0).text = "样品"
    table.cell(1, 1).text = "12"
    doc.save(root / "sample.docx")
    workbook = Workbook()
    first = workbook.active
    first.title = "订单"
    first.append(["名称", "数量", "单价", "金额"])
    first.append(["样品", 12, 10, "=B2*C2"])
    first.merge_cells("A4:B4")
    workbook.create_sheet("汇总").append(["订单", "KAUNG2026"])
    workbook.save(root / "sample.xlsx")
    old = xlwt.Workbook()
    sheet = old.add_sheet("旧版订单")
    sheet.write(0, 0, "数量")
    sheet.write(1, 0, 12)
    sheet.write(1, 1, xlwt.Formula("A2*10"))
    old.save(root / "sample.xls")
    for encoding, name in [("utf-8", "utf8.txt"), ("utf-16", "utf16.txt"), ("gb18030", "gb18030.txt")]:
        (root / name).write_bytes("中文 hello KAUNG2026".encode(encoding))
    (root / "sample.csv").write_text("名称,数量\n样品,12\n", encoding="utf-8")
    expected = {
        "ocr": ocr_text,
        "speechZh": "今天我们测试本地语音识别功能。所有文件都在本地处理。识别完成以后，我们可以查看文字和时间戳。",
        "speechEn": "Today we are testing local speech recognition. All files are processed locally. After recognition is complete, we can read the text and check the timestamps."
    }
    (root / "expected.json").write_text(json.dumps(expected, ensure_ascii=False, indent=2), encoding="utf-8")
    if args.legacy:
        executable = shutil.which("libreoffice")
        if not executable:
            raise RuntimeError("--legacy requires LibreOffice")
        with tempfile.TemporaryDirectory() as temp:
            # Keep legacy table semantics independent of LibreOffice's DOCX-to-DOC export.
            source = Path(temp) / "sample.rtf"
            source.write_text(r"""{\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}\f0\fs24
Kaung Document Sample\par Order KAUNG2026\par
\trowd\trgaph108\trleft0\cellx3000\cellx6000
\intbl Product\cell Quantity\cell\row
\trowd\trgaph108\trleft0\cellx3000\cellx6000
\intbl Sample\cell 12\cell\row\pard\par}
""", encoding="ascii")
            subprocess.run([executable, "-env:UserInstallation=" + (Path(temp) / "profile").as_uri(), "--headless", "--convert-to", "doc",
                            "--outdir", str(root), str(source)], check=True)
    print(root)


if __name__ == "__main__":
    main()
