# 内容处理固定样例

这些样例为项目合成数据，不含真实客户信息。

- `printed-zh-en.png`：中英文印刷文字，预期文字位于 `expected.json`。
- `native.pdf`、`scanned.pdf`、`mixed.pdf`：文字、扫描图、文字与扫描混排；文字型 PDF 包含简单表格。
- `encrypted.pdf`、`corrupt.pdf`：验证加密和损坏文件明确失败。
- `sample.docx`、`sample.doc`：标题、段落、列表和表格。
- `sample.xlsx`：两个工作表、公式及合并单元格；`sample.xls` 含旧版 BIFF 公式。
- `utf8.txt`、`utf16.txt`、`gb18030.txt`、`sample.csv`：文本编码和 CSV。
- `audio-zh.wav`、`audio-en.wav`：16 kHz 单声道合成录音，来自 macOS Tingting / Samantha 测试语音；逐字参考文本位于 `expected.json`。

使用 `infra/content-engine/generate_fixtures.py` 重建文档和图片；`--legacy` 通过标准 RTF 表格和 LibreOffice 生成 DOC，独立验证旧版表格结构。录音参考文本固定，准确率验收不忽略识别错误，只归一化大小写、空白和标点。
