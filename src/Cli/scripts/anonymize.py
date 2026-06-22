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
    (r'Né[e]?\s+le\s+\d{1,2}\s+\w+\s+\d{4}', "BIRTHDATE"),
    (r'\b\d{1,3}\s+ans\b', "AGE"),
    (r'\b(marié|mariée|célibataire|divorcé|divorcée)\b', "MARITAL"),
    (r'\b\d+\s+enfant[s]?\b', "CHILDREN"),
    (r'\b\d{5}\s+[A-Za-zÀ-ÖØ-öø-ÿ\- ]+\b', "ADDRESS"),
    (r'\b\d{1,4}\s+(rue|avenue|av\.?|boulevard|bd\.?|impasse|chemin|route)\b.+', "ADDRESS"),
]


def extract_clean_text(page):
    raw = page.get_text("rawdict")
    spans = []

    # 1) Collecter tous les spans avec position
    for block in raw["blocks"]:
        if block["type"] != 0:
            continue
        for line in block["lines"]:
            for span in line["spans"]:
                text = span.get("text", "")
                if not text.strip():
                    continue  # ignorer spans vides

                spans.append({
                    "text": text,
                    "x": span["bbox"][0],
                    "y": span["bbox"][1],
                    "size": span.get("size", 10),
                })

    if not spans:
        return ""

    # 2) Trier par Y puis par X
    spans.sort(key=lambda s: (round(s["y"], 1), s["x"]))

    # 3) Regrouper par lignes (tolérance verticale)
    lines = []
    current_line = []
    last_y = None
    y_threshold = 1.5

    for span in spans:
        if last_y is None or abs(span["y"] - last_y) <= y_threshold:
            current_line.append(span)
        else:
            lines.append(current_line)
            current_line = [span]
        last_y = span["y"]

    if current_line:
        lines.append(current_line)

    # 4) Reconstruire chaque ligne avec espaces basés sur la distance X
    result = []
    for line in lines:
        line.sort(key=lambda s: s["x"])
        text = ""
        last_x = None

        for span in line:
            if last_x is not None:
                gap = span["x"] - last_x
                if gap > span["size"] * 0.6:
                    text += " "
            text += span["text"]
            last_x = span["x"] + (span["size"] * len(span["text"]) * 0.5)

        result.append(text.strip())

    return "\n".join(result)


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

def extract_full_name_from_document(doc):
    for i in range(len(doc)):
        text = doc[i].get_text("text")
        name = get_full_name(text)
        if name:
            return name
    return None

def get_name_variants(full_name):
    if not full_name:
        return []
    name = full_name.strip()
    parts = name.split()
    variants = set()
    variants.add(name)
    variants.add(name.lower())
    variants.add(name.replace(" ", "").lower())
    variants.add(" ".join(list(name.replace(" ", ""))).lower())
    for p in parts:
        if len(p) > 2:
            variants.add(p)
            variants.add(p.lower())
            variants.add(" ".join(list(p.lower())))
    return list(variants)

def redact_standard_text(page, text, full_name, page_summary):
    entities = []
    if full_name:
        for variant in get_name_variants(full_name):
            entities.append({"text": variant, "label": "NAME"})
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
    name_variants = get_name_variants(full_name) if full_name else []
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
                if (
                    (name_parts and any(part in line_lower for part in name_parts))
                    or (name_variants and any(v in line_lower for v in name_variants))
                ):
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
        except Exception:
            continue
        if detector and detector.detect_faces(image_bytes):
            for rect in page.get_image_rects(xref):
                page.add_redact_annot(rect, text="", fill=(0.5, 0.5, 0.5))
                page_summary.setdefault("FACE_PHOTO", set()).add(f"Image XREF {xref}")

def redact_page(doc, page, global_summary, detector, full_name):
    for link in page.get_links():
        page.delete_link(link)
    text = extract_clean_text(page)
    page_summary = {}
    if detector:
        redact_images_with_faces(doc, page, detector, page_summary)
    redact_standard_text(page, text, full_name, page_summary)
    redact_urls_robust(page, full_name, page_summary)
    page.apply_redactions(images=fitz.PDF_REDACT_IMAGE_REMOVE)
    for k, v in page_summary.items():
        global_summary.setdefault(k, set()).update(v)

def find_onnx_model(model_dir):
    if os.path.isfile(model_dir) and model_dir.endswith(".onnx"):
        return model_dir
    if os.path.isdir(model_dir):
        for f in os.listdir(model_dir):
            if f.endswith(".onnx"):
                return os.path.join(model_dir, f)
    return None

def anonymize(model_dir, pdf_file):
    model_path = find_onnx_model(os.path.join(model_dir, "face"))
    detector = FaceDetector(model_path) if model_path and os.path.exists(model_path) else None
    doc = fitz.open(pdf_file)
    global_summary = {}
    full_name = extract_full_name_from_document(doc) if len(doc) > 0 else None
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
