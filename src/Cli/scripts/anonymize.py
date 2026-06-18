# /// script
# requires-python = ">=3.12"
# dependencies = [
#   "numpy",
#   "onnxruntime",
#   "opencv-python",
#   "PyMuPDF",
# ]
# ///
import sys
import re
import fitz
import os
from face_detector import FaceDetector

REGEX_PATTERNS = [
    (r'\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b', "EMAIL"),
    (r'(?:\+33|0033|0)\s*[1-9](?:[\s.\-]?\d{2}){4}', "PHONE"),
]

def get_full_name(text):
    lines = [l.strip() for l in text.split("\n") if l.strip()]
    if lines:
        first = lines[0]
        if re.search(r'[:;?!_*/\\]', first):
            return None
        if (
            1 < len(first.split()) <= 4
            and not any(c.isdigit() for c in first)
            and not first.isupper()
            and len(first) > 3
        ):
            return first.strip()
    return None


def redact_standard_text(page, text, full_name, page_summary):
    entities = []
    if full_name:
        entities.append({"text": full_name, "label": "NAME"})

    for pattern, label in REGEX_PATTERNS:
        for m in re.finditer(pattern, text, re.IGNORECASE):
            entities.append({"text": m.group(), "label": label})

    entities_sorted = sorted(entities, key=lambda x: len(x["text"]), reverse=True)
    seen = set()

    for ent in entities_sorted:
        if ent["text"] in seen:
            continue
        rects = page.search_for(ent["text"])
        for rect in rects:
            page.add_redact_annot(
                rect, text="[REDACTED]", fill=(0, 0, 0),
                text_color=(0, 0, 0), fontname="helv", fontsize=8
            )
            page_summary.setdefault(ent["label"], set()).add(ent["text"])
        seen.add(ent["text"])


def redact_urls_robust(page, full_name, page_summary):
    blocks = page.get_text("dict")["blocks"]
    name_parts = []
    if full_name:
        name_parts = [full_name.lower().replace(" ", "")] + [
            p.lower() for p in full_name.split() if len(p) > 2
        ]

    for b in blocks:
        if "lines" not in b:
            continue
        for line in b["lines"]:
            line_text = "".join(span["text"] for span in line["spans"]).strip()
            line_lower = line_text.lower()
            is_url = bool(re.search(r'https?://|www\.|linkedin\.com/|github\.com/', line_lower))

            if is_url:
                rect = fitz.Rect(line["bbox"])
                label = "URL"
                if "linkedin.com" in line_lower or "github.com" in line_lower:
                    label = "NETWORK"
                if name_parts and any(part in line_lower for part in name_parts):
                    label = "URL_NAME"

                page.add_redact_annot(
                    rect, text="[REDACTED]", fill=(0, 0, 0),
                    text_color=(0, 0, 0), fontname="helv", fontsize=8
                )
                page_summary.setdefault(label, set()).add(line_text)


def redact_images_with_faces(doc, page, detector, page_summary):
    for img_info in page.get_images(full=True):
        xref = img_info[0]

        try:
            base_image = doc.extract_image(xref)
            image_bytes = base_image["image"]
        except Exception as e:
            print(f"Cannot extract image xref {xref}: {e}")
            continue

        if detector.detect_faces(image_bytes):
            for rect in page.get_image_rects(xref):
                page.add_redact_annot(rect, text="", fill=(0.5, 0.5, 0.5))
                page_summary.setdefault("FACE_PHOTO", set()).add(f"Image XREF {xref}")


def redact_page(doc, page, global_summary, detector, full_name):
    for link in page.get_links():
        page.delete_link(link)

    text = page.get_text("text")
    page_summary = {}

    # Process images before text to avoid annotation overlap
    if detector:
        redact_images_with_faces(doc, page, detector, page_summary)

    redact_standard_text(page, text, full_name, page_summary)
    redact_urls_robust(page, full_name, page_summary)

    page.apply_redactions(images=fitz.PDF_REDACT_IMAGE_REMOVE)

    for k, v in page_summary.items():
        global_summary.setdefault(k, set()).update(v)


def find_onnx_model(model_dir):
    """Find a .onnx file in the specified directory."""
    if os.path.isfile(model_dir) and model_dir.endswith(".onnx"):
        return model_dir
    if os.path.isdir(model_dir):
        for f in os.listdir(model_dir):
            if f.endswith(".onnx"):
                return os.path.join(model_dir, f)
    return None


def anonymize(model_dir, pdf_file):
    model_path = find_onnx_model(os.path.join(model_dir, "face"))

    detector = None
    if model_path and os.path.exists(model_path):
        detector = FaceDetector(model_path)
    else:
        print(f"WARNING: No .onnx model found in '{model_dir}'. Face detection disabled.")

    doc = fitz.open(pdf_file)
    global_summary = {}

    full_name = None
    if len(doc) > 0:
        first_page_text = doc[0].get_text("text")
        full_name = get_full_name(first_page_text)

    for page in doc:
        redact_page(doc, page, global_summary, detector, full_name)

    out = pdf_file.replace(".pdf", ".anon.pdf")

    doc.set_metadata({})
    doc.save(out, garbage=4, deflate=True, clean=True)
    doc.close()

    report = {k: sorted(list(v)) for k, v in global_summary.items()}

    total = sum(len(v) for v in report.values())
    print(f"Output : {out}")
    print(f"Name   : {full_name or 'not detected'}")
    print(f"Redacted: {total} item(s) across {len(report)} categor{'y' if len(report) == 1 else 'ies'}")
    if report:
        for k, v in report.items():
            print(f"  {k:<12} {len(v):>2}  " + ", ".join(v))


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print("Usage: python anonymizer.py <model_dir> <file.pdf>")
        sys.exit(1)

    model_dir = sys.argv[1]
    pdf_file = sys.argv[2]

    if not os.path.exists(pdf_file):
        print(f"Error: PDF file '{pdf_file}' not found.")
        sys.exit(1)

    anonymize(model_dir, pdf_file)
