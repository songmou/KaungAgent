"""Explicit online preparation; processing itself runs with local files only."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import shutil


def main():
    root = Path(os.environ.get("MODEL_DIR", "/models"))
    root.mkdir(parents=True, exist_ok=True)
    from huggingface_hub import snapshot_download
    from rapidocr import RapidOCR
    import rapidocr

    # The pinned RapidOCR package fixes the default model generation and model checksums.
    RapidOCR()
    source = Path(rapidocr.__file__).parent / "models"
    target = root / "ocr"
    target.mkdir(exist_ok=True)
    config = {}
    for stage in ("Det", "Cls", "Rec"):
        choices = sorted(source.glob(f"*{stage.lower()}*.onnx"))
        if not choices:
            raise RuntimeError(f"RapidOCR did not prepare the {stage} model")
        model = choices[0]
        shutil.copy2(model, target / model.name)
        config[stage + ".model_path"] = model.name
    (target / "config.json").write_text(json.dumps(config), encoding="utf-8")
    sources = json.loads(Path(__file__).with_name("model-sources.json").read_text(encoding="utf-8"))
    for source in sources["docling"]:
        snapshot_download(source["repository"], revision=source["revision"],
                          allow_patterns=source.get("patterns"),
                          local_dir=root / "docling" / source["repository"].replace("/", "--"))
    (root / "docling" / ".ready").touch()
    source = sources["speech"]
    snapshot_download(source["repository"], revision=source["revision"], local_dir=root / "whisper-small")
    manifest = {}
    for directory in (root / "ocr", root / "docling", root / "whisper-small"):
        for path in sorted(directory.rglob("*")):
            if path.is_file() and ".cache" not in path.parts:
                digest = hashlib.sha256()
                with path.open("rb") as stream:
                    while chunk := stream.read(1048576):
                        digest.update(chunk)
                manifest[str(path.relative_to(root))] = digest.hexdigest()
    (root / "model-lock.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print("Models prepared; retain model-lock.json with the deployment.")


if __name__ == "__main__":
    main()
